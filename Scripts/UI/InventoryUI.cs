using Godot;
using System;
using System.Collections.Generic;
using FactorySim;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The Minecraft-style inventory screen: a full-screen <see cref="CanvasLayer"/>
/// built entirely in code (no .tscn), shown/hidden with TAB.
///
/// Layout: a centered panel with 144 slots — a 6×20 grid of storage
/// (inventory slots 24-143) above a 24-slot hotbar row (slots 0-23) — plus a
/// cursor stack that follows the mouse while dragging. A persistent 24-slot
/// hotbar is also rendered at the bottom of the screen (always visible) with a
/// selection highlight; number keys and the mouse wheel change the selected slot.
///
/// Interactions (per slot):
/// - Left-click: pick up / place / merge / swap (Minecraft single-click rules)
/// - Left-click-drag: pick up on press, drop on release over a slot (or keep
///   on the cursor when released elsewhere)
/// - Right-click: split (take half a stack, or place 1 item)
///
/// While open the mouse is released and the UI root eats all clicks; while
/// hidden the root's MouseFilter is Ignore so the 3D view is untouched. The
/// player's click-to-recapture is suppressed while <c>IsOpen</c> (see
/// <c>Player.cs</c>).
/// </summary>
public partial class InventoryUI : CanvasLayer
{
	private const float SlotSize = 48f;
	private const float SlotGap = 4f;

	private Inventory _inventory;
	private VoxelWorld _world;

	private Control _root;
	private Panel _panel;
	private readonly Control[] _slotControls = new Control[Inventory.TotalSlots];
	private readonly TextureRect[] _slotIcons = new TextureRect[Inventory.TotalSlots];
	private readonly Label[] _slotCounts = new Label[Inventory.TotalSlots];

	private Control _cursor;
	private TextureRect _cursorIcon;
	private Label _cursorCount;
	private ItemStack _cursorStack = ItemStack.From(0);

	private Panel _tooltip;
	private Label _tooltipName;
	private Label _tooltipCount;
	private int _tooltipSlot = -1;

	// Crafting pane (right side of the inventory panel): a tabbed, scrollable
	// list of recipes. The CraftingDatabase is the single source of truth for
	// "how many can I make"; the pane just renders it and dispatches crafts.
	private CraftingDatabase _crafting;
	private Control _craftPanel;
	private HBoxContainer _craftTabs;
	private ScrollContainer _craftScroll;
	private VBoxContainer _craftList;
	private readonly List<CraftingRecipe> _visibleRecipes = new();
	private readonly Dictionary<CraftingRecipe, TextureRect> _recipeIcons = new();
	private readonly Dictionary<CraftingRecipe, Label> _recipeNames = new();
	private readonly Dictionary<CraftingRecipe, Label> _recipeCounts = new();
	private readonly Dictionary<CraftingRecipe, Button> _recipeButtons = new();
	private string _craftCategory = "";
	private CraftingRecipe _hoveredRecipe;
	private CraftingRecipe _tooltipRecipe;
	private readonly Dictionary<CraftingRecipe, Control> _recipeHoverRects = new();

	// Crafting queue: clicking a recipe enqueues a job; _Process advances it.
	// Only one job crafts at a time (like a single crafting station); each job
	// takes CraftTime seconds and, on completion, consumes ingredients and adds
	// the product to the inventory.
	private readonly Queue<(CraftingRecipe Recipe, int Amount)> _craftQueue = new();
	private (CraftingRecipe Recipe, int Amount)? _activeJob;
	private double _jobElapsed;
	private ColorRect _queueProgressBg;
	private ColorRect _queueProgressFill;
	private Label _queueLabel;

	private Panel _recipeTooltip;
	private VBoxContainer _recipeTipContent;
	private Label _recipeTipName;
	private Label _recipeTipDesc;
	private VBoxContainer _recipeTipIngredients;

	// Dev pane (left side of the inventory panel): a scrollable list of EVERY
	// item in the game (all placeable materials + all tools). Clicking an entry
	// grants one of that item into the inventory (a full material stack, or a
	// single tool). It is the debug/god-mode way to give yourself any item so
	// you can test placement, breaking, and tool affinities without crafting.
	private Panel _devPanel;
	private VBoxContainer _devList;
	private readonly List<FactorySim.Items.ItemEntry> _devItems = new();
	// Dev-pane hover tooltip: shows the item's name + description when a dev
	// row is hovered (mirrors the recipe tooltip). The row->button map is used
	// to hit-test which row the mouse is over.
	private Panel _devTooltip;
	private VBoxContainer _devTipContent;
	private Label _devTipName;
	private Label _devTipDesc;
	private readonly Dictionary<FactorySim.Items.ItemEntry, Control> _devHoverRects = new();
	// The item the dev tooltip is currently showing (null when hidden), so the
	// content is only re-filled when the hovered item actually changes.
	private FactorySim.Items.ItemEntry? _devTipItem;

	// Persistent hotbar (always visible at the bottom of the screen).
	private Panel _hotbar;
	private readonly Control[] _hotbarSlots = new Control[Inventory.HotbarSize];
	private readonly TextureRect[] _hotbarIcons = new TextureRect[Inventory.HotbarSize];
	// A small tooltip label just above the hotbar naming the item in the
	// currently selected slot (the player's "held" item). Updated whenever the
	// selection or that slot's contents change.
	private Label _hotbarTooltip;
	private readonly Label[] _hotbarCounts = new Label[Inventory.HotbarSize];
	// Per-slot selection highlight: the ColorRect behind each slot's icon. The
	// selected slot's background is tinted greenish; the rest use the dark base.
	private readonly ColorRect[] _hotbarSlotBg = new ColorRect[Inventory.HotbarSize];
	// The two background colors used for the selection tint (unselected vs. selected).
	private static readonly Color HotbarBgUnselected = new Color(0.1f, 0.1f, 0.12f, 0.7f);
	private static readonly Color HotbarBgSelected = new Color(0.22f, 0.55f, 0.28f, 0.85f);
	private int _selectedHotbarSlot;

	private int _dragSlot = -1;
	private bool _dirty;

	public override void _Ready()
	{
		AddToGroup("inventory_ui");
		// Defer setup: Godot calls _Ready() bottom-up, so this CanvasLayer's
		// _Ready runs before the Inventory node's _Ready (which is where it
		// joins the "inventory" group). CallDeferred pushes Init() to after
		// the entire _Ready() pass, so the group lookup succeeds.
		CallDeferred(nameof(Init));
	}

	private void Init()
	{
		_inventory = GetTree().GetFirstNodeInGroup("inventory") as Inventory;
		_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;
		if (_inventory == null)
		{
			GD.PushError("[InventoryUI] No Inventory node in group 'inventory' — inventory UI disabled.");
			Visible = false;
			return;
		}
		// Crafting: the database tracks total ingredients in the inventory.
		_crafting = CraftingDatabase.BuildDefaultDatabase();
		_crafting.RebuildInventory(_inventory);
		// Any inventory change invalidates both the slot visuals AND the
		// crafting "possible count", so one dirty flag drives both.
		_inventory.InventoryChanged += () => _dirty = true;
		_inventory.Toggled += SetOpenVisuals;

		BuildUi();
		RefreshHotbar();
		BuildCrafting();
		BuildDevPanel();
		RefreshCrafting();
	}

	public override void _Process(double delta)
	{
		// Refresh slot visuals only when something actually changed.
		if (_dirty)
		{
			_dirty = false;
			RefreshAllSlots();
			RefreshHotbar();
		}

		if (_cursor != null && _cursorStack.IsEmpty == false)
		{
			// Center the cursor icon under the mouse (viewport coords == layer
			// coords for a default CanvasLayer).
			_cursor.Position = GetViewport().GetMousePosition() - new Vector2(SlotSize / 2, SlotSize / 2);
		}

		UpdateTooltip();
		UpdateRecipeTooltip();
		UpdateDevTooltip();
		ProcessCraftQueue(delta);
	}

	/// <summary>
	/// Shows the tooltip when the mouse hovers over a populated slot (and no
	/// stack is on the cursor), hides it otherwise. The tooltip follows the
	/// mouse with a small offset so it doesn't sit under the cursor.
	/// </summary>
	private void UpdateTooltip()
	{
		if (_tooltip == null || !IsOpen())
		{
			if (_tooltip != null)
			{
				_tooltip.Visible = false;
			}
			return;
		}

		Vector2 mousePos = GetViewport().GetMousePosition();
		int slot = SlotAt(mousePos);

		// Show the tooltip only when hovering a populated slot and the cursor
		// is empty (no drag in progress).
		bool show = slot >= 0 && _cursorStack.IsEmpty;
		if (show)
		{
			var stack = _inventory.Slots[slot];
			show = !stack.IsEmpty;
		}

		if (!show)
		{
			_tooltip.Visible = false;
			_tooltipSlot = -1;
			return;
		}

		var hoveredStack = _inventory.Slots[slot];

		// Only refresh the text when the hovered slot changes.
		if (slot != _tooltipSlot)
		{
			_tooltipSlot = slot;
			_tooltipName.Text = LookUpMaterialName(hoveredStack.MaterialId);
			_tooltipCount.Text = $"x{hoveredStack.Count}";
			// Size the tooltip to fit its text. GetCombinedMinimumSize gives
			// the label's actual rendered size for the current text.
			Vector2 nameSize = _tooltipName.GetCombinedMinimumSize();
			Vector2 countSize = _tooltipCount.GetCombinedMinimumSize();
			float marginLeft = 8f, marginTop = 4f, gap = 2f;
			// Position the count label just below the name.
			_tooltipCount.Position = new Vector2(marginLeft, marginTop + nameSize.Y + gap);
			_tooltip.Size = new Vector2(
				Mathf.Max(nameSize.X, countSize.X) + marginLeft * 2,
				marginTop + nameSize.Y + gap + countSize.Y + 4f); // +4 = bottom margin
		}

		// Position: 16px to the right and 16px below the mouse, clamped so the
		// tooltip stays on screen.
		Vector2 tooltipPos = mousePos + new Vector2(16f, 16f);
		Vector2 vpSize = GetViewport().GetVisibleRect().Size;
		tooltipPos.X = Mathf.Min(tooltipPos.X, vpSize.X - _tooltip.Size.X - 4f);
		tooltipPos.Y = Mathf.Min(tooltipPos.Y, vpSize.Y - _tooltip.Size.Y - 4f);
		_tooltip.Position = tooltipPos;
		_tooltip.Visible = true;
	}

