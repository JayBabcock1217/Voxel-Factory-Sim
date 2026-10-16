using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// The configurable list of voxel materials for a world. Each material is a
/// <see cref="VoxelMaterialDef"/> (a stable id + an appearance entry).
///
/// The default list is seeded so a fresh world looks like the original
/// (Grass, Dirt, Stone, CaveStone, Trunk, Leaves, Rock, Placed at ids 1..8,
/// plus Sand, Snow, Ice, Cactus at ids 9..12). Use <see cref="AddMaterial"/>
/// (or the inspector "Add" button) to grow the list; biomes and structure
/// generators reference materials by id.
/// </summary>
[GlobalClass]
public partial class VoxelMaterialConfig : Resource
{
	/// <summary>Fired whenever the material set changes (a def's entry edited, or the array swapped).</summary>
	public event Action<VoxelMaterialEntry> PropertyChanged;

	/// <summary>The material definitions, editable in the inspector (array order is display order).</summary>
	[Export] public VoxelMaterialDef[] Materials { get; set; } = Array.Empty<VoxelMaterialDef>();

	/// <summary>
	/// Allocates a new <see cref="VoxelMaterialDef"/> with a fresh unique id and a
	/// default (white) color, appends it to <see cref="Materials"/>, and returns it.
	/// This is what the inspector "Add" button invokes.
	/// </summary>
	public VoxelMaterialDef AddMaterial()
	{
		var def = new VoxelMaterialDef { Id = NextId(), Entry = new VoxelMaterialEntry { AlbedoColor = new Color(1f, 1f, 1f) } };
		AppendMaterial(def);
		return def;
	}

	/// <summary>The largest material id currently in use (0 when empty). New defs start just above it.</summary>
	public int MaxId()
	{
		int max = 0;
		if (Materials != null)
		{
			for (int i = 0; i < Materials.Length; i++)
			{
				if (Materials[i] != null && Materials[i].Id > max)
				{
					max = Materials[i].Id;
				}
			}
		}
		return max;
	}

	private int NextId() => MaxId() + 1;

	/// <summary>Looks up a material def by id, or null when absent (id 0 = air is never in the list).</summary>
	public VoxelMaterialDef GetDef(int id)
	{
		if (id <= 0 || Materials == null)
		{
			return null;
		}

		for (int i = 0; i < Materials.Length; i++)
		{
			var def = Materials[i];
			if (def != null && def.Id == id)
			{
				return def;
			}
		}
		return null;
	}

	/// <summary>
	/// All material defs in id order (id 1 first). Id order is the canonical order
	/// used for palette indexing; the editor array may be in any order.
	/// </summary>
	public VoxelMaterialDef[] GetAll()
	{
		if (Materials == null || Materials.Length == 0)
		{
			return Array.Empty<VoxelMaterialDef>();
		}

		return Materials.Where(d => d != null).OrderBy(d => d.Id).ToArray();
	}

	public VoxelMaterialConfig()
	{
		Materials = DefaultMaterials();
	}

	/// <summary>
	/// Appends a material def, wires its change event so live inspector edits
	/// propagate to <see cref="PropertyChanged"/>, and notifies listeners.
	/// </summary>
	private void AppendMaterial(VoxelMaterialDef def)
	{
		var list = new List<VoxelMaterialDef>(Materials ?? Array.Empty<VoxelMaterialDef>());
		list.Add(def);
		Materials = list.ToArray();

		def.PropertyChanged += OnDefChanged;
		PropertyChanged?.Invoke(def.Entry);
	}

	private void OnDefChanged(VoxelMaterialEntry entry) => PropertyChanged?.Invoke(entry);

	/// <summary>
	/// Builds the default material list (ids 1..16). The first eight match the
	/// original world's colors exactly so a fresh world looks identical on first
	/// load; ids 9..12 (Sand, Snow, Ice, Cactus) are the extra materials the
	/// default biomes use; ids 13..16 (Coal, Iron, Copper, Gold ore) are the
	/// default ores stamped in by the OreStructureGenerator.
	/// </summary>
	private static VoxelMaterialDef[] DefaultMaterials()
	{
		var defs = new VoxelMaterialDef[]
		{
			MakeDefault(1, "Grass", new Color(0.45f, 0.68f, 0.35f)),
			MakeDefault(2, "Dirt", new Color(0.48f, 0.35f, 0.23f)),
			MakeDefault(3, "Stone", new Color(0.55f, 0.57f, 0.60f)),
			MakeDefault(4, "CaveStone", new Color(0.34f, 0.31f, 0.29f)),
			MakeDefault(5, "Trunk", new Color(0.40f, 0.28f, 0.16f)),
			MakeDefault(6, "Leaves", new Color(0.16f, 0.45f, 0.22f)),
			MakeDefault(7, "Rock", new Color(0.62f, 0.58f, 0.52f)),
			MakeDefault(8, "Placed", new Color(1.00f, 0.45f, 0.15f)),
			MakeDefault(9, "Sand", new Color(0.85f, 0.78f, 0.55f)),
			MakeDefault(10, "Snow", new Color(0.92f, 0.93f, 0.95f)),
			MakeDefault(11, "Ice", new Color(0.75f, 0.85f, 0.95f)),
			MakeDefault(12, "Cactus", new Color(0.25f, 0.55f, 0.30f)),
			// Default ores (ids 13..16). Colors are stone-flavored so a vein reads
			// as "speckled rock" until a texture is assigned; BreakTime is the
			// base seconds for the default tool power (1.0).
			MakeOre(13, "CoalOre", new Color(0.30f, 0.30f, 0.33f), 1.5f),
			MakeOre(14, "IronOre", new Color(0.75f, 0.68f, 0.60f), 3.0f),
			MakeOre(15, "CopperOre", new Color(0.72f, 0.47f, 0.33f), 2.0f),
			MakeOre(16, "GoldOre", new Color(0.83f, 0.71f, 0.33f), 4.0f),
			// Bedrock: unbreakable world floor (y 0..1). Very dark grey.
			new VoxelMaterialDef
			{
				Id = 17,
				Name = "Bedrock",
				Entry = new VoxelMaterialEntry
				{
					AlbedoColor = new Color(0.12f, 0.12f, 0.14f),
					Unbreakable = true,
					BreakTime = 60f,
				},
			},
		};

		foreach (var def in defs)
		{
			def.WireEntry();
		}

		return defs;
	}

	private static VoxelMaterialDef MakeDefault(int id, string name, Color color)
		=> new() { Id = id, Name = name, Entry = new VoxelMaterialEntry { AlbedoColor = color } };

	private static VoxelMaterialDef MakeOre(int id, string name, Color color, float breakTime)
	{
		var def = new VoxelMaterialDef
		{
			Id = id,
			Name = name,
			Entry = new VoxelMaterialEntry { AlbedoColor = color, BreakTime = breakTime },
		};
		return def;
	}
}
