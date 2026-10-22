using Godot;
using System;

namespace FactorySim.Editor;

/// <summary>
/// A Godot <see cref="EditorPlugin"/> that adds a "Machine Debug" section to
/// the inspector for <see cref="FactorySim.Voxel.VoxelWorld"/> nodes, containing
/// two buttons:
///
///   - **Generate Collision Boxes** — for EVERY machine definition in the
///     world's <c>MachineConfig</c>, computes the collision box (AABB) from the
///     definition's mesh/scene (respecting its <c>Scale</c>) and writes it onto
///     the definition resource (<c>CollisionBoxSize</c> + <c>CollisionBoxOffset</c>).
///     This authors the collision data OUTSIDE OF RUNTIME: it runs entirely in
///     the editor, needs no live machines, and the result is persisted with the
///     .tres resource. Use it after re-importing or re-scaling a Blender mesh.
///     At runtime, a <c>Machine</c> uses this authored box when present.
///   - **Open Machine Config** — opens the in-game machine-config panel
///     (the list of all registered machine definitions). Only works in
///     play mode (the panel is an in-game CanvasLayer); in the editor it
///     shows a hint.
///
/// Setup (standard Godot editor-plugin layout):
///   1. <c>addons/voxel_world_machine_debug/plugin.cfg</c> — plugin metadata,
///      points at this script.
///   2. <c>project.godot</c> <c>[editor_plugins]</c> enabled list references
///      the <c>plugin.cfg</c> path.
///   3. Build the C# project (the plugin script must be compiled before the
///      editor can load it).
///   4. In the editor: Project → Project Settings → Plugins → enable this
///      plugin.
///   5. Select any <see cref="FactorySim.Voxel.VoxelWorld"/> node — the
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
			// Pass the ACTUAL node being inspected (the VoxelWorld in the scene
			// the user has open) so the buttons operate on it directly, rather
			// than searching the editor's root tree (which doesn't contain the
			// edited scene's nodes).
			AddCustomControl(new VoxelWorldDebugControl(@object as Node));
		}
	}
}

/// <summary>
/// A <see cref="Control"/> that renders the two action buttons for
/// <see cref="FactorySim.Voxel.VoxelWorld"/>. Added to the inspector via
/// <see cref="VoxelWorldInspectorPlugin._ParseEnd"/>. The buttons find the
/// relevant nodes in the running scene tree and call the corresponding
/// methods.
/// </summary>
[Tool]
public partial class VoxelWorldDebugControl : Control
{
	/// <summary>
	/// The <see cref="FactorySim.Voxel.VoxelWorld"/> node this control was
	/// created for (the one being inspected). Passed in by
	/// <see cref="VoxelWorldInspectorPlugin._ParseEnd"/> so the buttons
	/// operate on the exact node in the edited scene.
	/// </summary>
	private readonly Node _worldNode;

	private Button _rebuildBtn;
	private Button _configBtn;

