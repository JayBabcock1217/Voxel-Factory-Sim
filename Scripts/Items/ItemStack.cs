namespace FactorySim.Items;

/// <summary>
/// A stack of identical items: a voxel material id plus a count. A plain data
/// class (POCO), not a Resource — it is copied by value freely between the
/// inventory, the cursor, and item drops. Material id 0 (or a count of 0) is
/// an empty stack.
/// </summary>
public class ItemStack
{
	/// <summary>Maximum items per stack — used for drops and the inventory alike.</summary>
	public const int StackLimit = 64;

	/// <summary>The voxel material id of the item (byte 1-255; 0 = air/empty).</summary>
	public int MaterialId { get; set; }

	/// <summary>How many items are in this stack.</summary>
	public int Count { get; set; }

	/// <summary>True when the stack holds nothing.</summary>
	public bool IsEmpty => MaterialId == 0 || Count <= 0;

	/// <summary>Creates a stack of <paramref name="count"/> of a material.</summary>
	public static ItemStack From(int materialId, int count = 1)
		=> new() { MaterialId = materialId, Count = count };
}
