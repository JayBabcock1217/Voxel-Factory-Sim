using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;
using FactorySim.Machines;

namespace FactorySim.UI;

/// <summary>
/// The machine interaction UI. When the player right-clicks a targeted machine
/// (see <see cref="FactorySim.Voxel.VoxelTool"/>), this opens the player's
/// INVENTORY and shows the machine's panel in the inventory's RIGHT SLOT — the
/// exact place where the crafting pane lives. While the machine panel is open,
/// the crafting pane is HIDDEN, so only one right pane exists at a time.
///
/// The machine panel lives in its own <see cref="CanvasLayer"/> (layer 120,
/// above the inventory) and is positioned every frame over the inventory's
/// right slot (read from <see cref="InventoryUI.GetRightSlotRect"/>). This is
/// the proven, robust way to do it: no cross-CanvasLayer reparenting into a
/// container, no layout-mode conflicts — just a panel on top, anchored to the
/// craft slot.
///
/// Layout (the machine panel, over the craft slot):
///   - The machine's display name.
///   - A status line (power state).
///   - A recipe picker (dropdown): for a crafter, the shared crafting database's
///     recipes; for a smelter, the definition's paired smelt recipes.
///   - A craft/smelt progress bar.
///   - INPUT slots (one per distinct input material of the selected recipe) and
///     OUTPUT slots (one per distinct output material). The COUNT of input and
///     output slots is driven by the selected recipe, Factorio-style: each slot
///     is the machine's mini-inventory for that one item type.
///
/// The input/output slots behave exactly like inventory slots (they are built
/// by the shared <see cref="InventorySlotGrid"/>): left-click picks up / places /
/// merges / swaps, left-drag drops, right-click splits. They are backed by the
/// machine's <see cref="MachineBuffer"/> (the machine's own mini-inventory, which
/// is also what conveyors pull from / push into), so items the player drags in
/// are the same items the machine feeds on.
///
/// Opening a machine ALWAYS brings up the inventory (the machine panel takes the
/// craft slot's place); closing it (Tab, Escape, or the close button) shows the
/// craft pane again and closes the inventory, returning the mouse to the player.
/// </summary>
public partial class MachineUI : CanvasLayer
{
	private const float SlotSize = 48f;
	private const float SlotGap = 4f;
	// The panel's INITIAL size (a fallback before the first anchor). It is resized
	// to the inventory's craft-pane rect every frame in AnchorToRightSlot, so it
	// fits the right slot vertically exactly like the crafting pane.
	private const float InitialPanelW = 400f;
	private const float InitialPanelH = 356f; // ~ gridH + 12f (the craft pane height)

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
	private Label _inputHeading;
	private Label _outputHeading;
	private Control _inputGrid;
	private Control _outputGrid;
	private Button _closeButton;
	// The content VBox (inset from the panel's edges). Resized in AnchorToRightSlot
	// to track the panel's size (which changes to fit the craft-pane rect).
	private VBoxContainer _contentBox;
	private const float PanelInsetX = 14f;
	private const float PanelInsetY = 12f;

	// The slot controls + their bound materials, rebuilt whenever the recipe
	// (and thus the slot count / mapping) changes.
	private readonly List<Control> _inputSlots = new();
	private readonly List<Control> _outputSlots = new();
	private readonly List<int> _inputSlotMats = new();
	private readonly List<int> _outputSlotMats = new();

	// The InventoryUI (a sibling CanvasLayer) used to read the right-slot rect
	// and hide/show the craft pane. Resolved lazily (the group may not be ready
	// at _Ready).
	private InventoryUI _invUi;