	public VoxelWorldDebugControl(Node worldNode)
	{
		_worldNode = worldNode;
	}

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
			Text = "Generate Collision Boxes",
			TooltipText = "Compute the collision box (AABB) from each machine definition's mesh/scene and store it on the definition resource. Runs in the editor (no runtime needed). Use after re-importing or re-scaling a Blender mesh.",
		};
		_configBtn = new Button
		{
			Text = "Open Machine Config",
			TooltipText = "Open the machine-config panel listing all registered machine definitions. Works in play mode (press C in-game).",
		};
		_rebuildBtn.Pressed += OnGenerateCollisionBoxes;
		_configBtn.Pressed += OnConfigPressed;
		vbox.AddChild(_rebuildBtn);
		vbox.AddChild(_configBtn);
	}

	private void OnGenerateCollisionBoxes()
	{
		// Operate on the EXACT VoxelWorld node being inspected (passed in by the
		// inspector plugin). This is the node in the scene the user has open —
		// the editor's root tree does NOT contain the edited scene's nodes, so
		// searching Root would fail.
		//
		// IMPORTANT: in the editor, a non-[Tool] C# script node is a PLACEHOLDER
		// of its base type (Node3D), so `_worldNode as VoxelWorld` is null even
		// though the node IS a VoxelWorld. We therefore read the MachineConfig
		// via the dynamic Get() (which works on any Node), not a typed property.
		FactorySim.Machines.MachineDefConfig config = null;
		Node targetNode = null;

		if (_worldNode != null && _worldNode.HasMethod("RebuildAllMachineCollisions"))
		{
			// The inspected node is a VoxelWorld (by the method heuristic). Read
			// its MachineConfig dynamically (Get() returns a Variant; cast it).
			targetNode = _worldNode;
			var cfgVariant = _worldNode.Get("MachineConfig");
			if (cfgVariant.VariantType == Variant.Type.Object)
			{
				config = (FactorySim.Machines.MachineDefConfig)cfgVariant.Obj;
			}
		}

		// Fallback: search the running scene tree (play mode, or no node captured).
		if (targetNode == null)
		{
			var sceneTree = Engine.GetMainLoop() as SceneTree;
			if (sceneTree != null)
			{
				foreach (var node in sceneTree.Root.FindChildren("*", "Node3D", true, false))
				{
					if (node is FactorySim.Voxel.VoxelWorld w)
					{
						targetNode = w;
						config = w.MachineConfig;
						break;
					}
				}
			}
		}

		if (targetNode == null)
		{
			GD.Print("[VoxelWorldPlugin] No VoxelWorld node found. " +
				"Select the VoxelWorld node in a scene to generate collision boxes.");
			return;
		}

		if (config == null)
		{
			GD.Print("[VoxelWorldPlugin] The VoxelWorld has no MachineConfig assigned. " +
				"Assign a MachineDefConfig resource to the VoxelWorld's MachineConfig export first.");
			return;
		}

		var defs = config.Machines ?? Array.Empty<FactorySim.Machines.MachineDefinition>();
		int authored = 0;
		int skipped = 0;

		foreach (var def in defs)
		{
			if (def == null)
			{
				continue;
			}
			if (FactorySim.Machines.Machine.TryGetCollisionBoxFromDefinition(def, out Vector3 size, out Vector3 center))
			{
				def.SetCollisionBoxSize(size);
				def.SetCollisionBoxOffset(center);
				authored++;
				GD.Print($"[VoxelWorldPlugin]   '{def.Name}' ({def.Type}) -> box {size.X:F3} x {size.Y:F3} x {size.Z:F3} @ offset {center.X:F3}, {center.Y:F3}, {center.Z:F3}");
			}
			else
			{
				skipped++;
				GD.Print($"[VoxelWorldPlugin]   '{def.Name}' ({def.Type}) -> no mesh/scene assigned, skipped.");
			}
		}

		GD.Print($"[VoxelWorldPlugin] Generated collision boxes for {authored}/{defs.Length} definition(s) " +
			$"({skipped} skipped). Save the MachineDefConfig .tres to persist.");
	}

	private void OnConfigPressed()
	{
		// The config panel is an in-game CanvasLayer. It can only be opened
		// when the game is running (play mode). In the editor, show a hint.
		if (!Engine.IsEditorHint())
		{
			// Prefer the exact node being inspected; fall back to a tree search.
			var world = _worldNode as FactorySim.Voxel.VoxelWorld;
			if (world == null)
			{
				var sceneTree = Engine.GetMainLoop() as SceneTree;
				world = sceneTree?.Root.FindChild("VoxelWorld", true, false) as FactorySim.Voxel.VoxelWorld;
			}
			if (world == null)
			{
				GD.PushWarning("[VoxelWorldPlugin] No VoxelWorld found in the scene tree.");
				return;
			}
			world.OpenMachineConfigPanel();
			return;
		}

		GD.Print("[VoxelWorldPlugin] The machine config panel opens in play mode. " +
			"Run the game and press C, or edit the MachineDefConfig .tres directly in the editor.");
	}
}
