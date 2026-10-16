using System;
using System.Collections.Generic;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// The configurable list of ores for a world. Each ore is an
/// <see cref="OreDef"/> (material id, placement probability, cell size, vein
/// size, height range) — fully editor-modifiable, so new ores can be added
/// later with the "Add Ore" button (or by editing a saved .tres) without code
/// changes.
///
/// The default list seeds the four starter ores: Coal, Iron, Copper, Gold
/// (materials 13..16). The world builds one <see cref="OreStructureGenerator"/>
/// from this list; ore placement is a pure function of (cell, seed) so veins
/// crossing chunk borders are identical no matter which chunk generates them.
/// </summary>
[GlobalClass]
public partial class OreConfig : Resource
{
	/// <summary>The ore definitions, editable in the inspector.</summary>
	[Export] public OreDef[] Ores { get; set; } = Array.Empty<OreDef>();

	/// <summary>
	/// Allocates a new <see cref="OreDef"/> with a default name ("Ore N"),
	/// appends it to <see cref="Ores"/>, and returns it. This is what the
	/// inspector "Add" button invokes.
	/// </summary>
	public OreDef AddOre()
	{
		var ore = new OreDef { Name = $"Ore {Count + 1}" };
		var list = new List<OreDef>(Ores ?? Array.Empty<OreDef>());
		list.Add(ore);
		Ores = list.ToArray();
		NotifyChanged("ore added");
		return ore;
	}

	/// <summary>Number of ores in the config.</summary>
	public int Count => Ores != null ? Ores.Length : 0;

	/// <summary>The ore at the given index, or null when out of range.</summary>
	public OreDef GetOre(int index)
		=> index >= 0 && Ores != null && index < Ores.Length ? Ores[index] : null;

	/// <summary>
	/// Raised when the ore list itself changes (an ore added/removed via the
	/// inspector or <see cref="AddOre"/>). Individual field edits on an
	/// <see cref="OreDef"/> are NOT relayed here — the F-key regenerate (which
	/// rebuilds the config from the live resource) picks those up.
	/// </summary>
	public event Action<string> PropertyChanged;

	/// <summary>Notifies listeners that the ore list changed.</summary>
	protected virtual void NotifyChanged(string what) => PropertyChanged?.Invoke(what);

	public OreConfig()
	{
		Ores = DefaultOres();
	}

	/// <summary>
	/// Builds the default ore list:
	///   - Coal:   common, shallow-to-mid, small veins.
	///   - Copper: common, mid, medium veins.
	///   - Iron:   less common, mid-to-deep, medium veins.
	///   - Gold:   rare, deep, small veins.
	/// </summary>
	private static OreDef[] DefaultOres()
	{
		return new OreDef[]
		{
			new()
			{
				Name = "Coal",
				MaterialId = VoxelMaterialIds.CoalOre,
				Enabled = true,
				Probability = 0.06f,
				CellSize = 10,
				MinRadius = 1,
				MaxRadius = 2,
				Squash = 0.6f,
				MinHeight = 20,
				MaxHeight = 140,
			},
			new()
			{
				Name = "Copper",
				MaterialId = VoxelMaterialIds.CopperOre,
				Enabled = true,
				Probability = 0.04f,
				CellSize = 12,
				MinRadius = 1,
				MaxRadius = 3,
				Squash = 0.6f,
				MinHeight = 30,
				MaxHeight = 120,
			},
			new()
			{
				Name = "Iron",
				MaterialId = VoxelMaterialIds.IronOre,
				Enabled = true,
				Probability = 0.03f,
				CellSize = 14,
				MinRadius = 1,
				MaxRadius = 3,
				Squash = 0.6f,
				MinHeight = 10,
				MaxHeight = 100,
			},
			new()
			{
				Name = "Gold",
				MaterialId = VoxelMaterialIds.GoldOre,
				Enabled = true,
				Probability = 0.012f,
				CellSize = 20,
				MinRadius = 1,
				MaxRadius = 2,
				Squash = 0.6f,
				MinHeight = 4,
				MaxHeight = 56,
			},
		};
	}
}
