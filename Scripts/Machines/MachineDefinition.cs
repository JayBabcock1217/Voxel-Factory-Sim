using Godot;
using System;

namespace FactorySim.Machines;

/// <summary>
/// The per-machine-type configuration: the data that makes one
/// <see cref="MachineType"/> a concrete, placeable machine. This is the primary
/// EXTENSION POINT for adding machines — to add a new machine the user:
///
/// 1. Adds a <see cref="MachineType"/> enum value (if it's a brand-new kind),
/// 2. Drops their Blender export (.glb/.gltf) into the project so Godot imports
///    it as a mesh,
/// 3. Creates a <see cref="MachineDefinition"/> resource (right-click → New
///    Resource) and fills in the fields below: the type, a display name, the
///    imported <see cref="Mesh"/> (or <see cref="MeshScene"/>), and any
///    behavior parameters.
///
/// 4. Registers it in a <see cref="MachineDefConfig"/>.
///
/// No code changes are needed to add a machine that uses the base
/// <see cref="Machine"/> behavior — the definition alone is enough. A machine
/// with unique logic additionally provides a <see cref="Machine"/> subclass
/// (selected via <see cref="SceneOverride"/> or the type registry).
///
/// This is a Godot <see cref="Resource"/> (not a POCO) specifically so the user
/// can edit every field — including assigning the imported mesh — in the
/// inspector and save it as a .tres, exactly like <see cref="VoxelMaterialDef"/>.
/// </summary>
[GlobalClass]
public partial class MachineDefinition : Resource
{
	/// <summary>Fired whenever a field of this definition changes (live inspector edits).</summary>
	public event Action<MachineDefinition> PropertyChanged;

	[ExportGroup("Identity")]
	/// <summary>The machine kind this definition configures. Its (int) value is also the machine's item id.</summary>
	[Export] public MachineType Type { get; set; }

	/// <summary>Display name shown in the dev panel, tooltips, and the machine's HUD.</summary>
	[Export] public string Name { get; set; } = "Machine";

