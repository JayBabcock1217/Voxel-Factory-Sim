using Godot;

namespace FactorySim.Editor;

/// <summary>
/// A Godot <see cref="EditorPlugin"/> that adds a "Machine Debug" section to
/// the inspector for <see cref="FactorySim.Voxel.VoxelWorld"/> nodes, containing
/// two buttons:
///
///   - **Rebuild Machine Collisions** — re-runs the auto-generated box
///     collision on every machine currently in the scene. Use this after
///     re-importing or re-scaling a Blender mesh so the collider matches
///     the new visuals.
///   - **Open Machine Config** — opens the in-game machine-config panel
///     (the list of all registered machine definitions).
///
/// The buttons call into the <see cref="FactorySim.Voxel.VoxelWorld"/> in the
/// running scene tree. They work in play mode (the game is running); in the
/// editor viewport (not playing) the world may not be initialized, so the
/// buttons are effectively no-ops.
///
/// Setup:
///   1. This file is paired with <c>VoxelWorldPlugin.tscn</c> (an
///      <c>EditorPlugin</c> node with this script).
///   2. The plugin is enabled in <c>project.godot</c> under
///      <c>[editor_plugins]</c>.
///   3. Select any <see cref="FactorySim.Voxel.VoxelWorld"/> node — the
///      "Machine Debug" buttons appear at the bottom of its inspector.
/// </summary>
[Tool]
public partial class VoxelWorldPlugin : EditorPlugin
{
	private VoxelWorldInspectorPlugin _inspectorPlugin;

	public override void _EnterTree()
	{
		_inspectorPlugin = new VoxelWorldInspectorPlugin();
		AddInspectorPlugin(_inspectorPlugin);
	}

	public override void _ExitTree()
	{
		if (_inspectorPlugin != null)
		{
			RemoveInspectorPlugin(_inspectorPlugin);
			_inspectorPlugin = null;
		}
	}
}

/// <summary>
/// An <see cref="EditorInspectorPlugin"/> that adds a custom control to the
/// end of the inspector for <see cref="FactorySim.Voxel.VoxelWorld"/> nodes.
///
/// C# type identification: because <c>VoxelWorld</c> is not a <c>[Tool]</c>
/// script, the inspector may see it as its base type. We identify it by
/// checking whether the object has the <c>RebuildAllMachineCollisions</c>
/// method (which only <c>VoxelWorld</c> has).
/// </summary>
[Tool]
public partial class VoxelWorldInspectorPlugin : EditorInspectorPlugin
{
	public override bool _CanHandle(GodotObject @object)
	{
		return @object.HasMethod("RebuildAllMachineCollisions");
	}

	public override void _ParseEnd(GodotObject @object)
	{
		if (@object.HasMethod("RebuildAllMachineCollisions"))
		{
			AddCustomControl(new VoxelWorldDebugControl());
		}
	}
}

/// <summary>
/// A <see cref="Control"/> that renders the two action buttons for
/// <see cref="FactorySim.Voxel.VoxelWorld"/>. Added to the inspector via
/// <see cref="VoxelWorldInspectorPlugin._ParseEnd"/>. The buttons find the
/// <see cref="FactorySim.Voxel.VoxelWorld"/> in the running scene tree and
/// call the corresponding method.
/// </summary>
[Tool]
public partial class VoxelWorldDebugControl : Control
{
	private Button _rebuildBtn;
	private Button _configBtn;

	public override void _Ready()
	{
		CustomMinimumSize = new Vector2(0f, 48f);

		var vbox = new VBoxContainer { Name = "DebugButtons" };
		vbox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
		AddChild(vbox);

		var label = new Label
		{
			Text = "Machine Debug",
			Modulate = new Color(1f, 1f, 1f, 0.6f),
		};
		label.AddThemeFontSizeOverride("font_size", 12);
		vbox.AddChild(label);

		_rebuildBtn = new Button
		{
			Text = "Rebuild Machine Collisions",
			TooltipText = "Re-run the auto-generated box collision on every placed machine. Use after re-importing or re-scaling a Blender mesh.",
		};
		_configBtn = new Button
		{
			Text = "Open Machine Config",
			TooltipText = "Open the machine-config panel listing all registered machine definitions.",
		};
		_rebuildBtn.Pressed += OnRebuildPressed;
		_configBtn.Pressed += OnConfigPressed;
		vbox.AddChild(_rebuildBtn);
		vbox.AddChild(_configBtn);
	}

	private void OnRebuildPressed()
	{
		var world = FindVoxelWorld();
		if (world == null)
		{
			GD.PushWarning("[VoxelWorldPlugin] No VoxelWorld found in the scene tree.");
			return;
		}
		world.RebuildAllMachineCollisions();
		GD.Print("[VoxelWorldPlugin] Machine collisions rebuilt.");
	}

	private void OnConfigPressed()
	{
		var world = FindVoxelWorld();
		if (world == null)
		{
			GD.PushWarning("[VoxelWorldPlugin] No VoxelWorld found in the scene tree.");
			return;
		}
		world.OpenMachineConfigPanel();
	}

	/// <summary>
	/// Finds the <see cref="FactorySim.Voxel.VoxelWorld"/> in the running
	/// scene tree by name.
	/// </summary>
	private static FactorySim.Voxel.VoxelWorld FindVoxelWorld()
	{
		var sceneTree = Engine.GetMainLoop() as SceneTree;
		if (sceneTree == null)
		{
			return null;
		}
		var found = sceneTree.Root.FindChild("VoxelWorld", true, false);
		return found as FactorySim.Voxel.VoxelWorld;
	}
}
