using Godot;
using System;

namespace FactorySim.Deco;

/// <summary>
/// The per-deco-type configuration — the editor-facing data that makes one
/// <see cref="DecoType"/> a concrete, placeable / spawnable DecoVoxel. This is
/// the primary EXTENSION POINT for adding decos, mirroring
/// <see cref="FactorySim.Machines.MachineDefinition"/>:
///
/// 1. (Optionally) add a <see cref="DecoType"/> enum value for a brand-new kind.
/// 2. Create a <see cref="DecoDefinition"/> resource (right-click → New
///    Resource) and fill in the fields below: the type, a display name, and the
///    APPEARANCE group — the material, mesh, and/or scene for this deco.
/// 3. Register it in a <see cref="DecoDefConfig"/> and assign that config to the
///    <see cref="VoxelWorld.DecoConfig"/> export.
///
/// APPEARANCE RESOLUTION (highest to lowest precedence):
///   1. <see cref="CustomMaterial"/> — a full material (ShaderMaterial or a
///      StandardMaterial3D saved as a .tres) used INSTEAD of the built-in look.
///   2. <see cref="AlbedoTexture"/> + <see cref="AlbedoColor"/> — a texture
///      (and optional tint) applied to the type's built-in mesh.
///   3. The type's built-in default (e.g. grass's green crossed planes) when
///      neither is set.
///
/// The MESH can be overridden too: <see cref="Mesh"/> (a single imported mesh)
/// or <see cref="MeshScene"/> (a full PackedScene, like the machines) replaces
/// the type's built-in mesh. <see cref="Scale"/> fits an imported mesh to the
/// voxel grid.
///
/// This is a Godot <see cref="Resource"/> (not a POCO) specifically so the user
/// can edit every field — including assigning the imported mesh / material — in
/// the inspector and save it as a .tres, exactly like
/// <see cref="FactorySim.Machines.MachineDefinition"/>.
/// </summary>
[Tool]
[GlobalClass]
public partial class DecoDefinition : Resource
{
	/// <summary>Fired whenever a field of this definition changes (live inspector edits).</summary>
	public event Action<DecoDefinition> PropertyChanged;

	[ExportGroup("Identity")]
	/// <summary>The deco kind this definition configures (grass, wall, ...).</summary>
	[Export] public DecoType Type { get; set; }

	/// <summary>Display name shown in the dev panel and tooltips.</summary>
	[Export] public string Name { get; set; } = "Deco";

	/// <summary>Optional description (shown in tooltips).</summary>
	[Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";

	/// <summary>
	/// The inventory item id used to place this deco (in the deco id range,
	/// <see cref="FactorySim.Voxel.VoxelMaterialIds.DecoFirst"/>..
	/// <see cref="DecoLast"/>). Defaults to <c>DecoFirst + (int)Type</c>.
	/// </summary>
	[Export] public int ItemId { get; set; } = 0;

	/// <summary>The effective item id (<see cref="ItemId"/> if set, else the type default).</summary>
	public int EffectiveItemId =>
		ItemId != 0 ? ItemId : FactorySim.Voxel.VoxelMaterialIds.DecoFirst + (int)Type;

	[ExportGroup("Appearance")]
	/// <summary>
	/// A full custom material for this deco — a <see cref="ShaderMaterial"/> or a
	/// <see cref="StandardMaterial3D"/> saved as a .tres. When set, it is used
	/// INSTEAD of the built-in look (and instead of <see cref="AlbedoTexture"/>
	/// / <see cref="AlbedoColor"/>). This is the primary way to give a deco its
	/// own material in the editor.
	/// </summary>
	[Export] public Material CustomMaterial { get; set; }

	/// <summary>
	/// The albedo texture applied to the deco's built-in mesh (drag a texture
	/// from the FileSystem dock). Ignored when <see cref="CustomMaterial"/> is
	/// set. When set without a custom material, the deco builds a
	/// <see cref="StandardMaterial3D"/> with this texture + <see
	/// cref="AlbedoColor"/> tint.
	/// </summary>
	[Export] public Texture2D AlbedoTexture { get; set; }

	/// <summary>
	/// The albedo tint applied on top of <see cref="AlbedoTexture"/> (white =
	/// show the texture at full brightness; a color tints it). Ignored when
	/// <see cref="CustomMaterial"/> is set.
	/// </summary>
	[Export] public Color AlbedoColor { get; set; } = new(1f, 1f, 1f, 1f);

	/// <summary>
	/// A single imported mesh (e.g. from a Blender .glb) that replaces the
	/// deco's built-in mesh. When null, the type's built-in mesh is used.
	/// <see cref="Scale"/> is applied to fit it to the voxel grid.
	/// </summary>
	[Export] public Mesh Mesh { get; set; }

	/// <summary>
	/// Alternative to <see cref="Mesh"/>: a full PackedScene to instantiate as
	/// the deco's visual (use it when the import is a scene with multiple
	/// meshes/materials/nodes rather than a single mesh). When both <see
	/// cref="Mesh"/> and <see cref="MeshScene"/> are set, the mesh wins. When
	/// both are null, the type's built-in mesh is used.
	/// </summary>
	[Export] public PackedScene MeshScene { get; set; }

	/// <summary>
	/// Uniform scale applied to the deco's visual (1.0 = native size). Imported
	/// meshes are often a different scale than the voxel grid, so this fits the
	/// mesh to the world without re-exporting. Applied to <see cref="Mesh"/> /
	/// <see cref="MeshScene"/> visuals (the built-in mesh already spans the
	/// voxel, so it is scaled by the voxel size and this is left at 1.0).
	/// </summary>
	[Export(PropertyHint.Range, "0.01,16,0.01")] public Vector3 Scale { get; set; } = Vector3.One;

	[ExportGroup("Behavior (per-instance)")]
	/// <summary>
	/// The deco's health (like a voxel's / machine's). 0 = unbreakable. Used by
	/// the break pipeline when the player damages this deco.
	/// </summary>
	[Export(PropertyHint.Range, "0,100000,1")] public int Health { get; set; } = 100;

	// -- Inspector wiring (mirrors VoxelMaterialEntry / MachineDefinition) ----

	private void Raise() => PropertyChanged?.Invoke(this);

	public void SetType(DecoType value) { if (Type != value) { Type = value; Raise(); } }
	public void SetDecoName(string value) { if (Name != value) { Name = value ?? ""; Raise(); } }
	public void SetDescription(string value) { if (Description != value) { Description = value ?? ""; Raise(); } }
	public void SetItemId(int value) { if (ItemId != value) { ItemId = value; Raise(); } }
	public void SetCustomMaterial(Material value) { if (!ReferenceEquals(CustomMaterial, value)) { CustomMaterial = value; Raise(); } }
	public void SetAlbedoTexture(Texture2D value) { if (!ReferenceEquals(AlbedoTexture, value)) { AlbedoTexture = value; Raise(); } }
	public void SetAlbedoColor(Color value) { if (AlbedoColor != value) { AlbedoColor = value; Raise(); } }
	public void SetMesh(Mesh value) { if (!ReferenceEquals(Mesh, value)) { Mesh = value; Raise(); } }
	public void SetMeshScene(PackedScene value) { if (!ReferenceEquals(MeshScene, value)) { MeshScene = value; Raise(); } }
	public void SetScale(Vector3 value) { if (Scale != value) { Scale = value; Raise(); } }
	public void SetHealth(int value) { if (Health != value) { Health = value; Raise(); } }
}
