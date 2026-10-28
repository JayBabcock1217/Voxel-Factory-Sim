using Godot;
using System;
using System.IO;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The in-game LOADING SCREEN: a full-screen overlay (built in code, no .tscn
/// layout) that shows while the voxel world is generating the chunks the
/// player is about to spawn on. It tracks the spawn load progress (the 3x3 of
/// chunks the spawn gate waits on) via the <see cref="VoxelWorld"/>
/// <c>SpawnProgressChanged</c> event, and displays:
///   - a progress bar with a percentage next to it,
///   - world information: the world name, its seed, and basic stats about the
///     save file (size, last-saved time, edit / drop / machine counts).
///
/// The overlay is created in <c>Main.tscn</c> (a <c>CanvasLayer</c> node with
/// this script) so it sits above the 3D world. It is visible until the
/// <see cref="VoxelWorld"/> signals that the spawn gate has opened (progress
/// reaches 1.0), then it fades out and disables itself so the player can play.
/// A world with no slot (a directly-run scene) still works: the stats section
/// degrades to "No save" rather than crashing.
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
	// The gameplay world node the screen tracks. Located by group in _Ready.
	private VoxelWorld _world;

	// UI nodes (built in code in BuildUi).
	private Control _root;
	private Label _worldName;
	private Label _seedLabel;
	private Label _statsLabel;
	private ProgressBar _bar;
	private Label _percent;

	// Progress smoothing so the bar eases toward the target instead of
	// jumping in 1/9 increments.
	private float _shown;
	private float _target;
	private bool _done;

	// Fade-out state.
	private bool _fading;
	private float _fadeAlpha;

	public override void _Ready()
	{
		Name = "LoadingScreen";
		Layer = 100;
		BuildUi();

		// Locate the world node. The VoxelWorld is a child of the Main scene's
		// "Enviornment" node; find it by group so we don't depend on the exact
		// path.
		_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;
		if (_world == null)
		{
			// No world (shouldn't happen in the gameplay scene): hide the
			// screen so it never blocks play.
			GD.PushWarning("[LoadingScreen] No VoxelWorld node found — hiding the loading screen.");
			Visible = false;
			return;
		}

		// Publish the current progress immediately (the gate may have already
		// made progress before we subscribed).
		_target = _world.SpawnLoadProgress;
		_world.SpawnProgressChanged += OnProgress;

		// Populate the world info (name / seed / save stats) once, up front.
		PopulateWorldInfo();
	}

	public override void _Process(double delta)
	{
		if (_done)
		{
			return;
		}

		// Ease the shown progress toward the target for a smooth bar.
		if (_shown < _target)
		{
			// Approach the target at a fixed rate per second (so the bar fills
			// smoothly even though the underlying progress is stepped in 1/9
			// chunks), but never overshoot.
			_shown = Mathf.MoveToward(_shown, _target, (float)delta * 1.5f);
			if (_shown > _target)
			{
				_shown = _target;
			}
		}

		if (_bar != null)
		{
			_bar.Value = _shown * 100f;
		}
		if (_percent != null)
		{
			_percent.Text = $"{Mathf.RoundToInt(_shown * 100f)}%";
		}

		// When the gate has opened (target reached 1.0) AND the bar has caught
		// up, start the fade-out.
		if (!_fading && _target >= 0.999f && _shown >= 0.999f)
		{
			_fading = true;
		}

		if (_fading)
		{
			// Fade the whole overlay out over ~0.4s, then remove it.
			_fadeAlpha = Mathf.MoveToward(_fadeAlpha, 0f, (float)delta * 2.5f);
			ApplyAlpha(_fadeAlpha);
			if (_fadeAlpha <= 0.001f)
			{
				_done = true;
				Visible = false;
				ProcessMode = ProcessModeEnum.Disabled;
			}
		}
	}

	private void OnProgress(float progress)
	{
		_target = progress;
		// If the world already finished (progress 1.0) before we even got a
		// chance to run _Process, mark the target done immediately.
	}

	private void PopulateWorldInfo()
	{
		string name = "New World";
		int seed = 0;
		string stats = "No save file";

		// The WorldSave node lives in the gameplay scene (a sibling of the
		// VoxelWorld). Find it by searching the scene tree rather than hard
		// coding the path, so the screen keeps working if the scene layout
		// changes.
		WorldSave saveNode = FindWorldSave();

		if (saveNode != null)
		{
			if (!string.IsNullOrEmpty(saveNode.ActiveSlotName))
			{
				name = saveNode.ActiveSlotName;
			}
			seed = saveNode.HasSessionSeed ? saveNode.ActiveSessionSeed : 0;
			// The seed the world is actually generating with (the VoxelWorld's
			// own Seed export, which the world applied the session seed to).
			if (_world != null && _world.Seed != 0)
			{
				seed = _world.Seed;
			}
			stats = DescribeSaveFile(saveNode);
		}

		if (_world != null && _world.Seed != 0)
		{
			seed = _world.Seed;
		}

		if (_worldName != null)
		{
			_worldName.Text = name;
		}
		if (_seedLabel != null)
		{
			_seedLabel.Text = seed != 0 ? $"Seed: {seed}" : "Seed: —";
		}
		if (_statsLabel != null)
		{
			_statsLabel.Text = stats;
		}
	}

	/// <summary>
	/// Finds the gameplay scene's <see cref="WorldSave"/> node by searching the
	/// scene tree (it is a direct child of the Main node, a sibling of the
	/// VoxelWorld). Returns null when there is none (a directly-run scene).
	/// </summary>
	private WorldSave FindWorldSave()
	{
		// The WorldSave node is a direct child of the Main scene node (the
		// LoadingScreen's parent). Search the parent's children first (so we
		// never pick up a stale node from a previous scene), then fall back to
		// a tree-wide name search for robustness.
		var scene = GetParent();
		if (scene != null)
		{
			foreach (Node child in scene.GetChildren())
			{
				if (child is WorldSave ws)
				{
					return ws;
				}
			}
		}
		return GetTree().Root.FindChild("WorldSave", true, false) as WorldSave;
	}

	/// <summary>
	/// Builds a short, human-readable stat line for the active save file: its
	/// size on disk and the last-saved time, plus the counts of the sections
	/// the save carries (edits, drops, machines). Degrades gracefully when the
	/// file does not exist yet (a fresh world that has not been saved).
	/// </summary>
	private string DescribeSaveFile(WorldSave saveNode)
	{
		string slotId = saveNode.ActiveSlotId;
		if (string.IsNullOrEmpty(slotId))
		{
			return "No save file (fresh world)";
		}

		string path = WorldSave.SlotPath(slotId);
		var sb = new System.Text.StringBuilder();

		if (File.Exists(path))
		{
			long bytes = new FileInfo(path).Length;
			sb.Append($"Save: {FormatBytes(bytes)}");
			try
			{
				string modified = DateTime.FromFileTimeUtc(new FileInfo(path).LastWriteTimeUtc.Ticks).ToLocalTime().ToString("MMM d, yyyy  HH:mm");
				sb.Append($", last saved {modified}");
			}
			catch
			{
				// Ignore timestamp read failures; the size alone is enough.
			}
		}
		else
		{
			sb.Append("Save: not written yet (fresh world)");
		}

		// Section counts from the in-memory snapshot (what will be / was saved).
		var snap = saveNode.Snapshot;
		if (snap != null)
		{
			int edits = snap.Edits != null ? snap.Edits.Count : 0;
			int drops = snap.Drops != null ? snap.Drops.Count : 0;
			int machines = snap.Machines != null ? snap.Machines.Count : 0;
			if (edits > 0 || drops > 0 || machines > 0)
			{
				sb.Append($"  ·  {edits} edits, {drops} drops, {machines} machines");
			}
		}

		return sb.ToString();
	}

	private static string FormatBytes(long bytes)
	{
		if (bytes < 1024)
		{
			return $"{bytes} B";
		}
		double kb = bytes / 1024.0;
		if (kb < 1024)
		{
			return $"{kb:0.#} KB";
		}
		double mb = kb / 1024.0;
		return $"{mb:0.##} MB";
	}

	private void ApplyAlpha(float a)
	{
		if (_root == null)
		{
			return;
		}
		// Modulate the whole overlay by the fade alpha.
		_root.Modulate = new Color(1f, 1f, 1f, a);
	}

	// ------------------------------------------------------------------
	// UI construction (code-built, matching the project's menu style)
	// ------------------------------------------------------------------

	private void BuildUi()
	{
		_fadeAlpha = 1f;

		// Full-rect translucent backdrop so the world behind is dimmed.
		_root = new ColorRect
		{
			Name = "Root",
			Color = new Color(0.03f, 0.04f, 0.07f, 0.92f),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		AddChild(_root);

		// Full-rect center container that centers its child (the info column)
		// both horizontally and vertically.
		var centerWrap = new CenterContainer
		{
			Name = "CenterWrap",
		};
		centerWrap.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.AddChild(centerWrap);

		// The info column: title, world name, seed, stats, bar, percent.
		var center = new VBoxContainer
		{
			Name = "Center",
		};
		center.AddThemeConstantOverride("separation", 12);
		centerWrap.AddChild(center);

		var title = new Label
		{
			Text = "Loading World",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		title.AddThemeFontSizeOverride("font_size", 48);
		title.AddThemeColorOverride("font_color", new Color(0.95f, 0.96f, 1f));
		title.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.15f, 0.3f, 1f));
		title.AddThemeConstantOverride("outline_size", 8);
		center.AddChild(title);

		_worldName = new Label
		{
			Text = "…",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_worldName.AddThemeFontSizeOverride("font_size", 28);
		_worldName.AddThemeColorOverride("font_color", new Color(0.8f, 0.85f, 0.95f));
		center.AddChild(_worldName);

		_seedLabel = new Label
		{
			Text = "…",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_seedLabel.AddThemeFontSizeOverride("font_size", 18);
		_seedLabel.AddThemeColorOverride("font_color", new Color(0.55f, 0.6f, 0.75f));
		center.AddChild(_seedLabel);

		_statsLabel = new Label
		{
			Text = "…",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_statsLabel.AddThemeFontSizeOverride("font_size", 15);
		_statsLabel.AddThemeColorOverride("font_color", new Color(0.45f, 0.5f, 0.65f));
		center.AddChild(_statsLabel);

		// Spacer between the info block and the bar.
		center.AddChild(MakeSpacer(28f));

		// Progress row: the bar with a percentage label next to it.
		var barRow = new HBoxContainer
		{
			Name = "BarRow",
			CustomMinimumSize = new Vector2(520, 0),
		};
		barRow.AddThemeConstantOverride("separation", 16);
		center.AddChild(barRow);

		_bar = new ProgressBar
		{
			Name = "Bar",
			MinValue = 0f,
			MaxValue = 100f,
			Value = 0f,
			ShowPercentage = false,
			CustomMinimumSize = new Vector2(420, 26),
		};
		barRow.AddChild(_bar);

		_percent = new Label
		{
			Name = "Percent",
			Text = "0%",
			HorizontalAlignment = HorizontalAlignment.Left,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			CustomMinimumSize = new Vector2(64, 0),
		};
		_percent.AddThemeFontSizeOverride("font_size", 22);
		_percent.AddThemeColorOverride("font_color", new Color(0.85f, 0.9f, 1f));
		barRow.AddChild(_percent);
	}

	private static Control MakeSpacer(float height)
	{
		return new Control
		{
			CustomMinimumSize = new Vector2(0, height),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
	}
}
