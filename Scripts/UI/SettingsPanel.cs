using Godot;
using System;

namespace FactorySim.UI;

/// <summary>
/// A self-contained settings panel built entirely in code. Shown inside a
/// <see cref="SettingsMenu"/> (which is itself a child of the main-menu or
/// pause-menu scene). It edits <see cref="SettingsStore.Instance"/> and applies
/// changes live: display settings change the window immediately, and world
/// settings (chunk + LOD render distance, LOD on/off) are pushed onto the
/// loaded voxel world when there is one.
///
/// The panel is a full-rect <see cref="Control"/> with its own title bar and
/// Back button, so it can be swapped in/out of the menu scene without the host
/// having to know about its layout. Rows use <see cref="HBoxContainer"/> so the
/// label and control are always laid out side by side without overlapping.
/// </summary>
public partial class SettingsPanel : Control
{
	private const float LeftPad = 28f;
	private const float RightPad = 28f;
	private const float TitleHeight = 48f;
	// Gap between a row's label and its control (HBoxContainer separation).
	private const int RowSeparation = 24;
	// Fixed width of the label column so all controls line up vertically.
	private const float LabelColumnWidth = 220f;

	private SettingsStore _store => SettingsStore.Instance;

	private VBoxContainer _root;
	private Label _title;
	private OptionButton _resolutionButton;
	private CheckButton _fullscreenButton;
	private CheckButton _vSyncButton;
	private OptionButton _meshModeButton;
	private CheckButton _shadowsButton;
	private CheckButton _fogButton;
	private HSlider _fpsCap;
	private Label _fpsCapValue;
	private HSlider _fogDensity;
	private Label _fogDensityValue;
	private HSlider _ambientLight;
	private Label _ambientLightValue;
	private HSlider _sunIntensity;
	private Label _sunIntensityValue;
	private HSlider _chunkDistance;
	private Label _chunkDistanceValue;
	private HSlider _lodDistance;
	private Label _lodDistanceValue;
	private CheckButton _lodEnabled;
	private Button _backButton;

	/// <summary>Raised when the user clicks Back (the host hides this panel).</summary>
	public event Action BackRequested;

	public override void _Ready()
	{
		MouseFilter = Control.MouseFilterEnum.Stop;
		_store.Initialize();
		BuildUi();
		RefreshControls();
		_store.ApplyDisplay();
	}

	/// <summary>
	/// Re-syncs every control from the store (call after external changes).
	/// </summary>
	public void Layout()
	{
		// The HBoxContainer rows handle their own layout; nothing to do here.
		// (Kept for API compatibility with the host's CallDeferred.)
	}

	/// <summary>Re-syncs every control from the store (call after external changes).</summary>
	public void RefreshControls()
	{
		int resIndex = ResolutionIndex(_store.ResolutionWidth, _store.ResolutionHeight);
		if (resIndex < 0)
		{
			_resolutionButton.AddItem($"{_store.ResolutionWidth} x {_store.ResolutionHeight}");
			resIndex = _resolutionButton.ItemCount - 1;
		}
		_resolutionButton.Selected = resIndex;

		_fullscreenButton.ButtonPressed = _store.Fullscreen;
		_vSyncButton.ButtonPressed = _store.VSync;
		_fpsCap.Value = _store.FpsCap;
		_fpsCapValue.Text = _store.FpsCap == 0 ? "∞" : $"{_store.FpsCap}";

		_meshModeButton.Selected = _store.MeshMode;
		_shadowsButton.ButtonPressed = _store.ShadowsEnabled;
		_fogButton.ButtonPressed = _store.FogEnabled;
		_fogDensity.Value = _store.FogDensity;
		_fogDensityValue.Text = _store.FogDensity.ToString("0.000");
		_ambientLight.Value = _store.AmbientLightEnergy;
		_ambientLightValue.Text = _store.AmbientLightEnergy.ToString("0.00");
		_sunIntensity.Value = _store.SunIntensity;
		_sunIntensityValue.Text = _store.SunIntensity.ToString("0.00");

		_chunkDistance.Value = _store.ChunkRenderDistance;
		_chunkDistanceValue.Text = $"{_store.ChunkRenderDistance}";
		_lodDistance.Value = _store.LodRenderDistance;
		_lodDistanceValue.Text = $"{_store.LodRenderDistance}";
		_lodEnabled.ButtonPressed = _store.LodEnabled;
	}

