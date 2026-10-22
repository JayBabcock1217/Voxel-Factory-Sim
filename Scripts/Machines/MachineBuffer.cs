using System;
using FactorySim.Items;

namespace FactorySim.Machines;

/// <summary>
/// A small fixed-size item buffer owned by a <see cref="Machine"/> — the
/// machine's internal inventory (its input or output slots). It is the unit of
/// storage that conveyors pull from / push into and that the machine UI edits.
///
/// A buffer holds up to <see cref="Capacity"/> stacks of items. It is plain C#
/// (not a Node) so it is trivially testable and can be embedded in any machine.
/// Stacks follow the same convention as the player's inventory: identical
/// materials merge up to <see cref="ItemStack.StackLimit"/>, and tools/machines
/// are single-item (they never merge).
///
/// This is deliberately separate from the player's <see cref="Inventory"/>: a
/// machine's buffer is machine-local state, not the player's carry capacity.
/// </summary>
public class MachineBuffer
{
	private readonly ItemStack[] _slots;

	/// <summary>The number of item stacks this buffer can hold.</summary>
	public int Capacity => _slots.Length;

	/// <summary>Read-only access to the buffer's slots (index 0..Capacity-1).</summary>
	public ItemStack this[int index]
	{
		get => _slots[index];
		set { _slots[index] = value ?? ItemStack.From(0); }
	}

	/// <summary>
	/// Creates a buffer with <paramref name="capacity"/> slots. A capacity of
	/// 0 or less becomes 1 (a buffer must hold at least one stack so the UI has
	/// something to show and a machine with "no buffer" can still be modeled as
	/// a single-slot buffer).
	/// </summary>
	public MachineBuffer(int capacity)
	{
		int c = Math.Max(1, capacity);
		_slots = new ItemStack[c];
		for (int i = 0; i < c; i++)
		{
			_slots[i] = ItemStack.From(0);
		}
	}

	/// <summary>
	/// Total number of items (sum of all stack counts) currently in the buffer.
	/// </summary>
	public int TotalCount
	{
		get
		{
			int total = 0;
			for (int i = 0; i < _slots.Length; i++)
			{
				total += _slots[i].Count;
			}
			return total;
		}
	}

	/// <summary>True when the buffer holds no items at all.</summary>
	public bool IsEmpty => TotalCount == 0;

	/// <summary>True when every slot is full (no more items can be inserted).</summary>
	public bool IsFull
	{
		get
		{
			for (int i = 0; i < _slots.Length; i++)
			{
				var s = _slots[i];
				if (s.IsEmpty || s.Count < ItemStack.StackLimit)
				{
					return false;
				}
			}
			return true;
		}
	}

	/// <summary>
	/// Tries to insert <paramref name="stack"/> into the buffer, filling existing
	/// matching stacks first (up to the stack limit) then empty slots. Returns
	/// the number of items actually inserted (0 when nothing fit). The input
	/// stack is NOT mutated — the caller keeps the remainder.
	/// </summary>
	public int TryInsert(ItemStack stack)
	{
		if (stack == null || stack.IsEmpty)
		{
			return 0;
		}
		int remaining = stack.Count;

		// Pass 1: fill existing stacks of the same material.
		for (int i = 0; i < _slots.Length && remaining > 0; i++)
		{
			var s = _slots[i];
			if (!s.IsEmpty && s.MaterialId == stack.MaterialId && s.Count < ItemStack.StackLimit)
			{
				int space = ItemStack.StackLimit - s.Count;
				int add = Math.Min(space, remaining);
				s.Count += add;
				remaining -= add;
			}
		}

		// Pass 2: fill empty slots (one stack per empty slot).
		for (int i = 0; i < _slots.Length && remaining > 0; i++)
		{
			if (_slots[i].IsEmpty)
			{
				int add = Math.Min(ItemStack.StackLimit, remaining);
				_slots[i] = ItemStack.From(stack.MaterialId, add);
				remaining -= add;
			}
		}

		return stack.Count - remaining;
	}

	/// <summary>
	/// Tries to remove up to <paramref name="count"/> of <paramref
	/// name="materialId"/> from the buffer, draining the LAST slot first (so the
	/// most-recently-added items leave first, like a queue). Returns an
	/// <see cref="ItemStack"/> of what was removed (empty when nothing was
	/// available). A materialId of 0 removes up to <paramref name="count"/> of
	/// ANY item (used when emptying a buffer).
	/// </summary>
	public ItemStack TryExtract(int materialId, int count)
	{
		if (count <= 0)
		{
			return ItemStack.From(0);
		}
		int remaining = count;
		int removedId = materialId == 0 ? -1 : materialId;
		int removed = 0;

		for (int i = _slots.Length - 1; i >= 0 && remaining > 0; i--)
		{
			var s = _slots[i];
			if (s.IsEmpty)
			{
				continue;
			}
			if (materialId != 0 && s.MaterialId != materialId)
			{
				continue;
			}
			if (removedId < 0)
			{
				removedId = s.MaterialId;
			}
			int take = Math.Min(remaining, s.Count);
			s.Count -= take;
			if (s.Count <= 0)
			{
				_slots[i] = ItemStack.From(0);
			}
			remaining -= take;
			removed += take;
		}

		return removed > 0 ? ItemStack.From(removedId, removed) : ItemStack.From(0);
	}

	/// <summary>
	/// True when the buffer holds at least <paramref name="count"/> of
	/// <paramref name="materialId"/> (across all its slots).
	/// </summary>
	public bool HasAtLeast(int materialId, int count)
	{
		if (count <= 0)
		{
			return true;
		}
		int have = 0;
		for (int i = 0; i < _slots.Length; i++)
		{
			var s = _slots[i];
			if (!s.IsEmpty && s.MaterialId == materialId)
			{
				have += s.Count;
			}
		}
		return have >= count;
	}

	/// <summary>
	/// The total count of a single material across all slots (0 when none).
	/// </summary>
	public int CountOf(int materialId)
	{
		int have = 0;
		for (int i = 0; i < _slots.Length; i++)
		{
			var s = _slots[i];
			if (!s.IsEmpty && s.MaterialId == materialId)
			{
				have += s.Count;
			}
		}
		return have;
	}

	/// <summary>Removes all items from the buffer (e.g. when it breaks).</summary>
	public void Clear()
	{
		for (int i = 0; i < _slots.Length; i++)
		{
			_slots[i] = ItemStack.From(0);
		}
	}

	/// <summary>
	/// True when the buffer holds at least one full set of a recipe's
	/// ingredients (every ingredient present in at least its required count).
	/// Used by processors to decide when they can start a craft.
	/// </summary>
	public bool CanCraftRecipe(FactorySim.Items.CraftingRecipe recipe)
	{
		if (recipe == null)
		{
			return false;
		}
		foreach (var (materialId, count) in recipe.Ingredients)
		{
			if (count > 0 && !HasAtLeast(materialId, count))
			{
				return false;
			}
		}
		return true;
	}
}
