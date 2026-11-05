using Godot;

namespace FactorySim.UI;

/// <summary>
/// A small HUD readout pinned to the top-right corner of the screen that shows
/// information about the voxel the player is currently looking at (the target
/// the <see cref="FactorySim.Voxel.VoxelTool"/> is raycasting onto).
///
/// This is deliberately a standalone node (a <see cref="Node"/> that builds its
/// own <see cref="CanvasLayer"/> entirely in code, exactly like
/// <see cref="FpsCounter"/>) so it needs no scene file: add a single
/// <c>TargetInfoUI</c> node to the scene tree and it installs itself. It is a
/// NON-inventory UI element — always visible, independent of the inventory
/// panel, and it never intercepts mouse input (every control uses
/// <c>MouseFilter = Ignore</c>).
///
/// The panel is populated by the <see cref="FactorySim.Voxel.VoxelTool"/>, which
/// calls <see cref="Update"/> once per frame with a <see cref="TargetInfo"/>.
/// When there is no target (the player is looking at empty sky beyond the
/// raycast range) the whole panel is hidden.
///
/// Shown fields, depending on the target:
///
/// When the target is a PLAIN VOXEL (top to bottom):
///   - Material name (bold, the primary line)
///   - Health, as a percentage of full health (with a small bar)
///   - The most efficient tool to break it (by affinity)
///   - Base break time (seconds, at default tool power)
///   - Material category (soft / stone / wood / metal / generic)
///   - Distance from the camera, in world units
///   - A flag line when the material is unbreakable
///
/// When the target is a MACHINE (top to bottom):
///   - Machine name (bold, the primary line)
///   - Live status detail rows: power (consumer ✓/✗, source output), the
///     current recipe (input → output or product), progress %, and the
///     input/output buffer contents. This is the at-a-glance status the
///     player sees BEFORE right-clicking to open the machine.
///   - Distance from the camera, in world units
///
/// The mining rows (health / best tool / break time / category) are hidden
/// for a machine (it lives in an air voxel, so the material data would read
/// as "Air") and the detail rows are hidden for a plain voxel — so the panel
/// always shows the rows relevant to the target and resizes to fit.
/// </summary>
public partial class TargetInfoUI : Node
{
	// Panel geometry, shared by _Ready (layout) and Update (sizing). These must
	// match the StyleBoxFlat content margins set in _Ready, so the rows inset and
	// the panel height computation agree.
	private const float PanelWidth = 232f;
	private const float PanelMarginX = 10f; // left/right content margins
	private const float PanelMarginY = 8f;  // top/bottom content margins
	// The panel's top offset from the screen top (it sits below the FPS readout)
	// and its right margin. The height is NOT fixed — it grows to fit the rows.
	private const float PanelTop = 44f;
	private const float PanelMargin = 10f;

	private CanvasLayer _layer;
	private Panel _panel;
	private VBoxContainer _rows;
	private Label _nameLabel;
	private Label _healthLabel;
	private ColorRect _healthBarBg;
	private ColorRect _healthBarFill;
	private Label _bestToolLabel;
	private Label _breakTimeLabel;
	private Label _categoryLabel;
	private Label _distanceLabel;
	private Label _unbreakableLabel;
	private Label[] _machineDetailLabels; // detail rows (power, recipe, progress, buffers)
	private Label _rotationLabel;
	/// <summary>Max number of machine detail rows the panel can show at once.</summary>
	private const int MaxMachineDetailLines = 6;