	private static readonly (int Width, int Height)[] PresetResolutions =
	{
		(1280, 720),
		(1600, 900),
		(1920, 1080),
		(2560, 1440),
		(3840, 2160),
	};

	private int ResolutionIndex(int w, int h)
	{
		for (int i = 0; i < PresetResolutions.Length; i++)
		{
			if (PresetResolutions[i].Width == w && PresetResolutions[i].Height == h)
			{
				return i;
			}
		}
		return -1;
	}

	private void BuildUi()
	{
		// Full-rect background.
		var background = new Panel
		{
			Name = "Background",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		var bgStyle = new StyleBoxFlat
		{
			BgColor = new Color(0.08f, 0.09f, 0.12f, 0.98f),
		};
		background.AddThemeStyleboxOverride("panel", bgStyle);
		AddChild(background);

		// Root vertical box for all content, inset by padding.
		_root = new VBoxContainer
		{
			Name = "Root",
			LayoutMode = 1,
		};
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.OffsetLeft = LeftPad;
		_root.OffsetTop = 12f;
		_root.OffsetRight = -RightPad;
		_root.OffsetBottom = -12f;
		_root.AddThemeConstantOverride("separation", 10);
		AddChild(_root);

		// Title.
		_title = new Label
		{
			LayoutMode = 2,
			Text = "Settings",
			HorizontalAlignment = HorizontalAlignment.Left,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_title.AddThemeFontSizeOverride("font_size", 32);
		_title.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		_root.AddChild(_title);

		// Back button.
		_backButton = MakeButton("Back");
		_backButton.CustomMinimumSize = new Vector2(140f, 36f);
		_backButton.Pressed += () => BackRequested?.Invoke();
		_root.AddChild(_backButton);

		// Spacer between title/back and the first section.
		_root.AddChild(MakeSpacer(16f));

		// --- Display section ---
		_root.AddChild(MakeSectionLabel("Display"));
		_root.AddChild(MakeRow("Resolution", MakeResolutionDropdown()));
		_root.AddChild(MakeRow("Fullscreen", MakeFullscreenToggle()));
		_root.AddChild(MakeRow("V-Sync", MakeVSyncToggle()));
		_root.AddChild(MakeSliderRow("FPS cap (0 = unlimited)", 0, 360,
			out _fpsCap, out _fpsCapValue,
			value =>
			{
				_store.FpsCap = Mathf.RoundToInt(value);
				_store.Save();
				_store.ApplyDisplay();
			}));

		// --- Rendering section ---
		_root.AddChild(MakeSpacer(8f));
		_root.AddChild(MakeSectionLabel("Rendering"));
		_root.AddChild(MakeRow("Mesh mode", MakeMeshModeDropdown()));
		_root.AddChild(MakeRow("Shadows", MakeShadowsToggle()));
		_root.AddChild(MakeRow("Fog", MakeFogToggle()));
		_root.AddChild(MakeSliderRow("Fog density", 0f, 0.1f,
			out _fogDensity, out _fogDensityValue,
			value =>
			{
				_store.FogDensity = Mathf.Round(value * 1000f) / 1000f;
				_store.Save();
				_store.Apply();
			}, step: 0.001f, valueFormat: "0.000"));
		_root.AddChild(MakeSliderRow("Ambient light", 0f, 2f,
			out _ambientLight, out _ambientLightValue,
			value =>
			{
				_store.AmbientLightEnergy = Mathf.Round(value * 100f) / 100f;
				_store.Save();
				_store.Apply();
			}, step: 0.01f, valueFormat: "0.00"));
		_root.AddChild(MakeSliderRow("Sun intensity", 0f, 5f,
			out _sunIntensity, out _sunIntensityValue,
			value =>
			{
				_store.SunIntensity = Mathf.Round(value * 100f) / 100f;
				_store.Save();
				_store.Apply();
			}, step: 0.01f, valueFormat: "0.00"));

		// --- World section ---
		_root.AddChild(MakeSpacer(8f));
		_root.AddChild(MakeSectionLabel("World"));
		_root.AddChild(MakeSliderRow("Chunk render distance", 1, 32,
			out _chunkDistance, out _chunkDistanceValue,
			value =>
			{
				_store.ChunkRenderDistance = Mathf.RoundToInt(value);
				if (_store.LodRenderDistance < _store.ChunkRenderDistance)
				{
					_store.LodRenderDistance = _store.ChunkRenderDistance;
				}
				_store.Save();
				_store.Apply();
			}, step: 1f));
		_root.AddChild(MakeSliderRow("LOD render distance", 1, 256,
			out _lodDistance, out _lodDistanceValue,
			value =>
			{
				_store.LodRenderDistance = Mathf.RoundToInt(value);
				if (_store.LodRenderDistance < _store.ChunkRenderDistance)
				{
					_store.LodRenderDistance = _store.ChunkRenderDistance;
				}
				_store.Save();
				_store.Apply();
			}, step: 1f));
		_root.AddChild(MakeRow("Enable LOD", MakeLodToggle()));
	}

	// ------------------------------------------------------------------
	// Row / control factories
	// ------------------------------------------------------------------

	/// <summary>
	/// A single settings row: a fixed-width label followed by a control, laid
	/// out horizontally so they never overlap.
	/// </summary>
	private HBoxContainer MakeRow(string labelText, Control control)
	{
		var row = new HBoxContainer
		{
			LayoutMode = 2,
		};
		row.AddThemeConstantOverride("separation", RowSeparation);

		var label = new Label
		{
			LayoutMode = 2,
			Text = labelText,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.CustomMinimumSize = new Vector2(LabelColumnWidth, 0);
		label.AddThemeFontSizeOverride("font_size", 18);
		row.AddChild(label);

		// Let the control fill the remaining horizontal space.
		control.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		row.AddChild(control);

		return row;
	}

	private Label MakeSectionLabel(string text)
	{
		var label = new Label
		{
			LayoutMode = 2,
			Text = text,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.AddThemeFontSizeOverride("font_size", 20);
		label.AddThemeColorOverride("font_color", new Color(0.6f, 0.75f, 1f));
		return label;
	}

	private static Control MakeSpacer(float height)
	{
		var spacer = new Control
		{
			LayoutMode = 2,
		};
		spacer.CustomMinimumSize = new Vector2(0, height);
		return spacer;
	}

	private OptionButton MakeResolutionDropdown()
	{
		var button = new OptionButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
		};
		foreach (var (w, h) in PresetResolutions)
		{
			button.AddItem($"{w} x {h}");
		}
		button.ItemSelected += i =>
		{
			var (w, h) = PresetResolutions[Mathf.Min((int)i, PresetResolutions.Length - 1)];
			_store.ResolutionWidth = w;
			_store.ResolutionHeight = h;
			_store.Save();
			_store.ApplyDisplay();
		};
		_resolutionButton = button;
		return button;
	}

	private CheckButton MakeFullscreenToggle()
	{
		var toggle = new CheckButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
			Text = "On",
		};
		toggle.Toggled += pressed =>
		{
			_store.Fullscreen = pressed;
			_store.Save();
			_store.ApplyDisplay();
		};
		_fullscreenButton = toggle;
		return toggle;
	}

	private CheckButton MakeVSyncToggle()
	{
		var toggle = new CheckButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
			Text = "On",
		};
		toggle.Toggled += pressed =>
		{
			_store.VSync = pressed;
			_store.Save();
			_store.ApplyDisplay();
		};
		_vSyncButton = toggle;
		return toggle;
	}

	private OptionButton MakeMeshModeDropdown()
	{
		var button = new OptionButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
		};
		button.AddItem("Faces (voxel)");
		button.AddItem("Marching Cubes (smooth)");
		button.ItemSelected += i =>
		{
			_store.MeshMode = (int)i;
			_store.Save();
			_store.Apply();
		};
		_meshModeButton = button;
		return button;
	}

