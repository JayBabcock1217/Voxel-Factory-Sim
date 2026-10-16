using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// One configurable ore: an editor-facing row in the world's ore list.
///
/// Like biomes, ores are data-driven: generation references the ore's material
/// BY ID, and every placement parameter lives here, so new ores can be added in
/// the editor (the "Add Ore" button on <see cref="OreConfig"/>) without touching
/// code. The world builds a <see cref="OreStructureGenerator"/> from this list.
/// </summary>
[GlobalClass]
public partial class OreDef : Resource
{
	/// <summary>Display name (e.g. "Coal", "Iron").</summary>
	[Export] public string Name { get; set; } = "Ore";

	/// <summary>Material id stamped into the terrain for this ore (0 = disabled).</summary>
	[Export] public int MaterialId { get; set; } = VoxelMaterialIds.CoalOre;

	// ------------------------------------------------------------------
	// Placement (deterministic per cell/vein — see OreStructureGenerator)
	// ------------------------------------------------------------------

	/// <summary>Masters this ore (false = this ore never generates).</summary>
	[Export] public bool Enabled { get; set; } = true;

	/// <summary>Chance per grid cell to place a vein of this ore.</summary>
	[Export(PropertyHint.Range, "0,1,0.001")] public float Probability { get; set; } = 0.05f;

	/// <summary>Grid cell size used for deterministic vein placement.</summary>
	[Export(PropertyHint.Range, "2,64,1")] public int CellSize { get; set; } = 12;

	/// <summary>Minimum vein radius in voxels.</summary>
	[Export(PropertyHint.Range, "1,16,1")] public int MinRadius { get; set; } = 1;

	/// <summary>Maximum vein radius in voxels.</summary>
	[Export(PropertyHint.Range, "1,16,1")] public int MaxRadius { get; set; } = 3;

	/// <summary>Vertical squash of the vein (&lt; 1 flattens it, &gt; 1 stretches it up).</summary>
	[Export(PropertyHint.Range, "0.1,2,0.05")] public float Squash { get; set; } = 0.6f;

	// ------------------------------------------------------------------
	// Height range (absolute world Y in voxels; a vein may straddle it)
	// ------------------------------------------------------------------

	/// <summary>Lowest Y at which a vein's CENTER may spawn.</summary>
	[Export(PropertyHint.Range, "0,256,1")] public int MinHeight { get; set; } = 8;

	/// <summary>Highest Y at which a vein's CENTER may spawn.</summary>
	[Export(PropertyHint.Range, "0,256,1")] public int MaxHeight { get; set; } = 60;
}
