using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;
using FactorySim.Machines;
using FactorySim.UI;

namespace FactorySim.Voxel;

/// <summary>
/// Player voxel tool. Replaces the old temporary add/remove/pickaxe tools with
/// a real item-driven system:
///
/// - The player's "held" item is whatever is in the SELECTED HOTBAR SLOT (the
///   <see cref="InventoryUI.SelectedHotbarItem"/>). It is either:
///     * a TOOL (shovel / pickaxe / axe / sword — see <see cref="ToolType"/>),
///       or
///     * a PLACEABLE MATERIAL (any solid voxel you can hold).
///
/// The held item decides what left mouse does:
///
/// - Holding a TOOL: HOLD left mouse (captured) to damage the targeted voxel
///   over time. The break rate is <c>(ToolPower × affinity / BreakTime) × 100%
///   per second</c>, where the affinity comes from <see cref="ToolAffinity"/>
///   based on the tool's type and the target's material category. This is the
///   classic "right tool for the job": a shovel is fast on sand but slow on
///   stone, a pickaxe the reverse.
///
/// - Holding a PLACEABLE MATERIAL: CLICK left mouse (a single fresh press,
///   while captured) to PLACE one voxel of it into the empty cell adjacent to
///   the targeted face, consuming one item from the selected hotbar slot. This
///   is the default place-ability. It fires only on a fresh press, so it never
///   conflicts with mining (a material can't mine in the first place).
///
/// In short: tools break, materials place — no bare-hand mining, no
/// place-while-mining ambiguity.
///
/// The target voxel is found by raycasting through the voxel grid from the
/// camera up to <see cref="MaxRaycastDistance"/> world units. The targeted voxel
/// is highlighted with a translucent fill + wireframe (the machinery from the
/// old tool, kept).
/// </summary>
[GlobalClass]
public partial class VoxelTool : Node3D
{
	/// <summary>Path to the camera to raycast from (relative to this node).</summary>
	[Export] public NodePath CameraPath { get; set; } = new("../Camera3D");

	/// <summary>Path to the <see cref="VoxelWorld"/> to edit (relative to this node).</summary>
	[Export] public NodePath VoxelWorldPath { get; set; } = new("../../../Enviornment/VoxelWorld");

	/// <summary>Maximum raycast distance for tool targeting, in world units.</summary>
	public float MaxRaycastDistance { get; set; } = 10f;

	/// <summary>
	/// Base tool power (damage multiplier). The damage dealt to a voxel is
	/// <c>(ToolPower × affinity / material BreakTime) × 100% per second</c>, so
	/// a power of 1.0 with a 1.0 affinity breaks a material in exactly its
	/// BreakTime seconds. The affinity (from the held tool type) scales this.
	/// </summary>
	[Export(PropertyHint.Range, "0.1,32,0.05")] public float ToolPower { get; set; } = 1.0f;

	/// <summary>Highlight color for the targeted voxel.</summary>
	[Export] public Color HighlightColor { get; set; } = new(0.6f, 0.9f, 1.0f);

	/// <summary>
	/// Color of the power-radius indicator sphere shown when the player aims at
	/// a power source (a transparent yellow by default).
	/// </summary>
	[Export] public Color PowerRadiusColor { get; set; } = new(1.0f, 0.9f, 0.2f);

	/// <summary>Opacity (0..1) of the power-radius indicator sphere fill.</summary>
	[Export(PropertyHint.Range, "0.01,1,0.01")] public float PowerRadiusOpacity { get; set; } = 0.15f;

	private VoxelWorld _world;
	private Camera3D _camera;
	private InventoryUI _ui;
	private Inventory _inventory;
	private TargetInfoUI _targetInfo;

	private Node3D _highlight;
	private StandardMaterial3D _highlightMaterial;
	private StandardMaterial3D _highlightFillMaterial;
	private MeshInstance3D _highlightFill;
	private BoxMesh _highlightFillMesh;

	// Power-radius indicator: a transparent yellow sphere centered on a
	// targeted power source, sized to the live power radius. Shown only while
	// the player aims at a power-source machine (see _Process).
	private Node3D _powerIndicator;
	private MeshInstance3D _powerSphere;
	private SphereMesh _powerSphereMesh;
	private StandardMaterial3D _powerSphereMaterial;

	private bool _hasTarget;
	private Vector3I _targetVoxel;
	private Vector3 _targetNormal;

	// -- Collider-based targeting (machine colliders interact with the tool) --
	//
	// In ADDITION to the voxel-grid raycast (RaycastVoxels), the tool now
	// raycasts the camera ray against every machine's ACTUAL collision shape
	// (PhysicsDirectSpaceState3D.IntersectRay). The machine whose collider is
	// the NEAREST hit along the ray wins, and when it does the target becomes
	// that machine: the highlight is a box over its collider AABB, mining
	// damages it (TakeDamage), and placing is blocked against it. This makes
	// the machine's real collider geometry the thing the interaction system
	// reacts to — not just the voxel grid behind it. _colliderHitDistance is
	// the distance along the ray to the winning collider hit (0 when none).
	private bool _colliderHit;
	private Machine _colliderMachine;
	private Vector3 _colliderHitPoint;
	private Vector3 _colliderHitNormal;
	private float _colliderHitDistance;

	/// <summary>
	/// The machine the player is currently aiming at (closest machine whose
	/// voxel is within <see cref="MaxRaycastDistance"/>), or null. Machines are
	/// targeted IN ADDITION to voxels: if both a voxel and a machine are in the
	/// line of sight, the NEARER one wins. Right-clicking opens that machine's
	/// UI (see <see cref="MachineUI"/>); the target-info HUD shows its name.
	/// </summary>
	private Machine _targetMachine;

	// -- Machine placement state (preview ghost + R-key rotation) -----------
	//
	// The transparent preview ghost: a Machine node (IsPreview = true) shown at
	// the target cell while the player holds a machine item. It is (re)built on
	// demand in _Process and torn down when the held item is no longer a
	// placeable machine. _previewCell / _previewRotation / _previewItemId track
	// the current ghost so it is only rebuilt when something actually changes.
	private FactorySim.Machines.Machine _preview;
	private Vector3I _previewCell;
	private int _previewItemId;
	private FactorySim.Machines.MachineRotation _previewRotation;

	/// <summary>
	/// The player's CURRENT machine placement orientation (the R-key state).
	/// This is PERSISTENT: it is not reset when the player re-selects a machine
	/// or opens the inventory — it is the orientation the player last chose, and
	/// it is applied to the next machine they place. It is shared across all
	/// machine types (the player rotates the "placement cursor", not a specific
	/// machine).
	/// </summary>
	private FactorySim.Machines.MachineRotation _currentRotation = FactorySim.Machines.MachineRotation.Identity;

	/// <summary>RNG for the small random offset applied to item drop spawns.</summary>
	private readonly RandomNumberGenerator _dropRng = new();