	/// <summary>
	/// The snapshot of target-voxel information to display. The VoxelTool builds
	/// one each frame. When <see cref="HasTarget"/> is false the panel hides.
	/// </summary>
	public struct TargetInfo
	{
		public bool HasTarget;
		public string MaterialName;
		/// <summary>Current health as a percentage of full health (0..100).</summary>
		public float HealthPercent;
		/// <summary>The most efficient tool for this material (by break affinity).</summary>
		public string BestTool;
		/// <summary>Base break time in seconds (at default tool power, no affinity).</summary>
		public float BreakTime;
		/// <summary>Display name of the material category (soft/stone/wood/metal/generic).</summary>
		public string Category;
		/// <summary>Distance from the camera to the targeted voxel, in world units.</summary>
		public float Distance;
		/// <summary>True when the material cannot be broken (e.g. bedrock).</summary>
		public bool Unbreakable;
		/// <summary>
		/// The machine's display name, shown as the main (bold) name line when
		/// the player is aiming at a machine. Empty when the target is a plain
		/// voxel.
		/// </summary>
		public string MachineName;
		/// <summary>
		/// The machine's detail rows (power status, current recipe, progress,
		/// input/output buffer contents), each rendered as its own line under the
		/// machine name. Empty/absent when the target is a plain voxel. This is
		/// what the panel shows BEFORE the player right-clicks to open the
		/// machine — a quick at-a-glance status of the machine being looked at.
		/// </summary>
		public string[] MachineDetails;
		/// <summary>True when the target is a machine (not a plain voxel).</summary>
		public bool IsMachine;
		/// <summary>
		/// A one-line machine-placement-rotation readout (e.g. "Rotation: Y 90°
		/// (R: axis ←→: rotate ↑↓: set Esc: reset)"), shown only while the
		/// player is holding a placeable machine item. Empty otherwise — the
		/// VoxelTool fills it in so the player always sees the current
		/// orientation and the keys that change it.
		/// </summary>
		public string RotationLine;
	}

