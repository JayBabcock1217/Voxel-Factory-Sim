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
- **Item-driven tools** — hold a tool (shovel / pickaxe / axe / sword) from the
  hotbar to break blocks; each tool has a different affinity for different
  materials (a shovel is fast on sand but slow on stone, a pickaxe the reverse).
  Hold a placeable material to place it into the world.
- **Crafting + dev items** — a crafting pane (recipes) and a dev pane (god-mode
  list of every material + tool + machine) in the inventory screen.
- **Machines (factory design system)** — a data-driven, extensible system for
  placing machines (smelters, crafters, turrets, defense walls, conveyors, …)
  that render **custom meshes imported from Blender**. Each machine is a
  `MachineDefinition` resource (mesh + stats + item id); placing a machine item
  spawns a `Machine` node with that mesh. See [Machines](#machines-factory-design-system).
- **Save / load** — multiple named world slots, each a small binary file
  (only your edits + player state + inventory — including held tools — are
  persisted; terrain is regenerated from the seed). Autosaves on quit.

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
| 1–9, 0 / wheel | Select hotbar slot (the held item) |
| R | Cycle the machine placement rotation (12 orientations: 4×90° about X/Y/Z) — shown in the preview |
| Left mouse (hold) | Mine the target — **when holding a tool** |
| Left mouse (click) | Place the held item — a block voxel, or a **machine** (spawns its mesh) |
| Right mouse (click) | Open the **machine UI** of the targeted machine (recipe, buffers, progress) |
| Tab | Open / close the inventory (crafting + dev panes) — also closes the machine UI |
| F | Regenerate loaded chunks (debug) |
| Escape | Toggle mouse capture / close menus |

The **held item** is whatever is in the selected hotbar slot. Tools (shovel,
pickaxe, axe, sword) break blocks at an affinity-scaled rate (see
`ToolAffinity`); placeable materials place a voxel on click. The dev pane in the
inventory screen hands you any material or tool for testing.

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
  Items/                   Item system (FactorySim.Items)
    Inventory.cs           Item inventory (28 slots, save/load)
    ItemStack.cs           A stack of items (material + optional tool type)
    ItemRegistry.cs        Source of truth: all materials + tools
    ToolType.cs            Shovel / Pickaxe / Axe / Sword
    ToolAffinity.cs        Central tool-vs-material break-rate table
    CraftingRecipe.cs      A recipe (ingredients -> product)
    CraftingDatabase.cs    Recipe registry + "how many can I make"
  Machines/                Machine / factory design system (FactorySim.Machines)
    MachineType.cs         Enum of machine kinds (Smelter, Crafter, Turret, ...)
    MachineDefinition.cs   Per-machine data resource: mesh, stats, item id
    MachineDefConfig.cs    Registry of machine definitions (a .tres you edit)
    Machine.cs             Base StaticBody3D for a placed machine (mesh + hooks + buffers)
    MachineRegistry.cs     item id -> definition -> Machine node factory
    MachineSpawner.cs      Spawns Machine nodes (occupancy check) into "Machines"
    MachineRotation.cs     12 placement orientations (4x90 about X/Y/Z) + Basis helper
    MachineBuffer.cs       A machine's item buffer (input/output slots)
    MachineBehaviorRegistry.cs  Maps MachineType -> behavior subclass
    ConveyorMachine.cs     Conveyor behavior: 1-slot pull/push, any side
    CrafterMachine.cs      Crafter behavior: gather -> craft -> output (powered)
    PowerSourceMachine.cs  Power source behavior (passive producer)
    PowerNetwork.cs        Range-based power (production - consumption in radius)
  UI/InventoryUI.cs        Inventory screen (slots, crafting pane, dev pane)
  UI/MachineUI.cs          Machine interaction UI (right-click: recipe, buffers, progress)
  Voxel/                   World + save system (FactorySim.Voxel)
    VoxelWorld.cs          Chunk manager / generation orchestration
    VoxelChunk.cs          Per-chunk data + mesh + collision
    ChunkData.cs           Flat byte[] voxel storage
    MeshBuilder.cs         Hidden-face mesh + collision mesh
    MarchingCubes.cs       Per-material isosurface
    TerrainGenerator.cs    Deterministic terrain (seed-driven)
    GenConfig.cs           Generation parameters
    VoxelMaterial*.cs      Material system + palette
    VoxelTool.cs           Item-driven tool (mine with tools, place blocks)
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

## Machines (factory design system)

A data-driven, extensible system for placing **machines** in the world — the
base of the factory design system. Machines are first-class inventory items
(item ids 200–299, the `MachineType` enum values) that, when placed, spawn a
`Machine` node rendering a **custom mesh imported from Blender**.

The starter machine kinds are `Smelter`, `Crafter`, `Turret`, `DefenseWall`,
`Conveyor`, and `PowerSource` (`MachineType`). They are all placeable out of the
box (rendering a small placeholder cube); to give a machine its real look and
stats you register a `MachineDefinition` for it. The `Conveyor`, `Crafter`, and
`PowerSource` have built-in behaviors (item transport, automated crafting, and
power generation respectively — see [Item flow](#item-flow-conveyors--processors),
[Machine UI](#machine-ui-right-click-a-machine), and
[Power](#power-range-based)). The `Smelter`, `Turret`, and `DefenseWall` are
currently placeable placeholders (static meshes) whose behaviors come next.

### How it fits together

| Piece | Role |
|---|---|
| `MachineType` (enum) | The machine kind. Its `(int)` value is the machine's item id. **Add a new machine type by appending a value here.** |
| `MachineDefinition` (Resource) | Per-machine data: the `Type`, a display `Name`, the imported **`Mesh`** (or a `PackedScene`), a `Scale`, an optional `CollisionShape`, and generic behavior params (power, process time, output, buffer, health). Also an optional `MachineSceneOverride` to select a `Machine` subclass. |
| `MachineDefConfig` (Resource) | The registry: an array of `MachineDefinition`s. Assign this to the `VoxelWorld`'s `MachineConfig` export. |
| `Machine` (Node3D) | The placed node: renders the definition's mesh (or a placeholder cube), adds the collision shape, and exposes virtual hooks (`StartMachine` / `ProcessMachine` / `ShutdownMachine` / `OnBroken`) that subclasses override for machine-specific logic. |
| `MachineRegistry` (static) | Maps an item id → `MachineDefinition` → the right `Machine` node (loading the definition's `MachineSceneOverride` when set). |
| `MachineSpawner` (static) | Parents a `Machine` into the shared "Machines" scene container, positions it at its voxel, and starts it. |

### Importing a Blender mesh

1. Export the machine from Blender as **`.glb` or `.gltf`** (File → Export →
   glTF 2.0). Keep it roughly centered on its origin and at a sane scale
   (adjust later with the definition's `Scale` if needed).
2. Drop the `.glb`/`.gltf` file into the project (e.g. a `Models/` folder).
   Godot imports it automatically and it appears in the FileSystem dock as a
   `PackedScene` (you can get its `Mesh` from the imported scene).
3. Create a `MachineDefinition` resource (FileSystem dock → right-click →
   **New Resource** → `MachineDefinition`) and fill in:
   - `Type` — the machine kind,
   - `Name` / `Description`,
   - `Mesh` — assign the imported mesh (drag it from the FileSystem dock, or
     use the resource picker), or set `MeshScene` to the whole imported scene,
   - `Scale` — fit the mesh to the voxel grid,
   - `CollisionShape` (optional) — a `BoxShape3D` / `SphereShape3D` so the
     machine blocks the player,
   - behavior params as needed.
4. Create (or edit) a `MachineDefConfig` resource, add your
   `MachineDefinition`s to its `Machines` array.
5. In `Scenes/Main.tscn`, select the `VoxelWorld` node and assign your
   `MachineDefConfig` to its **Machines → MachineConfig** export.

Now the machine items appear in the dev pane (with your custom names), and
placing one spawns a `Machine` node with your Blender mesh.

### Placing machines: preview, rotation, occupancy

While you hold a machine item (and are looking at a voxel face), a **transparent
preview ghost** of that machine appears at the target cell, showing exactly where
and how it will be placed. The ghost uses the machine's real mesh (rendered
translucent) and reflects your current rotation live.

- **R key** cycles the placement rotation through the 12 supported orientations
  (four 90° rotations about each of the X, Y, and Z axes, wrapping). The rotation
  is **persistent** — it is your current placement orientation and is applied to
  the next machine you place, regardless of which machine type you're holding.
  The preview updates instantly when you press R.
- **Occupancy**: a cell can hold at most one machine, and you cannot place a
  machine into a cell that already has a solid voxel. When the target cell is
  occupied (or out of bounds), the preview hides and the placement is rejected
  (no item is consumed). This prevents stacking machines in one cell or burying
  a machine inside terrain.

### Item flow (conveyors + processors)

Machines move items between each other. There are no inserters — **conveyors**
are the only transport. A placed `Conveyor` has a single item slot and, on a
throttled tick (~4 ops/sec, tunable via the conveyor behavior's
`TransferInterval` export), it:

1. **PULLs** — if its slot is empty, it takes one item from an adjacent
   machine's **output** buffer.
2. **PUSH**es — if its slot has an item, it moves it into an adjacent machine's
   **input** buffer.

"Adjacent" is any of the six face-neighbors (±X, ±Y, ±Z), so items enter and
leave a machine from **any side**. A conveyor prefers to push into a **processor**
(a machine with a real input buffer, e.g. a crafter) over another conveyor, so
items flow toward machines; it falls back to a free neighbor conveyor slot to
chain along a belt.

A **Crafter** is the first real processor: it gathers recipe ingredients from
adjacent conveyors into its input buffer, runs the recipe it is set to (over the
recipe's craft time), and puts the product in its output buffer — which adjacent
conveyors then pull. A crafter **consumes power** (its definition's
`PowerConsumption`); with no power it idles but **keeps its buffers** (no items
lost).

### Machine UI (right-click a machine)

**Right-click** a targeted machine to open its UI (the top-right target HUD also
shows the machine's name + a short status). The panel shows:

- The machine's name and a status line (power state / output / craft progress).
- For a **crafter**: a recipe picker (a dropdown of every recipe in the shared
  crafting database) and a live craft-progress bar. Picking a recipe sets what
  the crafter produces; "(No recipe — idle)" stops it.
- The machine's **input** and **output** buffer slots. **Left-click** a slot to
  move one item between it and your selected hotbar slot; **right-click** to move
  a whole stack (direction is decided by which side has the item).

Close with **Tab**, **Escape**, or the close button. Right-clicking the
already-open machine toggles it closed. The panel hides itself automatically if
the machine is broken while it is open.

### Power (range-based)

Power is **not** carried on wires or conveyors — it is **range-based**. A
`PowerSource` machine produces a fixed rate of power (its definition's
`PowerOutput`) and feeds **every consumer machine within its radius** (a simple
Euclidean distance in world-voxel units, any direction — no cables to lay). The
radius is the `VoxelWorld`'s **`PowerRadius`** export (default 8).

For a given consumer, available power = (sum of every power source in its
radius) − (sum of every *other* consumer in that radius). A machine is "powered"
when its own `PowerConsumption` is met by that amount. This means one source
powers a cluster of machines until its output is exhausted — add too many
machines and the area browns out. Passive machines (conveyors, power sources)
consume no power and always run.

### Adding a new machine type

1. Append a value to the `MachineType` enum (e.g. `Furnace = 206`).
2. (Optional) Create a `Machine` subclass with its behavior, override the
   virtual hooks, and register it in `MachineBehaviorRegistry` (one line mapping
   the type → your subclass). Static machines (no per-tick logic) need no
   subclass — the base `Machine` is the fallback.
3. Create a `MachineDefinition` for it (pointing at its mesh; set `PowerOutput`
   / `PowerConsumption` / `BufferSize` / `ProcessTime` as needed).
4. Add the definition to your `MachineDefConfig`.

No spawner / tool / save code changes are needed — the system picks the new
type up from the enum + registry.

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
