using Godot;
using System;
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
		_inventory.InventoryChanged += () => _dirty = true;
		_inventory.Toggled += SetOpenVisuals;

		BuildUi();
		RefreshHotbar();
	}

	public override void _Process(double _)
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

	/// <summary>
	/// Changes the selected hotbar slot by <paramref name="delta"/> (wraps
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
