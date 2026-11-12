using Godot;
using System.Collections.Generic;

namespace FactorySim.Voxel;

/// <summary>
/// All generation parameters plus the pre-configured noise sources.
/// Built by <see cref="VoxelWorld"/> from its exported editor values.
/// </summary>
public sealed class GenConfig
{
	public int Seed;
	public int ChunkHeight;

	// Heightmap
	public float BaseHeight;
	public float HeightAmplitude;
	public int DirtDepth;
	public int CaveStoneDepth;
	public FastNoiseLite HeightNoise;

	// Caves (3D noise)
	public bool EnableCaves;
	public float CaveThreshold;
	public int CaveCeiling; // minimum voxels of crust above a cave
	public int CaveFloorDepth; // minimum voxels of solid rock below which caves won't carve
	public FastNoiseLite CaveNoise;

	// Data-driven material + biome lists (referenced by generation)
	public VoxelMaterialConfig MaterialConfig;
	public BiomeConfig BiomeConfig;

	// Data-driven ore list (referenced by the OreStructureGenerator)
	public OreConfig OreConfig;

	// Biome selection (independent 2D noise, separate from height/cave/forest)
	public FastNoiseLite BiomeNoise;

	// Foliage density noise (shared; each biome applies its own threshold + probability)
	public FastNoiseLite ForestNoise;

	/// <summary>
	/// Global multiplier (0..1) on the per-column foliage chance, applied AFTER
	/// the density-noise gate. 1.0 = biomes plant at their own Probability;
	/// lower values thin out the instances inside a forest (fewer trees in the
	/// same groves). 0.0 = no foliage at all.
	/// </summary>
	public float FoliageDensity;

	/// <summary>
	/// The world's structure registry: every piece of foliage / structure that
	/// grows in the world (trees, cacti, rocks, the hive), all as
	/// <see cref="StructureDefinition"/> instances. The generation stages read
	/// from it: the foliage stage rolls <see cref="StructureRegistry.ColumnStructures"/>
	/// per column, the structures stage stamps
	/// <see cref="StructureRegistry.CellStructures"/> (rocks) per cell, and the
	/// hive is placed by a per-chunk main-thread roll.
	/// </summary>
	public StructureRegistry Structures;

	/// <summary>
	/// Deep-rock structure generators (ore veins) applied in the structures
	/// stage. Kept separate from <see cref="Structures"/> because ore is a
	/// deep-rock feature (an <see cref="IStructureGenerator"/>), not surface
	/// foliage.
	/// </summary>
	public List<IStructureGenerator> OreGenerators { get; } = new();
}