	public override void _Ready()
	{
		AddToGroup("machine_ui");

		// Full-rect root: Stop while open (eats clicks so the 3D view is
		// untouched), Ignore while hidden. It is the panel's parent and the
		// visibility/focus flag for the machine UI.
		_root = new Control
		{
			Name = "MachineUIRoot",
			LayoutMode = 3, // Position
		};
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.MouseFilter = Control.MouseFilterEnum.Ignore;
		_root.Visible = false;
		AddChild(_root);

		// The panel, centered-ish in the root. It is repositioned every frame
		// over the inventory's right slot (see _Process / AnchorToRightSlot),
		// so the initial position is just a fallback.
		_panel = new Panel
		{
			Name = "MachinePanel",
			LayoutMode = 3, // Position
		};
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0.08f, 0.09f, 0.12f, 0.95f),
			BorderColor = new Color(0.4f, 0.6f, 0.8f, 0.9f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 14f,
			ContentMarginTop = 12f,
			ContentMarginRight = 14f,
			ContentMarginBottom = 12f,
		};
		_panel.AddThemeStyleboxOverride("panel", style);
		_panel.Size = new Vector2(InitialPanelW, InitialPanelH);
		// When the panel REGAINS keyboard focus, re-capture the mouse. This is
		// the reliable signal that the recipe-picker dropdown just CLOSED (the
		// popup holds focus/mouse while open, and returning focus to the panel
		// means the pick is done). Without it, selecting a recipe left the mouse
		// hidden ("the mouse disappears") because the popup system set it to
		// Visible to let you pick, and nothing re-captured it on close.
		_panel.FocusEntered += () =>
		{
			if (_root != null && _root.Visible)
			{
				Input.SetMouseMode(Input.MouseModeEnum.Captured);
			}
		};
		_root.AddChild(_panel);

		_contentBox = new VBoxContainer
		{
			LayoutMode = 3, // Position,
		};
		_contentBox.AddThemeConstantOverride("separation", 8);
		// Inset the content from the panel's edges so it doesn't hug the border.
		// Position/Size are (re)applied in AnchorToRightSlot each frame (the panel
		// resizes to the craft-pane rect).
		_contentBox.Position = new Vector2(PanelInsetX, PanelInsetY);
		_contentBox.Size = new Vector2(
			_panel.Size.X - PanelInsetX * 2f,
			_panel.Size.Y - PanelInsetY * 2f);
		_panel.AddChild(_contentBox);

		_titleLabel = new Label
		{
			Text = "Machine",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
		};
		_titleLabel.AddThemeFontSizeOverride("font_size", 18);
		_titleLabel.AddThemeColorOverride("font_color", new Color(0.6f, 0.9f, 1.0f));
		_contentBox.AddChild(_titleLabel);

