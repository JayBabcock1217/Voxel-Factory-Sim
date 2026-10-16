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
	/// Structure generators to run after terrain + caves.
	/// Add new implementations of <see cref="IStructureGenerator"/> here for future expansion.
	/// </summary>
	public List<IStructureGenerator> Structures { get; } = new();
}
