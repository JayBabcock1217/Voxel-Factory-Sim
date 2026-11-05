using Godot;
using System;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The SHARED inventory-slot widget + drag-and-drop engine. Both the player's
/// <see cref="InventoryUI"/> (the main 82-slot grid + hotbar) and the
/// <see cref="MachineUI"/> (a machine's recipe-driven input/output slots) build
/// their slots through this, so a slot behaves IDENTICALLY everywhere:
///
///   - Left-click: pick up / place / merge / swap (Minecraft single-click rules)
///   - Left-click-drag: pick up on press, drop on release over a slot
///   - Right-click: split (take half a stack, or place 1 item)
///
/// A slot is backed by an <see cref="ISlotStore"/> — an abstraction over
/// "where the stack lives" (the player's <see cref="Inventory"/> for the main
/// grid, or a machine's <see cref="FactorySim.Machines.MachineBuffer"/> for the
/// machine UI). The store also gates which stacks a slot ACCEPTS (e.g. a
/// machine input slot only accepts its bound material), so the same widget
/// enforces per-slot rules without special-casing.
///
/// The slot is a self-contained <see cref="Control"/> that handles its OWN
/// <c>GuiInput</c> (left press/release, right press) — no full-screen root or
/// manual hit-testing needed. The cursor stack is shared (one for the whole
/// game), so a drag carries across the inventory grid and the machine slots.
/// </summary>
public static class InventorySlotGrid
{
	// ------------------------------------------------------------------
	// Slot backing
	// ------------------------------------------------------------------

	/// <summary>
	/// The storage a slot reads/writes its stack from. Implemented by the player's
	/// <see cref="Inventory"/> (main grid) and by a machine buffer (machine UI).
	/// </summary>
	public interface ISlotStore
	{
		/// <summary>The stack currently in the slot (empty when none).</summary>
		ItemStack GetStack();

		/// <summary>Sets the slot's stack (the whole stack, or empty).</summary>
		void SetStack(ItemStack stack);

		/// <summary>
		/// True when the slot can accept <paramref name="stack"/> (the material
		/// matches the slot's binding, and there is room). Used to gate drops and
		/// merges so a slot never takes a stack it isn't meant to hold.
		/// </summary>
		bool CanAccept(ItemStack stack);

		/// <summary>Takes the slot's whole stack out, leaving it empty.</summary>
		ItemStack TakeFromSlot();

		/// <summary>Takes up to <paramref name="count"/> of the slot's stack, leaving the rest.</summary>
		ItemStack TakePartial(int count);

		/// <summary>
		/// Inserts <paramref name="stack"/> into the slot (merging into a matching
		/// stack or filling the empty slot), up to what fits. Returns the number
		/// of items actually inserted (0 when nothing fit). The input stack is NOT
		/// mutated — the caller keeps the remainder on the cursor.
		/// </summary>
		int CollectItem(ItemStack stack);
	}

	/// <summary>
	/// The player's <see cref="Inventory"/> as an <see cref="ISlotStore"/> for a
	/// single slot index. The inventory's main grid uses one of these per slot.
	/// </summary>
	public sealed class InventorySlotStore : ISlotStore
	{
		private readonly Inventory _inv;
		private readonly int _index;

		public InventorySlotStore(Inventory inv, int index)
		{
			_inv = inv;
			_index = index;
		}

		public ItemStack GetStack() => _inv.Slots[_index];
		public void SetStack(ItemStack stack) => _inv.Slots[_index] = stack ?? ItemStack.From(0);
		public bool CanAccept(ItemStack stack)
		{
			if (stack == null || stack.IsEmpty)
			{
				return false;
			}
			var cur = _inv.Slots[_index];
			return cur.IsEmpty || cur.MaterialId == stack.MaterialId;
		}
		public ItemStack TakeFromSlot() => _inv.TakeFromSlot(_index);
		public ItemStack TakePartial(int count) => _inv.TakePartial(_index, count);
		public int CollectItem(ItemStack stack)
		{
			if (stack == null || stack.IsEmpty)
			{
				return 0;
			}
			var cur = _inv.Slots[_index];
			if (!cur.IsEmpty && cur.MaterialId != stack.MaterialId)
			{
				return 0; // different material, slot occupied (the swap path handles this)
			}
			int space = cur.IsEmpty ? ItemStack.StackLimit : ItemStack.StackLimit - cur.Count;
			int add = Math.Min(space, stack.Count);
			if (add <= 0)
			{
				return 0;
			}
			if (cur.IsEmpty)
			{
				_inv.Slots[_index] = ItemStack.From(stack.MaterialId, add);
			}
			else
			{
				cur.Count += add;
			}
			return add;
		}
	}

