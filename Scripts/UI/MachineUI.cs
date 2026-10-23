using Godot;
using System;
using FactorySim.Items;
using FactorySim.Machines;

namespace FactorySim.UI;

/// <summary>
/// The machine interaction UI — a centered <see cref="CanvasLayer"/> panel (built
/// entirely in code, like <see cref="InventoryUI"/>) that opens when the player
/// RIGHT-CLICKS a targeted machine (see <see cref="FactorySim.Voxel.VoxelTool"/>'s
/// right-click handler). It shows:
///
///   - The machine's display name.
///   - A status line (power state / output / craft progress).
///   - For a <see cref="CrafterMachine"/>: a recipe picker (a dropdown of the
///     shared crafting database's recipes) and a craft-progress bar.
///   - The machine's INPUT and OUTPUT buffer slots (a small slot grid). Left-click
///     a buffer slot to move ONE item between it and the player's selected hotbar
///     slot; right-click to move a WHOLE stack. Direction is decided by which
///     side has the item.
///
/// While open the mouse is released and the panel's root eats clicks (the same
/// convention as the inventory); closing (Tab, Escape, or the close button)
/// returns the mouse capture to the player. The panel hides itself when its
/// target machine is freed (e.g. broken while open).
/// </summary>
public partial class MachineUI : CanvasLayer
{
	private const float SlotSize = 48f;

	private Machine _machine;
	private Inventory _inventory;

	private Control _root;
	private Panel _panel;
	private Label _titleLabel;
	private Label _statusLabel;
	private ColorRect _progressBg;
	private ColorRect _progressFill;
	private Label _progressLabel;
	private OptionButton _recipePicker;
	private Label _hintLabel;
	private Button _closeButton;
	private VBoxContainer _slotStack;

	// The buffer slot controls, keyed by buffer (so RefreshSlotsIfChanged can
	// tell when to rebuild). Rebuilt whenever a buffer's contents change.
	private readonly System.Collections.Generic.Dictionary<MachineBuffer, System.Collections.Generic.List<Control>> _bufferSlotControls
		= new();

	private int _lastInputTotal = -1;
	private int _lastOutputTotal = -1;

	public override void _Ready()
	{
		AddToGroup("machine_ui");

		// The full-rect root: Stop while open (eats clicks), Ignore while hidden.
		_root = new Control
		{
			Name = "MachineUIRoot",
			LayoutMode = 3, // Position
		};
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.MouseFilter = Control.MouseFilterEnum.Ignore;
		_root.Visible = false;
		AddChild(_root);

		// The centered panel.
		_panel = new Panel
		{
			Name = "MachinePanel",
			LayoutMode = 3, // Position
		};
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0.08f, 0.09f, 0.12f, 0.92f),
			BorderColor = new Color(0.4f, 0.6f, 0.8f, 0.9f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 16f,
			ContentMarginTop = 14f,
			ContentMarginRight = 16f,
			ContentMarginBottom = 14f,
		};
		_panel.AddThemeStyleboxOverride("panel", style);
		_panel.Size = new Vector2(360f, 320f);
		_panel.Position = new Vector2(
			(1920f - 360f) * 0.5f,
			(1080f - 320f) * 0.5f);
		_root.AddChild(_panel);

		var vbox = new VBoxContainer
		{
			LayoutMode = 3, // Position
		};
		vbox.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		vbox.AddThemeConstantOverride("separation", 8);
		_panel.AddChild(vbox);