		_statusLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
		};
		_statusLabel.AddThemeFontSizeOverride("font_size", 13);
		_statusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.9f));
		_contentBox.AddChild(_statusLabel);

		// Recipe picker (visible only for a crafter / smelter).
		_recipePicker = new OptionButton
		{
			LayoutMode = 2,
			Visible = false,
		};
		_recipePicker.ItemSelected += OnRecipeSelected;
		// The recipe picker is clickable by mouse but NOT in the keyboard
		// tab-order, so Tab/Escape always flow to the _Input close handler.
		_recipePicker.FocusMode = Control.FocusModeEnum.Click;
		_contentBox.AddChild(_recipePicker);

		// Progress bar (visible only for a crafter / smelter with a recipe).
		_progressBg = new ColorRect
		{
			Color = new Color(0.1f, 0.1f, 0.12f, 0.9f),
			CustomMinimumSize = new Vector2(0f, 8f),
			LayoutMode = 2,
			Visible = false,
		};
		_progressFill = new ColorRect
		{
			Color = new Color(0.3f, 0.8f, 0.4f, 1f),
			LayoutMode = 3, // Position,
			Position = Vector2.Zero,
		};
		_progressBg.AddChild(_progressFill);
		_contentBox.AddChild(_progressBg);
		_progressLabel = new Label
		{
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			LayoutMode = 2,
			Visible = false,
		};
		_progressLabel.AddThemeFontSizeOverride("font_size", 12);
		_contentBox.AddChild(_progressLabel);

		// INPUT heading + slot grid (rebuilt per recipe).
		_inputHeading = new Label
		{
			Text = "Input",
			LayoutMode = 2,
		};
		_inputHeading.AddThemeFontSizeOverride("font_size", 13);
		_inputHeading.AddThemeColorOverride("font_color", new Color(0.85f, 0.7f, 0.5f));
		_contentBox.AddChild(_inputHeading);
		_inputGrid = new Control
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0f, SlotSize),
		};
		_contentBox.AddChild(_inputGrid);

		// OUTPUT heading + slot grid.
		_outputHeading = new Label
		{
			Text = "Output",
			LayoutMode = 2,
		};
		_outputHeading.AddThemeFontSizeOverride("font_size", 13);
		_outputHeading.AddThemeColorOverride("font_color", new Color(0.6f, 0.9f, 0.7f));
		_contentBox.AddChild(_outputHeading);
		_outputGrid = new Control
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0f, SlotSize),
		};
		_contentBox.AddChild(_outputGrid);

		// Spacer pushes the close button to the bottom.
		var spacer = new Control
		{
			LayoutMode = 2,
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
		};
		_contentBox.AddChild(spacer);

		// Close button at the bottom.
		_closeButton = new Button
		{
			Text = "Close (Tab)",
			LayoutMode = 2,
		};
		_closeButton.Pressed += Close;
		_contentBox.AddChild(_closeButton);
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
		_resolveInvUi();

		SetOpen(true);
		// Opening a machine ALWAYS brings the inventory up with it: the machine
		// panel takes the inventory's right slot's place (the craft pane is
		// hidden), so the player sees their items (and can drag them into the
		// machine) the whole time.
		if (inventory != null && !inventory.IsOpen)
		{
			inventory.SetOpen(true);
		}
		SetCraftPaneVisible(false);
		BuildContent();
	}

	private void SetOpen(bool open)
	{
		if (_root == null)
		{
			return;
		}
		_root.Visible = open;
		_root.MouseFilter = open
			? Control.MouseFilterEnum.Stop
			: Control.MouseFilterEnum.Ignore;
		// SetOpen OWNS the mouse mode so EVERY close route (Tab, Escape, the
		// close button, the target machine being freed) re-captures the mouse.
		Input.SetMouseMode(open
			? Input.MouseModeEnum.Visible
			: Input.MouseModeEnum.Captured);
		// KEYBOARD FOCUS: while open, the panel's ROOT holds focus (not a child
		// control), so Tab/Escape can never be eaten by a focused child (the
		// recipe picker) and always flow to the _Input close handler.
		if (open)
		{
			_root.FocusMode = Control.FocusModeEnum.All;
			_root.GrabFocus();
		}
		else
		{
			_root.ReleaseFocus();
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
		// Show the craft pane again (the machine panel is gone).
		SetCraftPaneVisible(true);
		// Close the inventory that OpenFor brought up, so the player isn't left
		// staring at a full-screen inventory after the machine panel is gone.
		if (_inventory != null && _inventory.IsOpen)
		{
			_inventory.SetOpen(false);
		}
		_machine = null;
		_inventory = null;
		ClearGrid(_inputGrid, _inputSlots);
		ClearGrid(_outputGrid, _outputSlots);
		InventorySlotGrid.ClearCursor();
		Input.SetMouseMode(Input.MouseModeEnum.Captured);
	}

	/// <summary>
	/// Rebuilds the panel's contents (title, status, recipe picker, progress, and
	/// the recipe-driven input/output slot grids) for the current machine.
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
		// recipes from the definition's paired input/output lists).
		int outputMat = 0;
		List<int> inputMats = new();
		bool hasRecipe = false;

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

			if (crafter.Recipe != null)
			{
				hasRecipe = true;
				outputMat = crafter.Recipe.ProductId;
				foreach (var (mid, _) in crafter.Recipe.Ingredients)
				{
					if (mid > 0)
					{
						inputMats.Add(mid);
					}
				}
			}
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
			int currentIdx = (smelter.RecipeIndex >= 0 && smelter.RecipeIndex < smeltRecipes.Count)
				? smelter.RecipeIndex + 1
				: 0;
			_recipePicker.Selected = currentIdx;

			var recipe = smelter.CurrentRecipe;
			if (recipe.Input != 0 && recipe.Output != 0)
			{
				hasRecipe = true;
				outputMat = recipe.Output;
				inputMats.Add(recipe.Input);
			}
		}
		else
		{
			_recipePicker.Visible = false;
		}

		// Progress bar (only for a crafter / smelter with a recipe).
		bool showProgress = hasRecipe;
		_progressBg.Visible = showProgress;
		_progressLabel.Visible = showProgress;

		// Output slots: one per distinct product (a single product for both
		// machine types).
		var outputMats = new List<int>();
		if (outputMat > 0)
		{
			outputMats.Add(outputMat);
		}

		RebuildInputSlots(inputMats);
		RebuildOutputSlots(outputMats);
	}

	/// <summary>
	/// (Re)builds the INPUT slot grid: one slot per distinct input material of
	/// the selected recipe (Factorio-style — each slot is the machine's
	/// mini-inventory for that one item type). Slots are interactive inventory
	/// slots (drag/click) backed by the machine's input buffer, and they only
	/// accept their bound material.
	/// </summary>
	private void RebuildInputSlots(List<int> mats)
	{
		ClearGrid(_inputGrid, _inputSlots);
		_inputSlotMats.Clear();
		// Center the row of slots within the grid's width (the grid is full-width
		// in the content box; the slots are left-packed, so we offset them to
		// center the group).
		float rowW = mats.Count > 0
			? mats.Count * SlotSize + (mats.Count - 1) * SlotGap
			: 0f;
		float gridW = _inputGrid != null ? _inputGrid.Size.X : 0f;
		float startX = Mathf.Max(0f, (gridW - rowW) * 0.5f);
		for (int i = 0; i < mats.Count; i++)
		{
			int mat = mats[i];
			_inputSlotMats.Add(mat);
			var store = new MachineSlotStore(() => _machine?.InputBuffer, mat);
			var slot = InventorySlotGrid.CreateSlot(store, SlotSize);
			slot.Position = new Vector2(startX + i * (SlotSize + SlotGap), 0f);
			_inputGrid.AddChild(slot);
			_inputSlots.Add(slot);
		}
	}

	/// <summary>
	/// (Re)builds the OUTPUT slot grid: one slot per distinct output material of
	/// the selected recipe. Slots are interactive inventory slots backed by the
	/// machine's output buffer; they accept any stack (the player may store
	/// products, and a full output is what a conveyor drains).
	/// </summary>
	private void RebuildOutputSlots(List<int> mats)
	{
		ClearGrid(_outputGrid, _outputSlots);
		_outputSlotMats.Clear();
		float rowW = mats.Count > 0
			? mats.Count * SlotSize + (mats.Count - 1) * SlotGap
			: 0f;
		float gridW = _outputGrid != null ? _outputGrid.Size.X : 0f;
		float startX = Mathf.Max(0f, (gridW - rowW) * 0.5f);
		for (int i = 0; i < mats.Count; i++)
		{
			int mat = mats[i];
			_outputSlotMats.Add(mat);
			var store = new MachineSlotStore(() => _machine?.OutputBuffer, 0);
			var slot = InventorySlotGrid.CreateSlot(store, SlotSize);
			slot.Position = new Vector2(startX + i * (SlotSize + SlotGap), 0f);
			_outputGrid.AddChild(slot);
			_outputSlots.Add(slot);
		}
	}

	private void ClearGrid(Control grid, List<Control> slots)
	{
		if (grid != null)
		{
			foreach (var child in grid.GetChildren())
			{
				child.QueueFree();
			}
		}
		slots.Clear();
	}

	/// <summary>
	/// An <see cref="InventorySlotGrid.ISlotStore"/> that backs a machine
	/// buffer (the machine's mini-inventory) instead of the player's inventory.
	/// A <paramref name="boundMaterial"/> of 0 means the slot accepts any stack
	/// (output slots); otherwise it only accepts that one material (input slots).
	/// </summary>
	private sealed class MachineSlotStore : InventorySlotGrid.ISlotStore
	{
		private readonly Func<MachineBuffer> _bufferProvider;
		private readonly int _bound;

		public MachineSlotStore(Func<MachineBuffer> bufferProvider, int bound)
		{
			_bufferProvider = bufferProvider;
			_bound = bound;
		}

		public ItemStack GetStack()
		{
			var b = _bufferProvider();
			if (b == null || b.Capacity == 0)
			{
				return ItemStack.From(0);
			}
			return b[0];
		}

		public void SetStack(ItemStack stack)
		{
			var b = _bufferProvider();
			if (b == null || b.Capacity == 0)
			{
				return;
			}
			b[0] = stack ?? ItemStack.From(0);
		}

		public bool CanAccept(ItemStack stack)
		{
			if (stack == null || stack.IsEmpty)
			{
				return false;
			}
			if (_bound != 0 && stack.MaterialId != _bound)
			{
				return false;
			}
			var b = _bufferProvider();
			if (b == null || b.Capacity == 0)
			{
				return false;
			}
			var cur = b[0];
			return cur.IsEmpty || (cur.MaterialId == stack.MaterialId && cur.Count < ItemStack.StackLimit);
		}

		public ItemStack TakeFromSlot()
		{
			var b = _bufferProvider();
			if (b == null || b.Capacity == 0)
			{
				return ItemStack.From(0);
			}
			var stack = b[0];
			b[0] = ItemStack.From(0);
			return stack;
		}

		public ItemStack TakePartial(int count)
		{
			var b = _bufferProvider();
			if (b == null || b.Capacity == 0)
			{
				return ItemStack.From(0);
			}
			var stack = b[0];
			if (stack.IsEmpty)
			{
				return ItemStack.From(0);
			}
			int take = Math.Min(count, stack.Count);
			stack.Count -= take;
			b[0] = stack.Count <= 0 ? ItemStack.From(0) : stack;
			return ItemStack.From(stack.MaterialId, take);
		}

		public int CollectItem(ItemStack stack)
		{
			if (stack == null || stack.IsEmpty || !CanAccept(stack))
			{
				return 0;
			}
			var b = _bufferProvider();
			if (b == null || b.Capacity == 0)
			{
				return 0;
			}
			var cur = b[0];
			if (!cur.IsEmpty && cur.MaterialId != stack.MaterialId)
			{
				return 0; // different material, slot occupied
			}
			int space = cur.IsEmpty ? ItemStack.StackLimit : ItemStack.StackLimit - cur.Count;
			int add = Math.Min(space, stack.Count);
			if (add <= 0)
			{
				return 0;
			}
			if (cur.IsEmpty)
			{
				b[0] = ItemStack.From(stack.MaterialId, add);
			}
			else
			{
				cur.Count += add;
				b[0] = cur;
			}
			return add;
		}
	}

	/// <summary>A recipe was selected (index 0 = none, 1..N = the Nth recipe).
	/// Handles both the crafter (craft recipes) and the smelter (smelt recipes
	/// from the definition's paired lists), then rebuilds the slot grids.</summary>
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
			smelter.RecipeIndex = index == 0 ? -1 : (int)index - 1;
		}
		BuildContent();
	}

	// ------------------------------------------------------------------
	// Per-frame: anchor the panel over the inventory's right slot + progress
	// ------------------------------------------------------------------

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

		// Anchor the panel over the inventory's right slot every frame so it
		// always sits exactly where the crafting pane is (the inventory rebuilds
		// its layout on open / resize, so the anchor is recomputed each frame).
		AnchorToRightSlot();

		// Refresh the live slot visuals (the machine's buffers change as it
		// smelts/crafts and as conveyors pull/push).
		RefreshInputSlots();
		RefreshOutputSlots();

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
	}

	private void RefreshInputSlots()
	{
		for (int i = 0; i < _inputSlots.Count; i++)
		{
			if (i < _inputSlotMats.Count)
			{
				InventorySlotGrid.RefreshSlot(new MachineSlotStore(() => _machine?.InputBuffer, _inputSlotMats[i]),
					FindIcon(_inputSlots[i]), FindCount(_inputSlots[i]));
			}
		}
	}

	private void RefreshOutputSlots()
	{
		for (int i = 0; i < _outputSlots.Count; i++)
		{
			if (i < _outputSlotMats.Count)
			{
				InventorySlotGrid.RefreshSlot(new MachineSlotStore(() => _machine?.OutputBuffer, 0),
					FindIcon(_outputSlots[i]), FindCount(_outputSlots[i]));
			}
		}
	}

	private static TextureRect FindIcon(Control slot)
	{
		if (slot == null)
		{
			return null;
		}
		foreach (var c in slot.GetChildren())
		{
			if (c is TextureRect tr)
			{
				return tr;
			}
		}
		return null;
	}

	private static Label FindCount(Control slot)
	{
		if (slot == null)
		{
			return null;
		}
		foreach (var c in slot.GetChildren())
		{
			if (c is Label l)
			{
				return l;
			}
		}
		return null;
	}

	/// <summary>
	/// Positions the machine panel over the inventory's crafting pane (the right
	/// slot). Reads the craft pane's viewport-space rect from the
	/// <see cref="InventoryUI"/> and places the panel there, clamped to the
	/// viewport. Called every frame so it tracks resizes and the inventory's
	/// rebuilds.
	/// </summary>
	private bool _diagAnchored;

	private void AnchorToRightSlot()
	{
		_resolveInvUi();
		if (_invUi == null || _panel == null)
		{
			// ONE-SHOT DIAGNOSTIC (early-return path).
			if (!_diagAnchored)
			{
				_diagAnchored = true;
				GD.Print($"[MachineUI.Anchor] EARLY-RETURN: invUi={(_invUi == null ? "null" : "ok")} panel={(_panel == null ? "null" : "ok")}");
			}
			return;
		}
		bool haveRect = _invUi.GetRightSlotRect(out Rect2 slotRect);
		if (!haveRect)
		{
			// ONE-SHOT DIAGNOSTIC (no-rect path).
			if (!_diagAnchored)
			{
				_diagAnchored = true;
				GD.Print($"[MachineUI.Anchor] NO-RECT: GetRightSlotRect returned false (craft panel missing or inventory panel hidden)");
			}
			return;
		}
		// SIZE the panel to the craft pane's rect (so it fits the right slot
		// vertically, exactly like the crafting pane) and POSITION it to fill
		// the rect. The panel is a plain positioned control, so this is exact.
		_panel.Position = slotRect.Position;
		_panel.Size = slotRect.Size;
		// Resize the content box to track the panel (inset from the edges).
		if (_contentBox != null)
		{
			_contentBox.Position = new Vector2(PanelInsetX, PanelInsetY);
			_contentBox.Size = new Vector2(
				Math.Max(0f, _panel.Size.X - PanelInsetX * 2f),
				Math.Max(0f, _panel.Size.Y - PanelInsetY * 2f));
		}

		// ONE-SHOT DIAGNOSTIC: print the anchor values once so we can confirm
		// the rect is correct. Remove after confirming.
		if (!_diagAnchored)
		{
			_diagAnchored = true;
			float cbW = _contentBox != null ? _contentBox.Size.X : 0f;
			float cbH = _contentBox != null ? _contentBox.Size.Y : 0f;
			GD.Print($"[MachineUI.Anchor] slotRect={slotRect}  panelPos=({_panel.Position.X:0.##}, {_panel.Position.Y:0.##})  panelSize=({_panel.Size.X:0.##}, {_panel.Size.Y:0.##})  contentBoxSize=({cbW:0.##}, {cbH:0.##})");
		}
	}

	/// <summary>Resolves the InventoryUI (a sibling CanvasLayer) via its group.</summary>
	private void _resolveInvUi()
	{
		if (_invUi == null)
		{
			_invUi = GetTree().GetFirstNodeInGroup("inventory_ui") as InventoryUI;
		}
	}

	/// <summary>Hides/shows the inventory's crafting pane (the machine panel takes
	/// its place while open).</summary>
	private void SetCraftPaneVisible(bool visible)
	{
		_resolveInvUi();
		var craft = _invUi?.GetCraftPane();
		if (craft != null && IsInstanceValid(craft))
		{
			craft.Visible = visible;
		}
	}

	// Tab/Escape close is handled in _Input, NOT _UnhandledKeyInput. _Input runs
	// BEFORE GUI focus handling, so it always sees the key even when a focused
	// control would otherwise consume it for its own navigation. This is the
	// same bug class the InventoryUI fixed: this panel contains focusable
	// controls (the _recipePicker OptionButton), so after the player clicks it,
	// keyboard focus lands on that control, which EATS the Tab/Escape key for its
	// own tab/navigation — the key never reached the old _UnhandledKeyInput
	// handler and the UI was un-closeable. We also call
	// GetViewport().SetInputAsHandled() so the key is not forwarded on to the
	// focused control (which would otherwise move the dropdown selection as a
	// side effect of closing the UI).
	public override void _Input(InputEvent @event)
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
				GetViewport().SetInputAsHandled();
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
}
