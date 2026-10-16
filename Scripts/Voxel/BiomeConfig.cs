using System;
using System.Collections.Generic;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// The configurable list of biomes for a world. Each biome is a
/// <see cref="BiomeDef"/> (terrain modifiers + per-layer material slots +
/// per-biome foliage).
///
/// Biomes are selected by a 2D noise field (see <c>VoxelWorld.BiomeNoise*</c> /
/// <c>GenConfig.BiomeNoise</c>) that feeds a WEIGHTED CUMULATIVE DISTRIBUTION over
/// each biome's <see cref="BiomeDef.Weight"/>: a biome's share of the world is
/// proportional to its weight, so editor edits to the weights change the biome
/// mix. A column straddling a biome border carries a weighted blend of the biomes
/// it overlaps, so borders transition smoothly.
///
/// The default list seeds three biomes (Plains, Desert, Snow) so a fresh world
/// shows biome borders and per-biome foliage out of the box.
/// </summary>
[GlobalClass]
public partial class BiomeConfig : Resource
{
	/// <summary>The biome definitions, editable in the inspector.</summary>
	[Export] public BiomeDef[] Biomes { get; set; } = Array.Empty<BiomeDef>();

	/// <summary>
	/// Allocates a new <see cref="BiomeDef"/> with a default name ("Biome N"),
	/// appends it to <see cref="Biomes"/>, and returns it. This is what the
	/// inspector "Add" button invokes.
	/// </summary>
	public BiomeDef AddBiome()
	{
		var biome = new BiomeDef { Name = $"Biome {Count + 1}" };
		var list = new List<BiomeDef>(Biomes ?? Array.Empty<BiomeDef>());
		list.Add(biome);
		Biomes = list.ToArray();
		return biome;
	}

	/// <summary>Number of biomes in the config.</summary>
	public int Count => Biomes != null ? Biomes.Length : 0;

	/// <summary>The biome at the given index, or null when out of range.</summary>
	public BiomeDef GetBiome(int index)
		=> index >= 0 && Biomes != null && index < Biomes.Length ? Biomes[index] : null;

	/// <summary>
	/// Serializes this config (and its biomes) to a .tres file at <paramref
	/// name="path"/>. Because the <see cref="Biomes"/> array and each
	/// <see cref="BiomeDef"/> are <c>[Export]</c>, ResourceSaver writes them as
	/// sub-resources — so a saved file fully captures every per-biome field
	/// (Weight, materials, foliage, ...). This is how editor edits PERSIST:
	/// the VoxelWorld's BiomeConfig export points at the saved file, so
	/// re-running the scene loads the user's values instead of the code
	/// defaults. Call this (from the editor, or once at runtime) to (re)write
	/// the file after editing the biomes in the inspector.
	/// </summary>
	/// <returns>True on success; false if the file could not be written.</returns>
	public bool SaveTo(string path)
	{
		if (path == null)
		{
			return false;
		}
		// Make sure the parent directory exists (e.g. res://Resources/).
		int lastSlash = path.LastIndexOf('/');
		if (lastSlash > 0)
		{
			string dir = path.Substring(0, lastSlash);
			if (!DirAccess.DirExistsAbsolute(dir))
			{
				DirAccess.MakeDirRecursiveAbsolute(dir);
			}
		}
		Error err = ResourceSaver.Save(this, path);
		if (err != Error.Ok)
		{
			GD.PushError($"[BiomeConfig] Failed to save to {path}: {err}");
			return false;
		}
		GD.Print($"[BiomeConfig] Saved {Count} biome(s) to {path}");
		return true;
	}

	/// <summary>
	/// Raised when the biome list itself changes (a biome added/removed via the
	/// inspector or <see cref="AddBiome"/>). Individual field edits on a
	/// <see cref="BiomeDef"/> are NOT relayed here — the F-key regenerate (which
	/// rebuilds the config from the live resource) picks those up.
	/// </summary>
	public event Action<string> PropertyChanged;

	public BiomeConfig()
	{
		Biomes = DefaultBiomes();
	}

	protected virtual void NotifyChanged(string what) => PropertyChanged?.Invoke(what);

	/// <summary>
	/// Builds the default biome list:
	///   - Plains: grass surface, dirt band, stone deep, trees.
	///   - Desert: sand surface + subsurface, cacti (no trees), slightly lower probability.
	///   - Snow: snow surface, no foliage, slightly higher terrain.
	/// </summary>
	private static BiomeDef[] DefaultBiomes()
	{
		return new BiomeDef[]
		{
			new()
			{
				Name = "Plains",
				Weight = 1.0f,
				HeightModifier = 0f,
				AmplitudeModifier = 1.0f,
				SurfaceMaterial = VoxelMaterialIds.Grass,
				SubsurfaceMaterial = VoxelMaterialIds.Dirt,
				DeepMaterial = VoxelMaterialIds.Stone,
				CaveMaterial = VoxelMaterialIds.CaveStone,
				RockMaterial = VoxelMaterialIds.Rock,
				TrunkMaterial = VoxelMaterialIds.Trunk,
				LeafMaterial = VoxelMaterialIds.Leaves,
				EnableFoliage = true,
				DensityThreshold = 0f,
				Probability = 0.35f,
				TrunkMinHeight = 3,
				TrunkMaxHeight = 5,
				LeafMinRadius = 2,
				LeafMaxRadius = 3,
				IsCactus = false,
			},
			new()
			{
				Name = "Desert",
				Weight = 0.6f,
				HeightModifier = -2f,
				AmplitudeModifier = 0.8f,
				SurfaceMaterial = VoxelMaterialIds.Sand,
				SubsurfaceMaterial = VoxelMaterialIds.Sand,
				DeepMaterial = VoxelMaterialIds.Stone,
				CaveMaterial = VoxelMaterialIds.CaveStone,
				RockMaterial = VoxelMaterialIds.Rock,
				TrunkMaterial = 0,
				LeafMaterial = 0,
				CactusMaterial = VoxelMaterialIds.Cactus,
				EnableFoliage = true,
				DensityThreshold = 0f,
				Probability = 0.15f,
				TrunkMinHeight = 4,
				TrunkMaxHeight = 6,
				LeafMinRadius = 2,
				LeafMaxRadius = 3,
				IsCactus = true,
			},
			new()
			{
				Name = "Snow",
				Weight = 0.4f,
				HeightModifier = 4f,
				AmplitudeModifier = 1.0f,
				SurfaceMaterial = VoxelMaterialIds.Snow,
				SubsurfaceMaterial = VoxelMaterialIds.Dirt,
				DeepMaterial = VoxelMaterialIds.Stone,
				CaveMaterial = VoxelMaterialIds.CaveStone,
				RockMaterial = VoxelMaterialIds.Rock,
				TrunkMaterial = 0,
				LeafMaterial = 0,
				CactusMaterial = 0,
				EnableFoliage = false,
				DensityThreshold = 0f,
				Probability = 0f,
				TrunkMinHeight = 3,
				TrunkMaxHeight = 5,
				LeafMinRadius = 2,
				LeafMaxRadius = 3,
				IsCactus = false,
			},
		};
	}
}
