using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;

namespace FactorySim.Voxel;

/// <summary>
/// Player voxel editing tool.
///
/// Controls:
/// - Key 1: select the ADD tool (fills a sphere of solid voxels)
/// - Key 2: select the REMOVE tool (carves a sphere of voxels)
/// - Key 3: select the PICKAXE tool (hold left mouse to damage the targeted
///   voxel over time; when its health reaches 0 the voxel breaks)
/// - Left mouse button: apply the selected tool at the highlighted voxel
///
/// The target voxel is found by raycasting through the voxel grid from the
/// camera up to <see cref="MaxRaycastDistance"/> world units.
///
/// A small on-screen debug label (bottom-left) shows the active tool, the
/// targeted voxel + material, and the target's health bar; it is re-shown
/// (briefly) whenever the tool changes, so it is always visible while testing
/// a tool switch.
/// </summary>
[GlobalClass]
public partial class VoxelTool : Node3D
{
	public enum Tool
	{
		Add = 1,
		Remove = 2,
		Pickaxe = 3,
	}

	/// <summary>Path to the camera to raycast from (relative to this node).</summary>
	[Export] public NodePath CameraPath { get; set; } = new("../Camera3D");

	/// <summary>Path to the <see cref="VoxelWorld"/> to edit (relative to this node).</summary>
	[Export] public NodePath VoxelWorldPath { get; set; } = new("../../../Enviornment/VoxelWorld");

	/// <summary>Radius of the add-tool sphere, in voxels.</summary>
	public int AddRadius { get; set; } = 2;

	/// <summary>Radius of the remove-tool sphere, in voxels.</summary>
	public int RemoveRadius { get; set; } = 2;

	/// <summary>Maximum raycast distance for tool targeting, in world units.</summary>
	public float MaxRaycastDistance { get; set; } = 10f;

	/// <summary>Highlight color when the add tool is active.</summary>
	[Export] public Color AddHighlightColor { get; set; } = new(0.3f, 1.0f, 0.45f);

	/// <summary>Highlight color when the remove tool is active.</summary>
	[Export] public Color RemoveHighlightColor { get; set; } = new(1.0f, 0.35f, 0.2f);

	/// <summary>Highlight color when the pickaxe tool is active.</summary>
	[Export] public Color PickaxeHighlightColor { get; set; } = new(0.55f, 0.8f, 1.0f);

	/// <summary>
	/// The pickaxe's tool power (damage multiplier). The damage dealt to a
	/// voxel is <c>(ToolPower / material BreakTime) * 100% per second</c>, so a
	/// power of 1.0 breaks a material in exactly its BreakTime seconds.
	/// </summary>
	[Export(PropertyHint.Range, "0.1,32,0.05")] public float ToolPower { get; set; } = 1.0f;

	/// <summary>How long (seconds) the tool-switch debug label stays pinned on screen.</summary>
	[Export(PropertyHint.Range, "0,30,0.1")] public float DebugShowTime { get; set; } = 3f;

	private Tool _currentTool = Tool.Add;
	private VoxelWorld _world;
	private Camera3D _camera;
	private Node3D _highlight;
	private StandardMaterial3D _highlightMaterial;
	private bool _hasTarget;
	private Vector3I _targetVoxel;

	// On-screen debug (tool + target + health)
	private CanvasLayer _debugLayer;
	private Label _debugLabel;
	private float _debugTimer;

	/// <summary>RNG for the small random offset applied to item drop spawns.</summary>
	private readonly RandomNumberGenerator _dropRng = new();

	/// <summary>
	/// Pickaxe damage accumulated per targeted voxel, in percentage points.
	/// Keyed by world voxel so the damage PERSISTS: it is stored immediately
	/// each frame, so if the player stops, moves to another voxel, or re-aims
	/// at the same voxel later, the damage carried so far is retained and the
	/// voxel breaks the moment its total reaches full health. This is the
	/// source of truth for breaking — the voxel's stored health is reset by
	/// edits (see VoxelWorld.SetVoxel), so it can't be read back to decide when
	/// to break.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<Vector3I, float> _pickaxeDamage = new();