		_titleLabel = new Label
		{
			Text = "Machine",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
		};
		_titleLabel.AddThemeFontSizeOverride("font_size", 18);
		_titleLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.9f, 1.0f));
		vbox.AddChild(_titleLabel);

		_statusLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
		};
		_statusLabel.AddThemeFontSizeOverride("font_size", 13);
		_statusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
		vbox.AddChild(_statusLabel);

		// Progress bar (visible only for a crafter with a recipe).
		_progressBg = new ColorRect
		{
			Color = new Color(0.1f, 0.1f, 0.12f, 0.9f),
			CustomMinimumSize = new Vector2(0f, 8f),
			LayoutMode = 2,
			Visible = false,
		};
		vbox.AddChild(_progressBg);
		_progressFill = new ColorRect
		{
			Color = new Color(0.3f, 0.8f, 0.4f, 1f),
			LayoutMode = 3, // Position
			Position = Vector2.Zero,
		};
		_progressBg.AddChild(_progressFill);
		_progressLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
			Visible = false,
		};
		_progressLabel.AddThemeFontSizeOverride("font_size", 12);
		vbox.AddChild(_progressLabel);

		// Recipe picker (visible only for a crafter).
		_recipePicker = new OptionButton
		{
			LayoutMode = 2,
			Visible = false,
		};
		_recipePicker.ItemSelected += OnRecipeSelected;
		vbox.AddChild(_recipePicker);

		// Buffer slot grid (rebuilt on open / on content change).
		_slotStack = new VBoxContainer
		{
			LayoutMode = 2,
		};
		_slotStack.AddThemeConstantOverride("separation", 6);
		vbox.AddChild(_slotStack);

		// A hint line for how to move items in/out.
		_hintLabel = new Label
		{
			Text = "Left-click a slot: move 1 item in/out.\nRight-click a slot: move a whole stack.",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
		};
		_hintLabel.AddThemeFontSizeOverride("font_size", 11);
		_hintLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.65f, 0.7f));
		vbox.AddChild(_hintLabel);

		// Close button at the bottom.
		_closeButton = new Button
		{
			Text = "Close (Tab)",
			LayoutMode = 2,
		};
		_closeButton.Pressed += Close;
		vbox.AddChild(_closeButton);
	}

	/// <summary>
	/// Opens the UI for <paramref name="machine"/>, wiring it to the player's
	/// <paramref name="inventory"/> for item transfer. Calling it with the SAME
	/// machine that is already open closes it (toggle). Called deferred by the
	/// VoxelTool's right-click handler.
	/// </summary>
	public void OpenFor(Machine machine, Inventory inventory)
	{
		if (machine == null || !IsInstanceValid(machine))
		{
			Close();
			return;
		}
		// Toggle: right-clicking the already-open machine closes it.
		if (_machine == machine && _root.Visible)
		{
			Close();
			return;
		}

		_machine = machine;
		_inventory = inventory;

		SetOpen(true);
		BuildContent();
	}

	private void SetOpen(bool open)
	{
		if (_root == null)
		{
			return;
		}
		_root.Visible = open;
		// While open the root EATS mouse events so the 3D view is untouched;
		// while hidden it IGNORES them so the world is interactive.
		_root.MouseFilter = open
			? Control.MouseFilterEnum.Stop
			: Control.MouseFilterEnum.Ignore;
		if (open)
		{
			Input.SetMouseMode(Input.MouseModeEnum.Visible);
		}
	}

	/// <summary>Closes the UI and returns mouse capture to the player.</summary>
	public void Close()
	{
		if (_root == null || !_root.Visible)
		{
			return;
		}
		SetOpen(false);
		_machine = null;
		_inventory = null;
		ClearSlots();
		Input.SetMouseMode(Input.MouseModeEnum.Captured);
	}

	/// <summary>
	/// Rebuilds the panel's contents (title, status, recipe picker, buffer slots)
	/// for the current target machine.
	/// </summary>
	private void BuildContent()
	{
		if (_machine == null || _machine.Definition == null || !IsInstanceValid(_machine))
		{
			Close();
			return;
		}

		string name = string.IsNullOrEmpty(_machine.Definition.Name)
			? _machine.Type.ToString()
			: _machine.Definition.Name;
		_titleLabel.Text = name;

		// Status line: power state / output.
		string status = "";
		var def = _machine.Definition;
		if (def.PowerConsumption > 0f)
		{
			status += PowerNetwork.IsPowered(_machine.World, _machine.VoxelPos)
				? "Powered"
				: "NEEDS POWER";
		}
		else if (def.PowerOutput > 0f)
		{
			status += $"Outputs {def.PowerOutput:0.#} power";
		}
		_statusLabel.Text = status;

		// Recipe picker: for a crafter (craft recipes) or a smelter (smelt
		// recipes from the definition's paired input/output lists). Both use the
		// same OptionButton + progress bar; only the label and progress source
		// differ.
		if (_machine is CrafterMachine crafter)
		{
			_recipePicker.Visible = true;
			_recipePicker.Clear();
			_recipePicker.AddItem("(No recipe — idle)", 0);
			var recipes = crafter.AvailableRecipes;
			for (int i = 0; i < recipes.Count; i++)
			{
				string prodName = ItemName(recipes[i].ProductId);
				_recipePicker.AddItem($"Craft {prodName}", i + 1);
			}
			int currentIdx = 0;
			for (int i = 0; i < recipes.Count; i++)
			{
				if (recipes[i] == crafter.Recipe)
				{
					currentIdx = i + 1;
					break;
				}
			}
			_recipePicker.Selected = currentIdx;

			bool showProgress = crafter.Recipe != null;
			_progressBg.Visible = showProgress;
			_progressLabel.Visible = showProgress;
		}
		else if (_machine is SmelterMachine smelter)
		{
			_recipePicker.Visible = true;
			_recipePicker.Clear();
			_recipePicker.AddItem("(No recipe — idle)", 0);
			var smeltRecipes = smelter.SmeltableRecipes;
			for (int i = 0; i < smeltRecipes.Count; i++)
			{
				var (input, output) = smeltRecipes[i];
				string inName = ItemName(input);
				string outName = ItemName(output);
				_recipePicker.AddItem($"Smelt {inName} → {outName}", i + 1);
			}
			// The smelter auto-selects its first recipe on start, so the current
			// index is smelter.RecipeIndex (0-based) + 1 (the picker's 1-based
			// index); fall back to 0 (idle) when it is -1 or out of range.
			int currentIdx = (smelter.RecipeIndex >= 0 && smelter.RecipeIndex < smeltRecipes.Count)
				? smelter.RecipeIndex + 1
				: 0;
			_recipePicker.Selected = currentIdx;

			bool showProgress = smelter.RecipeIndex >= 0;
			_progressBg.Visible = showProgress;
			_progressLabel.Visible = showProgress;
		}
		else
		{
			_recipePicker.Visible = false;
			_progressBg.Visible = false;
			_progressLabel.Visible = false;
		}

		RebuildBufferSlots();
	}

	/// <summary>
	/// (Re)builds the buffer slot grid: one row per buffer (input, then output).
	/// Each slot is a clickable control that transfers items with the player's
	/// selected hotbar slot.
	/// </summary>
	private void RebuildBufferSlots()
	{
		ClearSlots();

		void AddRow(MachineBuffer buffer)
		{
			if (buffer == null || buffer.Capacity == 0)
			{
				return;
			}
			var row = new HBoxContainer
			{
				LayoutMode = 2,
			};
			row.AddThemeConstantOverride("separation", 4);
			var list = new System.Collections.Generic.List<Control>();
			for (int i = 0; i < buffer.Capacity; i++)
			{
				var slot = MakeSlotControl(buffer, i);
				row.AddChild(slot);
				list.Add(slot);
			}
			_bufferSlotControls[buffer] = list;
			_slotStack.AddChild(row);
		}

		AddRow(_machine.InputBuffer);
		AddRow(_machine.OutputBuffer);
	}

	/// <summary>
	/// Creates a single clickable buffer slot (icon + count). Left-click moves
	/// one item; right-click moves a whole stack, between the slot and the
	/// player's selected hotbar slot.
	/// </summary>
	private Control MakeSlotControl(MachineBuffer buffer, int index)
	{
		var stack = buffer[index];
		var slot = new Panel
		{
			CustomMinimumSize = new Vector2(SlotSize, SlotSize),
			Size = new Vector2(SlotSize, SlotSize),
			LayoutMode = 3, // Position
		};
		var slotStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.12f, 0.13f, 0.16f, 1f),
			BorderColor = new Color(0.35f, 0.45f, 0.55f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
		};
		slot.AddThemeStyleboxOverride("panel", slotStyle);

		if (!stack.IsEmpty)
		{
			var icon = new ColorRect
			{
				Color = ItemIconColor(stack.MaterialId),
				Size = new Vector2(SlotSize - 12f, SlotSize - 12f),
				Position = new Vector2(6f, 6f),
				LayoutMode = 3, // Position
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			slot.AddChild(icon);

			var count = new Label
			{
				Text = stack.Count.ToString(),
				Position = new Vector2(SlotSize - 22f, SlotSize - 18f),
				LayoutMode = 3, // Position
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			count.AddThemeFontSizeOverride("font_size", 12);
			slot.AddChild(count);
		}

		// A click-catcher that fills the slot and routes LMB/RMB.
		var click = new Control
		{
			LayoutMode = 3, // Position
		};
		click.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		click.GuiInput += ev =>
		{
			if (ev is InputEventMouseButton mb && mb.Pressed)
			{
				if (mb.ButtonIndex == MouseButton.Left)
				{
					TransferOne(buffer, index);
				}
				else if (mb.ButtonIndex == MouseButton.Right)
				{
					TransferStack(buffer, index);
				}
			}
		};
		slot.AddChild(click);
		return slot;
	}

	/// <summary>
	/// The player's selected hotbar slot (0..9), resolved from the InventoryUI
	/// (which owns the selection). -1 when the UI is not found.
	/// </summary>
	private int SelectedHotbarSlot
	{
		get
		{
			foreach (var n in GetTree().GetNodesInGroup("inventory_ui"))
			{
				if (n is InventoryUI ui)
				{
					return ui.SelectedHotbarSlot;
				}
			}
			return -1;
		}
	}

	/// <summary>Moves ONE item between the buffer slot and the selected hotbar slot.</summary>
	private void TransferOne(MachineBuffer buffer, int index)
	{
		int hotbar = SelectedHotbarSlot;
		if (_inventory == null || hotbar < 0)
		{
			return;
		}
		var held = _inventory.Slots[hotbar];
		var slotStack = buffer[index];

		if (!slotStack.IsEmpty && held.IsEmpty)
		{
			var taken = buffer.TryExtract(slotStack.MaterialId, 1);
			if (!taken.IsEmpty)
			{
				_inventory.AddToSlot(hotbar, taken);
			}
		}
		else if (!held.IsEmpty && slotStack.IsEmpty)
		{
			int inserted = buffer.TryInsert(ItemStack.From(held.MaterialId, 1));
			if (inserted > 0)
			{
				_inventory.TakePartial(hotbar, inserted);
			}
		}
		// Same material on both sides: ambiguous, do nothing.

		_inventory.RaiseChangedPublic();
		BuildContent();
	}

	/// <summary>Moves a WHOLE stack between the buffer slot and the selected hotbar slot.</summary>
	private void TransferStack(MachineBuffer buffer, int index)
	{
		int hotbar = SelectedHotbarSlot;
		if (_inventory == null || hotbar < 0)
		{
			return;
		}
		var held = _inventory.Slots[hotbar];
		var slotStack = buffer[index];

		if (!slotStack.IsEmpty && held.IsEmpty)
		{
			var taken = buffer.TryExtract(slotStack.MaterialId, slotStack.Count);
			if (!taken.IsEmpty)
			{
				_inventory.AddToSlot(hotbar, taken);
			}
		}
		else if (!held.IsEmpty && slotStack.IsEmpty)
		{
			int inserted = buffer.TryInsert(ItemStack.From(held.MaterialId, held.Count));
			if (inserted > 0)
			{
				_inventory.TakePartial(hotbar, inserted);
			}
		}

		_inventory.RaiseChangedPublic();
		BuildContent();
	}

	/// <summary>A recipe was selected (index 0 = none, 1..N = the Nth recipe). Handles both the crafter (craft recipes) and the smelter (smelt recipes from the definition's paired lists).</summary>
	private void OnRecipeSelected(long index)
	{
		if (_machine is CrafterMachine crafter)
		{
			if (index == 0)
			{
				crafter.Recipe = null;
			}
			else
			{
				var recipes = crafter.AvailableRecipes;
				int i = (int)index - 1;
				crafter.Recipe = i >= 0 && i < recipes.Count ? recipes[i] : null;
			}
		}
		else if (_machine is SmelterMachine smelter)
		{
			// The smelter's recipe index is 0-based (into SmeltableRecipes); the
			// picker is 1-based, so index 0 = idle (-1), index N = recipe N-1.
			smelter.RecipeIndex = index == 0 ? -1 : (int)index - 1;
		}
		BuildContent();
	}

	/// <summary>Clears the buffer slot grid and its control registry.</summary>
	private void ClearSlots()
	{
		if (_slotStack != null)
		{
			foreach (var child in _slotStack.GetChildren())
			{
				child.QueueFree();
			}
		}
		_bufferSlotControls.Clear();
	}

	public override void _Process(double delta)
	{
		if (_root == null || !_root.Visible || _machine == null)
		{
			if (_machine != null)
			{
				Close();
			}
			return;
		}
		if (!IsInstanceValid(_machine))
		{
			Close();
			return;
		}

		if (_machine is CrafterMachine crafter)
		{
			_progressFill.Size = new Vector2(
				_progressBg.Size.X * Mathf.Clamp(crafter.CraftProgress, 0f, 1f),
				_progressBg.Size.Y);
			if (crafter.Recipe != null)
			{
				_progressLabel.Text = $"Crafting: {Mathf.RoundToInt(crafter.CraftProgress * 100f)}%";
			}
		}
		else if (_machine is SmelterMachine smelter)
		{
			_progressFill.Size = new Vector2(
				_progressBg.Size.X * Mathf.Clamp(smelter.SmeltProgress, 0f, 1f),
				_progressBg.Size.Y);
			if (smelter.RecipeIndex >= 0)
			{
				_progressLabel.Text = $"Smelting: {Mathf.RoundToInt(smelter.SmeltProgress * 100f)}%";
			}
		}

		RefreshSlotsIfChanged();
	}

	/// <summary>Rebuilds the buffer slot controls only when a buffer's contents changed.</summary>
	private void RefreshSlotsIfChanged()
	{
		int inputTotal = _machine.InputBuffer?.TotalCount ?? 0;
		int outputTotal = _machine.OutputBuffer?.TotalCount ?? 0;
		if (inputTotal != _lastInputTotal || outputTotal != _lastOutputTotal)
		{
			_lastInputTotal = inputTotal;
			_lastOutputTotal = outputTotal;
			RebuildBufferSlots();
		}
	}

	/// <summary>Handles Tab/Escape to close the UI (while open).</summary>
	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (_root == null || !_root.Visible)
		{
			return;
		}
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.Tab || key.Keycode == Key.Escape
				|| key.PhysicalKeycode == Key.Tab || key.PhysicalKeycode == Key.Escape)
			{
				Close();
			}
		}
	}

	/// <summary>The display name for an item (material id) via the world's material config.</summary>
	private string ItemName(int materialId)
	{
		foreach (var n in GetTree().GetNodesInGroup("voxel_world"))
		{
			if (n is FactorySim.Voxel.VoxelWorld w && w.MaterialConfig != null)
			{
				return ItemRegistry.MaterialName(w.MaterialConfig, materialId);
			}
		}
		return $"Item {materialId}";
	}

	/// <summary>A flat color per material id, used as the slot icon.</summary>
	private static Color ItemIconColor(int materialId)
	{
		float hue = (materialId * 137.508f) % 360f; // golden-angle spread
		return Color.FromHsv(hue / 360f, 0.6f, 0.9f);
	}
}
