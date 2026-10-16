# AGENTS.md — Factory Sim

Godot 4.8 C# voxel sandbox: procedurally generated chunked voxel world with an FPS
player (`Scripts/Player.cs`) and a voxel edit tool (`Scripts/Voxel/VoxelTool.cs`).

## Agent Workflow

This repository uses a two-agent workflow:

* **Planner agent:** Investigates the repository, understands the user's request, identifies the relevant files and implementation approach, and reviews completed work.
* **Coder agent:** Performs repository implementation work. This includes creating, modifying, or deleting files, writing code, fixing bugs, refactoring, and running verification commands.
* When a user request requires repository changes, the planner should delegate the implementation to the coder rather than implementing the changes itself.
* The coder must read and follow this `AGENTS.md` before modifying the repository.
* The planner may inspect the repository to determine what needs to be changed, but should not duplicate implementation work that has been delegated to the coder.
* After implementation, the planner should review the coder's result and request follow-up changes if necessary.

### Verification Rules

* `dotnet build` is the command-line verification available for this project.
* There is no Godot executable available on this machine. Never claim that the game, scenes, rendering, physics, or runtime behavior have been verified from the command line.
* The root-level `TestMcRewrite.tscn` test must be run from the Godot editor when that specific test needs verification.
* When reporting verification results, distinguish between compilation (`dotnet build`) and actual Godot runtime/editor testing.


## Verification — what you can actually run

- **`dotnet build`** is the only executable check (~1s, expected 0 warnings / 0 errors).
  Output: `.godot/mono/temp/bin/Debug/Factory Sim.dll`. Restore is cached.
- **There is no Godot binary on this machine** (PATH, /opt, /usr all checked). You
  cannot run the game or any scene. Never claim runtime behavior is verified.
- No CI, no test runner, no lint config (`.editorconfig` sets UTF-8 only). The only
  test suite is a Godot node script: run root-level `TestMcRewrite.tscn` in the Godot
  editor; it prints `MC_REWRITE_TEST: ALL PASS` or `MC_REWRITE_TEST: N FAILURE(S)`
  and quits. (`McRewriteTest.cs` also at root; references `VoxelMaterialPalette`
  from `Scripts/Voxel/VoxelMaterial.cs`.)

## Gotcha: scene overrides export defaults

`Scenes/Main.tscn` is the runtime truth for `VoxelWorld` params, and it overrides the
code defaults:

| Export | Code default | `Main.tscn` value |
|---|---|---|
| `ChunkHeight` | 48 | **256** |
| `MeshMode` | Faces (0) | **1 = MarchingCubes** |
| `RenderDistanceChunks` | 4 | **12** |
| `BaseHeight` | — | 128.0 |
| `HeightFrequency` | — | 0.112 |
| `SpawnOffset` | — | 5.0 |
| `EnableCaves` / `EnableFoliage` / `EnableRocks` | — | false |

`ChunksPerFrame = null` in the scene → export default (3) is live. General rule: a
`null` scene value means the export default applies; a non-null value means the
export is dead code. Don't "fix" export defaults without checking the scene.

## Layout

- Root: `project.godot` (main scene `uid://p5wjbqmowa1v` = `Scenes/Main.tscn`; Jolt
  physics; 1920x1080), `Factory Sim.sln` / `Factory Sim.csproj`
  (`Godot.NET.Sdk/4.8.0-dev.6`, net8.0 — net9.0 for Android, `RootNamespace=FactorySim`,
  `EnableDynamicLoading`).
- `Scripts/Player.cs` — namespace `FactorySim`. FPS controller using the input map
  (WASD/Space/ShiftLeft); Escape toggles mouse capture.
- `Scripts/Voxel/` — namespace `FactorySim.Voxel`, 14 files:
  - `VoxelWorld.cs` — chunk manager (`Node3D`, group `"voxel_world"`); exports in
    `ExportGroup`s Chunks / World / Debug.
  - `VoxelChunk.cs` — `ChunkData` + visual `MeshInstance3D` + concave `CollisionShape3D`;
    `NeedsMesh` flag; `Rebuild(world)` builds visual + collision and clears the flag.
  - `ChunkData.cs` — flat `byte[]` voxel storage; the mutation target for all edits.
  - `MeshBuilder.cs` — hidden-face visual mesh + collision mesh.
  - `MarchingCubes.cs` — per-material isosurface.
  - `TerrainGenerator.cs`, `GenConfig.cs`, `RockStructureGenerator.cs`,
    `IStructureGenerator.cs` — generation (deterministic via `VoxelHash.cs`).
  - `VoxelMaterial.cs` / `VoxelMaterialConfig.cs` / `VoxelMaterialEntry.cs` —
    material system + `VoxelMaterialPalette`.
  - `VoxelTool.cs` — the edit tool.

## Key mechanics (voxel work)

- **Chunk lifecycle:** `VoxelWorld._Process` keeps chunks within
  `RenderDistanceChunks` of the player and (re)builds dirty chunks budgeted at
  `ChunksPerFrame` per frame. Spawn is gated on chunk (0,0) having `NeedsMesh ==
  false` — changes to the rebuild path must not break that gate.
- **Instant edits (by design):** `VoxelTool` — key `1` = add, key `2` = remove, LMB
  while the mouse is captured — calls `world.AddSphere` / `RemoveSphere` → private
  `FillSphere`: per-voxel `SetVoxel` into `ChunkData`, then **immediate**
  `chunk.Rebuild(this)` for every touched chunk **plus a 1-chunk ring** (border
  faces are culled against neighbor state, so bordering chunks must re-cull).
  Unloaded chunks are skipped and simply build fresh later. Do **not** route edits
  through the `ChunksPerFrame` budget or re-run `TerrainGenerator`.
- **Unloaded neighbors count as SOLID** in `VoxelSolidForMesh` /
  `VoxelSolidForCollision` — chunk-border faces depend on neighbor load state.
- **Two mesh modes** (`VoxelMeshMode`): `Faces` (hidden-face; matches collision) and
  `MarchingCubes` (inset isosurface; open seams at borders). `Rebuild` always rebuilds
  visual **and** collision — in MC mode collision triangles come from the MC surface
  itself. Toggling mode sets `NeedsMesh` on all loaded chunks.
- **F key** = full regeneration: `RegenerateKey` (default `Key.F`) +
  `EnableRegenerateShortcut` (both `[Export]` on `VoxelWorld`, Debug group) re-runs
  generation for loaded chunks. Debug shortcut — distinct from the instant edit path.

## Git

Branch `master` has **zero commits**; every file is currently untracked (whatever you
stage becomes the first commit). `.gitignore` covers `.godot/`, `android/`, `.aider*`;
`.gitattributes` = `text=auto`, `eol=lf`.
