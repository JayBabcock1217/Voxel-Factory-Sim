using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Machines;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// An in-game <see cref="CanvasLayer"/> panel that lists every registered
/// <see cref="MachineDefinition"/> in the current world's
/// <see cref="MachineDefConfig"/>. The user can:
///
///   - **See** all machines at a glance: name, type, item id, mesh assigned,
///     scale, power output, power consumption.
///   - **Add** a new machine definition (pick a type).
///   - **Remove** an existing machine definition.
///   - **Rebuild All Collisions** — re-runs the auto-generated box collision
///     on every placed machine in the scene.
///
/// The panel is opened by the editor's "Open Machine Config" button (via the
/// <see cref="FactorySim.Editor.VoxelWorldPlugin"/>) or by calling
/// <see cref="VoxelWorld.OpenMachineConfigPanel"/>. It toggles on/off.
///
/// Editing the config in-game modifies the in-memory <see cref="MachineDefConfig"/>
/// resource. To persist changes to disk, the user must save the .tres resource
/// in the editor (the panel does NOT auto-save resources).
/// </summary>
public partial class MachineConfigPanel : CanvasLayer
{
	private MachineDefConfig _config;
	private Control _root;
	private VBoxContainer _machineList;
	private Label _statusLabel;
	private bool _visible;

	/// <summary>
	/// Configures the panel with a <see cref="MachineDefConfig"/> to edit.
	/// Must be called before the first <see cref="Toggle"/>.
	/// </summary>
	public void Configure(MachineDefConfig config)
	{
		_config = config;
		BuildUI();
	}

	/// <summary>
	/// Toggles the panel's visibility. When shown, the machine list is
	/// refreshed from the current config.
	/// </summary>
	public void Toggle()
	{
		_visible = !_visible;
		if (_root != null)
		{
			_root.Visible = _visible;
		}
		if (_visible)
		{
			RefreshList();
		}
	}

	/// <summary>Returns true if the panel is currently visible.</summary>
	public bool PanelVisible => _visible;

