using Godot;
using System;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The main menu, built entirely in code (no .tscn layout needed beyond a single
/// root node). Shows a title and three buttons:
///   - Play: opens the save/load SLOT PICKER (<see cref="SlotPicker"/>). Picking
///     a slot loads it, "New World" starts fresh, then the scene switches to the
///     gameplay scene (the chosen slot is handed across the scene change via the
///     static <see cref="WorldSave.SessionSlotId"/> handoff).
///   - Settings: swaps to the shared <see cref="SettingsMenu"/>/panel.
///   - Quit: exits the game.
///
/// A dark gradient background is drawn in code so the menu looks complete without
/// any external assets. The scene also hosts a <c>WorldSave</c> node (see
/// <c>Scenes/MainMenu.tscn</c>) which the picker uses to list/manage slots.
/// </summary>
public partial class MainMenu : CanvasLayer
{
	private const string MainScenePath = "res://Scenes/Main.tscn";

	private SettingsMenu _settingsMenu;
	private SlotPicker _slotPicker;
	private WorldSave _worldSave;
	private Control _home;

	public override void _Ready()
	{
		Name = "MainMenu";
		Layer = 1;
		BuildBackground();
		BuildContent();

		// Apply the saved display settings (resolution / fullscreen) so the menu
		// opens at the user's preferred window size.
		SettingsStore.Instance.Initialize();
		SettingsStore.Instance.ApplyDisplay();
	}

	private void BuildBackground()
	{
		// A full-rect dark background so the menu is never transparent.
		var background = new ColorRect
		{
			Name = "Background",
			Color = new Color(0.05f, 0.06f, 0.09f, 1f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		AddChild(background);
	}

	private void BuildContent()
	{
		// Home content: title + button list, centered.
		_home = new VBoxContainer
		{
			Name = "Home",
			LayoutMode = 3,
		};
		_home.AddThemeConstantOverride("separation", 18);
		AddChild(_home);
		// Anchor the column to the top-left of the viewport so that positioning
		// it with an explicit Position (CenterHome) is deterministic and is not
		// fought by anchor/offset recomputation. The column is centered in
		// _Process once its size is known (and re-centered on resize).
		_home.AnchorLeft = 0f;
		_home.AnchorTop = 0f;
		_home.AnchorRight = 0f;
		_home.AnchorBottom = 0f;

		var title = new Label
		{
			LayoutMode = 2,
			Text = "Factory Sim",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		title.AddThemeFontSizeOverride("font_size", 64);
		title.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		title.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.2f, 0.4f, 1f));
		title.AddThemeConstantOverride("outline_size", 8);
		_home.AddChild(title);

		var subtitle = new Label
		{
			LayoutMode = 2,
			Text = "Voxel Sandbox",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		subtitle.AddThemeFontSizeOverride("font_size", 24);
		subtitle.AddThemeColorOverride("font_color", new Color(0.6f, 0.7f, 0.85f));
		_home.AddChild(subtitle);

		// Spacer between the title block and the buttons.
		var spacer = new Control
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 40),
		};
		_home.AddChild(spacer);

		_home.AddChild(MakeButton("Play", OpenSlotPicker));
		_home.AddChild(MakeButton("Settings", OpenSettings));
		_home.AddChild(MakeButton("Quit", () => GetTree().Quit()));

		// Settings overlay: shares the same panel asset as the pause menu.
		_settingsMenu = new SettingsMenu
		{
			Name = "SettingsMenu",
			LayoutMode = 3,
		};
		_settingsMenu.Home = _home;
		AddChild(_settingsMenu);

		// Slot picker overlay: shown by Play. The WorldSave node (a sibling in
		// the scene, see MainMenu.tscn) owns the slot index and the FSAV files.
		_worldSave = GetNodeOrNull<WorldSave>("WorldSave");
		_slotPicker = new SlotPicker
		{
			Name = "SlotPicker",
			LayoutMode = 3,
		};
		_slotPicker.LoadRequested += OnLoadSlot;
		_slotPicker.NewWorldRequested += OnNewWorld;
		AddChild(_slotPicker);

		// The VBox's size isn't finalized until the layout pass, so defer the
		// first centering to after it; _Process keeps it centered afterwards.
		CallDeferred(nameof(CenterHome));
	}

	public override void _Process(double delta)
	{
		CenterHome();
	}

	/// <summary>
	/// Centers the home column in the viewport using its real size. Runs every
	/// frame (cheap) so it stays centered across window resizes, and the first
	/// call is deferred past the layout pass so <see cref="Control.Size"/> is
	/// valid. Both X and Y are set explicitly, so the result does not depend on
	/// any anchor/offset state.
	/// </summary>
	private void CenterHome()
	{
		if (_home == null || _home.Size == Vector2.Zero)
		{
			return;
		}
		Vector2 vp = GetViewport().GetVisibleRect().Size;
		_home.Position = (vp - _home.Size) * 0.5f;
	}

	private void OpenSettings()
	{
		_settingsMenu.OpenSettings();
	}

	// ------------------------------------------------------------------
	// Slot picker
	// ------------------------------------------------------------------

	private void OpenSlotPicker()
	{
		// The WorldSave node lives in the scene (MainMenu.tscn); if it is missing
		// for any reason, degrade to starting a fresh world (no slot).
		if (_worldSave == null)
		{
			GD.PushWarning("[MainMenu] No WorldSave node found — starting a fresh world.");
			StartFreshWorld();
			return;
		}

		_slotPicker.Open(_home, _worldSave);
	}

	private void OnLoadSlot(string slotId, string slotName)
	{
		// Load the chosen slot into the menu's WorldSave (so the picker's state
		// is coherent if it is re-opened) and publish the slot for the gameplay
		// scene: the Main.tscn WorldSave node's _Ready reads the static
		// SessionSlotId and re-loads the slot file, so the world re-applies the
		// saved edits / player / inventory / drops.
		_worldSave.SetActiveSlot(slotId, slotName);
		if (!_worldSave.LoadFrom(slotId))
		{
			GD.PushWarning($"[MainMenu] Slot '{slotId}' could not be loaded — starting a fresh world.");
		}
		GotoGameScene();
	}

	private void OnNewWorld(string slotId, string slotName)
	{
		// A fresh world: the slot is already created (index entry) and published
		// by the picker; there is nothing to load. The in-game auto-save on quit
		// writes the slot's first file.
		_worldSave.SetActiveSlot(slotId, slotName);
		GotoGameScene();
	}

	private void StartFreshWorld()
	{
		// No WorldSave node (degraded): publish a fresh session so the game
		// scene's WorldSave (if present) loads nothing.
		WorldSave.SessionSlotId = null;
		WorldSave.SessionSlotName = null;
		WorldSave.SessionFresh = true;
		GotoGameScene();
	}

	private void GotoGameScene()
	{
		GetTree().ChangeSceneToFile(MainScenePath);
	}

	private static Button MakeButton(string text, Action pressed)
	{
		var button = new Button
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(320, 52),
			Text = text,
		};
		button.AddThemeFontSizeOverride("font_size", 26);
		var normal = new StyleBoxFlat
		{
			BgColor = new Color(0.14f, 0.18f, 0.28f, 1f),
			BorderColor = new Color(0.35f, 0.45f, 0.65f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 24f,
			ContentMarginTop = 10f,
			ContentMarginRight = 24f,
			ContentMarginBottom = 10f,
		};
		var hover = (StyleBoxFlat)normal.Duplicate();
		hover.BgColor = new Color(0.2f, 0.27f, 0.42f, 1f);
		hover.BorderColor = new Color(0.55f, 0.68f, 0.9f, 1f);
		var pressedStyle = (StyleBoxFlat)normal.Duplicate();
		pressedStyle.BgColor = new Color(0.1f, 0.13f, 0.2f, 1f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", pressedStyle);
		button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddThemeColorOverride("font_hover_color", Colors.White);
		button.Pressed += pressed;
		return button;
	}
}
