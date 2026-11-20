# MEMORY.md — Factory Sim (persistent memory)

## CURRENT STATE (Nov 2026) — player falls through terrain on save load

**FIXED (3-part)** (uncommitted): the "load a save, player falls through the
terrain" bug had three contributing defects:
1. GATE CHECKED THE WRONG SPOT: the spawn gate's live-collision check ran
   `HasLiveCollisionAt(SpawnSeedPosition(playerNode.GlobalPosition))` — the SCENE
   position (usually the origin), not the SAVED position the player is actually
   restored at. A save whose restored column had no live collision body passed
   the gate anyway. Fix: the gate now seeds from `_spawnRestorePos` (the saved
   XZ, set when the anchor froze; the seed Y still comes from the terrain scan,
   never the stale saved Y).
2. NO GROUND-UNDER CHECK ON RESTORE: `ReseedPlayerOntoSurface` only lifts the
   player UP when buried — it never verifies the ground is actually solid at the
   restored spot. With the saved Y above the surface (saved mid-air, on a
   since-broken structure, or a column whose surface the gate's scan resolves
   differently), the first physics step (no CCD: gravity 20 × 1/60s ≈ 0.33+
   voxels of travel) tunnels the capsule through the floor into the solid below,
   and a concave mesh can't push a body out from inside. Fix: new
   `ReseedIfNoGroundUnder` — probes ~2 voxels under the capsule bottom at the
   restored XZ; when it's air, seats the capsule on the column's real surface
   (same XZ/yaw). No-op when the saved spot already has ground (the normal case).
3. SEATING FORMULA PUT THE CAPSULE ABOVE THE GROUND (the real cause, confirmed
   by [PlayerDiag] STALL logs: position frozen at the spawn point, velocity at
   terminal -40, onFloor=False — the capsule was trapped INSIDE the concave MC
   mesh). `SpawnSeedPosition` / `ReseedPlayerOntoSurface` /
   `ReseedIfNoGroundUnder` all seated the capsule at
   `surfaceTop + SpawnCapsuleHalfHeight + SpawnSettleLift`, i.e. the capsule
   BOTTOM 0.02 ABOVE the surface top. The first physics step was therefore a
   FALL, and Godot's MoveAndSlide is DISCRETE (no CCD — Godot 4's motion_mode is
   only GROUNDED/FLOATING; I wrongly set FLOATING(1) once thinking it was CCD,
   which broke the controls and was reverted). A first-step fall over a concave
   MC mesh tunnels the capsule into the solid, where it can't be pushed out.
   Fix: the seating is now `surfaceTop + SpawnCapsuleHalfHeight - SpawnSettleLift`
   in ALL THREE places — the capsule bottom is embedded 0.02 INTO the surface
   (overlap, not a gap), so the first step is a settle, not a fall.
4. THE REAL ROOT CAUSE (confirmed by the voxel-column diag): the actual game
   runs the player as an INSTANCE of Scenes/Player.tscn inside Scenes/Main.tscn
   ("PlayerCharacter" under "Player"), and that instance carried LOCAL OVERRIDES
   that I kept missing (I was editing the standalone Player.tscn, not the
   instance): `floor_max_angle = 1.309` (75° — default is 45°), `wall_min_slide_angle
   = 0.873`, and `motion_mode = 1` (FLOATING). The 75° floor angle made a
   beveled/sloped surface at the spawn point classify as a degenerate wall/floor
   hybrid, so the (slightly embedded) capsule WEDGED: `onWall=True, onFloor=False`,
   and a concave MC mesh can't push an embedded body out → it jittered and fell
   forever, horizontal input killed. Fix: removed `motion_mode = 1` (FLOATING — the real wedge cause) and
   `wall_min_slide_angle = 0.873` from Main.tscn (kept WalkSpeed/SprintSpeed/
   Acceleration). KEPT `floor_max_angle = 1.3089969` (75°) — INTENTIONAL:
   the user wants the player to walk up steep terrain steps; it is safe now
   that motion_mode is GROUNDED (0) and the capsule is seated with a slight
   overlap (no deep embedding to wedge on). NOTE: the Zombie instance in Main.tscn
   keeps floor_max_angle=1.5707 (90°, intentional — it climbs walls).
LESSON: when a node is instanced, the .tscn it's an instance OF is NOT the source
of truth — the INSTANCE's local overrides in the PARENT scene win. Always check
the parent scene's instance overrides before concluding a property is "default."
Ordering note: `RestorePlayerFromSave` → `ReseedPlayerOntoSurface` (lift if
buried) → `ReseedIfNoGroundUnder` (seat if floating over void/broken ground).
The `[PlayerDiag]` STALL diagnostic in `Player.cs` and the
`VoxelWorld.DiagSeedPosition` helper were REMOVED once the fix was verified
in-editor (the player moves normally now).

## CURRENT STATE (Nov 2026) — grass deco on non-flat voxels