	private void BuildUI()
	{
		_root = new Control
		{
			Name = "PanelRoot",
		};
		_root.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.MouseFilter = Control.MouseFilterEnum.Stop;
		AddChild(_root);

		var bg = new ColorRect
		{
			Name = "Background",
			Color = new Color(0f, 0f, 0f, 0.6f),
		};
		bg.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.AddChild(bg);

		var center = new CenterContainer
		{
			Name = "Center",
		};
		center.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_root.AddChild(center);

		var panel = new PanelContainer
		{
			Name = "Panel",
			CustomMinimumSize = new Vector2(720f, 520f),
		};
		center.AddChild(panel);

		var vbox = new VBoxContainer
		{
			Name = "VBox",
		};
		vbox.AddThemeConstantOverride("separation", 8);
		panel.AddChild(vbox);

		var title = new Label
		{
			Name = "Title",
			Text = "Machine Config",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		title.AddThemeFontSizeOverride("font_size", 22);
		vbox.AddChild(title);

		var topRow = new HBoxContainer
		{
			Name = "TopRow",
		};
		topRow.AddThemeConstantOverride("separation", 8);
		vbox.AddChild(topRow);

		var addBtn = new Button { Text = "Add Machine" };
		addBtn.Pressed += OnAddMachine;
		topRow.AddChild(addBtn);

		var rebuildBtn = new Button { Text = "Rebuild All Collisions" };
		rebuildBtn.Pressed += OnRebuildCollisions;
		topRow.AddChild(rebuildBtn);

		var closeBtn = new Button { Text = "Close (Esc)" };
		closeBtn.Pressed += Toggle;
		topRow.AddChild(closeBtn);

		var scroll = new ScrollContainer
		{
			Name = "Scroll",
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
		};
		vbox.AddChild(scroll);

		_machineList = new VBoxContainer
		{
			Name = "MachineList",
		};
		_machineList.AddThemeConstantOverride("separation", 4);
		scroll.AddChild(_machineList);

		_statusLabel = new Label
		{
			Name = "StatusLabel",
			Text = "",
		};
		_statusLabel.AddThemeFontSizeOverride("font_size", 12);
		vbox.AddChild(_statusLabel);

		_root.Visible = false;
	}

	private void OnAddMachine()
	{
		if (_config == null)
		{
			return;
		}

		var existingTypes = new HashSet<MachineType>();
		foreach (var existingDef in _config.Machines ?? Array.Empty<MachineDefinition>())
		{
			if (existingDef != null)
			{
				existingTypes.Add(existingDef.Type);
			}
		}

		MachineType newType = MachineType.Smelter;
		foreach (MachineType t in Enum.GetValues(typeof(MachineType)))
		{
			if (!existingTypes.Contains(t))
			{
				newType = t;
				break;
			}
		}

		var def = _config.AddMachine(newType);
		RefreshList();
		SetStatus($"Added '{def.Name}' ({def.Type}). Edit its fields in the inspector or .tres.");
	}

	private void OnRemoveMachine(int index)
	{
		if (_config == null)
		{
			return;
		}
		var list = new List<MachineDefinition>(_config.Machines ?? Array.Empty<MachineDefinition>());
		if (index < 0 || index >= list.Count)
		{
			return;
		}
		var removed = list[index];
		list.RemoveAt(index);
		_config.Machines = list.ToArray();
		RefreshList();
		SetStatus($"Removed '{removed?.Name ?? "?"}'.");
	}

	private void OnRebuildCollisions()
	{
		var world = FindWorld();
		if (world == null)
		{
			SetStatus("No VoxelWorld found in the scene tree.");
			return;
		}
		world.RebuildAllMachineCollisions();
		SetStatus("Machine collisions rebuilt.");
	}

	private void RefreshList()
	{
		if (_machineList == null)
		{
			return;
		}

		foreach (var child in _machineList.GetChildren())
		{
			child.QueueFree();
		}

		var defs = _config?.Machines ?? Array.Empty<MachineDefinition>();
		if (defs.Length == 0)
		{
			var empty = new Label
			{
				Text = "No machine definitions registered.",
				Modulate = new Color(1f, 1f, 1f, 0.5f),
			};
			empty.AddThemeFontSizeOverride("font_size", 14);
			_machineList.AddChild(empty);
			return;
		}

		for (int i = 0; i < defs.Length; i++)
		{
			var machineDef = defs[i];
			if (machineDef == null)
			{
				continue;
			}
			var row = BuildMachineRow(machineDef, i);
			_machineList.AddChild(row);
		}
	}

	private Control BuildMachineRow(MachineDefinition def, int index)
	{
		var row = new PanelContainer
		{
			Name = $"Machine_{index}",
			CustomMinimumSize = new Vector2(0f, 44f),
		};

		var hbox = new HBoxContainer
		{
			Name = "Row",
		};
		hbox.AddThemeConstantOverride("separation", 12);
		row.AddChild(hbox);

		var nameLabel = new Label
		{
			Text = $"{def.Name} ({def.Type})",
			CustomMinimumSize = new Vector2(200f, 0f),
		};
		hbox.AddChild(nameLabel);

		var idLabel = new Label
		{
			Text = $"ID: {def.EffectiveItemId}",
			CustomMinimumSize = new Vector2(60f, 0f),
		};
		hbox.AddChild(idLabel);

		var hasMesh = def.Mesh != null;
		var hasScene = def.MeshScene != null;
		var meshLabel = new Label
		{
			Text = hasMesh ? "Mesh: ✓" : hasScene ? "Scene: ✓" : "No mesh",
			CustomMinimumSize = new Vector2(100f, 0f),
			Modulate = (hasMesh || hasScene) ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f),
		};
		hbox.AddChild(meshLabel);

		var scaleLabel = new Label
		{
			Text = $"Scale: {def.Scale.X:F2}",
			CustomMinimumSize = new Vector2(80f, 0f),
		};
		hbox.AddChild(scaleLabel);

		// Collision box status: shows whether an authored box exists on the
		// definition (green) or it's using the runtime auto-computed AABB (grey).
		var boxLabel = new Label
		{
			Text = def.HasCollisionBox
				? $"Box: {def.CollisionBoxSize.X:F2}x{def.CollisionBoxSize.Y:F2}x{def.CollisionBoxSize.Z:F2}"
				: "Box: auto",
			CustomMinimumSize = new Vector2(150f, 0f),
			Modulate = def.HasCollisionBox ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 1f, 1f, 0.5f),
		};
		hbox.AddChild(boxLabel);

		var powerLabel = new Label
		{
			Text = $"Power out: {def.PowerOutput:F1}",
			CustomMinimumSize = new Vector2(100f, 0f),
			Modulate = def.PowerOutput > 0 ? new Color(1f, 0.9f, 0.3f) : new Color(1f, 1f, 1f, 0.5f),
		};
		hbox.AddChild(powerLabel);

		var removeBtn = new Button
		{
			Text = "✕",
			CustomMinimumSize = new Vector2(32f, 32f),
			TooltipText = "Remove this machine definition",
		};
		int capturedIndex = index;
		removeBtn.Pressed += () => OnRemoveMachine(capturedIndex);
		hbox.AddChild(removeBtn);

		return row;
	}

	private void SetStatus(string message)
	{
		if (_statusLabel != null)
		{
			_statusLabel.Text = message;
		}
	}

	private VoxelWorld FindWorld()
	{
		var tree = GetTree();
		if (tree == null)
		{
			return null;
		}
		return tree.Root.FindChild("VoxelWorld", true, false) as VoxelWorld;
	}

	public override void _Input(InputEvent @event)
	{
		if (!_visible)
		{
			return;
		}
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.Escape)
			{
				Toggle();
			}
		}
	}
}
