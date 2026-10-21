# Factory Sim

A voxel sandbox built in **Godot 4.8 (C#)**: a procedurally generated, chunked
voxel world you can walk around and edit in first person, with a binary
save/load system and a world-slot picker.

> The "Factory" half is still coming — right now this is the sandbox +
> persistence foundation it will be built on.

## Features

- **Procedural chunked voxel world** — deterministic terrain generated from a
  seed (height noise, dirt/stone layers, optional caves / rocks / foliage /
  biomes), streamed in around the player.
- **Two mesh modes** — classic hidden-face voxel meshing or per-material
  marching-cubes isosurfaces.
- **First-person player** — WASD + mouse look, jump, sprint.
- **Voxel edit tool** — add / remove blocks by raycast (instant, no
  regeneration).
- **Save / load** — multiple named world slots, each a small binary file
  (only your edits + player state + inventory are persisted; terrain is
  regenerated from the seed). Autosaves on quit.

## Building

Requires the **.NET 8 SDK** (the project uses `Godot.NET.Sdk/4.8.0-dev.6`).
Godot 4.8 (mono) is needed to *run* the game; `dotnet build` alone only
compiles the C#.

```sh
dotnet build "Factory Sim.sln"
```

To play, open the project in the Godot 4.8 editor (mono build) and press
**Play** (main scene `Scenes/Main.tscn`).

> This machine has no Godot binary, so command-line verification is limited to
> `dotnet build`. Runtime behavior must be checked in the editor.

## Controls

| Input | Action |
|---|---|
| W / A / S / D | Move |
| Mouse | Look |
| Space | Jump |
| Shift (left) | Sprint |
| 1 | Add-block tool |
| 2 | Remove-block tool |
| Left mouse (while captured) | Edit the targeted voxel |
| F | Regenerate loaded chunks (debug) |
| Escape | Toggle mouse capture / close menus |

In the world-slot picker: **New World** (name + seed), **Load**, **Rename**,
**Delete** per slot; **Back** to return to the main menu.

## Project layout

```
project.godot              Godot project (main scene Scenes/Main.tscn, Jolt physics)
Factory Sim.sln / .csproj  .NET solution / project (net8.0, net9.0 for Android)
Scenes/                    Godot scenes (Main, MainMenu, Player, VoxelDrop, ...)
Scripts/
  Player.cs                FPS controller
  UI/                      MainMenu, SlotPicker (save/load UI), PauseMenu
  Items/Inventory.cs       Item inventory
  Voxel/                   World + save system (FactorySim.Voxel)
    VoxelWorld.cs          Chunk manager / generation orchestration
    VoxelChunk.cs          Per-chunk data + mesh + collision
    ChunkData.cs           Flat byte[] voxel storage
    MeshBuilder.cs         Hidden-face mesh + collision mesh
    MarchingCubes.cs       Per-material isosurface
    TerrainGenerator.cs    Deterministic terrain (seed-driven)
    GenConfig.cs           Generation parameters
    VoxelMaterial*.cs      Material system + palette
    VoxelTool.cs           Add/remove block tool
    WorldSave.cs           Slot manager (slot_N.world + index.json, autosave)
    WorldSaveFile.cs       Binary save format (magic + version + FNV-1a checksum)
    SaveSlotIndex.cs       index.json reader/writer
TestMcRewrite.tscn/.cs     Editor-run test: mesh rewrite
WorldSaveTest.tscn/.cs     Editor-run test: save format round-trip + versioning
```

## Save format

Each world slot is a small binary file (`slot_N.world`) with an `FSAV` magic,
a version, fixed sections (voxel edits, damaged-voxel health, player
transform, inventory, item drops), and a trailing FNV-1a checksum. Only the
player's *deltas* are stored — the terrain itself is regenerated from the
seed and the edits are re-applied on top. A slot registry (`index.json`) maps
slot ids to names/timestamps. The format is versioned (`WorldSaveFile.CurrentVersion`)
with a `Migrate` hook for forward compatibility, and corrupted files (bad
magic or missing checksum) are auto-repaired on read.

Run `WorldSaveTest.tscn` from the editor to verify the format end-to-end.

## Testing & CI

- `dotnet build` is the command-line check (expected: 0 warnings / 0 errors).
- The two test scenes (`TestMcRewrite.tscn`, `WorldSaveTest.tscn`) are Godot
  node scripts run from the **editor** (they need the scene tree); each prints
  `...: ALL PASS` or `...: N FAILURE(S)` and quits.
- **CI** (`.github/workflows/ci.yml`) runs `dotnet build` (warnings-as-errors)
  on every push/PR. When the `GODOT_URL` repository variable is set, it also
  downloads a headless Godot build and runs both test scenes automatically;
  otherwise that job is skipped and only the build gate is enforced.

## License

Private project.