	/// <summary>True while the inventory is open.</summary>
	private bool IsOpen()
		=> _inventory != null && _inventory.IsOpen;

	/// <summary>
	/// The index (0..HotbarSize-1) of the currently selected hotbar slot. This
	/// is the slot whose item is "active" in the player's hand — the tool
	/// system reads it to decide which tool breaks blocks, and the place
	/// ability reads it to decide which material to place.
	/// </summary>
	public int SelectedHotbarSlot => _selectedHotbarSlot;

	/// <summary>
	/// The item currently in the selected hotbar slot (empty when that slot is
	/// empty or the UI isn't wired to an inventory yet). This is the player's
	/// "held" item: a tool (shovel/pickaxe/...) or a placeable material.
	/// </summary>
	public ItemStack SelectedHotbarItem
	{
		get
		{
			if (_inventory == null)
			{
				return ItemStack.From(0);
			}
			int slot = Mathf.Clamp(_selectedHotbarSlot, 0, Inventory.HotbarSize - 1);
			return _inventory.Slots[slot];
		}
	}

	/// <summary>
	/// Cycles the selected hotbar slot by <paramref name="delta"/> (wraps).
	/// Exposed so the 3D-world input path (the VoxelTool, which is the node
	/// actually receiving wheel / number-key events while the mouse is captured
	/// and the inventory is closed) can drive the selection. No-op while the
	/// inventory is open (the UI owns the selection then).
	/// </summary>
	public void CycleHotbarFromWorld(int delta)
	{
		CycleHotbar(delta);
	}

	/// <summary>
	/// Sets the selected hotbar slot directly (0..HotbarSize-1). Exposed for the
	/// 3D-world number-key path, same rationale as
	/// <see cref="CycleHotbarFromWorld"/>. No-op while the inventory is open or
	/// the index is out of range.
	/// </summary>
	public void SelectHotbarSlotFromWorld(int index)
	{
		SelectHotbarSlot(index);
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!(@event is InputEventKey key && key.Pressed && !key.Echo))
		{
			return;
		}

		if (key.Keycode == Key.Tab || Input.IsActionJustPressed("inventory_toggle"))
		{
			// TAB always toggles, regardless of mouse mode.
			ToggleOpen();
			return;
		}

