using Godot;
using System;
using FactorySim.Machines;

namespace FactorySim.Items;

/// <summary>
/// The player's inventory: 82 slots (10 hotbar + 72 storage), holding
/// <see cref="ItemStack"/>s. Pure data/logic — no input handling; the
/// <c>InventoryUI</c> owns the TAB key, mouse, and rendering. Systems that
/// hand items to the player (item drops) find this node via the
/// "inventory" group and call <see cref="CollectItem"/>.
///
/// Slot layout: slots 0..9 are the hotbar (rendered as the bottom row),
/// slots 10..81 are the storage (the 6 rows above it, 12 wide).
/// </summary>
public partial class Inventory : Node
{
	/// <summary>Number of hotbar slots (the persistent bottom row).</summary>
	public const int HotbarSize = 10;

	/// <summary>Number of storage slots (the 6×12 grid above the hotbar).</summary>
	public const int StorageSize = 72;

	/// <summary>Total inventory slots (hotbar + storage).</summary>
	public const int TotalSlots = HotbarSize + StorageSize;

	/// <summary>Columns in the storage grid (12).</summary>
	public const int StorageCols = 12;

	/// <summary>Rows in the storage grid (6).</summary>
	public const int StorageRows = 6;

	/// <summary>All 82 slots, hotbar (0-9) first, then storage (10-81).</summary>
	public ItemStack[] Slots { get; }

	/// <summary>True while the inventory UI is open (set by the UI, read by the player).</summary>
	public bool IsOpen { get; private set; }

	/// <summary>Fired whenever any slot changes (or the open state toggles) — the UI refreshes on this.</summary>
	public event Action InventoryChanged;

	/// <summary>Fired when the open state changes, with the new value. The Player subscribes to freeze/unfreeze itself.</summary>
	public event Action<bool> Toggled;

	public Inventory()
	{
		Slots = new ItemStack[TotalSlots];
		for (int i = 0; i < TotalSlots; i++)
		{
			Slots[i] = ItemStack.From(0);
		}
	}

	public override void _Ready()
	{
		// Item drops and any other systems find the inventory via this group.
		AddToGroup("inventory");
	}

	/// <summary>
	/// Adds as many items from <paramref name="stack"/> as the inventory can
	/// hold: first into existing stacks of the same material that have room
	/// (filling them up to <see cref="ItemStack.StackLimit"/>), then into empty
	/// slots. Returns the number of items actually collected (0 when nothing
	/// fits, up to <paramref name="stack"/>.Count). The caller is responsible
	/// for handling the remainder.
	/// </summary>
	public int CollectItem(ItemStack stack)
	{
		if (stack == null || stack.IsEmpty)
		{
			return 0;
		}

		int remaining = stack.Count;

		// Fill existing same-material stacks first.
		for (int i = 0; i < TotalSlots && remaining > 0; i++)
		{
			var slot = Slots[i];
			if (slot.IsEmpty || slot.MaterialId != stack.MaterialId)
			{
				continue;
			}

			int space = ItemStack.StackLimit - slot.Count;
			int take = Math.Min(space, remaining);
			slot.Count += take;
			// Carry the incoming light level onto the existing stack. A torch's
			// LightLevel is intrinsic (always TorchLightLevel), so the incoming
			// stack is the authority; OR the two so a 0-level stack can never
			// wipe out a real light source's level on a merge.
			if (stack.LightLevel > 0)
			{
				slot.LightLevel = stack.LightLevel;
			}
			remaining -= take;
		}

		// Then fill empty slots (whole stacks).
		for (int i = 0; i < TotalSlots && remaining > 0; i++)
		{
			if (!Slots[i].IsEmpty)
			{
				continue;
			}

			int take = Math.Min(ItemStack.StackLimit, remaining);
			var placed = ItemStack.From(stack.MaterialId, take);
			placed.LightLevel = stack.LightLevel; // carry the light source's level
			Slots[i] = placed;
			remaining -= take;
		}

		int collected = stack.Count - remaining;
		if (collected > 0)
		{
			RaiseChanged();
		}
		return collected;
	}

