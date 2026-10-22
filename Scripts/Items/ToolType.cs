namespace FactorySim.Items;

/// <summary>
/// The kinds of tool the player can hold. A tool is an inventory item (see
/// <see cref="ItemStack.ToolType"/>); the one in the selected hotbar slot is
/// the active tool for breaking blocks. Each type has a different affinity for
/// different material categories — see <see cref="ToolAffinity"/> — so a shovel
/// breaks sand quickly but stone slowly, and a pickaxe is the reverse.
///
/// The underlying int values are the tool material ids
/// (<see cref="FactorySim.Voxel.VoxelMaterialIds.ToolShovel"/> ..
/// <see cref="FactorySim.Voxel.VoxelMaterialIds.ToolSword"/>), so a tool's
/// <see cref="ItemStack.MaterialId"/> is simply <c>(int)ToolType</c>.
/// </summary>
public enum ToolType
{
	Shovel = 100,
	Pickaxe = 101,
	Axe = 102,
	Sword = 103,
}
