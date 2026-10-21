# AGENTS.md — Factory Sim

Godot 4.8 C# voxel sandbox: procedurally generated chunked voxel world with an FPS
player (`Scripts/Player.cs`), a voxel edit tool (`Scripts/Voxel/VoxelTool.cs`), and a
binary save/load system with a world-slot picker (`Scripts/UI/SlotPicker.cs`).

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

- **`dotnet build`** is the only local executable check (~1s, expected 0 warnings /
  0 errors). Output: `.godot/mono/temp/bin/Debug/Factory Sim.dll`. Restore is cached.
- **There is no Godot binary on this machine** (PATH, /opt, /usr all checked). You
  cannot run the game or any scene. Never claim runtime behavior is verified.
- No local test runner or lint config (`.editorconfig` sets UTF-8 only). The test
  suites are Godot node scripts run from the editor:
  - `TestMcRewrite.tscn` (root) → prints `MC_REWRITE_TEST: ALL PASS` or
    `MC_REWRITE_TEST: N FAILURE(S)` and quits. (`McRewriteTest.cs` also at root;
    references `VoxelMaterialPalette` from `Scripts/Voxel/VoxelMaterial.cs`.)
  - `WorldSaveTest.tscn` (root) → prints `WORLD_SAVE_TEST: ALL PASS` or
    `WORLD_SAVE_TEST: N FAILURE(S)` and quits. (`WorldSaveTest.cs` at root; tests the
    binary save format round-trip, checksum, versioning, and the world
    edit-capture/re-apply path.)
- **CI** (`.github/workflows/ci.yml`) runs `dotnet build` on every push/PR and, when
  the `GODOT_URL` repo variable is set, also runs both test scenes headless. Without
  `GODOT_URL` the editor-test job is skipped and only the build gate is enforced.

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
- `Scripts/UI/` — namespace `FactorySim.UI`:
  - `MainMenu.cs` — the home screen; wires the save/load picker to the world.
  - `SlotPicker.cs` — the world-slot picker (New World / Load / Rename / Delete).
    All prompt confirm/cancel paths are **deferred** (`CallDeferred` →
    `DoInvokeOk`) so no node is freed while a `Pressed`/`TextSubmitted` signal is
    still on the stack. There is a single `ShowPrompt` overload (2-arg `onOk`);
    do not reintroduce a 1-arg overload (it caused an infinite-recursion stack
    overflow).
  - `PauseMenu.cs` — in-game pause (resume / quit to menu).
- `Scripts/Voxel/` — namespace `FactorySim.Voxel`:
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
  - **Save/load:**
    - `WorldSave.cs` — slot manager: per-slot binary files (`slot_N.world`) plus an
      `index.json` registry; auto-save on `_ExitTree`; `ListSlots` / `LoadFrom` /
      `CreateSlot` / `RenameSlot` / `DeleteSlot`; repairs corrupted slot files on
      read.
    - `WorldSaveFile.cs` — the binary save format: `FSAV` magic + version + sections
      (edits / health / player / inventory / drops) + trailing FNV-1a checksum.
      Versioning: `CurrentVersion`, `SupportedVersions` (the set of loadable
      versions), `IsSupportedVersion`, and `Migrate(data, fromVersion)` (a pure
      transform for older versions; identity for the current one). `Read` accepts any
      supported version, validates the checksum, then calls `Migrate`. Corrupted-magic
      / truncated-v1 bodies are recovered by `TryReadCorruptedV1` (returned with
      `Checksum = 0` so the caller re-writes a proper file).
    - `SaveSlotIndex.cs` — hand-rolled JSON reader/writer for `index.json` (the slot
      registry). Corrupt/bad-magic warnings are emitted at most once per path per
      episode (`_warnedCorruptPaths`, cleared on a successful read).
- Root test scenes (editor-run, not CLI): `TestMcRewrite.tscn` / `McRewriteTest.cs`
  and `WorldSaveTest.tscn` / `WorldSaveTest.cs`.

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

- Remote: `origin` = `https://github.com/JayBabcock1217/Voxel-Factory-Sim`; the
  working branch is `master` (tracks `origin/master`). Note the repo's GitHub *default
  branch* may still be `main` — flip it in Settings → Branches if you want `master` to
  be the default, or rename locally + remote.
- `.gitignore` covers `.godot/`, `android/`, `.aider*`; `.gitattributes` =
  `text=auto`, `eol=lf`.
- CI (`.github/workflows/ci.yml`) enforces a clean `dotnet build` (warnings-as-errors)
  on every push/PR. Keep the build at 0 warnings / 0 errors or CI fails.
