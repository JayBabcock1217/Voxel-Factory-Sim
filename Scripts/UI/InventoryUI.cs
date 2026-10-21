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
/// Layout: a centered panel with 28 slots — 2 rows of 10 for storage
/// (inventory slots 10-27) and a bottom row of 10 for the hotbar
/// (slots 0-9) — plus a cursor stack that follows the mouse while dragging.
/// A persistent 10-slot hotbar is also rendered at the bottom of the screen
/// (always visible) with a selection highlight; number keys 1-0 and the mouse
/// wheel change the selected slot.
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
	private readonly ColorRect[] _slotIcons = new ColorRect[Inventory.TotalSlots];
	private readonly Label[] _slotCounts = new Label[Inventory.TotalSlots];

	private Control _cursor;
	private ColorRect _cursorIcon;
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
	private readonly Dictionary<CraftingRecipe, ColorRect> _recipeIcons = new();
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

	// Persistent hotbar (always visible at the bottom of the screen).
	private Panel _hotbar;
	private readonly Control[] _hotbarSlots = new Control[Inventory.HotbarSize];
	private readonly ColorRect[] _hotbarIcons = new ColorRect[Inventory.HotbarSize];
	private readonly Label[] _hotbarCounts = new Label[Inventory.HotbarSize];
	private ReferenceRect _hotbarHighlight;
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

		// Number keys 1-9,0 select hotbar slots (disabled while inventory is open).
		if (!IsOpen())
		{
			int slot = -1;
			if (key.Keycode >= Key.Key1 && key.Keycode <= Key.Key9)
			{
				slot = (int)key.Keycode - (int)Key.Key1; // 0-8
			}
			else if (key.Keycode == Key.Key0)
			{
				slot = 9;
			}
			else if (key.PhysicalKeycode >= Key.Key1 && key.PhysicalKeycode <= Key.Key9)
			{
				slot = (int)key.PhysicalKeycode - (int)Key.Key1;
			}
			else if (key.PhysicalKeycode == Key.Key0)
			{
				slot = 9;
			}

			if (slot >= 0)
			{
				SelectHotbarSlot(slot);
			}
		}
	}

	/// <summary>Mouse wheel cycles the hotbar selection (disabled while inventory is open).</summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton wheel
			&& wheel.Pressed
			&& (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
		{
			CycleHotbar(wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1);
		}
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

		const int storageCols = 9;
		// Panel width is driven by the widest row (the 10-slot hotbar).
		float gridW = Inventory.HotbarSize * SlotSize + (Inventory.HotbarSize - 1) * SlotGap;
		float gridH = 3 * SlotSize + 2 * SlotGap;
		float panelW = gridW + 24f;
		float panelH = gridH + 56f;
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		Vector2 panelPos = new Vector2(
			(viewportSize.X - panelW) / 2f,
			(viewportSize.Y - panelH) / 2f);

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
			Position = new Vector2(12f, 8f),
			Size = new Vector2(panelW - 24f, 20f),
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		title.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		_panel.AddChild(title);

		// Slot grid: rows 0-1 = storage (9 cols each, slots 10-27),
		// row 2 = hotbar (10 cols, slots 0-9).
		// Storage rows are centered within the 10-column-wide panel.
		float gridY = 36f;
		float storageRowW = storageCols * SlotSize + (storageCols - 1) * SlotGap;
		float storageOffsetX = (gridW - storageRowW) / 2f;
		for (int row = 0; row < 3; row++)
		{
			int cols = row < 2 ? storageCols : Inventory.HotbarSize;
			float rowX = row < 2 ? 12f + storageOffsetX : 12f;
			for (int col = 0; col < cols; col++)
			{
				int slotIndex = row < 2
					? Inventory.HotbarSize + row * storageCols + col
					: col;
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
		_cursorIcon = new ColorRect
		{
			LayoutMode = 3, // Position
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
			var slotBg = new ColorRect
			{
				Color = new Color(0.1f, 0.1f, 0.12f, 0.7f),
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			slotBg.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
			slot.AddChild(slotBg);

			_hotbarIcons[i] = new ColorRect
			{
				Color = Colors.Transparent,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				LayoutMode = 3, // Position
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

		// Selection highlight: a ReferenceRect that moves to the selected slot.
		_hotbarHighlight = new ReferenceRect
		{
			Name = "HotbarHighlight",
			LayoutMode = 3, // Position
			Size = new Vector2(hbSlotSize, hbSlotSize),
			Position = new Vector2(hbPad, hbPad),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_hotbarHighlight.AddThemeColorOverride("border_color", new Color(1f, 1f, 1f, 0.9f));
		_hotbarHighlight.AddThemeConstantOverride("width", 2);
		// No fill — just the border.
		var highlightStyle = new StyleBoxEmpty();
		_hotbarHighlight.AddThemeStyleboxOverride("panel", highlightStyle);
		_hotbar.AddChild(_hotbarHighlight);
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
		float gridH = 3 * SlotSize + 2 * SlotGap;
		const float craftW = 320f;   // wider crafting pane
		const float gap = 16f;        // gap between the slot grid and the pane
		const float pad = 12f;        // panel padding (matches BuildUi)

		// Panel width = left pad + grid + gap + craft pane + right pad.
		float panelW = pad + gridW + gap + craftW + pad;
		float panelH = Math.Max(gridH, 280f) + 56f;

		// Center the whole panel (inventory + crafting) in the viewport.
		Vector2 vpSize = GetViewport().GetVisibleRect().Size;
		_panel.Position = new Vector2(
			Math.Max(0f, (vpSize.X - panelW) / 2f),
			Math.Max(0f, (vpSize.Y - panelH) / 2f));
		_panel.Size = new Vector2(panelW, panelH);

		// The slot grid starts at x = pad (set in BuildUi); the craft pane sits
		// to its right after the gap.
		float craftX = pad + gridW + gap;

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

		_slotIcons[slotIndex] = new ColorRect
		{
			Name = "Icon",
			LayoutMode = 3, // Position
			Color = Colors.Transparent,
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
			_slotIcons[i].Color = Colors.Transparent;
			_slotCounts[i].Text = "";
			return;
		}

		_slotIcons[i].Color = LookUpColor(stack.MaterialId);
		// Show the count only for partial stacks (full stacks match Minecraft's look).
		_slotCounts[i].Text = stack.Count.ToString();
	}

	/// <summary>
	/// The material's albedo from the world palette (index <c>materialId - 1</c>,
	/// grey fallback when unconfigured).
	/// </summary>
	private Color LookUpColor(int materialId)
	{
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
	/// The material's display name from the world's material config, or a
	/// generic fallback for unconfigured ids.
	/// </summary>
	private string LookUpMaterialName(int materialId)
	{
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

		_cursorIcon.Color = LookUpColor(_cursorStack.MaterialId);
		_cursorCount.Text = _cursorStack.Count.ToString();
	}

	/// <summary>
	/// Updates the persistent hotbar: item icons/counts for slots 0-9 and the
	/// selection highlight position.
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
				_hotbarIcons[i].Color = Colors.Transparent;
				_hotbarCounts[i].Text = "";
			}
			else
			{
				_hotbarIcons[i].Color = LookUpColor(stack.MaterialId);
				_hotbarCounts[i].Text = stack.Count.ToString();
			}
		}

		// Move the selection highlight to the selected slot.
		if (_hotbarHighlight != null)
		{
			float hbPad = 4f, hbSlotSize = 44f, hbGap = 4f;
			_hotbarHighlight.Position = new Vector2(
				hbPad + _selectedHotbarSlot * (hbSlotSize + hbGap),
				hbPad);
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
		var icon = new ColorRect
		{
			Color = LookUpColor(recipe.ProductId),
			LayoutMode = 3, // Position
			Position = new Vector2(6f, 5f),
			Size = new Vector2(iconSize, iconSize),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
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
				var dot = new ColorRect
				{
					Color = LookUpColor(materialId),
					CustomMinimumSize = new Vector2(12f, 12f),
					Size = new Vector2(12f, 12f),
					MouseFilter = Control.MouseFilterEnum.Ignore,
				};
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