	/// <summary>
	/// Mining damage accumulated per targeted voxel, in percentage points.
	/// Keyed by world voxel so the damage PERSISTS: it is stored immediately
	/// each frame, so if the player stops, moves to another voxel, or re-aims
	/// at the same voxel later, the damage carried so far is retained and the
	/// voxel breaks the moment its total reaches full health. This is the
	/// source of truth for breaking — the voxel's stored health is reset by
	/// edits (see VoxelWorld.SetVoxel), so it can't be read back to decide when
	/// to break.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<Vector3I, float> _mineDamage = new();

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

		// The inventory UI owns the hotbar selection and the inventory holds the
		// slots. Both are resolved LAZILY (see ResolveServices) because the
		// InventoryUI joins its group in its own _Ready, which may run AFTER this
		// tool's _Ready (the Player precedes the Inventory in the scene tree), so
		// a lookup here would be too early.
		ResolveServices();

		// Target highlight: a translucent FILLED box (the classic, instantly
		// readable Minecraft-style highlight) over the targeted voxel, with a
		// glowing wireframe of the voxel's own exposed-face edges on top. The
		// fill makes the highlighted block unmistakable; the wireframe shows the
		// voxel's true exposed shape. Both are unshaded emissive so they read
		// against any background.
		_highlightMaterial = new StandardMaterial3D
		{
			AlbedoColor = HighlightColor,
			EmissionEnabled = true,
			Emission = HighlightColor,
			EmissionEnergyMultiplier = 2.5f,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		// Translucent fill: semi-transparent so the voxel beneath still shows
		// through, but bright enough to clearly mark the targeted block.
		_highlightFillMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(HighlightColor, 0.35f),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			EmissionEnabled = true,
			Emission = HighlightColor,
			EmissionEnergyMultiplier = 1.2f,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled, // double-sided so it reads from inside too
		};

		// The highlight is a pivot Node3D (positioned at the targeted voxel's
		// center) with a child MeshInstance3D that holds the voxel-shaped
		// wireframe in local 0..1 space and is scaled 105% by the voxel size.
		// Its orientation is locked to the WORLD (set in _Process via
		// GlobalTransform with an identity basis) because this tool is a child
		// of the player's CharacterBody3D, which yaws with the mouse — without
		// the lock the highlight would spin around the voxel as the camera turns.
		_highlight = new Node3D { Name = "Highlight", Visible = false };
		AddChild(_highlight);

		// No placeholder mesh: a bare `new Mesh()` is an abstract base whose
		// virtuals (_get_surface_count, _get_blend_shape_count) are not
		// overridden, so assigning it to a MeshInstance3D throws
		// "Required virtual method must be overridden". The instance starts
		// with no mesh and stays invisible until the first target is built.
		_highlightMeshInstance = new MeshInstance3D
		{
			Name = "HighlightMesh",
			MaterialOverride = _highlightMaterial,
			Visible = false,
		};
		_highlight.AddChild(_highlightMeshInstance);

		// Translucent filled box: a full voxel-size BoxMesh that fills the
		// targeted block. The BoxMesh is centered on its own origin (local
		// -0.5..0.5 per axis for a size-1 box), so with the pivot at the voxel
		// center it fills the voxel exactly; it is scaled 105% with the rest of
		// the highlight so it reads as the block slightly enlarged.
		_highlightFillMesh = new BoxMesh { Size = new Vector3(1f, 1f, 1f) };
		_highlightFill = new MeshInstance3D
		{
			Name = "HighlightFill",
			Mesh = _highlightFillMesh,
			MaterialOverride = _highlightFillMaterial,
			Visible = false,
		};
		_highlight.AddChild(_highlightFill);