	public override void _Ready()
	{
		AddToGroup("target_info_ui");

		_layer = new CanvasLayer { Layer = 100 };

		// A semi-opaque panel so the text reads against any terrain, anchored to
		// the top-right corner just below the FPS readout.
		_panel = new Panel
		{
			Name = "TargetInfoPanel",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		var panelStyle = new StyleBoxFlat
		{
			BgColor = new Color(0f, 0f, 0f, 0.55f),
			BorderColor = new Color(0.4f, 0.6f, 0.8f, 0.8f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = PanelMarginX,
			ContentMarginTop = PanelMarginY,
			ContentMarginRight = PanelMarginX,
			ContentMarginBottom = PanelMarginY,
		};
		_panel.AddThemeStyleboxOverride("panel", panelStyle);

		// Anchor the panel to the top-right corner, with a margin. It sits just
		// below the FPS label (which occupies the very top-right ~40px). The
		// width is fixed; the height is set each frame by Update() to fit the
		// rows, so the background always wraps its content snugly.
		_panel.AnchorLeft = 1f;
		_panel.AnchorTop = 0f;
		_panel.AnchorRight = 1f;
		_panel.AnchorBottom = 0f;
		_panel.Position = new Vector2(-PanelWidth - PanelMargin, PanelTop);
		_panel.Size = new Vector2(PanelWidth, 100f); // initial; resized by Update

		// The rows live in a VBoxContainer inset by the panel's content margins.
		// Its width is fixed (the panel width minus margins); its height is what
		// drives the panel height — Update() reads GetCombinedMinimumSize() and
		// resizes both the panel and the vbox to fit.
		var vbox = new VBoxContainer
		{
			Name = "TargetInfoRows",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		vbox.AddThemeConstantOverride("separation", 5);
		vbox.Position = new Vector2(panelStyle.ContentMarginLeft, panelStyle.ContentMarginTop);
		vbox.Size = new Vector2(
			PanelWidth - PanelMarginX * 2f,
			100f - PanelMarginY * 2f);
		_panel.AddChild(vbox);
		_rows = vbox;

		_nameLabel = MakeLabel("No target", 16, new Color(0.98f, 0.98f, 1f));
		_nameLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
		_nameLabel.AddThemeConstantOverride("outline_size", 4);
		vbox.AddChild(_nameLabel);

		_healthLabel = MakeLabel("Health: —", 13, new Color(0.85f, 0.9f, 0.95f));
		vbox.AddChild(_healthLabel);

		// A slim health bar under the percentage: a dark track with a green fill
		// whose width tracks current health.
		_healthBarBg = new ColorRect
		{
			Color = new Color(0.1f, 0.1f, 0.12f, 0.9f),
			CustomMinimumSize = new Vector2(0f, 6f),
			SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		vbox.AddChild(_healthBarBg);
		_healthBarFill = new ColorRect
		{
			Color = new Color(0.3f, 0.8f, 0.4f, 1f),
			CustomMinimumSize = new Vector2(0f, 6f),
			SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_healthBarBg.AddChild(_healthBarFill);
		// The fill is a free child of the track (a ColorRect, not a container),
		// so it is positioned with explicit Position + Size rather than anchors:
		// pinned to the track's top-left, and its width is driven by Update().
		_healthBarFill.Position = Vector2.Zero;

		_bestToolLabel = MakeLabel("Best tool: —", 13, new Color(0.85f, 0.9f, 0.95f));
		vbox.AddChild(_bestToolLabel);

		_breakTimeLabel = MakeLabel("Break time: —", 13, new Color(0.85f, 0.9f, 0.95f));
		vbox.AddChild(_breakTimeLabel);

		_categoryLabel = MakeLabel("Category: —", 13, new Color(0.7f, 0.75f, 0.82f));
		vbox.AddChild(_categoryLabel);

		_distanceLabel = MakeLabel("Distance: —", 13, new Color(0.7f, 0.75f, 0.82f));
		vbox.AddChild(_distanceLabel);

		_unbreakableLabel = MakeLabel("Unbreakable", 13, new Color(0.95f, 0.5f, 0.45f));
		_unbreakableLabel.Visible = false;
		vbox.AddChild(_unbreakableLabel);

		// Machine detail rows: a fixed pool of labels, shown/hidden + filled
		// per-frame by Update(). The count varies by machine type (a power
		// source has one line, a smelter has four or five), so a pool lets the
		// panel grow/shrink without creating/destroying nodes each frame. When
		// a machine is targeted, its name replaces the bold primary name line
		// and these rows show its live status (power, recipe, progress, buffers).
		_machineDetailLabels = new Label[MaxMachineDetailLines];
		for (int i = 0; i < MaxMachineDetailLines; i++)
		{
			var lbl = MakeLabel("", 13, new Color(0.85f, 0.9f, 0.95f));
			lbl.Visible = false;
			vbox.AddChild(lbl);
			_machineDetailLabels[i] = lbl;
		}

		// The placement-rotation readout: only visible while the player holds a
		// machine item (the VoxelTool fills RotationLine then), showing the
		// current orientation + the keys that change it.
		_rotationLabel = MakeLabel("", 12, new Color(0.55f, 0.95f, 0.7f));
		_rotationLabel.Visible = false;
		vbox.AddChild(_rotationLabel);

		_panel.Visible = false; // hidden until the tool reports a target
		_layer.AddChild(_panel);
		AddChild(_layer);
	}

	private static Label MakeLabel(string text, int fontSize, Color color)
	{
		var label = new Label
		{
			Text = text,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.AddThemeFontSizeOverride("font_size", fontSize);
		label.AddThemeColorOverride("font_color", color);
		return label;
	}

	/// <summary>
	/// Sets the displayed target info. Hides the panel when
	/// <see cref="TargetInfo.HasTarget"/> is false. Called by the VoxelTool once
	/// per frame.
	/// </summary>
	public void Update(TargetInfo info)
	{
		if (_panel == null)
		{
			return;
		}

		if (!info.HasTarget)
		{
			_panel.Visible = false;
			return;
		}

		_panel.Visible = true;

		_distanceLabel.Text = $"Distance: {info.Distance:0.0}m";

		// MATERIAL TARGET: show the voxel's name + its mining rows (health, best
		// tool, break time, category, unbreakable).
		bool isMachine = info.IsMachine;
		if (!isMachine)
		{
			_nameLabel.Text = info.MaterialName;

			int hp = Mathf.RoundToInt(info.HealthPercent);
			_healthLabel.Text = $"Health: {hp}%";

			// Health bar fill: width is a fraction of the track width, clamped 0..1.
			// Fall back to the known content width if the track hasn't been laid
			// out yet (the first frame the panel becomes visible).
			float trackW = _healthBarBg.Size.X;
			if (trackW <= 0f)
			{
				trackW = PanelWidth - 2f * PanelMarginX;
			}
			float frac = Mathf.Clamp(info.HealthPercent / 100f, 0f, 1f);
			float barH = _healthBarBg.Size.Y > 0f ? _healthBarBg.Size.Y : 6f;
			_healthBarFill.Size = new Vector2(trackW * frac, barH);
			// Recolor the fill as health drops (green -> yellow -> red).
			_healthBarFill.Color = HealthColor(info.HealthPercent);

			_bestToolLabel.Text = info.Unbreakable ? "Best tool: — (unbreakable)" : $"Best tool: {info.BestTool}";
			_breakTimeLabel.Text = info.Unbreakable ? "Break time: — (unbreakable)" : $"Break time: {info.BreakTime:0.##}s";
			_categoryLabel.Text = $"Category: {info.Category}";
			_unbreakableLabel.Visible = info.Unbreakable;
		}
		else
		{
			// Hide the mining rows: a machine lives in an AIR voxel, so the
			// material data would read as "Air" / 100% health — misleading. The
			// machine's own status (name + detail rows) is what the player needs.
			_unbreakableLabel.Visible = false;
		}
		// The mining rows' visibility is set once below (they show for a voxel,
		// hide for a machine) so the panel height reflects the active rows.
		bool showMiningRows = !isMachine;
		_healthLabel.Visible = showMiningRows;
		_healthBarBg.Visible = showMiningRows;
		_bestToolLabel.Visible = showMiningRows;
		_breakTimeLabel.Visible = showMiningRows;
		_categoryLabel.Visible = showMiningRows;

		// MACHINE READOUT: when targeting a machine, its name replaces the bold
		// primary name line and its detail rows (power, recipe, progress,
		// buffers) fill the detail pool. This is the at-a-glance status the
		// player sees BEFORE right-clicking to open the machine.
		if (isMachine)
		{
			_nameLabel.Text = string.IsNullOrEmpty(info.MachineName) ? "Machine" : info.MachineName;
			var details = info.MachineDetails ?? System.Array.Empty<string>();
			int shown = System.Math.Min(details.Length, MaxMachineDetailLines);
			for (int i = 0; i < MaxMachineDetailLines; i++)
			{
				if (i < shown && !string.IsNullOrEmpty(details[i]))
				{
					_machineDetailLabels[i].Text = details[i];
					_machineDetailLabels[i].Visible = true;
				}
				else
				{
					_machineDetailLabels[i].Visible = false;
				}
			}
		}
		else
		{
			for (int i = 0; i < MaxMachineDetailLines; i++)
			{
				_machineDetailLabels[i].Visible = false;
			}
		}

		_rotationLabel.Visible = !string.IsNullOrEmpty(info.RotationLine);
		if (!string.IsNullOrEmpty(info.RotationLine))
		{
			_rotationLabel.Text = info.RotationLine;
		}

		// Resize the panel background to fit its rows: the VBox's combined
		// minimum size is the height the content actually needs, so adding the
		// panel's content margins gives the exact background height to wrap
		// everything snugly. This grows the panel when the unbreakable line
		// appears and shrinks it when it doesn't.
		ResizeToFit();
	}

	/// <summary>
	/// Resizes the panel (and its inner rows) so the background snugly wraps the
	/// rows' combined minimum size. Called from <see cref="Update"/> after the
	/// labels are set, because the unbreakable line's visibility changes the
	/// content height.
	/// </summary>
	private void ResizeToFit()
	{
		if (_rows == null || _panel == null)
		{
			return;
		}

		float contentH = _rows.GetCombinedMinimumSize().Y;
		float panelH = contentH + 2f * PanelMarginY;
		_panel.Size = new Vector2(PanelWidth, panelH);
		_rows.Size = new Vector2(
			PanelWidth - 2f * PanelMarginX,
			contentH);
	}

	/// <summary>A green→yellow→red color for a 0..100 health percentage.</summary>
	private static Color HealthColor(float percent)
	{
		float t = Mathf.Clamp(percent / 100f, 0f, 1f);
		// Red at 0, yellow at 0.5, green at 1.0 (simple two-segment lerp).
		if (t < 0.5f)
		{
			return new Color(0.9f, 0.3f, 0.3f).Lerp(new Color(0.95f, 0.85f, 0.3f), t / 0.5f);
		}
		return new Color(0.95f, 0.85f, 0.3f).Lerp(new Color(0.3f, 0.8f, 0.4f), (t - 0.5f) / 0.5f);
	}
}
