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

	/// <summary>Fired whenever <see cref="EnableBlend"/> changes. The world subscribes to remesh.</summary>
	public event Action BlendChanged;

	/// <summary>The material definitions, editable in the inspector (array order is display order).</summary>
	[Export] public VoxelMaterialDef[] Materials { get; set; } = Array.Empty<VoxelMaterialDef>();

	[ExportGroup("Blend")]
	/// <summary>
	/// Crossfade between two materials at every material boundary (a voxel face
	/// whose outward neighbour is a DIFFERENT solid material). When off, each
	/// face snaps to a single material (the original look). When on, the face
	/// gets a per-vertex vertical gradient (0 at the top of the face, 1 at the
	/// bottom) and the blend shader mixes the two materials' textures by that
	/// factor — a soft transition instead of a hard material line. Toggling this
	/// fires <see cref="BlendChanged"/> so the world remeshes.
	/// </summary>
	[Export] public bool EnableBlend { get; set; } = true;
	public void SetEnableBlend(bool value)
	{
		if (EnableBlend != value)
		{
			EnableBlend = value;
			BlendChanged?.Invoke();
		}
	}

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
	/// Serializes this config (and its materials) to a .tres file at <paramref
	/// name="path"/>. Because the <see cref="Materials"/> array and each
	/// <see cref="VoxelMaterialDef"/> / <see cref="VoxelMaterialEntry"/> are
	/// <c>[Export]</c>, ResourceSaver writes them as sub-resources — so a saved
	/// file fully captures every per-material field (color, texture,
	/// transparency, ...). This is how editor edits PERSIST: the VoxelWorld's
	/// MaterialConfig export points at the saved file, so re-running the scene
	/// loads the user's values instead of the code defaults. Call this (from the
	/// editor, or once at runtime) to (re)write the file after editing the
	/// materials in the inspector.
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
			GD.PushError($"[VoxelMaterialConfig] Failed to save to {path}: {err}");
			return false;
		}
		GD.Print($"[VoxelMaterialConfig] Saved {Materials?.Length ?? 0} material(s) to {path}");
		return true;
	}

	/// <summary>
	/// The optional description text for a material id (empty when the material
	/// has none or the id is unknown). Used by item / recipe tooltips.
	/// </summary>
	public string GetDescription(int id)
	{
		var def = GetDef(id);
		if (def != null && def.Entry != null && !string.IsNullOrEmpty(def.Entry.Description))
		{
			return def.Entry.Description;
		}
		return "";
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
	/// Ensures the known "special" materials that are NOT part of the editable
	/// terrain-material list — but MUST always have a palette slot to render —
	/// are present in <see cref="Materials"/>. A saved <c>.tres</c> that was
	/// created before one of these existed (e.g. the torch, id 23) will be
	/// missing it; loading such a config leaves the material with no slot, so
	/// a placed voxel of that id has no mesh (a see-through hole) and no
	/// light. Backfilling adds any missing special material with its canonical
	/// color so it always renders. Called by the world after the config is
	/// loaded, before the palette is sized/applied. Returns true when at least
	/// one material was added (so the caller can persist the config).
	/// </summary>
	public bool BackfillKnownMaterials()
	{
		bool added = false;
		// The torch (and any future special light-source materials) must always
		// have a slot. Their LightLevel is an ITEM property, but the material
		// itself still needs a palette entry to be drawn as a solid block.
		if (GetDef(VoxelMaterialIds.Torch) == null)
		{
			var torch = new VoxelMaterialDef
			{
				Id = VoxelMaterialIds.Torch,
				Name = "Torch",
				Entry = new VoxelMaterialEntry
				{
					AlbedoColor = new Color(1.00f, 0.78f, 0.30f),
					Description = "A handheld light source. Place it to illuminate dark caves.",
				},
			};
			AppendMaterial(torch);
			added = true;
		}
		return added;
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
					Description = "The unbreakable floor of the world.",
				},
			},
			// ------------------------------------------------------------------
			// Crafting products (ids 18..22). These are NOT generated in terrain;
			// they only exist as items you craft and place. Descriptions feed the
			// crafting tooltip.
			// ------------------------------------------------------------------
			MakeCrafted(18, "Plank", new Color(0.72f, 0.55f, 0.32f),
				"Wooden planks. A basic building material."),
			MakeCrafted(19, "Iron Plate", new Color(0.78f, 0.78f, 0.82f),
				"A pressed plate of iron. Used in machines and tools."),
			MakeCrafted(20, "Coal", new Color(0.20f, 0.20f, 0.22f),
				"Burnable fuel, smelted from coal ore."),
			MakeCrafted(21, "Stone Brick", new Color(0.50f, 0.52f, 0.56f),
				"Cut stone bricks. A sturdier building block."),
			MakeCrafted(22, "Machine Frame", new Color(0.45f, 0.48f, 0.55f),
				"A rigid frame for mounting machines. A preview of the coming machine system."),
			// Torch: a small solid light source (id 23). Emission level is an
			// ITEM property (carried by the torch item, not this def) — see
			// ItemRegistry.AllLightSources. Warm amber so it reads as a flame.
			MakeCrafted(23, "Torch", new Color(1.00f, 0.78f, 0.30f),
				"A handheld light source. Place it to illuminate dark caves."),
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

	/// <summary>
	/// Builds a crafted-product material def (not generated in terrain — only
	/// obtainable via crafting). Carries a description for the crafting tooltip.
	/// </summary>
	private static VoxelMaterialDef MakeCrafted(int id, string name, Color color, string description)
	{
		var def = new VoxelMaterialDef
		{
			Id = id,
			Name = name,
			Entry = new VoxelMaterialEntry { AlbedoColor = color, Description = description },
		};
		return def;
	}
}