	/// <summary>Optional description (shown in tooltips).</summary>
	[Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";

	[ExportGroup("Appearance")]
	/// <summary>
	/// The machine's visual mesh, imported from Blender. Assign the imported
	/// .glb/.gltf mesh resource here (drag it from the FileSystem dock, or use
	/// the picker). This is the primary way to give a machine its look.
	///
	/// When set, a <see cref="Machine"/> node builds a MeshInstance3D with this
	/// mesh. When left null but <see cref="MeshScene"/> is set, the scene is
	/// instantiated instead. When both are null, the machine renders a small
	/// default placeholder cube (so a machine is still visible before the user
	/// assigns a mesh).
	/// </summary>
	[Export] public Mesh Mesh { get; set; }

	/// <summary>
	/// Alternative to <see cref="Mesh"/>: a full PackedScene to instantiate as
	/// the machine's visual (use this when the Blender export is a scene with
	/// multiple meshes, materials, or nodes rather than a single mesh). When
	/// both <see cref="Mesh"/> and <see cref="MeshScene"/> are set, the mesh
	/// takes precedence.
	/// </summary>
	[Export] public PackedScene MeshScene { get; set; }

	/// <summary>
	/// Uniform scale applied to the machine's visual (1.0 = the imported mesh's
	/// native size). Blender exports are often a different scale than the voxel
	/// grid, so this lets the user fit the mesh to the world without re-exporting.
	/// </summary>
	[Export(PropertyHint.Range, "0.01,16,0.01")] public Vector3 Scale { get; set; } = Vector3.One;

	/// <summary>
	/// An optional collision shape for the machine's solid body. Assign a
	/// BoxShape3D / SphereShape3D / ConvexPolygonShape3D here. When set, a
	/// <see cref="Machine"/> adds a matching CollisionShape3D so the machine
	/// blocks the player / can be targeted. When null, the machine is
	/// non-colliding (visual only).
	/// </summary>
	[Export] public Shape3D CollisionShape { get; set; }

	[ExportGroup("Item")]
	/// <summary>
	/// The inventory item id used to place this machine. Defaults to
	/// <c>(int)Type</c> (the machine id range). Override only when a single
	/// MachineType maps to multiple placeable items.
	/// </summary>
	[Export] public int ItemId { get; set; } = 0;

	/// <summary>The effective item id (<see cref="ItemId"/> if set, else <c>(int)Type</c>).</summary>
	public int EffectiveItemId => ItemId != 0 ? ItemId : (int)Type;

	[ExportGroup("Behavior (generic params)")]
	// These are GENERIC, type-agnostic parameters so the machine system has a
	// uniform tuning surface today and a home for stats as specific machine
	// behaviors are implemented. A concrete Machine subclass reads the ones it
	// cares about. Adding a new stat = adding a field here.
	/// <summary>Power draw in energy units per second (0 = passive machine).</summary>
	[Export(PropertyHint.Range, "0,1024,0.1")] public float PowerConsumption { get; set; }

	/// <summary>Seconds per processing cycle (0 = not a processor).</summary>
	[Export(PropertyHint.Range, "0,3600,0.05")] public float ProcessTime { get; set; }

	/// <summary>How many output units one cycle produces (0 = no output).</summary>
	[Export(PropertyHint.Range, "0,64,1")] public int OutputCount { get; set; }

	/// <summary>How many items the machine can hold internally (0 = no buffer).</summary>
	[Export(PropertyHint.Range, "0,256,1")] public int BufferSize { get; set; }

	/// <summary>The machine's own health (like a voxel's). 0 = unbreakable.</summary>
	[Export(PropertyHint.Range, "0,100000,1")] public int Health { get; set; } = 100;

	/// <summary>
	/// Power this machine PRODUCES per second (0 = not a power source). A
	/// machine with a non-zero value is a power source: it feeds the range-based
	/// power network to all consumer machines within <see cref="PowerNetwork.Radius"/>.
	/// See <see cref="MachineDefinition.PowerConsumption"/> for the consumer side.
	/// </summary>
	[Export(PropertyHint.Range, "0,1024,0.1")] public float PowerOutput { get; set; }

	/// <summary>
	/// An optional path to a concrete <see cref="Machine"/> subclass scene for
	/// this machine type. Leave empty to use the base <see cref="Machine"/>
	/// behavior. When set, the <see cref="MachineRegistry"/> instantiates this
	/// scene instead of the default, so a machine with custom logic (e.g. a
	/// Turret that shoots) can plug in without changing the spawner.
	/// </summary>
	[Export(PropertyHint.File, "*.tscn")] public string MachineSceneOverride { get; set; } = "";

	// -- Inspector wiring (mirrors VoxelMaterialDef's event pattern) --------
	//
	// Note: the Set* helpers are named to avoid colliding with Resource's own
	// SetName (Godot's Resource base already has a SetName for the node name),
	// so "Name" is exposed via SetMachineName.

	private void Raise() => PropertyChanged?.Invoke(this);

	public void SetType(MachineType value) { if (Type != value) { Type = value; Raise(); } }
	public void SetMachineName(string value) { if (Name != value) { Name = value ?? ""; Raise(); } }
	public void SetDescription(string value) { if (Description != value) { Description = value ?? ""; Raise(); } }
	public void SetMesh(Mesh value) { if (!ReferenceEquals(Mesh, value)) { Mesh = value; Raise(); } }
	public void SetMeshScene(PackedScene value) { if (!ReferenceEquals(MeshScene, value)) { MeshScene = value; Raise(); } }
	public void SetScale(Vector3 value) { if (Scale != value) { Scale = value; Raise(); } }
	public void SetCollisionShape(Shape3D value) { if (!ReferenceEquals(CollisionShape, value)) { CollisionShape = value; Raise(); } }
	public void SetItemId(int value) { if (ItemId != value) { ItemId = value; Raise(); } }
	public void SetPowerConsumption(float value) { if (PowerConsumption != value) { PowerConsumption = value; Raise(); } }
	public void SetProcessTime(float value) { if (ProcessTime != value) { ProcessTime = value; Raise(); } }
	public void SetOutputCount(int value) { if (OutputCount != value) { OutputCount = value; Raise(); } }
	public void SetBufferSize(int value) { if (BufferSize != value) { BufferSize = value; Raise(); } }
	public void SetHealth(int value) { if (Health != value) { Health = value; Raise(); } }
	public void SetPowerOutput(float value) { if (PowerOutput != value) { PowerOutput = value; Raise(); } }
	public void SetMachineSceneOverride(string value) { if (MachineSceneOverride != value) { MachineSceneOverride = value ?? ""; Raise(); } }
}
