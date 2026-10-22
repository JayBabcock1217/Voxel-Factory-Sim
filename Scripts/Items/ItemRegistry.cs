using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.Items;

/// <summary>
/// A single item entry in the <see cref="ItemRegistry"/>: either a placeable
/// material or a tool. Carries everything the dev panel and the tool system
/// need to display and act on the item: a display name, an icon color, and a
/// kind flag.
/// </summary>
public readonly struct ItemEntry
{
	public int MaterialId { get; }
	public bool IsTool { get; }
	public ToolType? ToolType { get; }
	public string Name { get; }
	public Color IconColor { get; }

	public ItemEntry(int materialId, bool isTool, ToolType? toolType, string name, Color iconColor)
	{
		MaterialId = materialId;
		IsTool = isTool;
		ToolType = toolType;
		Name = name;
		IconColor = iconColor;
	}

	public override bool Equals(object obj) => obj is ItemEntry other && Equals(other);

	public bool Equals(ItemEntry other) =>
		MaterialId == other.MaterialId && IsTool == other.IsTool && ToolType == other.ToolType;

	public override int GetHashCode() => HashCode.Combine(MaterialId, IsTool, ToolType);

	public static bool operator ==(ItemEntry left, ItemEntry right) => left.Equals(right);

	public static bool operator !=(ItemEntry left, ItemEntry right) => !left.Equals(right);
}

/// <summary>
/// The single source of truth for "what items exist in the game". It enumerates
/// every placeable material (read from the world's
/// <see cref="VoxelMaterialConfig"/>, so new materials are picked up
/// automatically) plus every <see cref="ToolType"/>. The dev panel and the
/// tool system both read from here, so adding a new material id or tool type
/// shows up in one place.
///
/// Material display names come from the config's def names; tool names/colors
/// come from the static tool table below.
/// </summary>
public static class ItemRegistry
{
	// Tool display metadata (name + icon color). Kept in one place; the dev
	// panel and the hotbar icon both use these.
	private static readonly (ToolType Type, string Name, Color Color)[] _tools =
	{
		(ToolType.Shovel,  "Shovel",   new Color(0.80f, 0.65f, 0.35f)),
		(ToolType.Pickaxe, "Pickaxe",  new Color(0.60f, 0.62f, 0.68f)),
		(ToolType.Axe,     "Axe",      new Color(0.55f, 0.40f, 0.25f)),
		(ToolType.Sword,   "Sword",    new Color(0.75f, 0.78f, 0.85f)),
	};

	/// <summary>Every tool entry (one per <see cref="ToolType"/>).</summary>
	public static IReadOnlyList<ItemEntry> AllTools
	{
		get
		{
			var list = new List<ItemEntry>(_tools.Length);
			foreach (var (type, name, color) in _tools)
			{
				list.Add(new ItemEntry((int)type, true, type, name, color));
			}
			return list;
		}
	}

	/// <summary>
	/// Every placeable material, in id order, read from the given
	/// <paramref name="config"/>. A material with no def (or an unknown id) is
	/// skipped — only configured, nameable materials are offered.
	/// </summary>
	public static IReadOnlyList<ItemEntry> AllMaterials(VoxelMaterialConfig config)
	{
		var list = new List<ItemEntry>();
		if (config == null)
		{
			return list;
		}

		foreach (var def in config.GetAll())
		{
			if (def == null || def.Id <= 0)
			{
				continue;
			}
			// Bedrock is unbreakable and not a holdable/building item; skip it
			// so the dev panel doesn't offer an unbreakable, useless block.
			if (def.Id == VoxelMaterialIds.Bedrock)
			{
				continue;
			}
			string name = string.IsNullOrEmpty(def.Name) ? $"Material {def.Id}" : def.Name;
			Color color = def.Entry?.AlbedoColor ?? new Color(1f, 1f, 1f);
			list.Add(new ItemEntry(def.Id, false, null, name, color));
		}
		return list;
	}

	/// <summary>
	/// The full ordered item list: all materials first (id order), then all
	/// tools. This is what the dev panel renders as its scrollable list.
	/// </summary>
	public static List<ItemEntry> AllItems(VoxelMaterialConfig config)
	{
		var list = new List<ItemEntry>();
		list.AddRange(AllMaterials(config));
		list.AddRange(AllTools);
		return list;
	}

	/// <summary>The display name for a material id (falls back to "Material N").</summary>
	public static string MaterialName(VoxelMaterialConfig config, int materialId)
	{
		var def = config?.GetDef(materialId);
		if (def != null && !string.IsNullOrEmpty(def.Name))
		{
			return def.Name;
		}
		return $"Material {materialId}";
	}

	/// <summary>The display name for a tool type (falls back to the enum name).</summary>
	public static string ToolName(ToolType type)
	{
		foreach (var (t, name, _) in _tools)
		{
			if (t == type)
			{
				return name;
			}
		}
		return type.ToString();
	}

	/// <summary>The icon color for a tool type (a neutral grey fallback).</summary>
	public static Color ToolColor(ToolType type)
	{
		foreach (var (t, _, color) in _tools)
		{
			if (t == type)
			{
				return color;
			}
		}
		return new Color(0.6f, 0.6f, 0.6f);
	}
}