		// Note: hotbar selection (number keys 1-9,0 and the mouse wheel) is
		// owned entirely by the VoxelTool's 3D-world input path — it is a player
		// action taken while the inventory is CLOSED (mouse captured). It is
		// deliberately NOT handled here: while the inventory is open the player
		// rearranges items by clicking, and there is no hotbar selection to
		// change. (See VoxelTool._Input, which drives
		// CycleHotbarFromWorld / SelectHotbarSlotFromWorld.)
	}

	/// <summary>Opens/closes the inventory: toggles the model, the panel, and the mouse.</summary>
	private void ToggleOpen()
	{
		if (_inventory == null)
		{
			return;
		}

		_inventory.Toggle();
		// SetOpenVisuals is also called from the Toggled handler (below); the
		// SetOpen no-op guard makes the second call a no-op.
		SetOpenVisuals(_inventory.IsOpen);
	}

	private void SetOpenVisuals(bool open)
	{
		// The CanvasLayer's own Visible gate was set to false in Init() to
		// hide the UI before the first toggle. It must be true for anything
		// on the layer to render, so flip it alongside the panel.
		Visible = true;

		if (_panel != null)
		{
			_panel.Visible = open;
		}
		// Hide the persistent hotbar while the inventory panel is open —
		// the panel's bottom row (slots 0-9) serves as the hotbar, so two
		// hotbars on screen at once would be confusing.
		if (_hotbar != null)
		{
			_hotbar.Visible = !open;
		}
		// The held-item name label sits just above the hotbar, so it hides with
		// it while the inventory panel is open (the panel's bottom row serves as
		// the hotbar then, and there is no standalone hotbar to label).
		if (_hotbarTooltip != null)
		{
			_hotbarTooltip.Visible = !open;
		}
		if (_root != null)
		{
			// Stop = eat clicks while open (the player must not click through);
			// Ignore = the hidden UI never blocks the 3D view.
			_root.MouseFilter = open ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
		}
		Input.MouseMode = open ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;

		// Freeze the player's movement while the inventory is open.
		if (GetTree().GetFirstNodeInGroup("player") is Player player)
		{
			player.SetInputEnabled(!open);
		}

		if (!open)
		{
			// A close cancels any in-flight drag; the stack stays on the cursor
			// until the user re-opens and places it (nothing is lost).
			_dragSlot = -1;
			RefreshCursor();
		}
	}

	// ------------------------------------------------------------------
	// UI construction (all in code — see VoxelTool's debug-label pattern)
	// ------------------------------------------------------------------

	private void BuildUi()
	{
		// Full-rect root; Ignore when hidden so it never blocks the 3D view.
		_root = new Control
		{
			Name = "InventoryRoot",
			LayoutMode = 3, // Position
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		AddChild(_root);
		_root.GuiInput += OnRootGuiInput;

		// Panel width is driven by the widest row (the 24-slot hotbar) PLUS the
		// dev pane on the left. Layout: [pad | dev pane | gap | slot grid | pad].
		float gridW = Inventory.HotbarSize * SlotSize + (Inventory.HotbarSize - 1) * SlotGap;
		float gridH = (Inventory.StorageRows + 1) * SlotSize + Inventory.StorageRows * SlotGap;
		const float devW = 240f;
		const float devGap = 16f;
		const float pad = 12f;
		float panelW = pad + devW + devGap + gridW + pad;
		float panelH = gridH + 56f;
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		Vector2 panelPos = new Vector2(
			Math.Max(0f, (viewportSize.X - panelW) / 2f),
			Math.Max(0f, (viewportSize.Y - panelH) / 2f));

		_panel = new Panel
		{
			Name = "InventoryPanel",
			LayoutMode = 3, // Position
			Position = panelPos,
			Size = new Vector2(panelW, panelH),
			Visible = false,
			// Ignore: let clicks pass through to the root, which does the
			// hit-testing via SlotAt(). A Panel's default is Stop, which
			// would consume every click before the root sees it.
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var panelStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.12f, 0.12f, 0.16f, 0.95f),
			BorderColor = new Color(0.35f, 0.35f, 0.4f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 12f,
			ContentMarginTop = 12f,
			ContentMarginRight = 12f,
			ContentMarginBottom = 12f,
		};
		_panel.AddThemeStyleboxOverride("panel", panelStyle);
		_root.AddChild(_panel);

		var title = new Label
		{
			Name = "Title",
			LayoutMode = 3, // Position
			Text = "Inventory  (TAB to close)",
			Position = new Vector2(pad, 8f),
			Size = new Vector2(panelW - pad * 2f, 20f),
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		title.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		_panel.AddChild(title);

		// Slot grid: rows 0..5 = storage (20 cols each, slots 24-143),
		// row 6 = hotbar (24 cols, slots 0-23). The grid is shifted right of the
		// dev pane: it starts at x = pad + devW + devGap. Storage rows are
		// centered within the grid's own width (the hotbar row is wider — 24).
		float gridX = pad + devW + devGap;
		float gridY = 36f;
		float storageRowW = Inventory.StorageCols * SlotSize + (Inventory.StorageCols - 1) * SlotGap;
		float storageOffsetX = (gridW - storageRowW) / 2f;
		for (int row = 0; row < Inventory.StorageRows + 1; row++)
		{
			bool isHotbarRow = row == Inventory.StorageRows;
			int cols = isHotbarRow ? Inventory.HotbarSize : Inventory.StorageCols;
			float rowX = isHotbarRow ? gridX : gridX + storageOffsetX;
			for (int col = 0; col < cols; col++)
			{
				int slotIndex = isHotbarRow
					? col
					: Inventory.HotbarSize + row * Inventory.StorageCols + col;
				CreateSlot(slotIndex, new Vector2(rowX + col * (SlotSize + SlotGap), gridY + row * (SlotSize + SlotGap)));
			}
		}

		// Cursor stack: follows the mouse, above everything, never blocks input.
		_cursor = new Control
		{
			Name = "CursorStack",
			LayoutMode = 3, // Position
			Size = new Vector2(SlotSize, SlotSize),
			ZIndex = 100,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_cursorIcon = new TextureRect
		{
			LayoutMode = 3, // Position
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_cursorIcon.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_cursor.AddChild(_cursorIcon);
		_cursorCount = new Label
		{
			Name = "CursorCount",
			LayoutMode = 3, // Position
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_cursorCount.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_cursorCount.AddThemeColorOverride("font_color", Colors.White);
		_cursorCount.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
		_cursorCount.AddThemeConstantOverride("outline_size", 3);
		_cursor.AddChild(_cursorCount);
		_root.AddChild(_cursor);

		// Hover tooltip: a small panel that follows the mouse when over a
		// populated slot, showing the material name and count.
		_tooltip = new Panel
		{
			Name = "Tooltip",
			LayoutMode = 3, // Position
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ZIndex = 200,
		};
		var tooltipStyle = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 0.85f),
			BorderColor = new Color(0.6f, 0.6f, 0.65f, 0.8f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 8f,
			ContentMarginTop = 4f,
			ContentMarginRight = 8f,
			ContentMarginBottom = 4f,
		};
		_tooltip.AddThemeStyleboxOverride("panel", tooltipStyle);

		_tooltipName = new Label
		{
			Name = "TooltipName",
			LayoutMode = 3, // Position
			Position = new Vector2(8f, 4f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_tooltipName.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		_tooltip.AddChild(_tooltipName);

		_tooltipCount = new Label
		{
			Name = "TooltipCount",
			LayoutMode = 3, // Position
			Position = new Vector2(8f, 24f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_tooltipCount.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
		_tooltip.AddChild(_tooltipCount);

		_root.AddChild(_tooltip);

		BuildHotbar();
	}

	/// <summary>
	/// Builds the persistent hotbar at the bottom center of the screen:
	/// 10 slots with a selection highlight. Always visible (independent of
	/// the inventory panel). Number keys 1-0 and the mouse wheel change the
	/// selected slot; selection is disabled while the inventory is open.
	/// </summary>
	private void BuildHotbar()
	{
		float hbSlotSize = 44f;
		float hbGap = 4f;
		float hbPad = 4f;
		float hbW = Inventory.HotbarSize * hbSlotSize + (Inventory.HotbarSize - 1) * hbGap + hbPad * 2;
		float hbH = hbSlotSize + hbPad * 2;
		Vector2 vpSize = GetViewport().GetVisibleRect().Size;
		Vector2 hbPos = new Vector2(
			(vpSize.X - hbW) / 2f,
			vpSize.Y - hbH - 12f); // 12px from the bottom edge

		_hotbar = new Panel
		{
			Name = "Hotbar",
			LayoutMode = 3, // Position
			Position = hbPos,
			Size = new Vector2(hbW, hbH),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var hbStyle = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 0.55f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = hbPad,
			ContentMarginTop = hbPad,
			ContentMarginRight = hbPad,
			ContentMarginBottom = hbPad,
		};
		_hotbar.AddThemeStyleboxOverride("panel", hbStyle);
		_root.AddChild(_hotbar);

		for (int i = 0; i < Inventory.HotbarSize; i++)
		{
			var slot = new Control
			{
				Name = $"HotbarSlot_{i}",
				LayoutMode = 3, // Position
				Position = new Vector2(hbPad + i * (hbSlotSize + hbGap), hbPad),
				Size = new Vector2(hbSlotSize, hbSlotSize),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			// Per-slot background. Tinted greenish when the slot is selected,
			// dark base otherwise. Kept in _hotbarSlotBg so RefreshHotbar can
			// recolor it as the selection moves.
			_hotbarSlotBg[i] = new ColorRect
			{
				Color = HotbarBgUnselected,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			_hotbarSlotBg[i].SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
			slot.AddChild(_hotbarSlotBg[i]);

			_hotbarIcons[i] = new TextureRect
			{
				MouseFilter = Control.MouseFilterEnum.Ignore,
				LayoutMode = 3, // Position
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			};
			// Inset the icon slightly so it doesn't fill the whole slot.
			_hotbarIcons[i].Position = new Vector2(6f, 6f);
			_hotbarIcons[i].Size = new Vector2(hbSlotSize - 12f, hbSlotSize - 12f);
			slot.AddChild(_hotbarIcons[i]);

			_hotbarCounts[i] = new Label
			{
				Text = "",
				MouseFilter = Control.MouseFilterEnum.Ignore,
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Bottom,
			};
			_hotbarCounts[i].SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
			_hotbarCounts[i].AddThemeColorOverride("font_color", Colors.White);
			_hotbarCounts[i].AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
			_hotbarCounts[i].AddThemeConstantOverride("outline_size", 3);
			slot.AddChild(_hotbarCounts[i]);

			_hotbarSlots[i] = slot;
			_hotbar.AddChild(slot);
		}

		// Tooltip label just above the hotbar: names the item in the selected
		// slot (the player's "held" item). Centered on the hotbar, a short
		// distance above it. Always visible (independent of the inventory
		// panel) so the player always knows what they're holding.
		const float tipH = 20f;
		const float tipGap = 8f;
		// Centered directly above the hotbar: same left edge and width as the
		// hotbar panel itself, with the text centered within it.
		_hotbarTooltip = new Label
		{
			Name = "HotbarTooltip",
			LayoutMode = 3, // Position
			Position = new Vector2(hbPos.X, hbPos.Y - tipH - tipGap),
			Size = new Vector2(hbW, tipH),
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Text = "",
		};
		// A subtle dark backing so the name reads against any terrain.
		var tipStyle = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 0.45f),
			ContentMarginLeft = 8f,
			ContentMarginRight = 8f,
		};
		_hotbarTooltip.AddThemeStyleboxOverride("normal", tipStyle);
		_hotbarTooltip.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		_hotbarTooltip.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
		_hotbarTooltip.AddThemeConstantOverride("outline_size", 4);
		_root.AddChild(_hotbarTooltip);
	}

	// ------------------------------------------------------------------
	// Crafting pane (right side of the inventory panel)
	// ------------------------------------------------------------------

	/// <summary>
	/// Builds the crafting pane to the right of the slot grid: a title, a row of
	/// category tabs, and a scrollable recipe list. Always visible while the
	/// inventory is open (it lives inside <see cref="_panel"/>). Clicking a
	/// recipe crafts 1; Shift+click crafts as many as possible. Hovering shows a
	/// recipe info tooltip (name, description, ingredients).
	/// </summary>
	private void BuildCrafting()
	{
		float gridW = Inventory.HotbarSize * SlotSize + (Inventory.HotbarSize - 1) * SlotGap;
		float gridH = (Inventory.StorageRows + 1) * SlotSize + Inventory.StorageRows * SlotGap;
		const float craftW = 320f;   // wider crafting pane
		const float gap = 16f;        // gap between the slot grid and the pane
		const float pad = 12f;        // panel padding (matches BuildUi)
		const float devW = 240f;      // dev pane width (must match BuildUi)
		const float devGap = 16f;     // gap between the dev pane and the grid

		// Panel width = left pad + dev pane + gap + grid + gap + craft pane + right pad.
		float panelW = pad + devW + devGap + gridW + gap + craftW + pad;
		float panelH = Math.Max(gridH, 280f) + 56f;

		// Center the whole panel (dev + inventory + crafting) in the viewport.
		Vector2 vpSize = GetViewport().GetVisibleRect().Size;
		_panel.Position = new Vector2(
			Math.Max(0f, (vpSize.X - panelW) / 2f),
			Math.Max(0f, (vpSize.Y - panelH) / 2f));
		_panel.Size = new Vector2(panelW, panelH);

		// The slot grid starts at x = pad + devW + devGap (set in BuildUi); the
		// craft pane sits to its right after the gap.
		float craftX = pad + devW + devGap + gridW + gap;

		_craftPanel = new Panel
		{
			Name = "CraftPanel",
			LayoutMode = 3, // Position
			Position = new Vector2(craftX, pad),
			Size = new Vector2(craftW, panelH - pad * 2f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var craftStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.1f, 0.11f, 0.14f, 0.9f),
			BorderColor = new Color(0.35f, 0.4f, 0.5f, 0.8f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
		};
		_craftPanel.AddThemeStyleboxOverride("panel", craftStyle);
		_panel.AddChild(_craftPanel);

		// Inner layout of the craft pane (all relative to the pane's top-left):
		//   title (y 6..26) / tab strip (y 30..54) / scrollable list / queue bar.
		const float innerPad = 8f;
		const float titleH = 20f;
		const float tabH = 24f;
		const float queueH = 34f; // label (16) + gap + bar (6) + padding
		float innerW = craftW - innerPad * 2f;
		float paneInnerH = panelH - pad * 2f;
		float tabsTop = 6f + titleH + 4f;          // 30
		float listTop = tabsTop + tabH + 4f;       // 58
		float listH = paneInnerH - listTop - queueH - innerPad;

		var craftTitle = new Label
		{
			Name = "CraftTitle",
			LayoutMode = 3, // Position
			Text = "Crafting",
			Position = new Vector2(innerPad, 6f),
			Size = new Vector2(innerW, titleH),
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		craftTitle.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		_craftPanel.AddChild(craftTitle);

		// Category tabs in a horizontal ScrollContainer so many categories can't
		// overflow the pane's edge.
		var tabScroll = new ScrollContainer
		{
			LayoutMode = 3, // Position
			Position = new Vector2(innerPad, tabsTop),
			Size = new Vector2(innerW, tabH),
			VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		_craftTabs = new HBoxContainer
		{
			LayoutMode = 2,
		};
		_craftTabs.AddThemeConstantOverride("separation", 4);
		tabScroll.AddChild(_craftTabs);
		_craftPanel.AddChild(tabScroll);

		// Scrollable recipe list below the tabs.
		//
		// FollowFocus is the key: a ScrollContainer only stretches its SINGLE
		// child (the VBoxContainer below) to the full scroll area when this is
		// true — by default it is false, the child keeps its maximum-minimum
		// width (~the widest label), and a child's Fill flag has no extra width
		// to claim, which is why the recipe buttons stayed smushed to the left.
		_craftScroll = new ScrollContainer
		{
			LayoutMode = 3, // Position
			Position = new Vector2(innerPad, listTop),
			Size = new Vector2(innerW, listH),
			FollowFocus = true,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		_craftPanel.AddChild(_craftScroll);

		_craftList = new VBoxContainer
		{
			LayoutMode = 2,
		};
		_craftList.AddThemeConstantOverride("separation", 4);
		_craftScroll.AddChild(_craftList);

		// Crafting queue / progress: a thin bar + a status label at the bottom
		// of the pane. The bar fills as the active job advances; the label shows
		// what's crafting or how many jobs are queued.
		float barH = 6f;
		float barY = listTop + listH + 8f;
		_queueLabel = new Label
		{
			Name = "QueueLabel",
			LayoutMode = 3, // Position
			Position = new Vector2(innerPad, barY - 18f),
			Size = new Vector2(innerW, 16f),
			Text = "Idle",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_queueLabel.AddThemeFontSizeOverride("font_size", 12);
		_queueLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.75f, 0.85f));
		_craftPanel.AddChild(_queueLabel);

		_queueProgressBg = new ColorRect
		{
			Name = "QueueProgressBg",
			LayoutMode = 3, // Position
			Position = new Vector2(innerPad, barY),
			Size = new Vector2(innerW, barH),
			Color = new Color(0f, 0f, 0f, 0.5f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_craftPanel.AddChild(_queueProgressBg);

		_queueProgressFill = new ColorRect
		{
			Name = "QueueProgressFill",
			LayoutMode = 3, // Position
			Position = Vector2.Zero,
			Size = new Vector2(0f, barH),
			Color = new Color(0.4f, 0.8f, 0.4f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_queueProgressBg.AddChild(_queueProgressFill);

		BuildRecipeTooltip();
	}

	/// <summary>
	/// Builds the dev pane to the LEFT of the slot grid: a title, a short hint,
	/// and a vertically scrollable list of every item in the game (all placeable
	/// materials + all tools, from <see cref="FactorySim.Items.ItemRegistry"/>).
	/// Each row is a button: clicking it grants one of that item into the
	/// inventory (a full stack of the material, or a single tool). This is the
	/// god-mode way to hand yourself any item to test placing / breaking / tool
	/// affinities without having to craft or mine first.
	/// </summary>
	private void BuildDevPanel()
	{
		const float pad = 12f;
		const float devW = 240f;
		float panelH = _panel != null ? _panel.Size.Y : 300f;

		_devPanel = new Panel
		{
			Name = "DevPanel",
			LayoutMode = 3, // Position
			Position = new Vector2(pad, pad),
			Size = new Vector2(devW, panelH - pad * 2f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var devStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.1f, 0.11f, 0.14f, 0.9f),
			BorderColor = new Color(0.35f, 0.4f, 0.5f, 0.8f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
		};
		_devPanel.AddThemeStyleboxOverride("panel", devStyle);
		_panel.AddChild(_devPanel);

		const float innerPad = 8f;
		float innerW = devW - innerPad * 2f;
		const float titleH = 20f;
		const float hintH = 16f;
		const float listTop = 6f + titleH + 4f + hintH + 4f;
		float listH = (panelH - pad * 2f) - listTop - innerPad;

		var devTitle = new Label
		{
			Name = "DevTitle",
			LayoutMode = 3, // Position
			Text = "Dev Items",
			Position = new Vector2(innerPad, 6f),
			Size = new Vector2(innerW, titleH),
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		devTitle.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.4f));
		_devPanel.AddChild(devTitle);

		var devHint = new Label
		{
			Name = "DevHint",
			LayoutMode = 3, // Position
			Text = "click = give a stack",
			Position = new Vector2(innerPad, 6f + titleH + 4f),
			Size = new Vector2(innerW, hintH),
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		devHint.AddThemeFontSizeOverride("font_size", 11);
		devHint.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.75f));
		_devPanel.AddChild(devHint);

		// Vertically scrollable list of every item. FollowFocus stretches the
		// single VBox child to the full scroll area (same trick as the recipe
		// list), so the rows span the pane's width.
		var devScroll = new ScrollContainer
		{
			Name = "DevScroll",
			LayoutMode = 3, // Position
			Position = new Vector2(innerPad, listTop),
			Size = new Vector2(innerW, listH),
			FollowFocus = true,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
		};
		_devPanel.AddChild(devScroll);

		_devList = new VBoxContainer
		{
			Name = "DevList",
			LayoutMode = 2,
		};
		_devList.AddThemeConstantOverride("separation", 3);
		devScroll.AddChild(_devList);

		// Populate the list from the registry: every placeable material (from the
		// world's material config) + every tool + every machine. Future items
		// appear here automatically as they're added to the config / tool /
		// machine tables. The machine config (if the world has one) lets machine
		// rows use the user's custom definition names.
		_devItems.Clear();
		_devItems.AddRange(FactorySim.Items.ItemRegistry.AllItems(_world?.MaterialConfig, _world?.MachineConfig));
		foreach (var item in _devItems)
		{
			_devList.AddChild(CreateDevRow(item));
		}

		BuildDevTooltip();
	}

	/// <summary>
	/// Builds the dev-pane hover tooltip (name + description), mirroring the
	/// recipe tooltip: an opaque Panel with a FullRect VBoxContainer content
	/// child inset by a uniform pad so the left padding always equals the right.
	/// The panel is sized to fit its content each time the hovered item changes.
	/// </summary>
	private void BuildDevTooltip()
	{
		_devTooltip = new Panel
		{
			Name = "DevTooltip",
			LayoutMode = 3, // Position
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ZIndex = 220,
		};
		var tipStyle = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 1f),
			BorderColor = new Color(0.6f, 0.6f, 0.65f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
		};
		_devTooltip.AddThemeStyleboxOverride("panel", tipStyle);

		const float TipPad = 10f;
		_devTipContent = new VBoxContainer
		{
			Name = "DevTipContent",
			LayoutMode = 3, // Position
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_devTipContent.AddThemeConstantOverride("separation", 6);
		_devTipContent.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_devTipContent.Position = new Vector2(TipPad, TipPad);
		_devTipContent.Size = new Vector2(-TipPad * 2f, -TipPad * 2f);
		_devTooltip.AddChild(_devTipContent);

		_devTipName = new Label
		{
			Name = "DevTipName",
			LayoutMode = 2,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_devTipName.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		_devTipName.AddThemeFontSizeOverride("font_size", 15);
		_devTipContent.AddChild(_devTipName);

		_devTipDesc = new Label
		{
			Name = "DevTipDesc",
			LayoutMode = 2,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_devTipDesc.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
		_devTipContent.AddChild(_devTipDesc);

		_root.AddChild(_devTooltip);
	}

	/// <summary>
	/// The dev item whose row is under <paramref name="viewportPos"/>, or null.
	/// Mirrors <see cref="RecipeAt"/>: walks each row's ancestor chain up to the
	/// dev panel, accumulating positions, to compute the row's on-screen rect
	/// (robust to the ScrollContainer / VBoxContainer nesting).
	/// </summary>
	private FactorySim.Items.ItemEntry? DevItemAt(Vector2 viewportPos)
	{
		if (_devPanel == null || _panel == null)
		{
			return null;
		}

		Vector2 panelOrigin = _panel.Position + _devPanel.Position;

		foreach (var (item, row) in _devHoverRects)
		{
			if (row == null || !IsInstanceValid(row))
			{
				continue;
			}

			Vector2 offset = Vector2.Zero;
			var node = row;
			while (node != null && node != _devPanel)
			{
				offset += node.Position;
				node = node.GetParent() as Control;
			}

			Vector2 rowTopLeft = panelOrigin + offset;
			if (new Rect2(rowTopLeft, row.Size).HasPoint(viewportPos))
			{
				return item;
			}
		}
		return null;
	}

	/// <summary>
	/// Shows the dev-item tooltip when a dev row is hovered and the inventory is
	/// open; hides it otherwise. Follows the mouse with a small offset, clamped
	/// to the viewport (mirrors <see cref="UpdateRecipeTooltip"/>).
	/// </summary>
	private void UpdateDevTooltip()
	{
		if (_devTooltip == null)
		{
			return;
		}

		if (!IsOpen())
		{
			_devTooltip.Visible = false;
			return;
		}

		var item = DevItemAt(GetViewport().GetMousePosition());
		if (item == null)
		{
			_devTooltip.Visible = false;
			_devTipItem = null;
			return;
		}

		var hovered = item.Value;

		// (Re)fill the content only when the hovered item actually changes.
		if (_devTipItem != hovered)
		{
			_devTipItem = hovered;
			_devTipName.Text = hovered.Name;

			// Description: tools and machines get short blurbs; materials use
			// their config description (falling back to a generic line).
			string desc = hovered.IsTool
				? "Hold this in your hotbar to break blocks. Each tool is faster on some materials than others."
				: hovered.IsMachine
				? "Place this in the world to build a machine. Its appearance and behavior come from its MachineDefinition (imported mesh + stats)."
				: MaterialDescription(hovered.MaterialId);
			if (string.IsNullOrEmpty(desc))
			{
				desc = hovered.IsTool ? "A tool item." : hovered.IsMachine ? "A machine item." : "A placeable block.";
			}
			_devTipDesc.Text = desc;

			// Size the tooltip to fit its content (uniform TipPad inset).
			const float tipPad = 10f;
			Vector2 contentMin = _devTipContent.GetCombinedMinimumSize();
			_devTooltip.Size = new Vector2(
				Mathf.Max(contentMin.X, 140f) + tipPad * 2f,
				contentMin.Y + tipPad * 2f);
		}

		// Position: to the right of the mouse, clamped on-screen.
		Vector2 mousePos = GetViewport().GetMousePosition();
		Vector2 tipPos = mousePos + new Vector2(16f, 16f);
		Vector2 vpSize = GetViewport().GetVisibleRect().Size;
		tipPos.X = Mathf.Min(tipPos.X, vpSize.X - _devTooltip.Size.X - 4f);
		tipPos.Y = Mathf.Min(tipPos.Y, vpSize.Y - _devTooltip.Size.Y - 4f);
		_devTooltip.Position = tipPos;
		_devTooltip.Visible = true;
	}

	/// <summary>The material's description from the world config (empty fallback).</summary>
	private string MaterialDescription(int materialId)
	{
		if (_world != null && _world.MaterialConfig != null)
		{
			return _world.MaterialConfig.GetDescription(materialId);
		}
		return "";
	}

	/// <summary>
	/// Builds one dev-pane row: a small color swatch (the item's icon color),
	/// the item's name, and (for tools) a tag showing the tool kind. The whole
	/// row is a <see cref="Button"/> so a click anywhere on it grants the item.
	/// </summary>
	private Button CreateDevRow(FactorySim.Items.ItemEntry item)
	{
		// Mirror the working recipe-row pattern (see CreateRecipeRow) EXACTLY:
		//   * LayoutMode = 2 makes the Button a proper child of the VBoxContainer
		//     (the dev list), so the container actually lays it out. The old
		//     LayoutMode = 3 (Position) left the button unmanaged — the VBox
		//     ignored it, it collapsed, and it never received clicks.
		//   * SizeFlagsHorizontal = ExpandFill makes it span the pane's full
		//     width (an empty-text button's minimum width is ~0, so without
		//     Expand it stays smushed to the left).
		//   * MouseFilter = Stop so the button consumes the click (its Ignore
		//     children pass through to it).
		// The content's horizontal extent: icon (x 6..36) + name label (x 44..).
		const float contentRight = 200f;

		var button = new Button
		{
			Name = $"Dev_{item.Name.Replace(' ', '_')}",
			LayoutMode = 2,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(contentRight, 30f),
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		button.Text = "";

		// Icon on the left: the terrain's own texture when configured, else a
		// flat color swatch (same logic as the inventory slots).
		var icon = new TextureRect
		{
			Name = "Icon",
			LayoutMode = 3, // Position
			Position = new Vector2(6f, 5f),
			Size = new Vector2(20f, 20f),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		SetIcon(icon, item.MaterialId);
		button.AddChild(icon);

		// Name label to the right of the icon. Tools get a "(tool)" tag and
		// machines a "(machine)" tag so they're visually distinct from
		// placeable materials.
		string label = item.IsTool ? $"{item.Name} (tool)"
			: item.IsMachine ? $"{item.Name} (machine)"
			: item.Name;
		var labelCtl = new Label
		{
			Name = "Label",
			LayoutMode = 3, // Position
			Position = new Vector2(34f, 6f),
			Size = new Vector2(contentRight - 40f, 18f),
			Text = label,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		labelCtl.AddThemeFontSizeOverride("font_size", 13);
		labelCtl.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddChild(labelCtl);

		// Clicking grants the item (a full material stack, or a single tool).
		button.Pressed += () => GrantDevItem(item);

		// Register the row so the dev tooltip can hit-test hover (mirrors the
		// recipe rows' _recipeHoverRects registration).
		_devHoverRects[item] = button;
		return button;
	}

	/// <summary>
	/// Grants one of <paramref name="item"/> into the inventory: a full stack of
	/// the material, or a single tool (placed into the first empty slot so its
	/// <see cref="FactorySim.Items.ItemStack.ToolType"/> is preserved). Refreshes
	/// the UI. This is the only entry point the dev pane uses to add items, so
	/// it is the single place that knows how to hand out each item kind.
	/// </summary>
	private void GrantDevItem(FactorySim.Items.ItemEntry item)
	{
		if (_inventory == null)
		{
			return;
		}

		if (item.IsTool || item.IsMachine)
		{
			// Tools AND machines are single items that must keep their type tag
			// (ToolType / MachineType); place into the first empty slot
			// (CollectItem would rebuild the stack and drop the type). If there's
			// no room, a full inventory means the player must make room first.
			var single = FactorySim.Items.ItemStack.From(item.MaterialId, 1);
			single.ToolType = item.IsTool ? item.ToolType : null;
			single.MachineType = item.IsMachine ? item.MachineKind : null;
			for (int i = 0; i < Inventory.TotalSlots; i++)
			{
				if (_inventory.Slots[i].IsEmpty)
				{
					_inventory.Slots[i] = single;
					_inventory.RaiseChangedPublic();
					return;
				}
			}
			// No empty slot: if already holding an identical one, do nothing
			// (safety net — tools and machines don't stack).
			for (int i = 0; i < Inventory.TotalSlots; i++)
			{
				var s = _inventory.Slots[i];
				if (s.MaterialId == item.MaterialId && (s.IsTool || s.IsMachine))
				{
					return; // already holding one; do nothing
				}
			}
			return;
		}

		// Materials: grant a full stack (the dev pane's "give 1" is really "give
		// a usable amount"; a single block is hard to work with). Use CollectItem
		// so it fills existing stacks first, then empty slots.
		_inventory.CollectItem(FactorySim.Items.ItemStack.From(item.MaterialId, FactorySim.Items.ItemStack.StackLimit));
	}

	/// <summary>Builds the recipe info tooltip (name + description + ingredient list).</summary>
	private void BuildRecipeTooltip()
	{
		_recipeTooltip = new Panel
		{
			Name = "RecipeTooltip",
			LayoutMode = 3, // Position
			Visible = false,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ZIndex = 210,
		};
		var tipStyle = new StyleBoxFlat
		{
			// Fully opaque background (alpha 1.0) so the 3D scene never shows
			// through the tooltip.
			BgColor = new Color(0f, 0f, 0f, 1f),
			BorderColor = new Color(0.6f, 0.6f, 0.65f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 10f,
			ContentMarginTop = 8f,
			ContentMarginRight = 10f,
			ContentMarginBottom = 8f,
		};
		_recipeTooltip.AddThemeStyleboxOverride("panel", tipStyle);

		// The Panel itself is NOT a container — it does not lay out its children,
		// so adding the name/description/ingredients straight to it would leave
		// them all collapsed at the top-left, stacked on top of each other. A
		// single FullRect VBoxContainer child is the container that stacks the
		// rows vertically; the panel just draws the background. The panel's size
		// is set explicitly each time the content changes (see
		// UpdateRecipeTooltip) — a 0x0 rect would leave the StyleBoxFlat nothing
		// to draw into, so the background must be given a real size.
		//
		// The content is inset by TipPad on every side (the FullRect anchor's
		// negative offsets) so the text has even padding: the same on the left as
		// the right, and the same on the top as the bottom. TipPad is the single
		// source of truth for that inset, shared with the size calc below.
		const float TipPad = 10f;
		_recipeTipContent = new VBoxContainer
		{
			Name = "RecipeTipContent",
			LayoutMode = 3, // Position
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_recipeTipContent.AddThemeConstantOverride("separation", 6);
		_recipeTipContent.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_recipeTipContent.Position = new Vector2(TipPad, TipPad);
		_recipeTipContent.Size = new Vector2(-TipPad * 2f, -TipPad * 2f);
		_recipeTooltip.AddChild(_recipeTipContent);

		_recipeTipName = new Label
		{
			Name = "RecipeName",
			LayoutMode = 2,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_recipeTipName.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		_recipeTipContent.AddChild(_recipeTipName);

		// No autowrap and no custom width: the label is as wide as its text, so
		// the whole tooltip width is driven by the single widest line (the long
		// description, or the longest ingredient row) instead of a fixed 220px
		// that either clipped the text or left dead space.
		_recipeTipDesc = new Label
		{
			Name = "RecipeDesc",
			LayoutMode = 2,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_recipeTipDesc.AddThemeColorOverride("font_color", new Color(0.75f, 0.8f, 0.9f));
		_recipeTipContent.AddChild(_recipeTipDesc);

		_recipeTipIngredients = new VBoxContainer
		{
			Name = "RecipeIngredients",
			LayoutMode = 2,
		};
		_recipeTipIngredients.AddThemeConstantOverride("separation", 3);
		_recipeTipContent.AddChild(_recipeTipIngredients);

		_root.AddChild(_recipeTooltip);
	}

	private void CreateSlot(int slotIndex, Vector2 position)
	{
		var slot = new Control
		{
			Name = $"Slot_{slotIndex}",
			LayoutMode = 3, // Position
			Position = position,
			Size = new Vector2(SlotSize, SlotSize),
			// Ignore: let events pass through to the root, which does the
			// hit-testing via SlotAt(). If this were Stop, the slot would
			// consume the click and the root's GuiInput would never fire.
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var slotStyle = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 0.45f),
			BorderColor = new Color(0.5f, 0.5f, 0.55f, 0.6f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
		};
		slot.AddThemeStyleboxOverride("panel", slotStyle);
		// The slot body: a full-rect ColorRect (Control has no stylebox of its
		// own); the icon + count sit on top of it.
		var background = new ColorRect
		{
			Name = "SlotBg",
			LayoutMode = 3, // Position
			Color = new Color(0.08f, 0.08f, 0.1f, 0.85f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		slot.AddChild(background);

		_slotIcons[slotIndex] = new TextureRect
		{
			Name = "Icon",
			LayoutMode = 3, // Position
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_slotIcons[slotIndex].SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		slot.AddChild(_slotIcons[slotIndex]);

		_slotCounts[slotIndex] = new Label
		{
			Name = "Count",
			LayoutMode = 3, // Position
			Text = "",
			MouseFilter = Control.MouseFilterEnum.Ignore,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Bottom,
		};
		_slotCounts[slotIndex].SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_slotCounts[slotIndex].AddThemeColorOverride("font_color", Colors.White);
		_slotCounts[slotIndex].AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
		_slotCounts[slotIndex].AddThemeConstantOverride("outline_size", 3);
		slot.AddChild(_slotCounts[slotIndex]);

		_slotControls[slotIndex] = slot;
		_panel.AddChild(slot);
	}

	// ------------------------------------------------------------------
	// Slot visuals
	// ------------------------------------------------------------------

	private void RefreshAllSlots()
	{
		for (int i = 0; i < Inventory.TotalSlots; i++)
		{
			RefreshSlot(i);
		}
		// The crafting pane's "possible count" depends on the inventory totals,
		// which just changed — refresh it in the same pass.
		RefreshCrafting();
	}

	private void RefreshSlot(int i)
	{
		if (_slotIcons[i] == null || _slotCounts[i] == null)
		{
			return;
		}

		var stack = _inventory.Slots[i];
		if (stack.IsEmpty)
		{
			_slotIcons[i].Texture = null;
			_slotCounts[i].Text = "";
			return;
		}

		// Use the terrain's own texture as the icon when one is configured;
		// fall back to the material's albedo color otherwise.
		SetIcon(_slotIcons[i], stack.MaterialId);
		// Show the count only for partial stacks (full stacks match Minecraft's look).
		_slotCounts[i].Text = stack.Count.ToString();
	}

	/// <summary>
	/// The material's albedo from the world palette (index <c>materialId - 1</c>,
	/// grey fallback when unconfigured).
	/// </summary>
	private Color LookUpColor(int materialId)
	{
		// Tools aren't palette materials; give them their own icon color.
		if (FactorySim.Voxel.VoxelMaterialIds.IsToolId(materialId))
		{
			if (Enum.TryParse<FactorySim.Items.ToolType>(materialId.ToString(), out var tool))
			{
				return FactorySim.Items.ItemRegistry.ToolColor(tool);
			}
			return new Color(0.6f, 0.6f, 0.6f);
		}

		// Machines aren't palette materials either; give them their own icon color.
		if (FactorySim.Voxel.VoxelMaterialIds.IsMachineId(materialId))
		{
			if (Enum.TryParse<FactorySim.Machines.MachineType>(materialId.ToString(), out var machine))
			{
				return FactorySim.Items.ItemRegistry.MachineColor(machine);
			}
			return new Color(0.6f, 0.6f, 0.6f);
		}

		if (_world != null && _world.Palette != null
			&& _world.Palette.Materials.Length > 0
			&& materialId > 0 && materialId - 1 < _world.Palette.Materials.Length)
		{
			var mat = _world.Palette.Materials[materialId - 1];
			if (mat != null)
			{
				return mat.AlbedoColor;
			}
		}
		return new Color(0.5f, 0.5f, 0.5f);
	}

	/// <summary>
	/// Sets the icon on a <see cref="TextureRect"/> for the given material id:
	/// the terrain's own texture when one is configured (so the icon always
	/// matches what shows up on the terrain), or a flat color swatch otherwise.
	/// This is the single place that decides how an item is drawn as an icon.
	/// </summary>
	private void SetIcon(TextureRect icon, int materialId)
	{
		if (icon == null)
		{
			return;
		}

		// Terrain materials with a configured texture use the same texture on
		// the terrain AND as the item icon.
		if (_world != null && _world.Palette != null
			&& !FactorySim.Voxel.VoxelMaterialIds.IsToolId(materialId)
			&& !FactorySim.Voxel.VoxelMaterialIds.IsMachineId(materialId))
		{
			var tex = _world.Palette.GetTexture(materialId);
			if (tex != null)
			{
				icon.Texture = tex;
				return;
			}
		}

		// No texture (or a tool/machine): draw a flat color swatch. Build a
		// 1×1 ImageTexture of the icon color so the TextureRect has something
		// to draw (a null texture would leave the slot blank).
		var img = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		img.Fill(LookUpColor(materialId));
		icon.Texture = ImageTexture.CreateFromImage(img);
	}

	/// <summary>
	/// The material's display name from the world's material config, a tool's
	/// name for tool ids, or a generic fallback for unconfigured ids.
	/// </summary>
	private string LookUpMaterialName(int materialId)
	{
		if (FactorySim.Voxel.VoxelMaterialIds.IsToolId(materialId))
		{
			if (Enum.TryParse<FactorySim.Items.ToolType>(materialId.ToString(), out var tool))
			{
				return FactorySim.Items.ItemRegistry.ToolName(tool);
			}
		}
		if (FactorySim.Voxel.VoxelMaterialIds.IsMachineId(materialId))
		{
			if (Enum.TryParse<FactorySim.Machines.MachineType>(materialId.ToString(), out var machine))
			{
				return FactorySim.Items.ItemRegistry.MachineName(machine);
			}
		}
		if (_world != null && _world.MaterialConfig != null)
		{
			var def = _world.MaterialConfig.GetDef(materialId);
			if (def != null && !string.IsNullOrEmpty(def.Name))
			{
				return def.Name;
			}
		}
		return $"Material {materialId}";
	}

	private void RefreshCursor()
	{
		if (_cursor == null)
		{
			return;
		}

		_cursor.Visible = !_cursorStack.IsEmpty;
		if (_cursorStack.IsEmpty)
		{
			return;
		}

		SetIcon(_cursorIcon, _cursorStack.MaterialId);
		_cursorCount.Text = _cursorStack.Count.ToString();
	}

	/// <summary>
	/// Updates the persistent hotbar: item icons/counts for slots 0-9, the
	/// greenish selection tint on the selected slot's background, and the
	/// held-item name label above the hotbar.
	/// </summary>
	private void RefreshHotbar()
	{
		if (_hotbar == null)
		{
			return;
		}

		for (int i = 0; i < Inventory.HotbarSize; i++)
		{
			if (_hotbarIcons[i] == null || _hotbarCounts[i] == null)
			{
				continue;
			}

			var stack = _inventory.Slots[i];
			if (stack.IsEmpty)
			{
				_hotbarIcons[i].Texture = null;
				_hotbarCounts[i].Text = "";
			}
			else
			{
				SetIcon(_hotbarIcons[i], stack.MaterialId);
				_hotbarCounts[i].Text = stack.Count.ToString();
			}
		}

		// Selection indicator: tint the selected slot's background greenish and
		// reset the others to the dark base color.
		for (int i = 0; i < Inventory.HotbarSize; i++)
		{
			if (_hotbarSlotBg[i] != null)
			{
				_hotbarSlotBg[i].Color = (i == _selectedHotbarSlot) ? HotbarBgSelected : HotbarBgUnselected;
			}
		}

		// Update the tooltip above the hotbar with the name of the held item
		// (the selected slot's contents). Empty slot => blank tooltip.
		if (_hotbarTooltip != null)
		{
			var held = SelectedHotbarItem;
			_hotbarTooltip.Text = held.IsEmpty ? "" : LookUpMaterialName(held.MaterialId);
		}
	}

	// ------------------------------------------------------------------
	// Crafting pane logic
	// ------------------------------------------------------------------

	/// <summary>
	/// (Re)builds the category tabs and the visible recipe rows for the current
	/// category. Called at init and whenever the unlocked recipe set changes.
	/// </summary>
	private void RefreshCrafting()
	{
		if (_crafting == null || _craftPanel == null)
		{
			return;
		}

		// Make sure the database's ingredient totals are current.
		_crafting.RebuildInventory(_inventory);

		var unlocked = _crafting.GetUnlockedRecipes();
		var categories = CraftingDatabase.CategoriesOf(unlocked);

		// If no category is selected yet, pick the first.
		if (!string.IsNullOrEmpty(_craftCategory) && !categories.Contains(_craftCategory))
		{
			_craftCategory = categories.Count > 0 ? categories[0] : "";
		}
		else if (string.IsNullOrEmpty(_craftCategory) && categories.Count > 0)
		{
			_craftCategory = categories[0];
		}

		RebuildCategoryTabs(categories);
		RebuildRecipeRows(unlocked);
		RefreshRecipeCounts();
	}

	/// <summary>Rebuilds the tab buttons (one per category) and highlights the active one.</summary>
	private void RebuildCategoryTabs(List<string> categories)
	{
		foreach (Node child in _craftTabs.GetChildren())
		{
			child.QueueFree();
		}

		foreach (var category in categories)
		{
			bool active = category == _craftCategory;
			var tab = new Button
			{
				Text = category,
				TooltipText = category,
				LayoutMode = 2,
				Disabled = false,
			};
			tab.AddThemeFontSizeOverride("font_size", 14);
			var style = new StyleBoxFlat
			{
				BgColor = active ? new Color(0.3f, 0.4f, 0.6f) : new Color(0.16f, 0.18f, 0.24f),
				BorderColor = new Color(0.4f, 0.5f, 0.7f, active ? 1f : 0.6f),
				BorderWidthLeft = 1,
				BorderWidthTop = 1,
				BorderWidthRight = 1,
				BorderWidthBottom = 1,
				ContentMarginLeft = 8f,
				ContentMarginRight = 8f,
				ContentMarginTop = 2f,
				ContentMarginBottom = 2f,
			};
			tab.AddThemeStyleboxOverride("normal", style);
			tab.AddThemeStyleboxOverride("hover", style);
			tab.AddThemeStyleboxOverride("pressed", style);
			tab.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
			tab.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
			string captured = category;
			tab.Pressed += () =>
			{
				_craftCategory = captured;
				RefreshCrafting();
			};
			_craftTabs.AddChild(tab);
		}
	}

	/// <summary>Rebuilds the recipe rows for the active category.</summary>
	private void RebuildRecipeRows(IReadOnlyList<CraftingRecipe> unlocked)
	{
		foreach (Node child in _craftList.GetChildren())
		{
			child.QueueFree();
		}
		_visibleRecipes.Clear();
		_recipeIcons.Clear();
		_recipeNames.Clear();
		_recipeCounts.Clear();
		_recipeButtons.Clear();

		foreach (var recipe in unlocked)
		{
			if (recipe.Category != _craftCategory)
			{
				continue;
			}
			_visibleRecipes.Add(recipe);
			CreateRecipeRow(recipe);
		}
	}

	/// <summary>
	/// Creates one clickable recipe row: a <see cref="Button"/> that owns all of
	/// the recipe's content (background, product icon, name, possible count) as
	/// children. Pressing it enqueues a craft job into the crafting queue (1
	/// item, or as many as possible when Shift is held), but only when the
	/// player has the ingredients — the button's <c>Disabled</c> state mirrors
	/// that, and the handler re-validates so a stale click can never enqueue.
	/// The job is then processed over time in _Process (see ProcessCraftQueue),
	/// like a single crafting station working through the list.
	/// </summary>
	private void CreateRecipeRow(CraftingRecipe recipe)
	{
		// The content's horizontal extent: icon (x 6..36) + name/count labels
		// (x 44..164). The button's minimum width is set to this so it always
		// spans at least as wide as everything it parents; combined with
		// ExpandFill (below) the VBoxContainer stretches it to the pane's full
		// width when there is more room.
		const float contentRight = 164f;

		var button = new Button
		{
			Name = $"Recipe_{recipe.ProductId}_{recipe.Category}",
			LayoutMode = 2,
			// ExpandFill (Expand | Fill): without the Expand bit an empty-text
			// button's minimum width is ~0 and it stays smushed to the left of
			// the VBoxContainer. Expand makes it claim the container's free
			// width, Fill makes it stretch to exactly that. Same working
			// pattern as SlotPicker/SettingsPanel.
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(contentRight, 40f),
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Stop,
		};

		// No button text: the icon + name + count children (all children of the
		// button) carry the content.
		button.Text = "";

		var rowBg = new ColorRect
		{
			Color = new Color(0.13f, 0.15f, 0.2f, 0.7f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		rowBg.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		button.AddChild(rowBg);

		float iconSize = 30f;
		var icon = new TextureRect
		{
			LayoutMode = 3, // Position
			Position = new Vector2(6f, 5f),
			Size = new Vector2(iconSize, iconSize),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		SetIcon(icon, recipe.ProductId);
		button.AddChild(icon);

		var name = new Label
		{
			Text = LookUpMaterialName(recipe.ProductId),
			LayoutMode = 3, // Position
			Position = new Vector2(44f, 6f),
			Size = new Vector2(120f, 20f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		name.AddThemeFontSizeOverride("font_size", 15);
		name.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddChild(name);

		var count = new Label
		{
			Text = "",
			LayoutMode = 3, // Position
			Position = new Vector2(44f, 24f),
			Size = new Vector2(120f, 14f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		count.AddThemeFontSizeOverride("font_size", 12);
		button.AddChild(count);

		// Clicking enqueues a craft job, but only when the player has the
		// ingredients for it right now.
		button.Pressed += () => EnqueueRecipe(recipe);

		// Register the button so UpdateRecipeTooltip can hit-test hover.
		_recipeHoverRects[recipe] = button;
		_craftList.AddChild(button);
		_recipeButtons[recipe] = button;
		_recipeIcons[recipe] = icon;
		_recipeNames[recipe] = name;
		_recipeCounts[recipe] = count;
	}

	/// <summary>
	/// Enqueues one job for <paramref name="recipe"/> (as many as possible when
	/// Shift is held) — but only if the player currently has the ingredients.
	/// No-op when nothing can be crafted or the queue is full.
	/// </summary>
	private void EnqueueRecipe(CraftingRecipe recipe)
	{
		int amount = IsShiftDown() ? _crafting.PossibleCount(recipe) : 1;
		if (amount <= 0)
		{
			return; // can't craft any right now
		}
		// Cap the queue so it can't grow unbounded (e.g. spam-clicking).
		if (_craftQueue.Count < 64)
		{
			_craftQueue.Enqueue((recipe, amount));
		}
		RefreshQueueLabel();
	}

	/// <summary>
	/// Updates each visible recipe row's "possible count" label (green when ≥1,
	/// grey when 0) and the product icon color, and mirrors craftability into
	/// the button's Disabled state (a disabled button can't enqueue a job).
	/// </summary>
	private void RefreshRecipeCounts()
	{
		foreach (var recipe in _visibleRecipes)
		{
			if (!_recipeCounts.TryGetValue(recipe, out var countLabel))
			{
				continue;
			}
			int possible = _crafting.PossibleCount(recipe);
			string label = possible <= 0 ? "—" : $"can craft x{possible}";
			countLabel.Text = label;
			countLabel.AddThemeColorOverride("font_color",
				possible >= 1 ? new Color(0.5f, 0.9f, 0.5f) : new Color(0.55f, 0.55f, 0.6f));
			if (_recipeButtons.TryGetValue(recipe, out var button))
			{
				button.Disabled = possible <= 0;
			}
		}
	}

	/// <summary>
	/// Advances the crafting queue by <paramref name="delta"/> seconds. Only one
	/// job is active at a time; when its <c>CraftTime</c> elapses it consumes the
	/// ingredients and adds the product to the inventory, then the next queued
	/// job starts. A job that can no longer be afforded (ingredients spent
	/// elsewhere while queued) is skipped.
	/// </summary>
	private void ProcessCraftQueue(double delta)
	{
		if (_crafting == null || _inventory == null)
		{
			return;
		}

		// If nothing is active, pull the next job off the queue.
		if (_activeJob == null && _craftQueue.Count > 0)
		{
			var (recipe, amount) = _craftQueue.Dequeue();
			// Re-validate: the player may have spent ingredients since enqueue.
			if (_crafting.PossibleCount(recipe) >= amount)
			{
				_activeJob = (recipe, amount);
				_jobElapsed = 0;
			}
			// else: drop it and try the next job on a later frame.
		}

		if (_activeJob == null)
		{
			UpdateProgressBar(0f, false);
			return;
		}

		var (activeRecipe, activeAmount) = _activeJob.Value;
		_jobElapsed += delta;

		float needed = activeRecipe.CraftTime * activeAmount;
		float progress = needed > 0f ? (float)Math.Clamp(_jobElapsed / needed, 0f, 1f) : 1f;
		UpdateProgressBar(progress, true);

		if (_jobElapsed >= needed)
		{
			// Complete the job: consume ingredients + add product.
			int crafted = _crafting.Craft(activeRecipe, activeAmount, _inventory);
			_dirty = crafted > 0; // refresh counts + slots if it landed
			_activeJob = null;
			_jobElapsed = 0;
		}
	}

	/// <summary>Updates the crafting progress bar (fill + label) at the bottom of the pane.</summary>
	private void UpdateProgressBar(float progress, bool active)
	{
		if (_queueProgressFill != null)
		{
			// The fill is a child of _queueProgressBg (full-rect); scale its width.
			float bgW = _queueProgressBg.Size.X;
			_queueProgressFill.Size = new Vector2(bgW * Mathf.Clamp(progress, 0f, 1f), _queueProgressFill.Size.Y);
		}
		if (_queueLabel != null)
		{
			_queueLabel.Text = active
				? $"Crafting {LookUpMaterialName(_activeJob.Value.Recipe.ProductId)}…"
				: (_craftQueue.Count > 0 ? $"{_craftQueue.Count} queued" : "Idle");
		}
	}

	/// <summary>Updates the "N queued" label when the queue size changes.</summary>
	private void RefreshQueueLabel()
	{
		if (_queueLabel != null && _activeJob == null)
		{
			_queueLabel.Text = _craftQueue.Count > 0 ? $"{_craftQueue.Count} queued" : "Idle";
		}
	}

	/// <summary>
	/// True while Shift is held. Uses the Input singleton's physical-key state,
	/// which is the supported way to query a modifier in Godot 4 C#.
	/// </summary>
	private static bool IsShiftDown()
		=> Input.IsPhysicalKeyPressed(Key.Shift);

	/// <summary>
	/// Hit-tests the mouse position against the visible recipe rows and returns
	/// the hovered recipe (null when none). The row's on-screen rect is computed
	/// by walking its ancestor chain up to the craft panel and summing positions,
	/// which is robust to the ScrollContainer / VBoxContainer nesting.
	/// </summary>
	private CraftingRecipe RecipeAt(Vector2 viewportPos)
	{
		if (_craftPanel == null || _panel == null)
		{
			return null;
		}

		// The craft panel's top-left in viewport space.
		Vector2 panelOrigin = _panel.Position + _craftPanel.Position;

		foreach (var (recipe, row) in _recipeHoverRects)
		{
			if (row == null || !IsInstanceValid(row))
			{
				continue;
			}

			// Walk from the row up to (but not including) the craft panel,
			// accumulating each ancestor's position relative to its parent.
			Vector2 offset = Vector2.Zero;
			var node = row;
			while (node != null && node != _craftPanel)
			{
				offset += node.Position;
				node = node.GetParent() as Control;
			}

			Vector2 rowTopLeft = panelOrigin + offset;
			if (new Rect2(rowTopLeft, row.Size).HasPoint(viewportPos))
			{
				return recipe;
			}
		}
		return null;
	}

	/// <summary>
	/// Shows the recipe info tooltip when a recipe row is hovered and the
	/// inventory is open; hides it otherwise. Follows the mouse with a small
	/// offset, clamped to the viewport.
	/// </summary>
	private void UpdateRecipeTooltip()
	{
		if (_recipeTooltip == null)
		{
			return;
		}

		// Determine the hovered recipe from the mouse position.
		_hoveredRecipe = IsOpen() ? RecipeAt(GetViewport().GetMousePosition()) : null;

		bool show = _hoveredRecipe != null;
		if (!show)
		{
			_recipeTooltip.Visible = false;
			return;
		}

		var recipe = _hoveredRecipe;

		// (Re)fill the tooltip content when the hovered recipe changes.
		if (_tooltipRecipe != recipe)
		{
			_tooltipRecipe = recipe;
			_recipeTipName.Text = LookUpMaterialName(recipe.ProductId);

			string desc = recipe.Description;
			if (string.IsNullOrEmpty(desc) && _world != null)
			{
				desc = _world.MaterialConfig.GetDescription(recipe.ProductId);
			}
			_recipeTipDesc.Text = string.IsNullOrEmpty(desc) ? " " : desc;

			// Ingredient list.
			foreach (Node child in _recipeTipIngredients.GetChildren())
			{
				child.QueueFree();
			}
			// Build a lookup of (materialId → have) so each ingredient line can
			// be colored red when short.
			var missing = _crafting.MissingIngredients(recipe);
			var missingIds = new HashSet<int>();
			foreach (var (mid, _, _) in missing)
			{
				missingIds.Add(mid);
			}

			foreach (var (materialId, needed) in recipe.Ingredients)
			{
				bool isMissing = missingIds.Contains(materialId);
				var line = new HBoxContainer { LayoutMode = 2 };
				var dot = new TextureRect
				{
					CustomMinimumSize = new Vector2(12f, 12f),
					Size = new Vector2(12f, 12f),
					ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
					StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
					MouseFilter = Control.MouseFilterEnum.Ignore,
				};
				SetIcon(dot, materialId);
				line.AddChild(dot);
				var lbl = new Label
				{
					Text = $"{LookUpMaterialName(materialId)} x{needed}",
					MouseFilter = Control.MouseFilterEnum.Ignore,
				};
				lbl.AddThemeFontSizeOverride("font_size", 13);
				lbl.AddThemeColorOverride("font_color",
					isMissing ? new Color(0.95f, 0.45f, 0.45f) : new Color(0.85f, 0.85f, 0.9f));
				line.AddChild(lbl);
				_recipeTipIngredients.AddChild(line);
			}

			// The tooltip scales to fit its content: size the panel to the
			// VBoxContainer's minimum size (the stacked name/desc/ingredient rows,
			// each sized to its own text) plus the TipPad inset on every side.
			// The size must be set explicitly — a 0x0 panel rect gives the
			// StyleBoxFlat nothing to draw, which is why the background looked
			// invisible. No autowrap: the width is driven by the widest line.
			// TipPad is the same constant used to inset the content (see
			// BuildRecipeTooltip), so the left padding always equals the right.
			const float tipPad = 10f;
			Vector2 contentMin = _recipeTipContent.GetCombinedMinimumSize();
			_recipeTooltip.Size = new Vector2(
				contentMin.X + tipPad * 2f,
				contentMin.Y + tipPad * 2f);
		}

		// Position: to the right of the mouse, clamped on-screen.
		Vector2 mousePos = GetViewport().GetMousePosition();
		Vector2 vpSize = GetViewport().GetVisibleRect().Size;
		Vector2 pos = mousePos + new Vector2(18f, 18f);
		pos.X = Mathf.Min(pos.X, vpSize.X - _recipeTooltip.Size.X - 4f);
		pos.Y = Mathf.Min(pos.Y, vpSize.Y - _recipeTooltip.Size.Y - 4f);
		_recipeTooltip.Position = pos;
		_recipeTooltip.Visible = true;
	}

	/// <summary>Changes the selected hotbar slot by <paramref name="delta"/> (wraps
	/// around). Disabled while the inventory is open.
	/// </summary>
	private void CycleHotbar(int delta)
	{
		if (IsOpen())
		{
			return;
		}
		_selectedHotbarSlot = (_selectedHotbarSlot + delta + Inventory.HotbarSize) % Inventory.HotbarSize;
		RefreshHotbar();
	}

	/// <summary>
	/// Sets the selected hotbar slot directly. Disabled while the inventory
	/// is open.
	/// </summary>
	private void SelectHotbarSlot(int index)
	{
		if (IsOpen() || index < 0 || index >= Inventory.HotbarSize)
		{
			return;
		}
		_selectedHotbarSlot = index;
		RefreshHotbar();
	}

	// ------------------------------------------------------------------
	// Input: the full-screen root sees every click while open
	// ------------------------------------------------------------------

	private void OnRootGuiInput(InputEvent @event)
	{
		if (_inventory == null)
		{
			return;
		}

		if (@event is InputEventMouseButton mouse && mouse.Pressed)
		{
			int slot = SlotAt(mouse.Position);
			if (slot < 0)
			{
				return; // clicked the panel but not a slot: ignore
			}

			if (mouse.ButtonIndex == MouseButton.Left)
			{
				LeftClickSlot(slot);
				_dragSlot = slot; // begin a drag; release handles the drop
			}
			else if (mouse.ButtonIndex == MouseButton.Right)
			{
				RightClickSlot(slot);
			}
		}
		else if (@event is InputEventMouseButton mouseUp && !mouseUp.Pressed
			&& mouseUp.ButtonIndex == MouseButton.Left && _dragSlot >= 0)
		{
			// Left release: if the cursor holds a stack and we are over a
			// (possibly different) slot, place/merge/swap there.
			int under = SlotAt(mouseUp.Position);
			if (!_cursorStack.IsEmpty && under >= 0 && under != _dragSlot)
			{
				PlaceCursorInto(under);
			}
			_dragSlot = -1;
		}
	}

	/// <summary>The slot index under a root-space position, or -1.</summary>
	private int SlotAt(Vector2 position)
	{
		if (_panel == null)
		{
			return -1;
		}

		// Slots are positioned in the panel's space; the panel is a child of
		// the root, so subtract the panel's root-space offset.
		Vector2 local = position - _panel.Position;
		for (int i = 0; i < Inventory.TotalSlots; i++)
		{
			var slot = _slotControls[i];
			if (slot == null)
			{
				continue;
			}
			// Use the slot's position + size directly rather than GetRect()
			// (which can be affected by layout timing).
			var rect = new Rect2(slot.Position, slot.Size);
			if (rect.HasPoint(local))
			{
				return i;
			}
		}
		return -1;
	}

	// ------------------------------------------------------------------
	// Minecraft-style interactions (operating on the cursor stack + slots)
	// ------------------------------------------------------------------

	/// <summary>
	/// Left-click rules:
	/// - cursor empty: pick up the slot's whole stack;
	/// - same material: move as many as fit into the slot (remainder on cursor);
	/// - different material: swap cursor and slot.
	/// </summary>
	private void LeftClickSlot(int slot)
	{
		var slotStack = _inventory.Slots[slot];

		if (_cursorStack.IsEmpty)
		{
			if (slotStack.IsEmpty)
			{
				return;
			}
			_cursorStack = _inventory.TakeFromSlot(slot);
		}
		else if (slotStack.IsEmpty)
		{
			int place = Math.Min(_cursorStack.Count, ItemStack.StackLimit);
			_inventory.Slots[slot] = ItemStack.From(_cursorStack.MaterialId, place);
			_cursorStack = ItemStack.From(_cursorStack.MaterialId, _cursorStack.Count - place);
			Raise();
		}
		else if (slotStack.MaterialId == _cursorStack.MaterialId)
		{
			int space = ItemStack.StackLimit - slotStack.Count;
			int move = Math.Min(space, _cursorStack.Count);
			slotStack.Count += move;
			_cursorStack = ItemStack.From(_cursorStack.MaterialId, _cursorStack.Count - move);
			Raise();
		}
		else
		{
			_inventory.Slots[slot] = _cursorStack;
			_cursorStack = slotStack;
			Raise();
		}

		RefreshSlot(slot);
		RefreshCursor();
	}

	/// <summary>
	/// Right-click rules:
	/// - cursor empty: take half the slot's stack (1 if it is a single);
	/// - slot empty: place 1 item from the cursor;
	/// - same material with room: place 1 item from the cursor.
	/// </summary>
	private void RightClickSlot(int slot)
	{
		var slotStack = _inventory.Slots[slot];

		if (_cursorStack.IsEmpty)
		{
			if (slotStack.IsEmpty)
			{
				return;
			}
			int take = Math.Max(1, slotStack.Count / 2);
			_cursorStack = _inventory.TakePartial(slot, take);
		}
		else if (slotStack.IsEmpty)
		{
			_inventory.Slots[slot] = ItemStack.From(_cursorStack.MaterialId, 1);
			_cursorStack = ItemStack.From(_cursorStack.MaterialId, _cursorStack.Count - 1);
			Raise();
		}
		else if (slotStack.MaterialId == _cursorStack.MaterialId && slotStack.Count < ItemStack.StackLimit)
		{
			slotStack.Count += 1;
			_cursorStack = ItemStack.From(_cursorStack.MaterialId, _cursorStack.Count - 1);
			Raise();
		}
		else
		{
			return; // nothing fits the right-click rules
		}

		RefreshSlot(slot);
		RefreshCursor();
	}

	/// <summary>Drop-end: place the cursor stack into <paramref name="slot"/> (place / merge / swap).</summary>
	private void PlaceCursorInto(int slot)
	{
		var slotStack = _inventory.Slots[slot];

		if (slotStack.IsEmpty)
		{
			int place = Math.Min(_cursorStack.Count, ItemStack.StackLimit);
			_inventory.Slots[slot] = ItemStack.From(_cursorStack.MaterialId, place);
			_cursorStack = ItemStack.From(_cursorStack.MaterialId, _cursorStack.Count - place);
		}
		else if (slotStack.MaterialId == _cursorStack.MaterialId)
		{
			int space = ItemStack.StackLimit - slotStack.Count;
			int move = Math.Min(space, _cursorStack.Count);
			slotStack.Count += move;
			_cursorStack = ItemStack.From(_cursorStack.MaterialId, _cursorStack.Count - move);
		}
		else
		{
			_inventory.Slots[slot] = _cursorStack;
			_cursorStack = slotStack;
		}

		Raise();
		RefreshSlot(slot);
		RefreshCursor();
	}

	/// <summary>Bumps the dirty flag (slot visuals + cursor refresh next frame).</summary>
	private void Raise()
	{
		_dirty = true;
	}
}