	/// <summary>
	/// Adds <paramref name="stack"/> to <paramref name="slotIndex"/> if it fits
	/// (empty slot, or same material with room for the whole stack). Returns
	/// true when the whole stack was added.
	/// </summary>
	public bool AddToSlot(int slotIndex, ItemStack stack)
	{
		if (slotIndex < 0 || slotIndex >= TotalSlots || stack == null || stack.IsEmpty)
		{
			return false;
		}

		var slot = Slots[slotIndex];
		if (slot.IsEmpty)
		{
			Slots[slotIndex] = ItemStack.From(stack.MaterialId, Math.Min(stack.Count, ItemStack.StackLimit));
			RaiseChanged();
			return stack.Count <= ItemStack.StackLimit;
		}

		if (slot.MaterialId == stack.MaterialId && slot.Count + stack.Count <= ItemStack.StackLimit)
		{
			slot.Count += stack.Count;
			RaiseChanged();
			return true;
		}

		return false;
	}

	/// <summary>Takes the entire stack from a slot (the slot becomes empty). Empty stacks return empty.</summary>
	public ItemStack TakeFromSlot(int slotIndex)
	{
		if (slotIndex < 0 || slotIndex >= TotalSlots)
		{
			return ItemStack.From(0);
		}

		var taken = Slots[slotIndex];
		Slots[slotIndex] = ItemStack.From(0);
		RaiseChanged();
		return taken;
	}

	/// <summary>
	/// Takes up to <paramref name="count"/> items from a slot and returns them
	/// (fewer if the slot holds fewer). Empty slots / bad indices return empty.
	/// </summary>
	public ItemStack TakePartial(int slotIndex, int count)
	{
		if (slotIndex < 0 || slotIndex >= TotalSlots || count <= 0)
		{
			return ItemStack.From(0);
		}

		var slot = Slots[slotIndex];
		if (slot.IsEmpty)
		{
			return ItemStack.From(0);
		}

		int take = Math.Min(count, slot.Count);
		if (take >= slot.Count)
		{
			Slots[slotIndex] = ItemStack.From(0);
			RaiseChanged();
			return slot; // the whole remainder is the slot's stack
		}

		var taken = ItemStack.From(slot.MaterialId, take);
		slot.Count -= take;
		RaiseChanged();
		return taken;
	}

	/// <summary>Swaps two slots (empty slots swap too).</summary>
	public bool SwapSlots(int a, int b)
	{
		if (a < 0 || a >= TotalSlots || b < 0 || b >= TotalSlots || a == b)
		{
			return false;
		}

		var tmp = Slots[a];
		Slots[a] = Slots[b];
		Slots[b] = tmp;
		RaiseChanged();
		return true;
	}

	/// <summary>
	/// Minecraft-style move of the stack in <paramref name="fromSlot"/> onto
	/// <paramref name="toSlot"/>:
	/// - target empty: move the whole stack;
	/// - same material with room: move as many as fit (the remainder stays in the
	///   source slot);
	/// - different material: swap.
	/// Returns the leftover that could not be moved (empty when it all moved).
	/// </summary>
	public ItemStack MoveStack(int fromSlot, int toSlot)
	{
		if (fromSlot < 0 || fromSlot >= TotalSlots || toSlot < 0 || toSlot >= TotalSlots || fromSlot == toSlot)
		{
			return ItemStack.From(0);
		}

		var source = Slots[fromSlot];
		if (source.IsEmpty)
		{
			return ItemStack.From(0);
		}

		var target = Slots[toSlot];

		if (target.IsEmpty)
		{
			Slots[toSlot] = source;
			Slots[fromSlot] = ItemStack.From(0);
			RaiseChanged();
			return ItemStack.From(0);
		}

		if (target.MaterialId == source.MaterialId)
		{
			int space = ItemStack.StackLimit - target.Count;
			if (space <= 0)
			{
				return ItemStack.From(0); // target full: nothing moves
			}

			int move = Math.Min(space, source.Count);
			target.Count += move;
			if (move >= source.Count)
			{
				Slots[fromSlot] = ItemStack.From(0);
			}
			else
			{
				source.Count -= move;
			}
			RaiseChanged();
			return ItemStack.From(0);
		}

		// Different material: swap.
		Slots[fromSlot] = target;
		Slots[toSlot] = source;
		RaiseChanged();
		return ItemStack.From(0);
	}

	// ------------------------------------------------------------------
	// Save / restore (world persistence)
	// ------------------------------------------------------------------