**FIXED — grass spawning on slopes / the loaded-area edge** (uncommitted): the
`SpawnGrassForChunk` flat-surface guard read neighbours via `GetVoxel`, which
returns AIR for an UNLOADED or STILL-GENERATING chunk — but the mesher
(`VoxelSolidForMesh`) treats those cells as SOLID (open-edge rule, no wall at
the render boundary). So at the edge of the loaded area the guard saw phantom
air where the MESH actually shows a solid beveled slope, passed the "all eight
solid" test, and planted grass on the non-flat isosurface (grass sticking out
of / floating over the terrain). Fix: the guard now reads through
`VoxelSolidForMesh` (the mesher's own query), so the guard and the visible mesh
always agree. The position re-validation (below = grass block, self = air)
stays on raw `GetVoxel` (it must see the real material, and the own chunk is
data-ready there). The old CHUNK-EDGE GUARD (skip border positions) was REMOVED:
it was redundant — the own-chunk re-validation already covers it — and with
the solid query it would only have suppressed valid grass at the edge. Note:
grass is NOT persisted in the save (WorldSave has no deco section), so floating
grass at a chunk edge despawns with the chunk on unload; a neighbour that later
finishes with a lower surface leaves the grass floating until then (matches the
open-edge mesh; acceptable).

## CURRENT STATE (Nov 2026) — foliage overhang across chunk borders

**FIXED — overhang queue leak on faulting generations** (uncommitted, on top of
`bc1feca`): the canopy/hive border-overhang queues (`TerrainGenerator.
_pendingOverhangs` / `_pendingHiveOverhangs`, both `static`) were only ever
drained by `OnGenerationComplete` — a chunk whose generation FAULTS
(`OnGenerationFaulted`: e.g. OOM inside a stage) never reaches that drain, so its
queued overhangs linger. A chunk flapping at the load-ring edge
(fault → re-generate → fault) re-accumulates a fresh batch every cycle while the
old one is still queued → unbounded growth (memory leak + a bloated authoritative
`ReDrainOverhangsUntilStable` that makes the main thread stall during the
generation-complete burst — the stall slows neighbour generation, which is exactly
the timing window where overhangs get dropped). Fix: `OnGenerationFaulted` now
calls new `TerrainGenerator.DiscardPendingOverhangs(coord)` /
`DiscardPendingHiveOverhangs(coord)` (drop-without-apply, under the per-chunk
drain lock). Nothing is lost: the neighbours that queued for the faulting chunk
are still generating, so their foliage stages re-queue for the fresh run (the
re-generation's own `DrainOverhangsForOwnStage` + `OnGenerationComplete` drain
catches them).

The overhang pipeline itself (audited, looks race-tight): gen threads write
border voxels via `MakePut` (write if `DataReady`, else queue); the chunk's own
foliage stage runs a non-destructive drain-and-restore (`DrainOverhangsForOwnStage`);
the authoritative destructive drain (`ReDrainOverhangsUntilStable`, re-drain until
a pass applies nothing, under the per-chunk lock) runs on the MAIN thread in
`OnGenerationComplete` — and that is the ONLY place a queue entry is removed (plus
the new fault path). `MakePut` writes are air-only (canopy), so a re-applied voxel
into now-solid terrain is a no-op. Hives have a parallel queue drained by
`ReDrainHiveOverhangs` (single destructive pass; appender is main-thread only).

**FIXED — direct-write race on the finalize window** (uncommitted, second half-tree
fix): the `DataReady` flip in `OnGenerationComplete` used to happen BEFORE
`ReDrainOverhangsUntilStable`. A concurrent neighbour's gen-thread `MakePut` that
saw `DataReady=true` wrote DIRECTLY into the chunk (the old direct write took no
lock), and if it landed in a cell the drain's final pass had just read as solid
(and skipped), the voxel was silently lost — the half tree. Fix (both parts):
1. `MakePut`'s direct write now goes through
   `TerrainGenerator.ApplyOverhangDirect`, which takes the per-chunk
   `OverhangDrainLock` (now public) around the air-only Set. The write is
   idempotent (air-only), so the lock is pure ORDERING: a direct write is either
   fully before the finalize window (then it queued, since DataReady was still
   false) or fully after it.
2. `OnGenerationComplete` now holds that lock for the WHOLE finalize window:
   re-drain-until-stable → `DataReady = true` flip → hive drain → a FINAL
   re-drain pass (catches a MakePut that read DataReady=false just before the
   flip but queued after the first drain's last read — the queue branch takes no
   lock, so the sliver is real). `ApplyLoadedChunk` runs BEFORE the window (it
   goes through SetVoxel and can re-stamp NeedsMesh; keep the lock short).
   The drain loop was split into `ReDrainOverhangsUntilStable` (public, takes
   the lock) + `ReDrainOverhangsUntilStableUnlocked` (caller holds it).
Residual (acceptable, not the reported bug): `Rebuild()` snapshots neighbours at
build START — an overhang written into a NEIGHBOUR after that snapshot but before
the build reads it leaves the seam mesh stale until the next rebuild (the data is
correct; `MarkNeighboursForRebuild` at finalize usually re-stamps the neighbour
before its build launches, and any missed re-stamp is self-healing on the next
rebuild). Also still worth checking at runtime: whether
`[VoxelWorld] Terrain generation failed for chunk ...` errors actually fire (the
fault-leak fix above only matters when faults occur — OOM on 256³ chunks).

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