	public override void _Ready()
	{
		// Prefer the explicit path; fall back to the group so the tool keeps
		// working if the scene hierarchy is restructured.
		_world = GetNodeOrNull<VoxelWorld>(VoxelWorldPath)
			?? (GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld);
		if (_world is null)
		{
			GD.PushError($"[VoxelTool] VoxelWorld not found at '{VoxelWorldPath}' (group 'voxel_world' empty either). Tool is disabled.");
			return;
		}

		// A stale `= null` override loads as an empty path; degrade gracefully here —
		// _Process already guards for a missing camera.
		_camera = GetNodeOrNull<Camera3D>(CameraPath);
		if (_camera == null)
		{
			GD.PushWarning($"[VoxelTool] No camera at '{CameraPath}' — tool targeting disabled.");
		}

		// Target highlight: a glowing wireframe shaped like the voxel's own
		// mesh (the edges of the solid's exposed faces), scaled 105% around the
		// voxel center. Rendered as unshaded emissive lines so the glow reads
		// against any background; the mesh is rebuilt per targeted voxel.
		_highlightMaterial = new StandardMaterial3D
		{
			AlbedoColor = AddHighlightColor,
			EmissionEnabled = true,
			Emission = AddHighlightColor,
			EmissionEnergyMultiplier = 2.5f,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};

		// The highlight is a pivot Node3D (positioned at the targeted voxel's
		// corner) with a child MeshInstance3D that holds the voxel-shaped
		// wireframe in local 0..1 space and is scaled 105% by the voxel size.
		_highlight = new Node3D { Name = "Highlight", Visible = false };
		AddChild(_highlight);

		_highlightMeshInstance = new MeshInstance3D
		{
			Name = "HighlightMesh",
			Mesh = new Mesh(), // placeholder; replaced on the first target
			MaterialOverride = _highlightMaterial,
			Visible = false,
		};
		_highlight.AddChild(_highlightMeshInstance);

		// On-screen debug: a CanvasLayer + Label that follows the camera (so it
		// renders over the 3D world) and is re-shown on every tool switch.
		_debugLayer = new CanvasLayer { Name = "ToolDebugLayer", Layer = 10 };
		AddChild(_debugLayer);
		_debugLabel = new Label
		{
			Name = "ToolDebug",
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Top,
			Visible = false,
		};
		_debugLabel.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f, 0.95f));
		_debugLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
		_debugLabel.AddThemeConstantOverride("outline_size", 6);
		_debugLayer.AddChild(_debugLabel);

		// Broken voxels become item drops (pickaxe breaks and remove-tool
		// spheres both raise VoxelBroken).
		_world.VoxelBroken += OnVoxelBroken;

		_dropRng.Randomize();

		SetTool(Tool.Add, announce: false);
		GD.Print("[VoxelTool] Keys 1/2/3 = add/remove/pickaxe, Hold Left Mouse (pickaxe) = damage, F = regenerate terrain.");
	}

	public override void _ExitTree()
	{
		// Detach the event so a freed tool doesn't keep the world's delegate
		// list (and this tool instance) alive.
		if (_world != null)
		{
			_world.VoxelBroken -= OnVoxelBroken;
		}
	}

	/// <summary>
	/// Spawns a one-item drop at the center of a broken voxel. A small random
	/// horizontal offset keeps simultaneous drops (e.g. a whole sphere) from
	/// spawning on top of each other; the drops' own merge pass tidies up the
	/// rest within a second.
	/// </summary>
	private void OnVoxelBroken(Vector3I pos, int materialId)
	{
		if (materialId == VoxelMaterialIds.Air)
		{
			return;
		}

		Vector3 worldPos = new Vector3(pos.X, pos.Y, pos.Z) * _world.VoxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
		worldPos += new Vector3(_dropRng.RandfRange(-0.2f, 0.2f), 0f, _dropRng.RandfRange(-0.2f, 0.2f));
		DropSpawner.SpawnDrop(GetTree().Root, worldPos, materialId, 1);
	}

	public override void _Process(double delta)
	{
		if (_camera == null || _world == null)
		{
			return;
		}

		float dt = (float)delta;

		Vector3 from = _camera.GlobalPosition;
		Vector3 dir = -_camera.GlobalTransform.Basis.Z; // cameras look down -Z

		if (_world.RaycastVoxels(from, dir, MaxRaycastDistance, out Vector3I hitVoxel, out Vector3 hitNormal))
		{
			_hasTarget = true;
			_targetVoxel = hitVoxel;

			// The highlight is a Node3D whose pivot sits at the targeted voxel's
			// CENTER; its child mesh holds the voxel's own edge geometry in local
			// space (0..voxelSize per axis, after the world->local translation in
			// EnsureHighlightMesh) and is scaled 105% by the voxel size, so the
			// wireframe reads as the voxel's shape slightly larger than the voxel,
			// expanding around the voxel center.
			float vs = _world.VoxelSize;
			_highlight.GlobalPosition = new Vector3(hitVoxel.X, hitVoxel.Y, hitVoxel.Z) * vs
				+ new Vector3(0.5f, 0.5f, 0.5f) * vs;
			_highlightMeshInstance.Scale = new Vector3(vs * 1.05f, vs * 1.05f, vs * 1.05f);

			// Re-shape the highlight to the target's own voxel geometry (cached
			// per voxel so the mesh is only rebuilt when the target changes).
			EnsureHighlightMesh(hitVoxel);
		}
		else
		{
			_hasTarget = false;
			// No target: stop damaging. Per-voxel damage stays in _pickaxeDamage
			// so re-aiming at a partially-mined voxel resumes where it left off.
			_highlight.Visible = false;
			_highlightMeshInstance.Visible = false;
		}

		// Pickaxe: hold left mouse (captured) to damage the target over time.
		if (_currentTool == Tool.Pickaxe
			&& _hasTarget
			&& Input.MouseMode == Input.MouseModeEnum.Captured
			&& Input.IsMouseButtonPressed(MouseButton.Left))
		{
			ApplyPickaxeDamage(dt);
		}

		UpdateDebugHud(dt);
	}

	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.Key1)
			{
				SetTool(Tool.Add);
			}
			else if (key.Keycode == Key.Key2)
			{
				SetTool(Tool.Remove);
			}
			else if (key.Keycode == Key.Key3)
			{
				SetTool(Tool.Pickaxe);
			}
		}
		else if (@event is InputEventMouseButton mouse && mouse.Pressed && mouse.ButtonIndex == MouseButton.Left)
		{
			// Only act while the mouse is captured (i.e. not clicking the UI).
			if (Input.MouseMode == Input.MouseModeEnum.Captured && _hasTarget)
			{
				ApplyTool();
			}
		}
	}

	/// <summary>
	/// Damages the current target over time: damage rate =
	/// <c>(ToolPower / material BreakTime) * 100% per second</c>. The rate is
	/// looked up each frame from the target's material (air / missing material
	/// falls back to BreakTime 1.0).
	///
	/// Damage is accumulated PER TARGETED VOXEL in <see cref="_pickaxeDamage"/>
	/// and stored immediately each frame, so it persists across frames and
	/// across re-aiming: the moment the targeted voxel's total damage reaches
	/// its full health (100%) the voxel breaks (is removed) and its entry is
	/// cleared. This is the source of truth for breaking — the voxel's stored
	/// health is reset to full by <see cref="VoxelWorld.SetVoxel"/> on every
	/// edit, so it can't be read back to decide when to break.
	/// </summary>
	private void ApplyPickaxeDamage(float dt)
	{
		int material = _world.GetVoxel(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z);
		if (material == VoxelMaterialIds.Air)
		{
			// Target turned to air (broke elsewhere / edited): drop its damage.
			_pickaxeDamage.Remove(_targetVoxel);
			return;
		}

		// Unbreakable materials (bedrock) take no damage and never break.
		if (_world.IsUnbreakable(material))
		{
			_pickaxeDamage.Remove(_targetVoxel);
			return;
		}

		// Refresh the target's damage from storage each frame (a fresh value
		// when the player re-aimed at this voxel, its carried value otherwise).
		float damage = _pickaxeDamage.TryGetValue(_targetVoxel, out float stored) ? stored : 0f;

		float breakTime = 1.0f;
		if (_world.MaterialConfig != null && _world.MaterialConfig.GetDef(material) is { } def && def.Entry != null)
		{
			breakTime = Mathf.Max(0.05f, def.Entry.BreakTime);
		}

		damage += ToolPower / breakTime * VoxelHealth.MaxHealth * dt;

		if (damage >= VoxelHealth.MaxHealth)
		{
			// The voxel broke: remove it (resets health + rebuilds the chunk).
			GD.Print($"[VoxelTool] Broke voxel {(_world.GetVoxelName(material))} @ {_targetVoxel}");
			_world.BreakVoxel(_targetVoxel);
			_pickaxeDamage.Remove(_targetVoxel);
			return;
		}

		// Store immediately so the damage is persistent if the player stops or
		// moves to another voxel this frame.
		_pickaxeDamage[_targetVoxel] = damage;
	}

	private void UpdateDebugHud(float dt)
	{
		if (_debugLabel == null)
		{
			return;
		}

		// The label is pinned (always visible) for DebugShowTime seconds after a
		// tool switch; afterwards it is only visible while the mouse is captured
		// (so it never shows over the UI menu).
		_debugTimer -= dt;
		bool visible = (_debugTimer > 0f) || (Input.MouseMode == Input.MouseModeEnum.Captured);
		_debugLabel.Visible = visible;
		if (!visible)
		{
			return;
		}

		string toolName = _currentTool switch
		{
			Tool.Add => "ADD (key 1)",
			Tool.Remove => "REMOVE (key 2)",
			_ => "PICKAXE (key 3)",
		};

		if (!_hasTarget)
		{
			_debugLabel.Text = $"Tool: {toolName}\n(no target)";
			return;
		}

		int material = _world.GetVoxel(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z);
		string matName = _world.GetVoxelName(material);
		// Show the target's current health: full minus the damage this voxel
		// has taken from the pickaxe (see ApplyPickaxeDamage). The stored value
		// is reset by edits, so the live remaining health is derived from the
		// per-voxel damage instead.
		float targetDamage = _pickaxeDamage.TryGetValue(_targetVoxel, out float dmg) ? dmg : 0f;
		float health = Mathf.Clamp(VoxelHealth.MaxHealth - targetDamage, 0f, VoxelHealth.MaxHealth);

		// A simple 10-segment health bar.
		int filled = Mathf.Clamp(Mathf.CeilToInt(health / 10f), 0, 10);
		var bar = new System.Text.StringBuilder(14);
		bar.Append("[");
		for (int i = 0; i < 10; i++)
		{
			bar.Append(i < filled ? '#' : '.');
		}
		bar.Append(']');

		_debugLabel.Text =
			$"Tool: {toolName}\n" +
			$"Target: {matName} @ {_targetVoxel.X},{_targetVoxel.Y},{_targetVoxel.Z}\n" +
			$"Health: {health:0.#}%  {bar.ToString()}";
	}

	private void SetTool(Tool tool, bool announce = true)
	{
		_currentTool = tool;
		Color color = tool switch
		{
			Tool.Add => AddHighlightColor,
			Tool.Remove => RemoveHighlightColor,
			_ => PickaxeHighlightColor,
		};
		// Recolor the highlight wireframe (both albedo and emission) so the
		// glow matches the active tool.
		if (_highlightMaterial != null)
		{
			_highlightMaterial.AlbedoColor = color;
			_highlightMaterial.Emission = color;
		}
		// Switching tools drops any in-progress pickaxe mining; the per-voxel
		// damage table is kept so a voxel mined with the pickaxe can be
		// re-mined later with its carried damage (switch to add, back to
		// pickaxe, same target = resumes).

		// Pin the on-screen debug label so the switch is visible.
		_debugTimer = DebugShowTime;
		if (_debugLabel != null)
		{
			_debugLabel.Visible = true;
		}

		if (announce)
		{
			GD.Print($"[VoxelTool] Tool: {toolNameOf(tool)}");
		}
	}

	private static string toolNameOf(Tool tool) => tool switch
	{
		Tool.Add => "ADD (key 1)",
		Tool.Remove => "REMOVE (key 2)",
		_ => "PICKAXE (key 3)",
	};

	private void ApplyTool()
	{
		if (_currentTool == Tool.Add)
		{
			_world.AddSphere(_targetVoxel, AddRadius, VoxelMaterialIds.Placed);
			GD.Print($"[VoxelTool] Added sphere r={AddRadius} @ {_targetVoxel}");
		}
		else if (_currentTool == Tool.Remove)
		{
			_world.RemoveSphere(_targetVoxel, RemoveRadius);
			GD.Print($"[VoxelTool] Removed sphere r={RemoveRadius} @ {_targetVoxel}");
		}
		// Pickaxe: no one-shot action — holding left mouse damages over time
		// (see ApplyPickaxeDamage). A plain click does nothing.
	}

	/// <summary>
	/// Builds (or reuses) the highlight mesh for the targeted voxel: the edges
	/// of the voxel's own exposed-face surface, as a glowing wireframe. The mesh
	/// is in local space (0..1 per axis, the unit voxel) and is cached per
	/// targeted voxel so it is rebuilt only when the target changes.
	///
	/// The voxel's real material is meshed with the world's solid-query, so the
	/// highlight is the voxel's TRUE exposed shape (a full box for an isolated
	/// voxel, fewer faces where a neighbour is solid). The shared palette supplies
	/// a valid material slot; <see cref="_highlightMaterial"/> (set via
	/// MaterialOverride) is what actually renders the glow.
	/// </summary>
	private void EnsureHighlightMesh(Vector3I voxel)
	{
		if (_meshedTarget == voxel && _highlightWireframe != null)
		{
			return;
		}

		int material = _world.GetVoxel(voxel.X, voxel.Y, voxel.Z);
		if (material <= 0)
		{
			// Target turned to air: show nothing (the next frame hides it too).
			_highlightMeshInstance.Visible = false;
			return;
		}

		var data = new ChunkData(1, 1, 1);
		data.Set(0, 0, 0, (byte)material);

		// The chunk's local (0,0,0) maps to the targeted world voxel, so the
		// solid query is expressed in that local frame: the target cell is solid,
		// every neighbour is queried against the real world (so faces against a
		// solid neighbour are culled, exactly like the real chunk mesh).
		System.Func<int, int, int, bool> solidQuery = (lx, y, lz) =>
		{
			if (lx == 0 && lz == 0)
			{
				// Inside our 1x1x1 chunk: solid at the target, air elsewhere.
				return y >= 0 && y < 1;
			}
			// Neighbour: ask the real world using the SAME solid query the chunk
			// meshes use, so the highlight matches the visible surface (and
			// chunk-border neighbours are handled identically).
			return _world.VoxelSolidForMesh(voxel.X + lx, voxel.Y + y, voxel.Z + lz);
		};

		// Mesh with the world's real palette so the material slot is valid.
		// BuildVisual emits vertices in WORLD-VOXEL coordinates (origin = the
		// target voxel * VoxelSize), so record the pivot's world position here
		// and translate the resulting edges into the pivot's local frame
		// (-0.5..0.5 * voxelSize per axis, centered on the voxel) in BuildEdgeMesh.
		float vs = _world.VoxelSize;
		_highlightPivotWorld = new Vector3(voxel.X, voxel.Y, voxel.Z) * vs
			+ new Vector3(0.5f, 0.5f, 0.5f) * vs;
		var faces = MeshBuilder.BuildVisual(data, voxel.X, voxel.Y, 1f, _world.Palette, solidQuery);
		_highlightWireframe = BuildEdgeMesh(faces, _highlightPivotWorld);
		_meshedTarget = voxel;

		_highlightMeshInstance.Mesh = _highlightWireframe;
		_highlightMeshInstance.MaterialOverride = _highlightMaterial;
		_highlightMeshInstance.Visible = true;
		_highlight.Visible = true;
	}

	/// <summary>The voxel of the last highlight mesh built (rebuild on change only).</summary>
	private Vector3I _meshedTarget = new(int.MinValue, int.MinValue, int.MinValue);

	/// <summary>
	/// The absolute world position of the <see cref="_highlight"/> pivot at the
	/// last mesh build. <c>MeshBuilder.BuildVisual</c> emits vertices in
	/// world-voxel coordinates (origin = voxel * VoxelSize), so the built
	/// geometry must be translated into the pivot's local frame by subtracting
	/// this; it is also the point the 105% scale expands around.
	/// </summary>
	private Vector3 _highlightPivotWorld;

	/// <summary>The voxel-shaped wireframe mesh currently assigned to the highlight.</summary>
	private Mesh _highlightWireframe;

	/// <summary>
	/// The MeshInstance3D that holds the voxel-shaped wireframe mesh. Kept as a
	/// separate field (distinct from the <see cref="_highlight"/> pivot node) so
	/// the mesh can be scaled 105% independently of the pivot.
	/// </summary>
	private MeshInstance3D _highlightMeshInstance;

	/// <summary>
	/// Extracts the unique edges of the triangle soup in <paramref name="mesh"/>
	/// (an ArrayMesh built by <see cref="MeshBuilder.BuildVisual"/>) into a
	/// line-primitive mesh. Each edge is emitted once (deduplicated by an
	/// unordered quantized vertex-key set) and drawn as line primitives, giving
	/// a clean wireframe of the voxel's exposed surface.
	///
	/// <paramref name="worldOffset"/> is the pivot's absolute world position:
	/// BuildVisual emits vertices in world-voxel coordinates, so each vertex is
	/// translated by <c>-worldOffset</c> into the pivot's local frame (0..1 per
	/// axis for a unit voxel). The caller then scales the MeshInstance3D by
	/// voxelSize * 1.05 around the pivot to expand the wireframe.
	///
	/// Vertices are read through a <see cref="MeshDataTool"/> per surface (the
	/// supported API in Godot 4.8 C# — <see cref="ArrayMesh"/> exposes no direct
	/// array access), mirroring the pattern used in the rewrite test suite.
	/// </summary>
	private static Mesh BuildEdgeMesh(ArrayMesh mesh, Vector3 worldOffset)
	{
		var lines = new List<Vector3>();
		var seen = new HashSet<long>();
		var data = new MeshDataTool();

		for (int s = 0; ; s++)
		{
			if (data.CreateFromSurface(mesh, s) != Error.Ok)
			{
				break; // no more surfaces
			}

			int count = data.GetVertexCount();
			for (int i = 0; i + 2 < count; i += 3)
			{
				AddEdge(lines, seen, data.GetVertex(i) - worldOffset, data.GetVertex(i + 1) - worldOffset);
				AddEdge(lines, seen, data.GetVertex(i + 1) - worldOffset, data.GetVertex(i + 2) - worldOffset);
				AddEdge(lines, seen, data.GetVertex(i + 2) - worldOffset, data.GetVertex(i) - worldOffset);
			}
		}

		if (lines.Count == 0)
		{
			// No geometry (e.g. a fully-culled voxel): return an empty mesh so
			// callers can still assign it without a null check.
			return new Mesh();
		}

		var tool = new SurfaceTool();
		tool.Begin(Mesh.PrimitiveType.Lines);
		foreach (Vector3 v in lines)
		{
			tool.AddVertex(v);
		}
		return tool.Commit();
	}

	private static void AddEdge(List<Vector3> lines, HashSet<long> seen, Vector3 a, Vector3 b)
	{
		long ka = QuantizeKey(a);
		long kb = QuantizeKey(b);
		long low = Math.Min(ka, kb);
		long high = Math.Max(ka, kb);
		// Combine two 20-bit quantized vertices into a 64-bit unordered pair key.
		long pairKey = (low << 20) | high;
		if (seen.Add(pairKey))
		{
			lines.Add(a);
			lines.Add(b);
		}
	}

	private static long QuantizeKey(Vector3 v)
	{
		// Quantize to 1/1000 so floating point differences don't split edges.
		// Local highlight coordinates live in 0..1, so 20 bits per axis is far
		// more than enough room.
		uint x = (uint)(int)Math.Floor(v.X * 1000f) & 0xFFFFF;
		uint y = (uint)(int)Math.Floor(v.Y * 1000f) & 0xFFFFF;
		uint z = (uint)(int)Math.Floor(v.Z * 1000f) & 0xFFFFF;
		return x | ((long)y << 20) | ((long)z << 40);
	}
}