	/// <summary>
	/// Serializes the 82 slots into parallel (materialId, count) arrays, one
	/// entry per slot in order (hotbar 0..9 then storage 10..81). Pure data —
	/// the <c>WorldSave</c> service writes these straight into the save file.
	/// </summary>
	public (int[] MaterialIds, int[] Counts) SaveState()
	{
		var ids = new int[TotalSlots];
		var counts = new int[TotalSlots];
		for (int i = 0; i < TotalSlots; i++)
		{
			ids[i] = Slots[i].MaterialId;
			counts[i] = Slots[i].Count;
		}
		return (ids, counts);
	}

	/// <summary>
	/// Restores the 82 slots from parallel (materialId, count) arrays (the
	/// inverse of <see cref="SaveState"/>). Slots whose arrays are shorter are
	/// left empty; a trailing <c>InventoryChanged</c> fires so the UI refreshes.
	/// </summary>
	public void LoadState(int[] materialIds, int[] counts)
	{
		int n = Math.Min(TotalSlots, Math.Min(materialIds?.Length ?? 0, counts?.Length ?? 0));
		for (int i = 0; i < TotalSlots; i++)
		{
			if (i < n && materialIds[i] != 0 && counts[i] > 0)
			{
				var stack = ItemStack.From(materialIds[i], counts[i]);
				// Reconstruct a tool's ToolType from its material id: tool ids
				// (see VoxelMaterialIds.ToolFirst..ToolLast) are the ToolType
				// enum values themselves, and SaveState only persisted the id +
				// count (not the ToolType), so we recover it here. Without this,
				// a saved tool would load as a plain, unrecognizable material.
				if (FactorySim.Voxel.VoxelMaterialIds.IsToolId(materialIds[i])
					&& Enum.TryParse<ToolType>(materialIds[i].ToString(), out var tool))
				{
					stack.ToolType = tool;
					stack.Count = 1; // tools are single items, never stacked
				}
				// Same reconstruction for machines: machine ids are the MachineType
				// enum values, so a saved machine item recovers its MachineType
				// here (and is single-counted like a tool).
				else if (FactorySim.Voxel.VoxelMaterialIds.IsMachineId(materialIds[i])
					&& Enum.TryParse<MachineType>(materialIds[i].ToString(), out var machine))
				{
					stack.MachineType = machine;
					stack.Count = 1; // machines are single items, never stacked
				}
				// A torch's LightLevel is an intrinsic item property (always
				// TorchLightLevel), not per-instance state, so it is not persisted
				// in the (id, count) inventory format — reconstruct it here the
				// way tools/machines reconstruct their type, so a saved torch
				// still emits block light when re-placed.
				if (materialIds[i] == FactorySim.Voxel.VoxelMaterialIds.Torch)
				{
					stack.LightLevel = FactorySim.Items.ItemRegistry.TorchLightLevel;
				}
				Slots[i] = stack;
			}
			else
			{
				Slots[i] = ItemStack.From(0);
			}
		}
		RaiseChanged();
	}

	/// <summary>
	/// Fires <see cref="InventoryChanged"/> so the UI refreshes. Exposed so
	/// external systems that mutate a slot directly (e.g. the tool's place
	/// ability consuming one item, or the dev panel granting an item) can
	/// notify the UI without duplicating the slot-mutation logic.
	/// </summary>
	public void RaiseChangedPublic()
	{
		RaiseChanged();
	}

	/// <summary>Flips <see cref="IsOpen"/> and fires <see cref="InventoryChanged"/> + <see cref="Toggled"/>.</summary>
	public void Toggle()
	{
		SetOpen(!IsOpen);
	}

	/// <summary>
	/// Sets the open state explicitly. Fires <see cref="InventoryChanged"/> (so
	/// the UI refreshes) and <see cref="Toggled"/> (so the Player can
	/// freeze/unfreeze itself and the UI can manage the mouse). No-op when the
	/// value is unchanged.
	/// </summary>
	public void SetOpen(bool open)
	{
		if (IsOpen == open)
		{
			return;
		}
		IsOpen = open;
		RaiseChanged();
		Toggled?.Invoke(open);
	}

	private void RaiseChanged()
	{
		InventoryChanged?.Invoke();
	}
}
