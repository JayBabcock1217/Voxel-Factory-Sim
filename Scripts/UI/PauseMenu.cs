using Godot;
using System;
using FactorySim.Commands;

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

	// Command console: a text input at the bottom of the menu. Each submitted
	// line is parsed by the CommandRegistry and dispatched to the matching
	// ConsoleCommand; the command's feedback string is shown in the label below.
	private LineEdit _commandInput;
	private Label _commandFeedback;

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

		// Give the command input focus when the menu opens (so the player can
		// start typing a command immediately), and release it when the menu
		// closes (so typed keys go to the game, not a hidden LineEdit).
		if (open && _commandInput != null)
		{
			_commandInput.GrabFocus();
		}
		else if (!open && _commandInput != null)
		{
			_commandInput.ReleaseFocus();
		}
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
		title.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 0.95f));
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

		// --- Command console ---
		// A label hint, a LineEdit for typing commands, and a feedback line that
		// shows the last command's result. The input submits on Enter (TextSubmitted
		// signal) which runs the command through the CommandRegistry.
		var cmdHint = new Label
		{
			LayoutMode = 2,
			Text = "Command (type 'help' for a list, then Enter to run):",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		cmdHint.AddThemeFontSizeOverride("font_size", 18);
		cmdHint.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.75f));
		_home.AddChild(cmdHint);

		_commandInput = new LineEdit
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(340, 44),
			PlaceholderText = "e.g.  settime 0.5",
		};
		_commandInput.AddThemeFontSizeOverride("font_size", 22);
		_commandInput.TextSubmitted += OnCommandSubmitted;
		_home.AddChild(_commandInput);

		_commandFeedback = new Label
		{
			LayoutMode = 2,
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.Word,
			CustomMinimumSize = new Vector2(420, 0),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_commandFeedback.AddThemeFontSizeOverride("font_size", 18);
		_commandFeedback.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.8f));
		_home.AddChild(_commandFeedback);

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

	/// <summary>
	/// Runs the text the player typed into the command input. The line is passed
	/// to the <see cref="CommandRegistry"/>, which parses it and dispatches to the
	/// matching command. The command's feedback string is shown in the label
	/// below the input, coloured green for "OK:" results and red for "ERR:" ones.
	/// The input is cleared after each submit so the player can immediately type
	/// the next command. Note: the tree is PAUSED while the menu is open, but
	/// this CanvasLayer uses ProcessMode.Always so the input still accepts text
	/// and the command still runs (commands that need a live node resolve it from
	/// the tree, which exists even while paused).
	/// </summary>
	private void OnCommandSubmitted(string text)
	{
		string result = CommandRegistry.Instance.Run(text);

		// Colour the feedback by outcome: OK green, ERR red, anything else neutral.
		if (result.StartsWith("ERR:", StringComparison.Ordinal))
		{
			_commandFeedback.AddThemeColorOverride("font_color", new Color(0.95f, 0.4f, 0.4f));
		}
		else if (result.StartsWith("OK:", StringComparison.Ordinal))
		{
			_commandFeedback.AddThemeColorOverride("font_color", new Color(0.55f, 0.9f, 0.55f));
		}
		else
		{
			_commandFeedback.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.8f));
		}
		_commandFeedback.Text = result;

		// Clear the input for the next command and keep focus on it so the player
		// can chain commands without re-clicking.
		_commandInput.Text = "";
		_commandInput.GrabFocus();
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
			BgColor = new Color(0.14f, 0.14f, 0.14f, 0.95f),
			BorderColor = new Color(0.35f, 0.35f, 0.35f, 1f),
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			CornerRadiusTopLeft = 8,
			CornerRadiusTopRight = 8,
			CornerRadiusBottomLeft = 8,
			CornerRadiusBottomRight = 8,
			ContentMarginLeft = 26f,
			ContentMarginTop = 12f,
			ContentMarginRight = 26f,
			ContentMarginBottom = 12f,
		};
		var hover = (StyleBoxFlat)normal.Duplicate();
		hover.BgColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
		var pressedStyle = (StyleBoxFlat)normal.Duplicate();
		pressedStyle.BgColor = new Color(0.1f, 0.1f, 0.1f, 0.95f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", pressedStyle);
		button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
		button.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
		button.Pressed += pressed;
		return button;
	}
}