		// POWER-RADIUS INDICATOR: a transparent yellow sphere, centered on a
		// targeted power source, with a radius equal to the live power radius
		// (in world units). It is a pivot Node3D (positioned at the source's
		// voxel center) holding a child MeshInstance3D with a unit SphereMesh
		// that is scaled by the radius each frame. The material is unshaded and
		// translucent (alpha) with culling disabled so it reads as a soft
		// volume even when the camera is inside it. Like the highlight, its
		// orientation is locked to the world in _Process (identity basis) so it
		// does not spin with the player's yaw.
		_powerSphereMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(PowerRadiusColor, PowerRadiusOpacity),
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled, // double-sided so it reads from inside
		};
		_powerSphereMesh = new SphereMesh { Radius = 0.5f, Height = 1f };
		// A unit sphere (radius 0.5, height 1) centered on its origin: scaling
		// the MeshInstance by (2 * radius) makes the world radius exactly
		// `radius` (diameter = 2 * radius * 0.5 * scale... see _Process).
		_powerSphere = new MeshInstance3D
		{
			Name = "PowerSphere",
			Mesh = _powerSphereMesh,
			MaterialOverride = _powerSphereMaterial,
			Visible = false,
		};
		_powerIndicator = new Node3D { Name = "PowerRadiusIndicator", Visible = false };
		_powerIndicator.AddChild(_powerSphere);
		AddChild(_powerIndicator);

		// Broken voxels become item drops (mining breaks raise VoxelBroken).
		_world.VoxelBroken += OnVoxelBroken;

		_dropRng.Randomize();
	}

	public override void _ExitTree()
	{
		// Detach the event so a freed tool doesn't keep the world's delegate
		// list (and this tool instance) alive.
		if (_world != null)
		{
			_world.VoxelBroken -= OnVoxelBroken;
		}

		// Free any preview ghost so it doesn't linger in the tree.
		DestroyPreview();
	}

	/// <summary>
	/// Handles a <c>VoxelBroken</c> event. The event is raised TWICE per broken
	/// voxel: first by the player edit itself (the mining break), which QUEUES
	/// the drop with the world (see <see cref="_pendingEditStamp"/>); then by the
	/// world's <c>FlushPendingDrops</c> once the chunk update for the edited
	/// chunk has finished, which SPAWNS the drop. This two-pass design keeps a
	/// drop from visibly resting in mid-air: the surface is not re-meshed (and
	/// its collision not rebuilt) until the chunk update finishes, so the drop
	/// can only land on solid, visible terrain once that has completed.
	/// </summary>
	private void OnVoxelBroken(Vector3I pos, int materialId)
	{
		if (materialId == VoxelMaterialIds.Air)
		{
			return;
		}

		if (_pendingEditStamp > 0)
		{
			// First pass (raised by the edit): queue the drop, recording the
			// edit stamp captured when the edit was applied. The world releases
			// the drop once the chunk's EditPriority reaches at least this stamp
			// (i.e. the chunk update this edit started has been rebuilt).
			_world.PendingDrops.Add(new VoxelWorld.PendingDrop(
				pos,
				materialId,
				_pendingEditStamp,
				_world.VoxelChunkCoord(pos),
				Time.GetTicksMsec() / 1000.0));
			return;
		}

		// Second pass (raised by the world after the chunk update finished):
		// actually spawn the drop.
		SpawnDropForBrokenVoxel(pos, materialId);
	}

	/// <summary>
	/// The <c>VoxelWorld.EditPriorityCounter</c> value captured at the start of
	/// the edit that broke voxels. Non-zero while that edit's <c>VoxelBroken</c>
	/// events are being processed (so this handler queues drops); reset to 0
	/// immediately after the edit returns. A queued drop carries this stamp, and
	/// the world only releases it once the edited chunk's <c>EditPriority</c>
	/// reaches at least this value — a self-contained proof the chunk update has
	/// finished.
	/// </summary>
	private int _pendingEditStamp;

	/// <summary>
	/// Captures the world's edit stamp before an edit and clears it after, so
	/// the <c>VoxelBroken</c> events raised DURING the edit know which chunk
	/// update to wait for.
	/// </summary>
	private void CaptureEditStamp()
	{
		_pendingEditStamp = _world.EditPriorityCounter;
	}

	/// <summary>
	/// Actually spawns the drop for a broken voxel: a small random horizontal
	/// offset keeps simultaneous drops from spawning on top of each other; the
	/// drops' own merge pass tidies up the rest within a second.
	/// </summary>
	private void SpawnDropForBrokenVoxel(Vector3I pos, int materialId)
	{
		Vector3 worldPos = new Vector3(pos.X, pos.Y, pos.Z) * _world.VoxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
		worldPos += new Vector3(_dropRng.RandfRange(-0.2f, 0.2f), 0f, _dropRng.RandfRange(-0.2f, 0.2f));
		DropSpawner.SpawnDrop(GetTree().Root, worldPos, materialId, 1);
	}

	/// <summary>
	/// Resolves the inventory UI and inventory nodes via their groups. Called
	/// from <c>_Ready</c> and again lazily from <see cref="HeldItem"/> / the
	/// place path when the cached references are still null (the UI's own
	/// <c>_Ready</c> may run after this tool's, so the first lookup can be too
	/// early). Once resolved, the references are stable for the rest of the
	/// session.
	/// </summary>
	private void ResolveServices()
	{
		if (GetTree() == null)
		{
			return;
		}
		if (_ui == null)
		{
			_ui = GetTree().GetFirstNodeInGroup("inventory_ui") as InventoryUI;
		}
		if (_inventory == null)
		{
			_inventory = GetTree().GetFirstNodeInGroup("inventory") as Inventory;
		}
		if (_targetInfo == null)
		{
			_targetInfo = GetTree().GetFirstNodeInGroup("target_info_ui") as TargetInfoUI;
		}
	}

	/// <summary>
	/// The item currently in the player's selected hotbar slot (empty when the
	/// UI isn't wired or the slot is empty). Resolves the UI lazily and reads
	/// fresh each frame so hotbar switches and dev-panel grants take effect
	/// immediately.
	/// </summary>
	private ItemStack HeldItem
	{
		get
		{
			ResolveServices();
			if (_ui != null)
			{
				return _ui.SelectedHotbarItem;
			}
			if (_inventory != null)
			{
				return _inventory.Slots[0];
			}
			return ItemStack.From(0);
		}
	}

	/// <summary>The tool kind the player is holding, or null when the held item is a material (or empty).</summary>
	private ToolType? HeldTool => HeldItem.IsTool ? HeldItem.ToolType : null;

	public override void _Process(double delta)
	{
		if (_camera == null || _world == null)
		{
			return;
		}

		float dt = (float)delta;

		Vector3 from = _camera.GlobalPosition;
		Vector3 dir = -_camera.GlobalTransform.Basis.Z; // cameras look down -Z

		// COLLIDER TARGETING: raycast the camera ray against every machine's
		// ACTUAL collision shape. The nearest collider hit along the ray wins
		// (see RaycastMachineColliders). This is the machine-collider half of
		// the interaction system: when it hits, the target is that machine and
		// the highlight/mining/placing all react to its real collider geometry.
		RaycastMachineColliders(from, dir);

		if (_world.RaycastVoxels(from, dir, MaxRaycastDistance, out Vector3I hitVoxel, out Vector3 hitNormal))
		{
			_hasTarget = true;
			_targetVoxel = hitVoxel;
			_targetNormal = hitNormal;
		}
		else
		{
			_hasTarget = false;
			_targetVoxel = default;
			_targetNormal = Vector3.Zero;
		}

		// RECONCILE: the overall target is the NEAREST of the voxel hit and the
		// machine-collider hit. The collider wins when it is a hit AND (there is
		// no voxel hit, OR the collider is strictly nearer than the voxel's
		// center). When a machine collider wins, the highlight becomes a box
		// over the collider AABB and the voxel wireframe is hidden (the
		// collider, not the voxel, is what is being aimed at).
		bool colliderWins = _colliderHit
			&& (!_hasTarget || _colliderHitDistance < VoxelHitDistance(from) - 0.01f);

		if (colliderWins)
		{
			// Target is the machine collider.
			_hasTarget = true;
			float vs = _world.VoxelSize;
			// The highlight box is sized to the machine's collider AABB and
			// centered on it (the collider's world-space bounding box).
			var (aabbMin, aabbMax) = _colliderMachine.ColliderWorldAabb();
			_highlight.GlobalTransform = new Transform3D(Basis.Identity, (aabbMin + aabbMax) * 0.5f);
			Vector3 boxSize = aabbMax - aabbMin + new Vector3(vs * 0.05f, vs * 0.05f, vs * 0.05f);
			// The fill box is a unit BoxMesh scaled to the collider AABB size.
			// The wireframe (voxel-shaped) is hidden: the collider is the target.
			_highlightMeshInstance.Visible = false;
			_highlightFill.Visible = true;
			_highlightFill.Scale = boxSize;
			_highlight.Visible = true;
			// Record the targeted voxel as the machine's voxel so mining/placing
			// and the HUD readout have a voxel anchor (the machine's own cell).
			_targetVoxel = _colliderMachine.VoxelPos;
			_targetNormal = _colliderHitNormal;
		}
		else if (_hasTarget)
		{
			// Target is the voxel (the normal voxel-grid path).
			float vs = _world.VoxelSize;
			_highlight.GlobalTransform = new Transform3D(
				Basis.Identity,
				new Vector3(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z) * vs
				+ new Vector3(0.5f, 0.5f, 0.5f) * vs);
			float hs = vs * 1.05f;
			_highlightMeshInstance.Scale = new Vector3(hs, hs, hs);
			_highlightFill.Scale = new Vector3(hs, hs, hs);

			try
			{
				EnsureHighlightMesh(_targetVoxel);
			}
			catch (System.Exception e)
			{
				GD.PushWarning($"[VoxelTool] Highlight rebuild failed for voxel {_targetVoxel}: {e.Message}");
			}
		}
		else
		{
			// No target at all: hide the highlight. Per-voxel damage stays in
			// _mineDamage so re-aiming at a partially-mined voxel resumes.
			_hasTarget = false;
			_highlight.Visible = false;
			_highlightMeshInstance.Visible = false;
			_highlightFill.Visible = false;
		}

		// MACHINE TARGETING (for the UI): the machine the player is interacting
		// with is either the collider-hit machine (when the collider won) or the
		// node-scan machine (the closest in the line of sight). Right-click opens
		// its UI. The collider-hit machine takes precedence when it won.
		_targetMachine = colliderWins ? _colliderMachine : FindTargetMachine(from, dir);

		// POWER-RADIUS INDICATOR: while the player aims at a power source, show
		// a transparent yellow sphere centered on it, sized to the live power
		// radius (the range its power reaches). Hidden otherwise.
		UpdatePowerIndicator();

		// Push the targeted-voxel info to the top-right HUD readout each frame
		// (the panel hides itself when there is no target).
		ResolveServices();
		_targetInfo?.Update(BuildTargetInfo(from));

		// MACHINE PLACEMENT PREVIEW: while the player holds a placeable machine
		// item (and has a target), show a transparent ghost of that machine at
		// the target cell, reflecting the current R-key rotation. Rebuilt only
		// when the cell / item / rotation changes (or shown/hidden).
		UpdatePreview();

		// MINE: hold left mouse (captured) to damage the target over time, but
		// only when the held item is a TOOL. A tool breaks blocks at an
		// affinity-scaled rate; a placeable material does NOT mine (it places,
		// see _Input), so there is no conflict between placing and breaking —
		// the held item decides which.
		if (HeldTool is { } && _hasTarget
			&& Input.MouseMode == Input.MouseModeEnum.Captured
			&& Input.IsMouseButtonPressed(MouseButton.Left))
		{
			ApplyMiningDamage(dt);
		}
	}

	public override void _Input(InputEvent @event)
	{
		// Place: a single fresh left-mouse press (while captured, on a target)
		// places the held material into the empty cell adjacent to the targeted
		// face. Using JustPressed (not the held state) means holding to mine
		// never also triggers a place on the same press.
		if (@event is InputEventMouseButton mouse
			&& mouse.ButtonIndex == MouseButton.Left
			&& mouse.Pressed
			&& Input.MouseMode == Input.MouseModeEnum.Captured
			&& _hasTarget)
		{
			TryPlaceHeldMaterial();
		}

		// INTERACT: a single fresh RIGHT-mouse press (while captured) opens the
		// UI of the machine the player is targeting (see _targetMachine). If no
		// machine is targeted, right-click does nothing (it is NOT a mine or
		// place action). When a machine IS targeted, the preview ghost is hidden
		// for that frame so the player can see the real machine they are
		// clicking.
		if (@event is InputEventMouseButton rmouse
			&& rmouse.ButtonIndex == MouseButton.Right
			&& rmouse.Pressed
			&& Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			if (_targetMachine != null)
			{
				// Defer so the UI panel node is touched while no other signal is
				// on the stack (the same deferred pattern the slot-picker
				// confirm/cancel paths use).
				CallDeferred(nameof(OpenTargetMachineUI));
			}
		}

		// Hotbar selection (mouse wheel + number keys 1-9,0). This is a player
		// action taken while the inventory is CLOSED (mouse captured, in the 3D
		// world) — it is deliberately handled here, NOT in the inventory UI, so
		// it is fully separate from the UI. It is ignored while the inventory is
		// open (the player is rearranging items by clicking then; there is no
		// hotbar selection to change). Drives the UI's highlight via the
		// CycleHotbarFromWorld / SelectHotbarSlotFromWorld methods.
		//
		// The wheel is read directly off the event (ButtonIndex WheelUp/Down)
		// rather than IsActionJustPressed, so each scroll tick cycles exactly
		// once — IsActionJustPressed is frame-based and could fire for more than
		// one input event in the same frame.
		if (!InventoryOpen)
		{
			if (@event is InputEventMouseButton wheel && wheel.Pressed)
			{
				if (wheel.ButtonIndex == MouseButton.WheelUp)
				{
					_ui?.CycleHotbarFromWorld(1);
				}
				else if (wheel.ButtonIndex == MouseButton.WheelDown)
				{
					_ui?.CycleHotbarFromWorld(-1);
				}
			}
			else
			{
				// R key: cycle the machine placement rotation through the 12
				// supported orientations (4×90° about each axis, wrapping). This
				// is only meaningful while holding a machine item, but it is
				// harmless to allow it always (it just changes the persistent
				// _currentRotation, which is applied to the next machine placed
				// and shown in the preview). Using the physical keycode so it
				// works regardless of keyboard layout / IME.
				if (@event is InputEventKey rkey
					&& rkey.Pressed && !rkey.Echo
					&& (rkey.PhysicalKeycode == Key.R || rkey.Keycode == Key.R))
				{
					CycleMachineRotation();
				}
				else if (@event is InputEventKey ckey
					&& ckey.Pressed && !ckey.Echo
					&& (ckey.PhysicalKeycode == Key.C || ckey.Keycode == Key.C))
				{
					// C key: toggle the machine-config panel (the list of all
					// registered machine definitions, with add/remove).
					var world = GetTree()?.GetFirstNodeInGroup("voxel_world") as VoxelWorld;
					world?.OpenMachineConfigPanel();
				}
				else
				{
					int slot = HotbarSlotFromEvent(@event);
					if (slot >= 0)
					{
						_ui?.SelectHotbarSlotFromWorld(slot);
					}
				}
			}
		}
	}

	/// <summary>
	/// Cycles the player's persistent machine placement rotation to the next of
	/// the 12 supported orientations (4×90° about X/Y/Z, wrapping). The new
	/// value is stored in <see cref="_currentRotation"/> and, if a preview ghost
	/// is currently shown, the ghost is rebuilt to reflect it immediately.
	/// </summary>
	private void CycleMachineRotation()
	{
		_currentRotation = _currentRotation.Next();
		// If a preview is up, reflect the new rotation now (the next _Process
		// frame would also catch it, but doing it here makes the R press feel
		// instant).
		if (_preview != null)
		{
			UpdatePreview();
		}
	}

	/// <summary>
	/// Opens (or re-targets) the <see cref="MachineUI"/> for the currently
	/// targeted machine. Called deferred from the right-click in <see
	/// cref="_Input"/>. Resolves the MachineUI node (a child of the world or
	/// found via group) and hands it the targeted machine. If the machine is
	/// already the open UI's target, a second right-click closes it (toggle).
	/// </summary>
	private void OpenTargetMachineUI()
	{
		if (_targetMachine == null)
		{
			return;
		}
		ResolveServices();
		// The MachineUI installs itself into the "machine_ui" group (the same
		// pattern the inventory / target-info / FPS UIs use), so find it there.
		MachineUI machineUI = null;
		foreach (var n in GetTree().GetNodesInGroup("machine_ui"))
		{
			if (n is MachineUI mu)
			{
				machineUI = mu;
				break;
			}
		}
		machineUI?.OpenFor(_targetMachine, _inventory);
	}

	/// <summary>
	/// True while the inventory UI is open. Resolved via the inventory node's
	/// group (the same node the place-ability consumes items from).
	/// </summary>
	private bool InventoryOpen
	{
		get
		{
			ResolveServices();
			return _inventory != null && _inventory.IsOpen;
		}
	}

	/// <summary>
	/// Maps a freshly-pressed number-key input event (1-9, 0) to a hotbar slot
	/// index (0-9), or -1 when the event is not a hotbar number key. Uses the
	/// physical keycode so it works regardless of keyboard layout / IME.
	/// </summary>
	private static int HotbarSlotFromEvent(InputEvent @event)
	{
		if (!(@event is InputEventKey key && key.Pressed && !key.Echo))
		{
			return -1;
		}

		Key k = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
		if (k >= Key.Key1 && k <= Key.Key9)
		{
			return (int)k - (int)Key.Key1; // 0-8
		}
		if (k == Key.Key0)
		{
			return 9;
		}
		return -1;
	}

	/// <summary>
	/// The default place-ability. The held item decides what happens:
	///
	/// - A MACHINE item: spawn a <see cref="FactorySim.Machines.Machine"/> node
	///   (its imported mesh + behavior) into the cell adjacent to the targeted
	///   face. This is the factory design system's placement path.
	/// - A PLACEABLE MATERIAL: place one voxel of it into that same cell.
	///
	/// In both cases one item is consumed from the selected hotbar slot. Does
	/// nothing when the held item is a tool (tools mine, they don't place),
	/// empty, or when the target cell is not free.
	/// </summary>
	private void TryPlaceHeldMaterial()
	{
		var held = HeldItem;
		if (held.IsEmpty || held.IsTool)
		{
			return; // tools don't place; empty does nothing
		}

		// If a MACHINE COLLIDER is the current target (the collider won the
		// reconcile), placing is blocked: you cannot place a voxel/machine into
		// a cell the machine's collider occupies. The collider is the thing you
		// are looking at, not the empty cell behind it.
		if (_colliderHit && _colliderMachine != null && IsInstanceValid(_colliderMachine))
		{
			return; // the machine collider is in the way — can't place here
		}

		// The cell to place into: the empty cell on the far side of the targeted
		// face, i.e. the hit voxel stepped along the face normal (the normal
		// points back toward the camera, into the adjacent empty cell).
		Vector3I place = _targetVoxel + new Vector3I(
			Mathf.RoundToInt(_targetNormal.X),
			Mathf.RoundToInt(_targetNormal.Y),
			Mathf.RoundToInt(_targetNormal.Z));

		bool placed;
		if (held.IsMachine)
		{
			// Machine: spawn a Machine node at the target cell with the current
			// R-key rotation. The spawner enforces the OCCUPANCY CHECK (one
			// machine per cell, no placing into a solid voxel) and bounds, and
			// returns null when placement isn't allowed — in that case nothing
			// is placed and nothing is consumed.
			var machine = FactorySim.Machines.MachineSpawner.SpawnMachine(
				_world, held.MaterialId, place, _currentRotation);
			placed = machine != null;
		}
		else
		{
			// Material: place a voxel. A light-source item (LightLevel > 0)
			// marks the placed voxel as a block-light source of that level.
			if (!VoxelMaterialIds.IsPlaceableMaterialId(held.MaterialId))
			{
				return;
			}
			placed = _world.PlaceVoxel(place, held.MaterialId, held.LightLevel);
		}

		if (!placed)
		{
			return; // already solid (material), unregistered machine, or bad cell
		}

		// Consume one item from the selected hotbar slot.
		Inventory inv = _inventory;
		if (inv == null || _ui == null)
		{
			return;
		}
		int slot = Mathf.Clamp(_ui.SelectedHotbarSlot, 0, Inventory.HotbarSize - 1);
		var stack = inv.Slots[slot];
		if (stack.IsEmpty || stack.MaterialId != held.MaterialId)
		{
			return;
		}
		stack.Count -= 1;
		if (stack.Count <= 0)
		{
			inv.Slots[slot] = ItemStack.From(0);
		}
		// RaiseChanged so the hotbar UI + crafting counts refresh.
		inv.RaiseChangedPublic();
	}

	// ------------------------------------------------------------------
	// Machine placement preview (transparent ghost at the target cell)
	// ------------------------------------------------------------------

	/// <summary>
	/// The cell the preview ghost would be placed into: the empty cell on the
	/// far side of the targeted face (the same cell <see cref="TryPlaceHeldMaterial"/>
	/// uses). Only valid when <see cref="_hasTarget"/> is true.
	/// </summary>
	private Vector3I PreviewCell
	{
		get
		{
			if (!_hasTarget)
			{
				return default;
			}
			return _targetVoxel + new Vector3I(
				Mathf.RoundToInt(_targetNormal.X),
				Mathf.RoundToInt(_targetNormal.Y),
				Mathf.RoundToInt(_targetNormal.Z));
		}
	}

	/// <summary>
	/// Shows / updates / hides the transparent machine placement preview. Called
	/// every frame from <c>_Process</c> (cheap: it early-returns when nothing
	/// changed) and from <see cref="CycleMachineRotation"/> for an instant R-key
	/// response.
	///
	/// The preview is shown only when ALL of: the held item is a placeable
	/// machine (not a tool, not empty, not a plain material), there is a target,
	/// and the target cell is placeable (in bounds and not occupied — see
	/// <see cref="MachineSpawner.CanPlaceAt"/>). Otherwise the ghost is hidden
	/// (a red-tinted ghost would be a nice "can't place here" cue, but hiding is
	/// the simplest correct behavior for now).
	/// </summary>
	private void UpdatePreview()
	{
		var held = HeldItem;

		// Only show a preview for a placeable MACHINE item.
		bool shouldShow = !held.IsEmpty
			&& !held.IsTool
			&& held.IsMachine
			&& _hasTarget;

		if (!shouldShow)
		{
			HidePreview();
			return;
		}

		Vector3I cell = PreviewCell;
		int itemId = held.MaterialId;
		var rotation = _currentRotation;

		// The cell must be placeable (in bounds + not occupied) for the ghost to
		// be meaningful; if it isn't, hide the ghost (the placement would fail
		// anyway).
		if (!FactorySim.Machines.MachineSpawner.CanPlaceAt(_world, cell))
		{
			HidePreview();
			return;
		}

		// Nothing changed since the last frame AND the ghost is currently shown:
		// it is already in the right place with the right rotation. (If the ghost
		// was hidden this frame — e.g. the player briefly lost the target — we
		// still need to re-show it, so require Visible too.)
		if (_preview != null && _preview.Visible
			&& _previewCell == cell && _previewItemId == itemId && _previewRotation == rotation)
		{
			return;
		}

		// Something changed (cell moved, different machine, rotation cycled, or
		// the ghost is hidden): tear down the old ghost and build a fresh one at
		// the new cell.
		DestroyPreview();

		var ghost = FactorySim.Machines.MachineSpawner.SpawnMachine(_world, itemId, cell, rotation);
		if (ghost == null)
		{
			return; // CanPlaceAt said OK but spawn failed (shouldn't happen); no ghost.
		}
		ghost.IsPreview = true;
		// Re-apply the preview visual now that IsPreview is set (the spawner
		// built the visual as a real machine). This swaps in the transparent
		// material override and removes any collision the spawner added.
		ghost.ApplyPreviewVisual();

		_preview = ghost;
		_previewCell = cell;
		_previewItemId = itemId;
		_previewRotation = rotation;
	}

	/// <summary>Hides the preview ghost without destroying it (keeps it for reuse).</summary>
	private void HidePreview()
	{
		if (_preview != null)
		{
			_preview.Visible = false;
		}
	}

	/// <summary>Destroys the preview ghost node (frees it from the tree).</summary>
	private void DestroyPreview()
	{
		if (_preview != null)
		{
			_preview.QueueFree();
			_preview = null;
		}
		_previewCell = default;
		_previewItemId = 0;
		_previewRotation = default;
	}


	/// <summary>
	/// Damages the current target over time. The target is either a VOXEL (the
	/// normal path — damage is accumulated per voxel in <see cref="_mineDamage"/>
	/// until it reaches full health, then the voxel breaks) or a MACHINE
	/// COLLIDER (when <see cref="_colliderHit"/> is set and the collider won the
	/// reconcile). A machine takes <c>ToolPower × affinity × dt</c> damage per
	/// second via <see cref="Machine.TakeDamage"/>; when its health reaches 0 it
	/// breaks (is removed from the world).
	///
	/// For a voxel, the damage rate is
	/// <c>(ToolPower × affinity / material BreakTime) × 100% per second</c>,
	/// where the affinity comes from <see cref="ToolAffinity"/> for the held
	/// tool type (or 1.0 when the held item is a plain material).
	/// </summary>
	private void ApplyMiningDamage(float dt)
	{
		// MACHINE TARGET: the collider won the reconcile, so the target is the
		// machine (not a voxel). Damage it directly via TakeDamage.
		if (_colliderHit && _colliderMachine != null && IsInstanceValid(_colliderMachine))
		{
			// Affinity: a machine is "metal" (it is built from metal parts); the
			// held tool's affinity vs the Metal category scales the break rate.
			float machineAffinity = 1.0f;
			if (HeldTool is { } mTool)
			{
				machineAffinity = ToolAffinity.GetMultiplier(mTool, ToolAffinity.MaterialCategory.Metal);
			}
			float machineDamage = ToolPower * machineAffinity * dt;
			bool broke = _colliderMachine.TakeDamage((int)System.Math.Ceiling(machineDamage));
			if (broke)
			{
				// The machine broke: remove it from the world (frees the node,
				// which triggers its _ExitTree / cleanup).
				_colliderMachine.QueueFree();
				_colliderHit = false;
				_colliderMachine = null;
			}
			return;
		}

		// VOXEL TARGET: the normal per-voxel mining path.
		int material = _world.GetVoxel(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z);
		if (material == VoxelMaterialIds.Air)
		{
			// Target turned to air (broke elsewhere / edited): drop its damage.
			_mineDamage.Remove(_targetVoxel);
			return;
		}

		// Unbreakable materials (bedrock) take no damage and never break.
		if (_world.IsUnbreakable(material))
		{
			_mineDamage.Remove(_targetVoxel);
			return;
		}

		// Refresh the target's damage from storage each frame (a fresh value
		// when the player re-aimed at this voxel, its carried value otherwise).
		float damage = _mineDamage.TryGetValue(_targetVoxel, out float stored) ? stored : 0f;

		float breakTime = 1.0f;
		if (_world.MaterialConfig != null && _world.MaterialConfig.GetDef(material) is { } def && def.Entry != null)
		{
			breakTime = Mathf.Max(0.05f, def.Entry.BreakTime);
		}

		// Affinity: the held tool's type vs the target's material category. A
		// held material (no tool) mines at the neutral 1.0 base rate.
		float affinity = 1.0f;
		if (HeldTool is { } tool)
		{
			affinity = ToolAffinity.GetMultiplier(tool, material);
		}

		damage += ToolPower * affinity / breakTime * VoxelHealth.MaxHealth * dt;

		if (damage >= VoxelHealth.MaxHealth)
		{
			// The voxel broke: remove it (resets health + rebuilds the chunk).
			// Capture the edit stamp around the break so the VoxelBroken event
			// it raises queues a drop that waits for THIS chunk update.
			CaptureEditStamp();
			_world.BreakVoxel(_targetVoxel);
			_pendingEditStamp = 0;
			_mineDamage.Remove(_targetVoxel);
			return;
		}

		// Store immediately so the damage is persistent if the player stops or
		// moves to another voxel this frame.
		_mineDamage[_targetVoxel] = damage;
	}

	/// <summary>
	/// Builds a <see cref="TargetInfoUI.TargetInfo"/> snapshot for the top-right
	/// HUD readout from the current target state. Returns a no-target snapshot
	/// (which hides the panel) when the raycast found nothing. The best tool is
	/// the one with the highest break affinity for the target's material
	/// category; the distance is the camera-to-voxel-center length in world
	/// units.
	/// </summary>
	private TargetInfoUI.TargetInfo BuildTargetInfo(Vector3 cameraPos)
	{
		var info = new TargetInfoUI.TargetInfo();
		if (!_hasTarget)
		{
			return info; // HasTarget stays false → panel hides
		}

		int material = _world.GetVoxel(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z);
		if (material == VoxelMaterialIds.Air)
		{
			return info; // turned to air mid-frame; treat as no target
		}

		bool unbreakable = _world.IsUnbreakable(material);
		string name = "Unknown";
		float breakTime = 1f;
		if (_world.MaterialConfig != null && _world.MaterialConfig.GetDef(material) is { } def)
		{
			name = string.IsNullOrEmpty(def.Name) ? $"Material {def.Id}" : def.Name;
			breakTime = def.Entry != null ? Mathf.Max(0.05f, def.Entry.BreakTime) : 1f;
		}

		// Current health: full minus the damage accumulated on this voxel (the
		// same _mineDamage the miner writes to, so the readout tracks mining).
		float damage = _mineDamage.TryGetValue(_targetVoxel, out float stored) ? stored : 0f;
		float health = Mathf.Max(0f, VoxelHealth.MaxHealth - damage);

		// Distance: camera to the target voxel's CENTER (voxel corner + half a
		// voxel along each axis), in world units (VoxelSize per voxel).
		Vector3 center = new Vector3(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z) * _world.VoxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
		float distance = (center - cameraPos).Length();

		info.HasTarget = true;
		info.MaterialName = name;
		info.HealthPercent = health;
		info.BreakTime = breakTime;
		info.Unbreakable = unbreakable;
		info.Distance = distance;
		info.Category = CategoryDisplayName(ToolAffinity.CategoryOf(material));
		info.BestTool = unbreakable ? "" : BestToolName(material);

		// If a machine is the target (nearer than the voxel behind it), show a
		// machine readout instead of the material rows. The voxel info is still
		// computed (above) but the panel emphasizes the machine.
		if (_targetMachine != null && _targetMachine.Definition != null)
		{
			info.IsMachine = true;
			info.MachineLine = BuildMachineLine(_targetMachine, cameraPos);
		}
		return info;
	}

	/// <summary>
	/// Builds the one-line machine readout for the target HUD: the machine's
	/// display name plus a short status (e.g. "— needs power", "— 50%").
	/// </summary>
	private string BuildMachineLine(Machine m, Vector3 cameraPos)
	{
		string name = string.IsNullOrEmpty(m.Definition.Name) ? m.Type.ToString() : m.Definition.Name;
		string status = "";
		var def = m.Definition;
		if (def.PowerConsumption > 0f)
		{
			status = PowerNetwork.IsPowered(_world, m.VoxelPos) ? "" : " — needs power";
		}
		else if (def.PowerOutput > 0f)
		{
			status = $" — outputs {def.PowerOutput:0.#}";
		}
		if (m is FactorySim.Machines.CrafterMachine crafter)
		{
			if (crafter.Recipe != null)
			{
				int pct = Mathf.RoundToInt(crafter.CraftProgress * 100f);
				status += crafter.Recipe != null ? $" — {pct}%" : "";
			}
			else
			{
				status += " — no recipe";
			}
		}
		return $"{name}{status}";
	}

	/// <summary>
	/// The distance from the camera to the CURRENT voxel target's CENTER (the
	/// same convention <see cref="BuildTargetInfo"/> uses for its readout). Used
	/// to reconcile the voxel hit against the machine-collider hit (nearer wins).
	/// Returns a large value when there is no voxel target.
	/// </summary>
	private float VoxelHitDistance(Vector3 cameraPos)
	{
		if (!_hasTarget)
		{
			return float.PositiveInfinity;
		}
		Vector3 center = new Vector3(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z) * _world.VoxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
		return (center - cameraPos).Length();
	}

	/// <summary>
	/// Raycasts the camera ray against every placed (non-preview) machine's
	/// ACTUAL collision shape and records the NEAREST hit. This is what makes a
	/// machine's collider interact with the player's interaction system: the
	/// tool's targeting (and therefore the highlight, mining, and place-
	/// blocking) reacts to the machine's real collider geometry, not just the
	/// voxel grid behind it.
	///
	/// Uses <see cref="PhysicsDirectSpaceState3D.IntersectRay"/> with a collision
	/// mask that matches the machines' collision layer. Machines are on a
	/// dedicated collision layer (layer 3 / value 4) so the ray hits ONLY machine
	/// colliders (not the voxel terrain, which is a separate body). The result
	/// is stored in the _collider* fields; <see cref="_colliderHit"/> is set
	/// true when a hit was found.
	/// </summary>
	private void RaycastMachineColliders(Vector3 from, Vector3 dir)
	{
		// Default to "no collider hit" each frame (the fields are reused).
		_colliderHit = false;
		_colliderMachine = null;
		_colliderHitPoint = Vector3.Zero;
		_colliderHitNormal = Vector3.Zero;
		_colliderHitDistance = 0f;

		PhysicsDirectSpaceState3D space = GetWorld3D()?.DirectSpaceState;
		if (space == null)
		{
			return;
		}

		// Create the query from the camera to a point MaxRaycastDistance ahead,
		// with a collision mask that targets ONLY the machines' collision layer
		// (layer 3, value 4). This keeps the ray from hitting voxel terrain (a
		// separate body on a different layer).
		var query = PhysicsRayQueryParameters3D.Create(from, from + dir * MaxRaycastDistance, 1 << 3);

		// IntersectRay returns a Godot.Collections.Dictionary (empty on a miss).
		Godot.Collections.Dictionary result = space.IntersectRay(query);
		if (result.Count == 0)
		{
			return; // no hit
		}

		// The "collider" entry is the Machine (a StaticBody3D — the
		// CollisionShape3D's owner body). A Variant converts to a GodotObject via
		// its implicit operator, so the pattern match resolves the Machine.
		if (result.TryGetValue("collider", out Variant colliderV))
		{
			GodotObject obj = (GodotObject)colliderV;
			if (obj is Machine machine && !machine.IsPreview)
			{
				_colliderHit = true;
				_colliderMachine = machine;
				if (result.TryGetValue("position", out Variant posV))
				{
					_colliderHitPoint = posV.AsVector3();
				}
				if (result.TryGetValue("normal", out Variant nrmV))
				{
					_colliderHitNormal = nrmV.AsVector3();
				}
				_colliderHitDistance = (_colliderHitPoint - from).Length();
			}
		}
	}

	/// <summary>
	/// Finds the closest machine in the line of sight from the camera, within
	/// <see cref="MaxRaycastDistance"/>. A machine is targeted when the camera's
	/// ray passes within one voxel of its center. Returns null when no machine
	/// is in range (the target then stays a plain voxel).
	/// </summary>
	private Machine FindTargetMachine(Vector3 from, Vector3 dir)
	{
		Machine best = null;
		float bestDist = MaxRaycastDistance;

		var machinesRoot = GetTree().Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return null;
		}

		float vs = _world.VoxelSize;
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (!(machinesRoot.GetChild(i) is Machine m) || m.IsPreview)
			{
				continue;
			}
			// The machine's world-space center (voxel center).
			Vector3 center = new Vector3(m.VoxelPos.X, m.VoxelPos.Y, m.VoxelPos.Z) * vs
				+ new Vector3(0.5f, 0.5f, 0.5f) * vs;
			Vector3 toCenter = center - from;
			float t = toCenter.Dot(dir);
			if (t <= 0f || t > MaxRaycastDistance)
			{
				continue; // behind the camera or beyond range
			}
			// Perpendicular distance from the ray to the machine center.
			float perp = (toCenter - dir * t).Length();
			if (perp > vs * 0.75f)
			{
				continue; // ray misses the machine
			}
			// Use the distance along the ray (t) for nearest-wins.
			if (t < bestDist)
			{
				bestDist = t;
				best = m;
			}
		}
		return best;
	}

	/// <summary>
	/// Shows the transparent yellow power-radius sphere while the targeted
	/// machine is a power source (its definition's <see
	/// cref="FactorySim.Machines.MachineDefinition.PowerOutput"/> is &gt; 0), and
	/// hides it otherwise. When shown, the sphere is centered on the source's
	/// voxel and scaled to the live power radius (world units = radius ×
	/// VoxelSize), so the player can see exactly how far the source's power
	/// reaches. The sphere's orientation is locked to the world (identity
	/// basis) so it does not spin with the player's yaw.
	/// </summary>
	private void UpdatePowerIndicator()
	{
		if (_powerIndicator == null)
		{
			return;
		}

		bool isSource = _targetMachine != null
			&& _targetMachine.Definition != null
			&& _targetMachine.Definition.PowerOutput > 0f;

		if (!isSource)
		{
			_powerIndicator.Visible = false;
			_powerSphere.Visible = false;
			return;
		}

		float vs = _world.VoxelSize;
		// Center the indicator on the source's voxel center (the same convention
		// the highlight uses).
		var center = new Vector3(
			_targetMachine.VoxelPos.X,
			_targetMachine.VoxelPos.Y,
			_targetMachine.VoxelPos.Z) * vs
			+ new Vector3(0.5f, 0.5f, 0.5f) * vs;
		// Identity basis overrides the inherited player yaw (see the highlight).
		_powerIndicator.GlobalTransform = new Transform3D(Basis.Identity, center);

		// The unit SphereMesh has radius 0.5 (diameter 1), so scaling the
		// MeshInstance by (2 * radius * voxelSize) makes the world radius exactly
		// (radius * voxelSize).
		float worldRadius = FactorySim.Machines.PowerNetwork.Radius * vs;
		float scale = worldRadius * 2f;
		_powerSphere.Scale = new Vector3(scale, scale, scale);

		_powerIndicator.Visible = true;
		_powerSphere.Visible = true;
	}

	/// <summary>
	/// The display name of the single most efficient tool for the given material
	/// (highest break affinity in its category). Ties resolve to the earlier
	/// tool in the enum order, which is fine because affinity ties are rare and
	/// any of the tied tools is equally "best".
	/// </summary>
	private static string BestToolName(int materialId)
	{
		var category = ToolAffinity.CategoryOf(materialId);
		ToolType best = ToolType.Shovel;
		float bestAffinity = float.NegativeInfinity;
		foreach (ToolType tool in new[] { ToolType.Shovel, ToolType.Pickaxe, ToolType.Axe, ToolType.Sword })
		{
			float a = ToolAffinity.GetMultiplier(tool, category);
			if (a > bestAffinity)
			{
				bestAffinity = a;
				best = tool;
			}
		}
		return ItemRegistry.ToolName(best);
	}

	/// <summary>Human-readable name for a material category (lowercase).</summary>
	private static string CategoryDisplayName(ToolAffinity.MaterialCategory category) => category switch
	{
		ToolAffinity.MaterialCategory.Soft => "Soft",
		ToolAffinity.MaterialCategory.Stone => "Stone",
		ToolAffinity.MaterialCategory.Wood => "Wood",
		ToolAffinity.MaterialCategory.Metal => "Metal",
		ToolAffinity.MaterialCategory.Generic => "Generic",
		_ => "Unknown",
	};

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
			_highlightFill.Visible = false;
			return;
		}

		// The translucent fill box marks the targeted block and is always shown
		// for a solid target — even when the wireframe has few or no exposed
		// faces — so the highlight is unmistakable.
		_highlightFill.Visible = true;

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
		// and translate the resulting edges into the pivot's local frame. The
		// pivot sits at the voxel CENTER, so the edges are centered on the local
		// origin (-0.5..0.5 * voxelSize per axis) — that makes the MeshInstance
		// origin coincide with the voxel center, so the 105% scale expands the
		// wireframe symmetrically about the voxel.
		float vs = _world.VoxelSize;
		_highlightPivotWorld = new Vector3(voxel.X, voxel.Y, voxel.Z) * vs
			+ new Vector3(0.5f, 0.5f, 0.5f) * vs;
		// The 1×1×1 chunk's (0,0,0) maps to the targeted world voxel, so its
		// world origin is (voxel.X, voxel.Y). Pass a neighborChunkQuery that
		// returns null for every neighbor: BuildLightFieldWide handles that by
		// reading air inside the 1-voxel shell and solid beyond it — the correct
		// behavior for a highlight that only needs its own voxel's surface (the
		// solid/air culling is already driven by the solidQuery closure above).
		System.Func<Vector2I, ChunkData> noNeighbors = _ => null;
		var faces = MeshBuilder.BuildVisual(
			data, voxel.X, voxel.Y, 1f, _world.Palette, solidQuery,
			neighborChunkQuery: noNeighbors);
		var wire = BuildEdgeMesh(faces, _highlightPivotWorld);
		_meshedTarget = voxel;

		// BuildEdgeMesh returns null when the voxel has no exposed faces (fully
		// buried / at an unloaded border). In that case the wireframe is hidden,
		// but the pivot stays visible so the translucent FILL BOX still marks the
		// targeted block. (Assigning a bare Mesh() would re-trigger the "required
		// virtual method" error, so we keep the previous wireframe.)
		if (wire == null)
		{
			_highlightWireframe = null;
			_highlightMeshInstance.Visible = false;
			_highlight.Visible = true; // keep the fill box visible
			return;
		}

		_highlightWireframe = wire;
		_highlightMeshInstance.Mesh = wire;
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
	/// <paramref name="worldOffset"/> is the pivot's absolute world position (the
	/// targeted voxel's CENTER): BuildVisual emits vertices in world-voxel
	/// coordinates, so each vertex is translated by <c>-worldOffset</c> into the
	/// pivot's local frame — centered on the local origin (-0.5..0.5 per axis for
	/// a unit voxel). The caller then scales the MeshInstance3D by voxelSize *
	/// 1.05 around that origin, expanding the wireframe symmetrically about the
	/// voxel center.
	///
	/// Vertices are read through a <see cref="MeshDataTool"/> per surface (the
	/// supported API in Godot 4.8 C# — <see cref="ArrayMesh"/> exposes no direct
	/// array access), mirroring the pattern used in the rewrite test suite.
	/// </summary>
	private static Mesh BuildEdgeMesh(ArrayMesh mesh, Vector3 worldOffset)
	{
		// A voxel with no exposed faces (fully buried, or at an unloaded chunk
		// border where every neighbour reads as solid) yields an ArrayMesh with
		// ZERO surfaces. CreateFromSurface on an empty mesh throws
		// "Index 0 out of bounds (surfaces.size() = 0)", so bail before the loop.
		if (mesh == null || mesh.GetSurfaceCount() == 0)
		{
			return null;
		}

		var lines = new List<Vector3>();
		var seen = new HashSet<long>();
		var data = new MeshDataTool();

		// Bound by the real surface count: CreateFromSurface returns Error.Ok
		// even for an out-of-bounds surface index (it only throws inside the
		// C++ surface_get_primitive_type), so the loop must stop at
		// GetSurfaceCount() or it overruns and errors.
		int surfaceCount = mesh.GetSurfaceCount();
		for (int s = 0; s < surfaceCount; s++)
		{
			if (data.CreateFromSurface(mesh, s) != Error.Ok)
			{
				continue; // surface failed to load; skip it
			}

			int count = data.GetVertexCount();
			for (int i = 0; i + 2 < count; i += 3)
			{
				AddEdge(lines, seen, data.GetVertex(i) - worldOffset, data.GetVertex(i + 1) - worldOffset, data.GetVertex(i + 2) - worldOffset);
			}
		}

		if (lines.Count == 0)
		{
			// No geometry (e.g. a fully-culled voxel): return null so the caller
			// hides the highlight instead of assigning a bare, unoverridable
			// Mesh() (which would raise "required virtual method must be
			// overridden").
			return null;
		}

		var tool = new SurfaceTool();
		tool.Begin(Mesh.PrimitiveType.Lines);
		foreach (Vector3 v in lines)
		{
			tool.AddVertex(v);
		}
		return tool.Commit();
	}

	private static void AddEdge(List<Vector3> lines, HashSet<long> seen, Vector3 a, Vector3 b, Vector3 c)
	{
		AddEdgePair(lines, seen, a, b);
		AddEdgePair(lines, seen, b, c);
		AddEdgePair(lines, seen, c, a);
	}

	private static void AddEdgePair(List<Vector3> lines, HashSet<long> seen, Vector3 a, Vector3 b)
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
