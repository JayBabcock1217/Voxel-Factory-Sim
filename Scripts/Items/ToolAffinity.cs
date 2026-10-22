using FactorySim.Voxel;

namespace FactorySim.Items;

/// <summary>
/// The central tool-vs-material affinity table. This is the single tunable
/// place that decides how fast (or slow) a given tool breaks a given material.
///
/// Materials are grouped into <see cref="MaterialCategory"/> buckets; the table
/// maps each (<see cref="ToolType"/>, <see cref="MaterialCategory"/>) pair to a
/// break-speed MULTIPLIER applied on top of the material's own
/// <c>BreakTime</c>. A multiplier of 1.0 means "break in the material's base
/// BreakTime seconds"; 2.0 means half that; 0.5 means double it. Materials not
/// in a category (e.g. the unbreakable bedrock) are handled by the world
/// before this is consulted.
///
/// The default table encodes the classic Minecraft-style rule the user asked
/// for: a shovel is fast on soft materials (sand, dirt, grass, snow, ice) and
/// slow on stone; a pickaxe is fast on stone/ores and slow on soft material;
/// an axe is fast on wood and slow elsewhere; a sword is a generic, even
/// breaker (no strong specialty) — it breaks everything at a middling rate.
/// </summary>
public static class ToolAffinity
{
	/// <summary>
	/// A material category for affinity purposes. The table is keyed by this
	/// bucket, not by individual material id, so adding a new stone-family
	/// material only needs a <see cref="CategoryOf"/> entry, not a new table
	/// row.
	/// </summary>
	public enum MaterialCategory
	{
		Soft,      // dirt, grass, sand, snow, ice — shovel-friendly
		Stone,     // stone, rock, cave stone, ores — pickaxe-friendly
		Wood,      // trunk, leaves, cactus, planks — axe-friendly
		Metal,     // iron plate, machine frame — pickaxe-friendly, slow
		Generic,   // anything uncategorized — even rate for all tools
	}

	/// <summary>
	/// Maps a material id to its <see cref="MaterialCategory"/>. Unknown ids
	/// fall to <see cref="MaterialCategory.Generic"/> so every material still
	/// has a defined break rate.
	/// </summary>
	public static MaterialCategory CategoryOf(int materialId) => materialId switch
	{
		VoxelMaterialIds.Grass => MaterialCategory.Soft,
		VoxelMaterialIds.Dirt => MaterialCategory.Soft,
		VoxelMaterialIds.Sand => MaterialCategory.Soft,
		VoxelMaterialIds.Snow => MaterialCategory.Soft,
		VoxelMaterialIds.Ice => MaterialCategory.Soft,

		VoxelMaterialIds.Stone => MaterialCategory.Stone,
		VoxelMaterialIds.CaveStone => MaterialCategory.Stone,
		VoxelMaterialIds.Rock => MaterialCategory.Stone,
		VoxelMaterialIds.CoalOre => MaterialCategory.Stone,
		VoxelMaterialIds.IronOre => MaterialCategory.Stone,
		VoxelMaterialIds.CopperOre => MaterialCategory.Stone,
		VoxelMaterialIds.GoldOre => MaterialCategory.Stone,
		VoxelMaterialIds.StoneBrick => MaterialCategory.Stone,

		VoxelMaterialIds.Trunk => MaterialCategory.Wood,
		VoxelMaterialIds.Leaves => MaterialCategory.Wood,
		VoxelMaterialIds.Cactus => MaterialCategory.Wood,
		VoxelMaterialIds.Plank => MaterialCategory.Wood,

		VoxelMaterialIds.IronPlate => MaterialCategory.Metal,
		VoxelMaterialIds.MachineFrame => MaterialCategory.Metal,

		_ => MaterialCategory.Generic,
	};

	// The affinity multipliers. Kept as an explicit table (not a switch) so the
	// tuning surface is one place and easy to scan. Rows = tool, cols = category.
	//
	//                 Soft   Stone  Wood   Metal  Generic
	// Shovel           2.0    0.4    0.6    0.3    1.0
	// Pickaxe          0.4    2.0    0.6    1.5    1.0
	// Axe              0.6    0.5    2.0    0.4    1.0
	// Sword            1.0    1.0    1.0    1.0    1.0
	private static readonly float[,] _multipliers =
	{
		// Shovel
		{ 2.0f, 0.4f, 0.6f, 0.3f, 1.0f },
		// Pickaxe
		{ 0.4f, 2.0f, 0.6f, 1.5f, 1.0f },
		// Axe
		{ 0.6f, 0.5f, 2.0f, 0.4f, 1.0f },
		// Sword
		{ 1.0f, 1.0f, 1.0f, 1.0f, 1.0f },
	};

	private static int ToolIndex(ToolType tool) => (int)tool - (int)ToolType.Shovel;

	/// <summary>
	/// The break-speed multiplier for <paramref name="tool"/> on a material of
	/// the given category. A tool not in the table (or an out-of-range category)
	/// returns 1.0 (the material's base rate).
	/// </summary>
	public static float GetMultiplier(ToolType tool, MaterialCategory category)
	{
		int t = ToolIndex(tool);
		int c = (int)category;
		if (t < 0 || t >= _multipliers.GetLength(0) || c < 0 || c >= _multipliers.GetLength(1))
		{
			return 1.0f;
		}
		return _multipliers[t, c];
	}

	/// <summary>
	/// Convenience: the multiplier for <paramref name="tool"/> directly on a
	/// material id (looks up its category first).
	/// </summary>
	public static float GetMultiplier(ToolType tool, int materialId)
		=> GetMultiplier(tool, CategoryOf(materialId));
}