	// ------------------------------------------------------------------
	// Slot construction
	// ------------------------------------------------------------------

	/// <summary>
	/// Builds one interactive slot <see cref="Control"/>. The slot is a full-rect
	/// Control (MouseFilter.Stop so it consumes clicks) with a dark background,
	/// the icon, and the count label. It wires its own GuiInput to the
	/// left-click / right-click / drag rules. The caller positions and parents it.
	/// </summary>
	/// <param name="store">The storage the slot reads/writes (null = an inert slot).</param>
	/// <param name="slotSize">The slot's square size in pixels (default 48).</param>
	public static Control CreateSlot(ISlotStore store, float slotSize = 48f)
	{
		var slot = new Control
		{
			Name = "Slot",
			LayoutMode = 3, // Position
			Size = new Vector2(slotSize, slotSize),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		var bg = new ColorRect
		{
			Name = "SlotBg",
			LayoutMode = 3,
			Color = new Color(0.08f, 0.08f, 0.1f, 0.85f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		bg.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		slot.AddChild(bg);

		var icon = new TextureRect
		{
			Name = "Icon",
			LayoutMode = 3,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		icon.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		slot.AddChild(icon);

		var count = new Label
		{
			Name = "Count",
			LayoutMode = 3,
			Text = "",
			MouseFilter = Control.MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
		};
		count.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		count.AddThemeColorOverride("font_color", Colors.White);
		count.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
		count.AddThemeConstantOverride("outline_size", 3);
		slot.AddChild(count);

		if (store != null)
		{
			// Capture the store + visual nodes in the lambda so the handler can
			// refresh the slot and route the drag-release to THIS slot.
			ISlotStore s = store;
			TextureRect ic = icon;
			Label ct = count;
			slot.GuiInput += ev => OnSlotGuiInput(ev, s, ic, ct, slot);
			RefreshSlot(s, icon, count);
		}
		return slot;
	}

	/// <summary>
	/// Refreshes a slot's icon + count from its store. Call this after any change
	/// to the underlying stack (or every frame for live-updating slots).
	/// </summary>
	public static void RefreshSlot(ISlotStore store, TextureRect icon, Label count)
	{
		if (store == null)
		{
			return;
		}
		var stack = store.GetStack();
		if (icon != null)
		{
			icon.Texture = stack.IsEmpty ? null : ItemIconTexture(stack.MaterialId);
		}
		if (count != null)
		{
			count.Text = stack.IsEmpty ? "" : stack.Count.ToString();
		}
	}

	// ------------------------------------------------------------------
	// Slot input (the Minecraft-style rules)
	// ------------------------------------------------------------------

	/// <summary>
	/// The slot's GuiInput handler. Left press begins a drag (and applies the
	/// left-click rules); left release over a slot drops the cursor there; right
	/// press applies the right-click rules.
	/// </summary>
	private static void OnSlotGuiInput(InputEvent @event, ISlotStore store, TextureRect icon, Label count, Control slot)
	{
		if (!(@event is InputEventMouseButton mouse))
		{
			return;
		}

		if (mouse.Pressed)
		{
			if (mouse.ButtonIndex == MouseButton.Left)
			{
				if (mouse.ShiftPressed)
				{
					// SHIFT+CLICK: quick-move the slot's whole stack out of this
					// grid and into the player's inventory (merge then empty
					// slots). For a machine slot this is the "take the product /
					// pull the input back out" action; the remainder (if the
					// inventory is full) stays in the slot. No drag/cursor
					// involved.
					ShiftClickSlot(store, icon, count);
					return;
				}
				_pressSlot = slot;
				LeftClickSlot(store, icon, count);
			}
			else if (mouse.ButtonIndex == MouseButton.Right)
			{
				RightClickSlot(store, icon, count);
			}
		}
		else if (mouse.ButtonIndex == MouseButton.Left && slot == _pressSlot)
		{
			// Left release over the SAME slot that was pressed: if the cursor
			// holds a stack and it differs from the slot's content, place/merge/
			// swap it in. (A release over a DIFFERENT slot is handled by THAT
			// slot's press path, since each slot only sees its own events.)
			if (!CursorStack.IsEmpty)
			{
				PlaceCursorInto(store, icon, count);
			}
			_pressSlot = null;
		}
	}

	/// <summary>
	/// The left-click rules:
	/// - cursor empty: pick up the slot's whole stack;
	/// - same material: move as many as fit into the slot (remainder on cursor);
	/// - different material: swap cursor and slot (only if the slot can accept it).
	/// </summary>
	private static void LeftClickSlot(ISlotStore store, TextureRect icon, Label count)
	{
		var slotStack = store.GetStack();

		if (CursorStack.IsEmpty)
		{
			if (slotStack.IsEmpty)
			{
				return;
			}
			CursorStack = store.TakeFromSlot();
		}
		else if (slotStack.IsEmpty && store.CanAccept(CursorStack))
		{
			int placed = store.CollectItem(CursorStack);
			if (placed > 0)
			{
				CursorStack = ItemStack.From(CursorStack.MaterialId, CursorStack.Count - placed);
			}
		}
		else if (slotStack.MaterialId == CursorStack.MaterialId && store.CanAccept(CursorStack))
		{
			int placed = store.CollectItem(CursorStack);
			if (placed > 0)
			{
				CursorStack = ItemStack.From(CursorStack.MaterialId, CursorStack.Count - placed);
			}
		}
		else if (!slotStack.IsEmpty && store.CanAccept(CursorStack))
		{
			// Different material with room: swap.
			store.SetStack(CursorStack);
			CursorStack = slotStack;
		}
		else
		{
			return; // nothing fits the left-click rules for this slot
		}

		RefreshSlot(store, icon, count);
		RefreshCursor();
	}

	/// <summary>
	/// The right-click rules:
	/// - cursor empty: take half the slot's stack (1 if it is a single);
	/// - slot empty: place 1 item from the cursor (if it can accept it);
	/// - same material with room: place 1 item from the cursor.
	/// </summary>
	private static void RightClickSlot(ISlotStore store, TextureRect icon, Label count)
	{
		var slotStack = store.GetStack();

		if (CursorStack.IsEmpty)
		{
			if (slotStack.IsEmpty)
			{
				return;
			}
			int take = Math.Max(1, slotStack.Count / 2);
			CursorStack = store.TakePartial(take);
		}
		else if (slotStack.IsEmpty && store.CanAccept(CursorStack))
		{
			int placed = store.CollectItem(ItemStack.From(CursorStack.MaterialId, 1));
			if (placed > 0)
			{
				CursorStack = ItemStack.From(CursorStack.MaterialId, CursorStack.Count - 1);
			}
		}
		else if (slotStack.MaterialId == CursorStack.MaterialId && store.CanAccept(CursorStack))
		{
			int placed = store.CollectItem(ItemStack.From(CursorStack.MaterialId, 1));
			if (placed > 0)
			{
				CursorStack = ItemStack.From(CursorStack.MaterialId, CursorStack.Count - 1);
			}
		}
		else
		{
			return; // nothing fits the right-click rules for this slot
		}

		RefreshSlot(store, icon, count);
		RefreshCursor();
	}

	/// <summary>
	/// Shift+click rule: take the slot's whole stack and quick-move it into the
	/// player's inventory (merging into same-material stacks, then empty slots).
	/// Used by machine slots (the machine UI) so the player can pull an item
	/// straight out of the machine in one click, and by any shared-grid slot.
	/// The remainder (when the inventory can't hold it all) is left in the slot.
	/// </summary>
	private static void ShiftClickSlot(ISlotStore store, TextureRect icon, Label count)
	{
		var slotStack = store.GetStack();
		if (slotStack == null || slotStack.IsEmpty)
		{
			return;
		}

		// Resolve the player's inventory (the model in the "inventory" group).
		var tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null)
		{
			return;
		}
		var inv = tree.GetFirstNodeInGroup("inventory") as Inventory;
		if (inv == null)
		{
			return;
		}

		// Take the whole stack out of the slot, then collect as much as the
		// inventory can hold; whatever doesn't fit goes back into the slot.
		var stack = store.TakeFromSlot();
		int collected = inv.CollectItem(stack);
		if (collected < stack.Count)
		{
			store.SetStack(ItemStack.From(stack.MaterialId, stack.Count - collected));
		}

		RefreshSlot(store, icon, count);
	}

	/// <summary>
	/// The drop-end rule: place the cursor stack into <paramref name="store"/>
	/// (place / merge / swap). Called on left release over the pressed slot.
	/// </summary>
	private static void PlaceCursorInto(ISlotStore store, TextureRect icon, Label count)
	{
		var slotStack = store.GetStack();

		if (slotStack.IsEmpty)
		{
			if (store.CanAccept(CursorStack))
			{
				int placed = store.CollectItem(CursorStack);
				if (placed > 0)
				{
					CursorStack = ItemStack.From(CursorStack.MaterialId, CursorStack.Count - placed);
				}
			}
		}
		else if (slotStack.MaterialId == CursorStack.MaterialId)
		{
			int placed = store.CollectItem(CursorStack);
			if (placed > 0)
			{
				CursorStack = ItemStack.From(CursorStack.MaterialId, CursorStack.Count - placed);
			}
		}
		else if (store.CanAccept(CursorStack))
		{
			store.SetStack(CursorStack);
			CursorStack = slotStack;
		}
		else
		{
			return; // the slot can't take the cursor's material
		}

		RefreshSlot(store, icon, count);
		RefreshCursor();
	}

	// ------------------------------------------------------------------
	// Shared cursor stack (one for the whole game, so a drag carries across the
	// inventory grid and the machine slots)
	// ------------------------------------------------------------------

	/// <summary>The stack currently on the cursor (empty when not dragging).</summary>
	public static ItemStack CursorStack { get; set; } = ItemStack.From(0);

	/// <summary>The slot a left-drag was pressed on (null when not dragging).</summary>
	private static Control _pressSlot;

	/// <summary>The cursor visual (set by the InventoryUI so the grid can refresh it).</summary>
	private static Control _cursorControl;
	private static TextureRect _cursorIcon;
	private static Label _cursorCount;

	/// <summary>
	/// Wires the shared cursor visual (the InventoryUI's cursor stack control) so
	/// the grid can show/hide it and set its icon/count. Call once from the
	/// inventory after it builds its cursor.
	/// </summary>
	public static void SetCursorVisual(Control cursorControl, TextureRect cursorIcon, Label cursorCount)
	{
		_cursorControl = cursorControl;
		_cursorIcon = cursorIcon;
		_cursorCount = cursorCount;
		RefreshCursor();
	}

	private static void RefreshCursor()
	{
		if (_cursorControl == null)
		{
			return;
		}
		_cursorControl.Visible = !CursorStack.IsEmpty;
		if (CursorStack.IsEmpty)
		{
			return;
		}
		if (_cursorIcon != null)
		{
			_cursorIcon.Texture = ItemIconTexture(CursorStack.MaterialId);
		}
		if (_cursorCount != null)
		{
			_cursorCount.Text = CursorStack.Count.ToString();
		}
	}

	/// <summary>Clears the cursor stack (e.g. when a UI closes).</summary>
	public static void ClearCursor()
	{
		CursorStack = ItemStack.From(0);
		_pressSlot = null;
		RefreshCursor();
	}

	// ------------------------------------------------------------------
	// Item icon (shared by the inventory grid and the machine slots)
	// ------------------------------------------------------------------

	/// <summary>
	/// The icon texture for a material id: the terrain's own texture when one is
	/// configured (so the icon matches what shows up on the terrain), or a flat
	/// color swatch otherwise (tools/machines get their own swatch color). This
	/// is the single place that decides how an item is drawn as an icon.
	/// </summary>
	public static Texture2D ItemIconTexture(int materialId)
	{
		// Terrain materials with a configured texture use the same texture on the
		// terrain AND as the item icon.
		if (VoxelWorld.Current is { } world
			&& world.Palette != null
			&& !VoxelMaterialIds.IsToolId(materialId)
			&& !VoxelMaterialIds.IsMachineId(materialId))
		{
			var tex = world.Palette.GetTexture(materialId);
			if (tex != null)
			{
				return tex;
			}
		}

		// No texture (or a tool/machine): draw a flat color swatch.
		var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		img.Fill(LookUpColor(materialId));
		return ImageTexture.CreateFromImage(img);
	}

	/// <summary>
	/// The material's albedo from the world palette (grey fallback when
	/// unconfigured); tools and machines get their own swatch colors.
	/// </summary>
	public static Color LookUpColor(int materialId)
	{
		if (VoxelMaterialIds.IsToolId(materialId))
		{
			if (Enum.TryParse<ToolType>(materialId.ToString(), out var tool))
			{
				return ItemRegistry.ToolColor(tool);
			}
			return new Color(0.6f, 0.6f, 0.6f);
		}
		if (VoxelMaterialIds.IsMachineId(materialId))
		{
			if (Enum.TryParse<FactorySim.Machines.MachineType>(materialId.ToString(), out var machine))
			{
				return ItemRegistry.MachineColor(machine);
			}
			return new Color(0.6f, 0.6f, 0.6f);
		}

		if (VoxelWorld.Current is { } world && world.Palette != null)
		{
			return world.Palette.GetAlbedoColor(materialId);
		}
		return new Color(0.5f, 0.5f, 0.5f);
	}
}
