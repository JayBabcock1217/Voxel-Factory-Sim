using Godot;
using System;

namespace FactorySim.UI;

/// <summary>
/// The in-game pause menu. Toggled with the Escape key: opening it pauses the
/// whole game (tree pause), releases the mouse, and freezes player input;
/// closing it does the reverse.
///
/// Buttons:
///   - Resume: closes the menu and unpauses.
///   - Settings: opens the shared <see cref="SettingsMenu"/>/panel (same asset
///     as the main menu, reading <see cref="SettingsStore.Instance"/>).
///   - Quit to Main Menu: reloads the main menu scene (which unpauses).
///
/// The node lives in the "pause_menu" group so the Escape handler can find it
/// regardless of where it is in the scene tree.
/// </summary>
public partial class PauseMenu : CanvasLayer
{
	private const string MainScenePath = "res://Scenes/MainMenu.tscn";

	private SettingsMenu _settingsMenu;
	private Control _home;
	private Player _player;
	private bool _open;

	public bool IsOpen => _open;

	public override void _Ready()
	{
		Layer = 50;
		ProcessMode = ProcessModeEnum.Always; // keep running so it can render while paused
		AddToGroup("pause_menu");
		Visible = false;

		_player = GetTree().GetFirstNodeInGroup("player") as Player;

		BuildContent();
		SetOpen(false);
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		// Escape toggles the pause menu. _UnhandledKeyInput runs after the
		// player's _UnhandledInput, so the player does not also recapture the
		// mouse on the same press.
		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
		{
			Toggle();
		}
	}

	/// <summary>Toggles the pause menu open/closed.</summary>
	public void Toggle()
	{
		// Re-fetch the player in case it wasn't found at _Ready (e.g. the menu's
		// _Ready ran before the player joined its group); by the time the user
		// presses Escape the player is present.
		_player ??= GetTree().GetFirstNodeInGroup("player") as Player;
		SetOpen(!_open);
	}

	private void SetOpen(bool open)
	{
		if (_open == open)
		{
			return;
		}
		_open = open;
		Visible = open;
		GetTree().Paused = open;
		if (_player != null)
		{
			_player.SetInputEnabled(!open);
			// While the menu is open, Escape is owned by the menu (see Toggle),
			// so the Player's own Escape capture/release toggle must stand down.
			_player.PauseMenuOpen = open;
		}
		Input.MouseMode = open
			? Input.MouseModeEnum.Visible
			: Input.MouseModeEnum.Captured;
	}

	private void BuildContent()
	{
		// A dim overlay behind the buttons so the frozen world is still visible.
		// Added first so it renders beneath the home content and settings panel.
		var dim = new ColorRect
		{
			Name = "Dim",
			Color = new Color(0, 0, 0, 0.45f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		AddChild(dim);

		// Home content: title + button list, centered.
		_home = new VBoxContainer
		{
			Name = "Home",
			LayoutMode = 3,
		};
		_home.AddThemeConstantOverride("separation", 18);
		AddChild(_home);
		// Anchor the column to the top-left of the viewport so that positioning
		// it with an explicit Position (CenterHome) is deterministic. The column
		// is centered in _Process once its size is known (and re-centered on
		// resize), but only while the menu is open.
		_home.AnchorLeft = 0f;
		_home.AnchorTop = 0f;
		_home.AnchorRight = 0f;
		_home.AnchorBottom = 0f;

		var title = new Label
		{
			LayoutMode = 2,
			Text = "Paused",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		title.AddThemeFontSizeOverride("font_size", 56);
		title.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		title.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.9f));
		title.AddThemeConstantOverride("outline_size", 6);
		_home.AddChild(title);

		var spacer = new Control
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 28),
		};
		_home.AddChild(spacer);

		_home.AddChild(MakeButton("Resume", Resume));
		_home.AddChild(MakeButton("Settings", OpenSettings));
		_home.AddChild(MakeButton("Quit to Main Menu", QuitToMenu));

		// Settings overlay: same shared panel as the main menu.
		_settingsMenu = new SettingsMenu
		{
			Name = "SettingsMenu",
			LayoutMode = 3,
		};
		_settingsMenu.Home = _home;
		AddChild(_settingsMenu);

		// Defer the first centering to after the layout pass (the column's size
		// is unknown until then); _Process keeps it centered afterwards.
		CallDeferred(nameof(CenterHome));
	}

	public override void _Process(double delta)
	{
		// Only re-center while open; when hidden the position is irrelevant and
		// this keeps the menu node inert during gameplay.
		if (_open)
		{
			CenterHome();
		}
	}

	/// <summary>
	/// Centers the home column in the viewport using its real size. Both X and
	/// Y are set explicitly so the result does not depend on any anchor/offset
	/// state; called every frame while open so it survives window resizes.
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

	private void Resume()
	{
		SetOpen(false);
	}

	private void OpenSettings()
	{
		_settingsMenu.OpenSettings();
	}

	private void QuitToMenu()
	{
		// ChangeSceneToFile unpauses the tree as part of the scene switch, and
		// the new scene's _Ready applies the saved display settings.
		GetTree().Paused = false;
		GetTree().ChangeSceneToFile(MainScenePath);
	}

	private static Button MakeButton(string text, Action pressed)
	{
		var button = new Button
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(300, 48),
			Text = text,
		};
		button.AddThemeFontSizeOverride("font_size", 24);
		var normal = new StyleBoxFlat
		{
			BgColor = new Color(0.14f, 0.18f, 0.28f, 0.95f),
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
		hover.BgColor = new Color(0.2f, 0.27f, 0.42f, 0.95f);
		var pressedStyle = (StyleBoxFlat)normal.Duplicate();
		pressedStyle.BgColor = new Color(0.1f, 0.13f, 0.2f, 0.95f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", pressedStyle);
		button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddThemeColorOverride("font_hover_color", Colors.White);
		button.Pressed += pressed;
		return button;
	}
}
