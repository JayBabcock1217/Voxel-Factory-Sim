using Godot;
using System;

namespace FactorySim.UI;

/// <summary>
/// A full-rect container that shows the shared <see cref="SettingsPanel"/> and
/// swaps back to a "home" content node when the user clicks Back. The host scene
/// (main menu or pause menu) sets <see cref="Home"/> to the node that should be
/// visible when settings is closed; this class toggles between it and the panel.
///
/// Because the same <see cref="SettingsPanel"/> (reading
/// <see cref="SettingsStore.Instance"/>) is used by both the main menu and the
/// in-game pause menu, the settings are a single shared asset.
/// </summary>
public partial class SettingsMenu : Control
{
	private SettingsPanel _panel;

	/// <summary>
	/// The node shown when settings is closed (e.g. the menu's button list).
	/// Set by the host scene in its <c>_Ready</c>; must be a child of this menu.
	/// </summary>
	public Control Home { get; set; }

	/// <summary>Raised when the user clicks Back out of settings.</summary>
	public event Action HomeShown;

	public override void _Ready()
	{
		// Ignore while the panel is closed so this full-rect container never
		// swallows clicks meant for the home content behind it; it switches to
		// Stop only while the settings panel is showing (see Show/Hide).
		MouseFilter = Control.MouseFilterEnum.Ignore;
		SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);

		_panel = new SettingsPanel
		{
			Name = "Panel",
			LayoutMode = 3,
			Visible = false,
		};
		_panel.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_panel.BackRequested += OnBack;
		AddChild(_panel);

		// Start on the home screen.
		ShowHome();
	}

	/// <summary>Shows the settings panel and hides the home content.</summary>
	public void OpenSettings()
	{
		if (Home != null)
		{
			Home.Visible = false;
		}
		_panel.Visible = true;
		MouseFilter = Control.MouseFilterEnum.Stop;
		// Re-sync the panel controls in case settings changed elsewhere.
		_panel.RefreshControls();
		// Defer the layout: the panel's Size is 0 until the layout pass runs,
		// which happens after _Ready / after this call. Layout() needs the real
		// width, so it must run once the size is known.
		_panel.CallDeferred(nameof(SettingsPanel.Layout));
	}

	/// <summary>Hides the settings panel and shows the home content.</summary>
	public void ShowHome()
	{
		_panel.Visible = false;
		MouseFilter = Control.MouseFilterEnum.Ignore;
		if (Home != null)
		{
			Home.Visible = true;
		}
	}

	private void OnBack()
	{
		ShowHome();
		HomeShown?.Invoke();
	}
}
