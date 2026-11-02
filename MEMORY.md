# MEMORY.md — Factory Sim (persistent memory)

## CURRENT STATE (Nov 2026) — torch lighting
Branch `feat/fast-voxel-edits`. Committed + pushed:
- `a46c53d` — torch lights its own chunk: full-field `RecomputeLightAtSource`
  (delta + source patch into the wide volumes), ±2 remesh shell
  (`LightSampleCellsForChunkLocal`), cold-start guard scoped to the edited
  chunk only (`worldSettled = !instantChunk.MeshBuildInFlight`).
- `87765a0` — cross-chunk neighbour loop no longer skips a neighbour just
  because it has an async build in flight (delta is idempotent; the neighbour's
  pending full rebuild supersedes it). Kept `DataReady`/`HasWideCache`.

**COMMITTED `825ca59` — torch light across borders + second-torch seam (final, simplified approach):**
The cross-chunk torch path was REPLACED with the simple, terrain-independent
approach: iterate a SPHERE of world voxels around the torch (radius lightR),
collect the DISTINCT chunks those voxels touch (excluding the edited chunk), and
mark each loaded neighbour `NeedsMesh = true` for a full rebuild. A full rebuild
re-runs the light flood fill reading the edited chunk's now-correct `BlockLight`
(SetVoxel wrote the torch's level), so the neighbour converges to the exact
full-flood-fill result. NO wide-index mapping, NO sky-column interaction, NO
cache staleness — works on flat AND rough/angled terrain alike. The dead
`RecomputeLightLocalAt` method was REMOVED from VoxelChunk.cs.
KEY FIX for the "weird line when placing a 2nd torch near a 1st": when a LIGHT
SOURCE is placed/broken (_patchLight > 0), the edited chunk is NO LONGER marked
instant-satisfied — so the budget loop launches its full rebuild THIS frame.
The instant sub-cell remesh only re-meshes sub-cells whose sampled light changed
per the delta; placing a 2nd torch near a 1st leaves the region BETWEEN them
already lit (delta reports no change there), so those sub-cells are never
re-meshed and a stale seam (weird line) appears where old light meets new.
Forcing a full rebuild for light-source edits re-meshes every sub-cell with the
authoritative flood-fill field, eliminating the seam. Plain geometry edits
(_patchLight == 0) still get the instant-satisfied optimisation.

**COMMITTED `fa64a19` — affected chunks rebuild ASAP:** when a light source is
placed, each touched neighbour chunk is stamped with a fresh rising `EditPriority`
(`++_editPriorityCounter`) + `NeedsMesh = true`, so the budget loop prioritises
its full rebuild. Distinct from MarkNeighboursForRebuild (generation
completions), which deliberately does NOT stamp a rising priority.

**ATTEMPTED & REVERTED — same-frame neighbour lighting (caused a hitch, do NOT
redo naively):** to kill the frame-of-lag in fa64a19, a synchronous, same-frame
light recompute (`RecomputeLightSynchronous`) was added: on the MAIN thread it
re-ran the full flood fill (`BuildLightFieldWide`) + re-meshed every sub-cell for
each torch-affected neighbour. It was byte-correct but caused a BIG HITCH — a full
flood fill on a 16×256×16 chunk is ~1–3 ms on the main thread, and it ran for
every affected neighbour (up to 8) in the same frame the torch was placed. The
working tree was reverted to fa64a19 (the deferred `NeedsMesh` + rising
`EditPriority` stamp). If a faster neighbour light-up is wanted again, the fix must
keep the flood fill OFF the main thread (worker thread / existing async budget),
NOT run it synchronously.

**WORKING BRANCH `feat/cross-chunk-torch-delta` (option 1 — "the Minecraft way") —
instant cross-chunk torch lighting via a BOUNDED DELTA (NOT a full rebuild, NOT a
sync flood fill):** the neighbour is lit THIS frame by a Starlight DELTA scoped to
the torch, reusing the cached wide field + volumes. Three pieces:
 1. `SkyLight.ComputeDelta` gained `skipSky` + `radius` params. `skipSky=true`
    skips the sky-column pass (the source's solid state didn't change, so the sky
    is already correct; on rough/angled terrain the sky-column pass's topOpen walk
    set the air above a partially-open column's source to maxLevel, defeating the
    block-light offer — THE exact bug that made the old patch-and-delta fail on
    rough terrain). `radius` clamps the BFS to the torch's light sphere (level-1),
    stopping it from leaking past the neighbour's wide-field pad (the neighbour's
    field is only 1 chunk + one light-range wide, but the torch's light can reach
    deeper when the torch is set far from the shared border).
 2. `VoxelChunk.RecomputeLightFromSource(world, srcWorldX, srcWorldY, srcWorldZ)`:
    maps the torch's WORLD position to THIS chunk's wide index (relative to THIS
    chunk's origin — the mapping the old approach got wrong), then runs
    `ComputeDelta(skipSky:true, radius: level-1)`. KEY INSIGHT: the torch's
    BlockLight is ALREADY in this chunk's cached wide `block` volume (BuildWide
    read the edited chunk's live BlockLight), so NO patch is needed; the cached
    field is already the converged result and we only propagate the change.
    Returns the sub-cells to re-mesh.
 3. `VoxelWorld` cross-chunk block: for each touched neighbour (not mid-build),
    call `RecomputeLightFromSource` then `RebuildSubCell` for each changed cell
    (one shared neighbour snapshot). Same-frame, bounded — no full flood fill, no
    whole-chunk remesh, no main-thread hitch (the delta is ~sub-millisecond).
VALIDATED: a standalone test (/tmp/sltest) proved the skipSky+radius delta is
BIT-IDENTICAL (mismatches=0) to the full `SkyLight.Compute` reference over the
whole field (2613 voxels changed, refLit=newLit). The earlier test failures were
a TEST-harness bug (the reference curve didn't account for the blockLit mask),
NOT an algorithm bug. `dotnet build`: 0 warnings / 0 errors. Runtime (editor)
verification of a real torch is still pending (no Godot binary on this machine).

## OPEN BUG (next task): dark edge between chunk borders
A visible dark line at chunk seams (enclosed + open areas). NOT yet fixed.
Investigated statically (no fix yet): the MC sub-cell surface + seam-ownership
(east/south/diag loaded) + per-vertex light sampling (`ResolveSubCellVertex`
samples the cached wide field by the air corner's world voxel) all look correct
in isolation. The two candidate causes to pin down with a ONE-SHOT (NOT
per-frame!) probe:
  (a) LIGHT MISMATCH — the two sides of the seam sample different light values
      (field/sampling inconsistency at the border).
  (b) SURFACE GAP — one side's seam sub-cell produces no surface at the exact
      seam plane (a 1-voxel gap from ownership), reading as a dark line.
WARNING: a previous attempt added a PER-FRAME diagnostic (DiagnoseSeamLightOnce
called every frame in _Process) that broke the torches again (frame churn /
race). Any new probe MUST be strictly one-shot (fire a few times max, gated by
a flag, and never run on the hot edit path). Prefer static analysis + a
standalone solver repro (/tmp/sltest pattern) before touching runtime code.

## Lighting system — architecture & perf (Nov 2026)

The voxel lighting is a **Minecraft-style flood-fill** (sky + block light) that is
heavily optimized for instant edits. Key pieces:

- `Scripts/Voxel/SkyLight.cs` — the light solver:
  - `Compute` — full max-BFS flood fill over a wide (3x3-chunk) padded volume.
  - `ComputeLocal` — re-solve a bounded box from its fixed boundary (fallback).
  - `ComputeDelta` — **Starlight-style incremental** delta: propagates only the
    change from one edited voxel (bit-identical to the full flood fill). Uses a
    per-chunk `raw` integer-level working buffer (synced by `RecomputeDeltaRaw`).
- `Scripts/Voxel/MeshBuilder.cs` — `BuildLightFieldWide` (flood fill),
  `ApplyLightResponse` (the falloff/dark-gamma/floor curve).
- `Scripts/Voxel/VoxelChunk.cs` — per-chunk CACHED wide light field
  (`_cachedLightField`, already curved), `_cachedWideSolid`/`_cachedWideBlock`
  volumes, `_deltaRawField`, and the local recompute + sub-cell remesh helpers.
- `Scripts/Voxel/VoxelWorld.cs` — the **instant-edit path** (in `_Process`, after
  the budget loop): geometry-dirty sub-cells always re-mesh; light-dirty sub-cells
  re-mesh only if their sampled light changed; cross-chunk light for border-crossing
  sources. There is a **cold-start guard** that skips the light recompute while the
  world is settling (first builds in flight).

### Light response curve = LUT (perf)
`ApplyLightResponse`, `VoxelChunk.ApplyLightResponseBox`, and
`VoxelChunk.MakeLightCurve` all evaluate the falloff (`x^LightFalloff`) + dark-gamma
(`x^DarknessGamma`) stages through `MeshBuilder.CurveLut(exp, dg)` +
`MeshBuilder.CurveIndex(v)` — a 256-entry LUT over the coarse integer light grid,
instead of up to 2× `Math.Pow` per voxel across the ~385k-voxel wide field. The
scene uses `LightFalloff = 15`, `MaxLightLevel = 30`, `MinLight = 0.011`, so this is
the active hot path. **Block-lit voxels are EXEMPT from the falloff/gamma** (they go
straight to the floor) — that's why the LUT only applies to sky-lit/dark voxels.
Keep these three call sites consistent if you touch the curve.

### Bugs fixed (Nov 2026): torch light not reaching the full area
Two related defects, both "place a torch, the light doesn't fill its area":

1. REMESH-EXTENT MISMATCH (neighbours + edited chunk): sub-cells are 8×32×8 and a
   sub-cell's surface re-meshes from its region **+ a 2-voxel padding** (see
   `SubCellSamplesLightChanged`: `x0-2..x1+2`). The changed-voxel→sub-cell map used
   `DirtyCellsForChunkLocal` (containing cell + 1-voxel shell), so sub-cells 2..N
   voxels from a changed light voxel were never re-meshed → kept stale dark light.
   Fix: `VoxelChunk.LightSampleCellsForChunkLocal` (±2 shell) maps changed light
   voxels to the sub-cells that actually SAMPLE them (used by the light paths).
   Geometry edits still use the tighter `DirtyCellsForChunkLocal` (±1).

2. EDITED-CHUNK light path. Symptom (recurring): "the torch lights its neighbours
   but NOT the chunk it sits on." The edited chunk's light originally used
   `RecomputeLightLocal` (box-restricted). It was switched to a new
   `VoxelChunk.RecomputeLightAtSource` (full-field `ComputeDelta` centred on the
   edited voxel — the SAME path that works for neighbours, `RecomputeLightLocalAt`).
   **The real root cause (the one that actually fixed it):** `RecomputeLightAtSource`
   must PATCH the edited voxel's new solid + block-light source into the cached wide
   volumes (`_cachedWideSolid`/`_cachedWideBlock`) BEFORE running `ComputeDelta`.
   The cached wide block volume holds the PRE-edit value, so a just-placed torch
   reads `blockLight == 0` in the delta → the delta sees no source → propagates
   nothing → the source chunk stays dark. The neighbour path never needs the patch
   (its wide block volume already carries the source from the previous full build),
   which is why neighbours lit but the source chunk didn't. The patch is the SAME one
   `RecomputeLightLocal` applies; `RecomputeLightAtSource` now applies it (and
   consumes `_hasPatch`). The remesh gate: geometry-dirty cells ALWAYS remesh;
   light-dirty cells remesh iff their sampled light changed. Dead box-sizing code
   (boxRange / sourceAddedOnly) and the `lightRecomputed` flag were removed.

### THE REAL torch-light root cause (Nov 2026): the COLD-START GUARD scope
Symptom: "place a torch in an enclosed cave, the light doesn't update until a few
blocks out / it never fills near the torch." Root cause (found via a [TORCH-GATE]
runtime log): the instant-edit light gate `if (anyLightRelevant && worldSettled)` was
skipped because `worldSettled` was **false**. `worldSettled` was computed by iterating
**ALL** chunks and returning false if *any* chunk had an async build in flight
(`MeshBuildInFlight`). In a 12-chunk render distance, async builds are near-constant,
so `worldSettled` was almost always false → the torch's `ComputeDelta` light update
NEVER ran → the near region stayed dark. The log showed `worldSettled=False` while the
edited chunk itself had `meshInFlight=False dataReady=True hasWideCache=True`.
Fix: scope the guard to the EDITED chunk only —
`bool worldSettled = !instantChunk.MeshBuildInFlight;`. A distant chunk's in-flight
build doesn't contend with this chunk's wide light field, so it must not block the
torch. The guard still prevents the one-frame race when the edited chunk's own async
build owns the field + sub-cells. (The other three fixes — LightSampleCellsForChunkLocal,
full-field RecomputeLightAtSource, and the source patch — were correct but masked by
this gate; all four are needed for correct torch lighting.)

### IMPORTANT: torches on the OPEN SURFACE legitimately show NO glow
The lighting is a MAX-BFS: block light (a torch) can only ADD light, never subtract.
Open-sky air is already at maxLevel (raw 30/30 → curve → 1.0, the ceiling). A torch
(block light 14) offers raw 13 to neighbours that are already at raw 30, so it changes
nothing (verified standalone: `ComputeDelta` returns changed=1, just the source voxel).
So a torch placed on top of terrain (sky above) is CORRECT to show no glow — this is
not a bug and was always true (it predates the light fixes). The "torch lights its
neighbours" impression from an open-surface test was misleading: the neighbours were
already at full sky brightness. **To verify torch lighting, place the torch in an
ENCLOSED dark space (a cave / under a ceiling), where the air is dark and the torch
actually adds light.** (Standalone repro in /tmp/sltest confirmed: enclosed torch
lights ~3000 voxels, open-sky torch lights 0.)

### Verified
`dotnet build` = 0 warnings / 0 errors. No Godot binary on this machine, so
runtime/editor behaviour (and the `TestMcRewrite.tscn` / `WorldSaveTest.tscn` editor
suites) are NOT verified from the CLI. The torch test (`McRewriteTest.TestTorchLightsAir`)
uses tolerance-based checks on an ENCLOSED box, so it exercises the real torch path.
