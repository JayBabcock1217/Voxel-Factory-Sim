namespace FactorySim.Items;

/// <summary>
/// A stack of identical items: a voxel material id plus a count. A plain data
/// class (POCO), not a Resource — it is copied by value freely between the
/// inventory, the cursor, and item drops. Material id 0 (or a count of 0) is
/// an empty stack.
///
/// A stack may also carry a <see cref="ToolType"/>: when <c>MaterialId</c> is a
/// tool id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsToolId"/>), the
/// <see cref="ToolType"/> tells the world/tool system what kind of tool this is
/// (shovel, pickaxe, ...). Tool stacks never hold more than one (Count is 1) —
/// a tool is not stackable like a material.
/// </summary>
public class ItemStack
{
	/// <summary>Maximum items per stack — used for drops and the inventory alike.</summary>
	public const int StackLimit = 64;

	/// <summary>The voxel material id of the item (byte 1-255; 0 = air/empty).</summary>
	public int MaterialId { get; set; }

	/// <summary>How many items are in this stack.</summary>
	public int Count { get; set; }

	/// <summary>
	/// The tool kind this stack represents, when <see cref="MaterialId"/> is a
	/// tool id (see <see cref="FactorySim.Voxel.VoxelMaterialIds.IsToolId"/>).
	/// <see langword="null"/> for plain materials and empty stacks.
	/// </summary>
	public ToolType? ToolType { get; set; }

	/// <summary>True when the stack holds nothing.</summary>
	public bool IsEmpty => MaterialId == 0 || Count <= 0;

	/// <summary>True when this stack is a tool (carries a non-null <see cref="ToolType"/>).</summary>
	public bool IsTool => ToolType.HasValue;

	/// <summary>Creates a stack of <paramref name="count"/> of a material.</summary>
	public static ItemStack From(int materialId, int count = 1)
		=> new() { MaterialId = materialId, Count = count };

	/// <summary>Creates a single tool stack of the given <paramref name="type"/>.</summary>
	public static ItemStack FromTool(ToolType type)
		=> new()
		{
			MaterialId = (int)type,
			Count = 1,
			ToolType = type,
		};
}
