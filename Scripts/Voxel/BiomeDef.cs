using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// One configurable biome. Editor-modifiable settings: a display name, terrain
/// modifiers, per-layer material slots (each references a material BY id), and
/// per-biome foliage settings.
///
/// Biomes are read at generation time (the F-key regenerate picks up edits).
/// Terrain modifiers and the material slots are BLENDED across the biomes that
/// overlap a column (see <c>TerrainGenerator.BiomeAt</c>): within the transition
/// band between two biomes, a column's height/amplitude is the weighted blend of
/// both biomes' modifiers, and each layer's material is the weighted majority of
/// the biomes' slots.
/// </summary>
[GlobalClass]
public partial class BiomeDef : Resource
{
	/// <summary>Display name (e.g. "Plains", "Desert", "Snow").</summary>
	[Export] public string Name { get; set; } = "Biome";

	// ------------------------------------------------------------------
	// Biome selection
	// ------------------------------------------------------------------

	/// <summary>
	/// Relative weight of this biome (its share of the world). Distinct from
	/// the per-column foliage <c>Probability</c> below. Biomes are chosen by a
	/// WEIGHTED CUMULATIVE DISTRIBUTION: a column's biome is picked from
	/// <c>noise * totalWeight</c> across the biomes' weights, so a biome with
	/// twice the weight of another covers twice the area. The relative values
	/// matter (they are normalized internally), so 1.0 / 0.5 / 0.5 gives a
	/// 50/25/25 split. A 0 (or negative) weight makes the biome never select.
	/// This is what lets you tune biome coverage in the editor — see
	/// <c>TerrainGenerator.SampleBiomes</c>.
	/// </summary>
	[Export(PropertyHint.Range, "0,10,0.01")] public float Weight { get; set; } = 1.0f;

	// ------------------------------------------------------------------
	// Terrain modifiers (blended across biomes)
	// ------------------------------------------------------------------

	/// <summary>
	/// Additive height offset in voxels (can be negative). Blended across biomes
	/// by their column weights, then added to the base height.
	/// </summary>
	[Export] public float HeightModifier { get; set; }

	/// <summary>
	/// Multiplier on the height amplitude (1.0 = no change). Blended across
	/// biomes by their column weights.
	/// </summary>
	[Export] public float AmplitudeModifier { get; set; } = 1.0f;

	// ------------------------------------------------------------------
	// Material slots (each references a material BY id; 0 = air / none)
	// ------------------------------------------------------------------

	/// <summary>Top layer material (the surface).</summary>
	[Export] public int SurfaceMaterial { get; set; } = VoxelMaterialIds.Grass;

	/// <summary>The dirt band just under the surface.</summary>
	[Export] public int SubsurfaceMaterial { get; set; } = VoxelMaterialIds.Dirt;

	/// <summary>Material below the cave-stone depth.</summary>
	[Export] public int DeepMaterial { get; set; } = VoxelMaterialIds.Stone;

	/// <summary>Material below <c>CaveStoneDepth</c> (the deep "bedrock-ish" band).</summary>
	[Export] public int CaveMaterial { get; set; } = VoxelMaterialIds.CaveStone;

	/// <summary>Material used for boulders in this biome (see RockStructureGenerator).</summary>
	[Export] public int RockMaterial { get; set; } = VoxelMaterialIds.Rock;

	/// <summary>Tree trunk material.</summary>
	[Export] public int TrunkMaterial { get; set; } = VoxelMaterialIds.Trunk;

	/// <summary>Tree leaf material.</summary>
	[Export] public int LeafMaterial { get; set; } = VoxelMaterialIds.Leaves;

	/// <summary>
	/// Cactus material (0 = no cactus for this biome). Set to a cactus /
	/// sand-block material id and <see cref="IsCactus"/> to true for cactus
	/// biomes.
	/// </summary>
	[Export] public int CactusMaterial { get; set; }

	// ------------------------------------------------------------------
	// Per-biome foliage
	// ------------------------------------------------------------------

	/// <summary>Masters foliage (trees / cacti) in this biome.</summary>
	[Export] public bool EnableFoliage { get; set; } = true;

	/// <summary>
	/// Biome-local forest-density noise threshold: the dominant biome only plants
	/// where the (shared) forest-density noise is >= this value. Replaces the old
	/// global forest threshold.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float DensityThreshold { get; set; }

	/// <summary>Per-column chance of a plant inside the biome's forested area.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float Probability { get; set; } = 0.35f;

	/// <summary>Minimum trunk height (voxels).</summary>
	[Export(PropertyHint.Range, "1,32,1")] public int TrunkMinHeight { get; set; } = 7;

	/// <summary>Maximum trunk height (voxels).</summary>
	[Export(PropertyHint.Range, "1,32,1")] public int TrunkMaxHeight { get; set; } = 12;

	/// <summary>
	/// Minimum radius (voxels) of the main canopy blob — the large leaf sphere at
	/// the top of the tree.
	/// </summary>
	[Export(PropertyHint.Range, "1,16,1")] public int LeafMinRadius { get; set; } = 3;

	/// <summary>
	/// Maximum radius (voxels) of the main canopy blob — the large leaf sphere at
	/// the top of the tree.
	/// </summary>
	[Export(PropertyHint.Range, "1,16,1")] public int LeafMaxRadius { get; set; } = 5;

	/// <summary>
	/// Whether the tree grows a second, smaller canopy tier lower down (Minecraft
	/// oak style). The tier-2 blob is <see cref="LeafTier2RadiusFactor"/> of the
	/// main canopy's radius, centered <see cref="LeafTier2HeightBelow"/> voxels
	/// below the main canopy center.
	/// </summary>
	[Export] public bool HasLeafTier2 { get; set; } = true;

	/// <summary>
	/// Size of the lower canopy tier relative to the main canopy (0.5 = half its
	/// radius). Only used when <see cref="HasLeafTier2"/> is true.
	/// </summary>
	[Export(PropertyHint.Range, "0.1,1,0.05")] public float LeafTier2RadiusFactor { get; set; } = 0.5f;

	/// <summary>
	/// Voxels below the main canopy center where the tier-2 blob is centered.
	/// Only used when <see cref="HasLeafTier2"/> is true.
	/// </summary>
	[Export(PropertyHint.Range, "1,16,1")] public int LeafTier2HeightBelow { get; set; } = 4;

	/// <summary>
	/// When true, foliage plants a single vertical column of <see cref="CactusMaterial"/>
	/// (height ~ trunk height, no leaf blob) instead of a tree + leaves blob.
	/// </summary>
	[Export] public bool IsCactus { get; set; }
}