	private CheckButton MakeShadowsToggle()
	{
		var toggle = new CheckButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
			Text = "On",
		};
		toggle.Toggled += pressed =>
		{
			_store.ShadowsEnabled = pressed;
			_store.Save();
			_store.Apply();
		};
		_shadowsButton = toggle;
		return toggle;
	}

	private CheckButton MakeFogToggle()
	{
		var toggle = new CheckButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
			Text = "On",
		};
		toggle.Toggled += pressed =>
		{
			_store.FogEnabled = pressed;
			_store.Save();
			_store.Apply();
		};
		_fogButton = toggle;
		return toggle;
	}

	private CheckButton MakeLodToggle()
	{
		var toggle = new CheckButton
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 36),
			Text = "On",
		};
		toggle.Toggled += pressed =>
		{
			_store.LodEnabled = pressed;
			_store.Save();
			_store.Apply();
		};
		_lodEnabled = toggle;
		return toggle;
	}

	/// <summary>
	/// A slider row: label + slider + value label, all in one HBoxContainer.
	/// The slider expands to fill available space; the value label is fixed
	/// width on the far right.
	/// </summary>
	private HBoxContainer MakeSliderRow(string labelText, float min, float max,
		out HSlider slider, out Label valueLabel, Action<float> onChanged,
		float step = 1f, string valueFormat = "0")
	{
		var row = new HBoxContainer
		{
			LayoutMode = 2,
		};
		row.AddThemeConstantOverride("separation", RowSeparation);

		var label = new Label
		{
			LayoutMode = 2,
			Text = labelText,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.CustomMinimumSize = new Vector2(LabelColumnWidth, 0);
		label.AddThemeFontSizeOverride("font_size", 18);
		row.AddChild(label);

		slider = new HSlider
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(80, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MinValue = min,
			MaxValue = max,
			Step = step,
		};
		row.AddChild(slider);

		// Local (not the out parameter) so the lambda can capture it.
		var valueLabelText = new Label
		{
			LayoutMode = 2,
			Text = "",
			HorizontalAlignment = HorizontalAlignment.Right,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		valueLabelText.CustomMinimumSize = new Vector2(56, 0);
		valueLabelText.AddThemeFontSizeOverride("font_size", 18);
		row.AddChild(valueLabelText);

		slider.ValueChanged += v =>
		{
			valueLabelText.Text = ((float)v).ToString(valueFormat);
			onChanged((float)v);
		};
		valueLabelText.Text = ((float)slider.Value).ToString("0");

		valueLabel = valueLabelText;
		return row;
	}

	private static Button MakeButton(string text)
	{
		var button = new Button
		{
			LayoutMode = 2,
			Text = text,
		};
		button.AddThemeFontSizeOverride("font_size", 20);
		var normal = new StyleBoxFlat
		{
			BgColor = new Color(0.16f, 0.2f, 0.3f, 1f),
			BorderColor = new Color(0.4f, 0.5f, 0.7f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 16f,
			ContentMarginTop = 8f,
			ContentMarginRight = 16f,
			ContentMarginBottom = 8f,
		};
		var hover = normal.Duplicate() as StyleBoxFlat;
		hover.BgColor = new Color(0.22f, 0.28f, 0.42f, 1f);
		var pressed = normal.Duplicate() as StyleBoxFlat;
		pressed.BgColor = new Color(0.12f, 0.15f, 0.24f, 1f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", pressed);
		button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddThemeColorOverride("font_hover_color", Colors.White);
		return button;
	}
}
