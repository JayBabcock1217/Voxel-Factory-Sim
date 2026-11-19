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

	// Grass (DecoVoxel) density noise
	/// <summary>
	/// 2D Perlin noise for the grass-density field. Each exposed grass voxel
	/// column rolls a per-column probability of <c>GrassDensity × noise</c>
	/// (noise remapped to [0,1]), so grass density varies smoothly across the
	/// terrain: thick meadows in high-noise regions, sparse/bare patches in
	/// low-noise regions.
	/// </summary>
	public FastNoiseLite GrassNoise;

	/// <summary>
	/// Global multiplier (0..1) on the per-column grass probability (see
	/// <see cref="GrassNoise"/>). 1.0 = up to full coverage in the densest
	/// patches; 0.0 = no grass at all.
	/// </summary>
	public float GrassDensity;

	/// <summary>
	/// When true (the default), grass only spawns on a FLAT, fully-solid
	/// surface: the grass voxel's eight horizontal neighbours (four same-level
	/// ±X/±Z and four below ±X/±Z) must all be solid (not air). This keeps
	/// grass off SLOPES and overhangs AND stops the smooth MarchingCubes
	/// isosurface (which bevels its edges into the solid) from poking through
	/// the grass cell. When false, grass spawns on any exposed grass voxel (the
	/// old behavior). See
	/// <see cref="TerrainGenerator.IsFullySolidSurface"/>.
	/// </summary>
	public bool GrassFlatSurfaceOnly;

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
