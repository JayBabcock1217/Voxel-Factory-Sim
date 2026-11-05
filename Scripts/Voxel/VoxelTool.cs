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
	/// Opacity (0..1) of the translucent filled box that marks the targeted
	/// voxel (the "cube that shows where the player is pointing"). Higher = more
	/// opaque (the highlighted block is more solid and harder to see through);
	/// lower = fainter (the voxel beneath reads through more clearly). Adjustable
	/// in the editor on the VoxelTool node.
	/// </summary>
	[Export(PropertyHint.Range, "0.01,1,0.01")] public float HighlightFillOpacity { get; set; } = 0.35f;

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

	/// <summary>
	/// True when the current target voxel is a MACHINE cell (an AIR voxel that a
	/// placed machine occupies), as opposed to a solid voxel. The target
	/// highlight renders a plain fill box + cube outline for a machine cell
	/// (see <see cref="EnsureHighlightMesh"/>) instead of the material's own
	/// exposed-face wireframe, which would be empty for air.
	/// </summary>
	private bool _targetIsMachine;

	/// <summary>
	/// The machine the player is currently aiming at, or null. This is the
	/// machine the placement preview is anchored to: when the AABB raycast
	/// strikes a machine, it is the one whose face was hit (derived from the
	/// SAME hit that drives the highlight + placement cell, so right-click is
	/// always consistent with what the player sees). When the raycast misses,
	/// it falls back to the closest machine center in the line of sight (within
	/// <see cref="MaxRaycastDistance"/>). Right-clicking opens this machine's
	/// UI (see <see cref="MachineUI"/>); the target-info HUD shows its name.
	/// </summary>
	private Machine _targetMachine;

	/// <summary>
	/// The dropped item the player is currently aiming at (the nearest
	/// <see cref="FactorySim.Items.VoxelDrop"/> whose small AABB the camera ray
	/// strikes within <see cref="MaxRaycastDistance"/>), or null. Drops are
	/// targeted in addition to voxels and machines — the nearest hit wins — so
	/// the target-info HUD can show the drop's item name + count BEFORE the
	/// player walks up to collect it (mirrors the machine readout).
	/// </summary>
	private FactorySim.Items.VoxelDrop _targetDrop;

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

	// The placement-preview ROTATION AXIS LINE: a short, bright, unshaded line
	// that runs from the center of the preview ghost out along the active
	// rotation axis (X/Y/Z, cycled by R). It is colored with the axis color
	// (X=red, Y=green, Z=blue) so the player always sees WHICH axis the arrow
	// keys will rotate about. It is a sibling of the ghost (added to the same
	// "Machines" root) so it shares the ghost's lifetime: hidden with it, freed
	// with it.
	private MeshInstance3D _axisLine;
	private BoxMesh _axisLineMesh;
	private StandardMaterial3D _axisLineMat;
	private int _axisLineAxis = -1; // the axis the line currently points along (-1 = not built)
	private float _axisLineLen;    // half-length in world units (VoxelSize-scaled)

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
		// through, but bright enough to clearly mark the targeted block. The
		// alpha comes from HighlightFillOpacity (adjustable in the editor).
		_highlightFillMaterial = new StandardMaterial3D
		{
			AlbedoColor = new Color(HighlightColor, HighlightFillOpacity),
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

		// TARGETING: raycast the voxel grid AND the placed machines, and let the
		// NEARER hit win. Machines live in AIR voxels (the spawner refuses to put
		// a machine into a solid voxel), so the pure DDA voxel walk never "hits"
		// one — without the extra machine raycast the target highlight would miss
		// machines entirely, and the "targeted cell" readout / placement preview
		// would be computed from the voxel BEHIND the machine instead of the
		// machine's face (so the player couldn't build off a machine's side).
		bool voxelHit = _world.RaycastVoxels(from, dir, MaxRaycastDistance, out Vector3I hitVoxel, out Vector3 hitNormal);
		float voxelDist = voxelHit ? VoxelHitDistance(from, hitVoxel, hitNormal) : float.PositiveInfinity;
		// The machine raycast also reports WHICH FACE the ray entered through
		// (machEntryAxis) — that face's outward normal is what the placement
		// offset is based on (see TryRaycastMachine).
		bool machineHit = TryRaycastMachine(from, dir, out Vector3I machVoxel, out int machEntryAxis, out float machT);
		// The machine the AABB raycast struck (the preview/highlight anchor).
		// This is the machine right-click should open: it is derived from the
		// SAME hit that drives the placement preview, so the machine you open is
		// always the one the preview is pointing at (not a separate "closest
		// center" machine that could disagree with what you see).
		Machine aabbMachine = machineHit ? MachineAtVoxel(machVoxel) : null;
		// The entry axis tells us WHICH face was struck, but the normal's SIGN
		// depends on the direction of travel: entering through the +X face means
		// the ray moves in −X (dir.x < 0) and the outward normal is (+1,0,0);
		// entering through the −X face means dir.x > 0 and the normal is (−1,0,0).
		// The face the ray enters through is the one it is moving AWAY from, so
		// the outward normal along the entry axis is −sign(dir[axis]).
		Vector3 machNormal = Vector3.Zero;
		if (machineHit && machEntryAxis >= 0 && machEntryAxis < 3)
		{
			float s = dir[machEntryAxis] > 0f ? 1f : -1f;
			machNormal[machEntryAxis] = -s;
		}

		// DROPS: raycast the camera ray against every drop's small AABB. The
		// nearest of {voxel, machine, drop} becomes the target — so aiming at a
		// drop (in the air, in front of a far voxel) targets the drop, letting
		// the HUD show its item + count before the player collects it.
		bool dropHit = TryRaycastDrop(from, dir, out var targetDrop, out float dropT);

		// Nearest-wins among the three: a drop in the air in front of a far
		// voxel should win (you're looking at the drop, not the wall behind it).
		float bestT = float.PositiveInfinity;
		int bestKind = -1; // 0=voxel, 1=machine, 2=drop
		if (voxelHit && voxelDist < bestT) { bestT = voxelDist; bestKind = 0; }
		if (machineHit && machT < bestT)   { bestT = machT;   bestKind = 1; }
		if (dropHit && dropT < bestT)      { bestT = dropT;   bestKind = 2; }

		bool isMachineTarget = bestKind == 1;
		_targetDrop = bestKind == 2 ? targetDrop : null;

		if (isMachineTarget)
		{
			// The machine is the nearest hit: it becomes THE target, so the
			// highlight renders on the machine's voxel and the placement cell
			// (PreviewCell) is the EMPTY voxel on the far side of the machine
			// face the ray struck — machVoxel + the entry face's outward
			// normal (machNormal). This stops the placement preview from
			// rendering inside the machine (it used to be placed at the machine
			// voxel itself, because the old PreviewCell used the raw target
			// normal without accounting for the entry face).
			_hasTarget = true;
			_targetVoxel = machVoxel;
			_targetNormal = machNormal;
			_targetIsMachine = true;
		}
		else if (bestKind == 2)
		{
			// A drop is the target: aim the highlight at the voxel the drop
			// occupies (so the highlight sits under the drop) and mark it as a
			// drop target. _targetIsMachine stays false (a drop is not a
			// machine); BuildTargetInfo checks _targetDrop to show the drop
			// readout instead of the voxel's mining rows.
			_hasTarget = true;
			_targetVoxel = new Vector3I(
			 Mathf.FloorToInt(targetDrop.GlobalPosition.X / _world.VoxelSize),
			 Mathf.FloorToInt(targetDrop.GlobalPosition.Y / _world.VoxelSize),
			 Mathf.FloorToInt(targetDrop.GlobalPosition.Z / _world.VoxelSize));
			_targetNormal = Vector3.Zero;
			_targetIsMachine = false;
		}
		else if (voxelHit)
		{
			// A solid voxel is the target. The hit normal points away from the
			// solid voxel, so the placement cell (PreviewCell) is voxel + normal.
			_hasTarget = true;
			_targetVoxel = hitVoxel;
			_targetNormal = hitNormal;
			_targetIsMachine = false;
		}
		else
		{
			_hasTarget = false;
			_targetIsMachine = false;
			_targetDrop = null;
			// No target: stop damaging. Per-voxel damage stays in _mineDamage
			// so re-aiming at a partially-mined voxel resumes where it left off.
			_highlight.Visible = false;
			_highlightMeshInstance.Visible = false;
			_highlightFill.Visible = false;
		}

		if (_hasTarget)
		{
			// The highlight is a Node3D whose pivot sits at the targeted voxel's
			// CENTER; its child mesh holds the voxel's own edge geometry in local
			// space (0..voxelSize per axis, after the world->local translation in
			// EnsureHighlightMesh) and is scaled 105% by the voxel size, so the
			// wireframe reads as the voxel's shape slightly larger than the voxel,
			// expanding around the voxel center.
			//
			// Orientation is locked to the world: this tool is a child of the
			// player's CharacterBody3D, which yaws with the mouse (Player
			// UpdateMouseLook sets the body's Y rotation). Setting only
			// GlobalPosition would leave the highlight inheriting that yaw, so it
			// would orbit the voxel as the camera turns. Giving it a
			// GlobalTransform with an identity basis overrides the inherited
			// rotation, keeping the wireframe axis-aligned with the world.
			float vs = _world.VoxelSize;
			if (_targetDrop != null)
			{
				// DROP TARGET: the highlight hugs the drop itself (a small box at
				// the drop's position), not the full voxel it occupies. The mesh
				// is rebuilt as a plain unit cube (via EnsureHighlightMesh with a
				// machine-style flag) and scaled to the drop's size, positioned
				// at the drop's center so it tracks the bobbing/spinning drop.
				_highlight.GlobalTransform = new Transform3D(
					Basis.Identity,
					_targetDrop.GlobalPosition);
				float ds = 0.35f; // ~ the drop mesh size (0.3) + a little margin
				_highlightMeshInstance.Scale = new Vector3(ds, ds, ds);
				_highlightFill.Scale = new Vector3(ds, ds, ds);
				try
				{
					// Rebuild the mesh as a plain unit cube (machine style) so it
					// reads as a small box around the drop, not the voxel's
					// exposed-face wireframe.
					EnsureHighlightMesh(_targetVoxel, true);
				}
				catch (System.Exception e)
				{
					GD.PushWarning($"[VoxelTool] Highlight rebuild failed for drop: {e.Message}");
				}
			}
			else
			{
				_highlight.GlobalTransform = new Transform3D(
					Basis.Identity,
					new Vector3(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z) * vs
					+ new Vector3(0.5f, 0.5f, 0.5f) * vs);
				// Both the wireframe and the fill box are centered on the local
				// origin (the voxel center), so scaling each MeshInstance about
				// its origin expands the highlight symmetrically about the
				// voxel. 1.05x keeps the fill hugging the block (slightly larger
				// so it's clearly the targeted one) and the wireframe reads just
				// outside it.
				float hs = vs * 1.05f;
				_highlightMeshInstance.Scale = new Vector3(hs, hs, hs);
				_highlightFill.Scale = new Vector3(hs, hs, hs);

				// Re-shape the highlight to the target's own voxel geometry
				// (cached per voxel so the mesh is only rebuilt when the target
				// changes). Guarded: a bad target (e.g. a mid-rebuild chunk) must
				// never spam the console — on failure we keep the previous mesh.
				try
				{
					EnsureHighlightMesh(_targetVoxel, _targetIsMachine);
				}
				catch (System.Exception e)
				{
					GD.PushWarning($"[VoxelTool] Highlight rebuild failed for voxel {_targetVoxel}: {e.Message}");
				}
			}
		}

		// MACHINE TARGETING: the machine right-click opens is the one the
		// placement preview is anchored to. When the AABB raycast struck a
		// machine (aabbMachine), that is THE machine to open — it matches the
		// highlight/preview exactly, so right-click is always consistent with
		// what the player is seeing. When the AABB raycast missed (the preview
		// is on a solid voxel, or nothing), fall back to the closest machine
		// center in the line of sight so a machine you're roughly aiming at can
		// still be opened (the original behavior).
		_targetMachine = aabbMachine ?? FindTargetMachine(from, dir);

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
				// MACHINE ROTATION CONTROLS (only meaningful while holding a
				// machine item, but harmless to allow always — they change the
				// persistent _currentRotation / _rotationAxis, applied to the
				// next machine and shown in the preview + HUD):
				//
				//   R / Q   : cycle the active rotation AXIS (X → Y → Z → X).
				//   ↑ / → / ↓ / ← : set the ABSOLUTE direction about the active
				//                   axis — Up = 0°, Right = 90°, Down = 180°,
				//                   Left = 270°. (The four arrows are the four
				//                   directions, so every press is unambiguous and
				//                   the ghost never "doesn't change".)
				//   F1      : reset to Identity (0° about any axis).
				//
				// Physical keycodes so it works regardless of layout / IME.
				// The arrow keys are not used for anything else (WASD is
				// movement), so there is no collision.
				if (@event is InputEventKey k && k.Pressed && !k.Echo)
				{
					Key key = k.PhysicalKeycode != Key.None ? k.PhysicalKeycode : k.Keycode;
					switch (key)
					{
						case Key.R:
						case Key.Q:
							CycleRotationAxis();
							break;
						case Key.Up:
							SetMachineRotation(0);   // 0°
							break;
						case Key.Right:
							SetMachineRotation(1);   // 90°
							break;
						case Key.Down:
							SetMachineRotation(2);   // 180°
							break;
						case Key.Left:
							SetMachineRotation(3);   // 270°
							break;
						case Key.F1:
							ResetMachineRotation();
							break;
					}
				}

				if (@event is InputEventKey ckey
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
	/// The active rotation AXIS for the ←/→/↑/↓ controls (X → Y → Z, cycled by
	/// R). This is PERSISTENT like <see cref="_currentRotation"/>: it survives
	/// re-selecting a machine and opening the inventory.
	/// </summary>
	private int _rotationAxis = 0;

	/// <summary>
	/// Cycles the active rotation axis (X → Y → Z → X) and refreshes the
	/// preview so the HUD line updates immediately.
	/// </summary>
	private void CycleRotationAxis()
	{
		_rotationAxis = (_rotationAxis + 1) % 3;
		RefreshRotationPreview();
	}

	/// <summary>
	/// Sets the placement rotation to an ABSOLUTE direction about the active
	/// axis: <paramref name="quadrant"/> 0 = 0° (Identity), 1 = 90°, 2 = 180°,
	/// 3 = 270°. The four arrow keys map one-to-one onto these (↑=0, →=90,
	/// ↓=180, ←=270), so every press produces exactly one unambiguous state.
	/// </summary>
	private void SetMachineRotation(int quadrant)
	{
		_currentRotation = RotationFor(_rotationAxis, quadrant);
		RefreshRotationPreview();
	}

	/// <summary>Resets the placement rotation to Identity (0° about any axis).</summary>
	private void ResetMachineRotation()
	{
		_currentRotation = MachineRotation.Identity;
		RefreshRotationPreview();
	}

	/// <summary>
	/// The direction (quadrant) of the current rotation, in steps of 90°:
	/// Identity = 0, 90° = 1, 180° = 2, 270° = 3.
	/// </summary>
	private static int QuadrantsOf(MachineRotation r) => (int)r % 4;

	/// <summary>
	/// Builds the <see cref="MachineRotation"/> for a given axis (0=X, 1=Y, 2=Z)
	/// and direction in 90° quadrants (0 = Identity, 1 = 90°, 2 = 180°, 3 = 270°).
	/// </summary>
	private static MachineRotation RotationFor(int axis, int quadrants)
	{
		int q = ((quadrants % 4) + 4) % 4;
		switch (axis)
		{
			case 0: return q switch { 0 => MachineRotation.Identity, 1 => MachineRotation.X90, 2 => MachineRotation.X180, _ => MachineRotation.X270 };
			case 1: return q switch { 0 => MachineRotation.Identity, 1 => MachineRotation.Y90, 2 => MachineRotation.Y180, _ => MachineRotation.Y270 };
			default: return q switch { 0 => MachineRotation.Identity, 1 => MachineRotation.Z90, 2 => MachineRotation.Z180, _ => MachineRotation.Z270 };
		}
	}

	/// <summary>
	/// Rebuilds the placement preview immediately after a rotation change so the
	/// R/arrow press feels instant (the next _Process frame would also catch it).
	/// </summary>
	private void RefreshRotationPreview()
	{
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
	/// changed) and from the rotation controls (<see cref="RefreshRotationPreview"/>)
	/// for an instant R / arrow-key response.
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
		int axis = _rotationAxis;

		// The cell must be placeable (in bounds + not occupied) for the ghost to
		// be meaningful; if it isn't, hide the ghost (the placement would fail
		// anyway).
		if (!FactorySim.Machines.MachineSpawner.CanPlaceAt(_world, cell))
		{
			HidePreview();
			return;
		}

		// AXIS LINE FIRST: if only the rotation axis changed (R key) while the
		// ghost stayed in the same cell, re-orient/retint the line in place
		// WITHOUT touching the ghost. Doing this before the early-return (and
		// not folding the axis into the ghost's change-detect) matters: an
		// axis-only change must NOT rebuild the ghost, and the line must be
		// updated on the same frame the axis changes — if we waited for the
		// early-return to fall through, a same-cell axis change would never
		// reach the line update (the ghost itself only changes on arrow keys /
		// cell moves, which is when the line gets updated as a side effect).
		if (_preview != null && _preview.Visible)
		{
			UpdateAxisLine(ghostCenterOf(_preview), axis);
		}

		// Nothing changed since the last frame AND the ghost is currently shown:
		// it is already in the right place with the right rotation AND the axis
		// line already points the right way (it was just updated above). (If the
		// ghost was hidden this frame — e.g. the player briefly lost the target
		// — we still need to re-show it, so require Visible too.)
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

		// The ghost was just (re)spawned: make sure the line is up at the new
		// center with the current axis.
		UpdateAxisLine(ghostCenterOf(ghost), axis);
	}

	/// <summary>
	/// The world-space CENTER of a preview ghost's voxel (voxel corner + half a
	/// voxel along each axis, scaled by VoxelSize) — where the rotation-axis
	/// line is anchored.
	/// </summary>
	private Vector3 ghostCenterOf(Machine m)
	{
		Vector3I v = m.VoxelPos;
		return new Vector3(v.X, v.Y, v.Z) * _world.VoxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
	}

	/// <summary>
	/// Shows / re-orients / retints the placement-preview ROTATION AXIS LINE: a
	/// short bright line from the preview center out along the active rotation
	/// axis (X=red, Y=green, Z=blue), in both directions along that axis. The
	/// line is a sibling of the ghost (same "Machines" root) so it shares its
	/// lifetime. Cheap: it early-returns when the axis, length, and position
	/// are all unchanged.
	/// </summary>
	private void UpdateAxisLine(Vector3 center, int axis)
	{
		float len = _world.VoxelSize * 0.75f; // a bit longer than half a voxel on each side

		if (_axisLine == null)
		{
			_axisLineMesh = new BoxMesh();
			_axisLineMat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				AlbedoColor = new Color(1f, 0f, 0f, 0.9f),
				EmissionEnabled = true,
				Emission = new Color(1f, 0f, 0f, 1f),
				EmissionEnergyMultiplier = 0.6f,
				DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always, // draw through the faint ghost
			};
			_axisLine = new MeshInstance3D
			{
				Name = "PreviewAxisLine",
				Mesh = _axisLineMesh,
				MaterialOverride = _axisLineMat,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // no shadow cost
			};
			// Parent it to the "Machines" root so it shares the ghosts' parent
			// (and the world's transforms, if the root is ever re-parented).
			// A ghost being spawned means the root exists; fall back to the
			// scene root only as a safety net.
			var root = GetTree()?.Root.GetNodeOrNull<Node3D>("Machines");
			if (root != null)
			{
				root.AddChild(_axisLine);
			}
			else
			{
				GetTree()?.Root.AddChild(_axisLine);
			}
			_axisLineAxis = -1;
		}

		bool shapeChanged = _axisLineAxis != axis || Mathf.Abs(_axisLineLen - len) > 1e-5f;
		if (shapeChanged)
		{
			// Orient the box along the active axis: its long dimension is the
			// axis, the other two are thin.
			Vector3 size = axis switch
			{
				0 => new Vector3(len, 0.06f, 0.06f),   // X
				1 => new Vector3(0.06f, len, 0.06f),   // Y
				_ => new Vector3(0.06f, 0.06f, len),   // Z
			};
			_axisLineMesh.Size = size;

			// Axis color: X=red, Y=green, Z=blue.
			Color c = axis switch
			{
				0 => new Color(1f, 0.25f, 0.25f, 0.9f),
				1 => new Color(0.3f, 1f, 0.35f, 0.9f),
				_ => new Color(0.35f, 0.55f, 1f, 0.9f),
			};
			_axisLineMat.AlbedoColor = c;
			_axisLineMat.Emission = new Color(c.R, c.G, c.B, 1f);

			_axisLineAxis = axis;
			_axisLineLen = len;
		}

		_axisLine.Position = center;
	}

	/// <summary>Hides the preview ghost (and its rotation-axis line) without destroying them.</summary>
	private void HidePreview()
	{
		if (_preview != null)
		{
			_preview.Visible = false;
		}
		if (_axisLine != null)
		{
			_axisLine.Visible = false;
		}
	}

	/// <summary>Destroys the preview ghost (queued free) and its rotation-axis line.</summary>
	private void DestroyPreview()
	{
		if (_preview != null)
		{
			_preview.QueueFree();
			_preview = null;
		}
		// Free the axis line IMMEDIATELY (not queued): the line is re-created
		// every time the ghost is rebuilt (arrow key / cell move), and a queued
		// free would leave the old line alive in the tree for another frame —
		// long enough for it to overwrite the freshly built one in the render
		// pass, which made the line "disappear until you move to a new voxel".
		FreeAxisLine();
		_previewCell = default;
		_previewItemId = 0;
		_previewRotation = default;
	}

	/// <summary>
	/// Immediately frees the rotation-axis line (node + mesh + material) and
	/// resets its state. Safe to call when the line is not up.
	/// </summary>
	private void FreeAxisLine()
	{
		if (_axisLine != null)
		{
			_axisLine.Free();
			_axisLine = null;
		}
		if (_axisLineMesh != null)
		{
			_axisLineMesh.Dispose();
			_axisLineMesh = null;
		}
		if (_axisLineMat != null)
		{
			_axisLineMat.Dispose();
			_axisLineMat = null;
		}
		_axisLineAxis = -1;
		_axisLineLen = 0f;
	}


	/// <summary>
	/// Damages the current target over time. The damage rate is
	/// <c>(ToolPower × affinity / material BreakTime) × 100% per second</c>,
	/// where the affinity comes from <see cref="ToolAffinity"/> for the held
	/// tool type (or 1.0 when the held item is a plain material — bare-hand
	/// mining at the base rate).
	///
	/// Damage is accumulated PER TARGETED VOXEL in <see cref="_mineDamage"/> and
	/// stored immediately each frame, so it persists across frames and across
	/// re-aiming: the moment the targeted voxel's total damage reaches its full
	/// health (100%) the voxel breaks (is removed) and its entry is cleared.
	/// This is the source of truth for breaking — the voxel's stored health is
	/// reset to full by <see cref="VoxelWorld.SetVoxel"/> on every edit, so it
	/// can't be read back to decide when to break.
	/// </summary>
	private void ApplyMiningDamage(float dt)
	{
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

		// DROP TARGET: the player is looking at a dropped item. Show the item's
		// name + count (and the distance) so the player knows what it is BEFORE
		// walking up to collect it — mirrors the machine readout. A drop is a
		// small object in an air voxel, so the material readout below would see
		// Air; the drop readout takes priority instead.
		if (_targetDrop != null && !_targetDrop.Stack.IsEmpty)
		{
			info.HasTarget = true;
			info.IsDrop = true;
			info.MachineName = ItemName(_targetDrop.MaterialId); // reuse the name line
			info.MachineDetails = BuildDropDetails(_targetDrop);
			info.Distance = (_targetDrop.GlobalPosition - cameraPos).Length();
			return info;
		}

		// MACHINE TARGET: a machine lives in an AIR voxel, so the material readout
		// (below) would see Air and return early — hiding the panel entirely. When
		// the target IS a machine, build the machine readout (name + live details:
		// power, recipe, progress, buffers) instead, so the player sees the
		// machine's status BEFORE right-clicking to open it.
		if (_targetIsMachine && _targetMachine != null && _targetMachine.Definition != null)
		{
			info.HasTarget = true;
			info.IsMachine = true;
			info.MachineName = BuildMachineName(_targetMachine);
			info.MachineDetails = BuildMachineDetails(_targetMachine);
			// Distance to the machine's center (still useful context).
			Vector3 mCenter = new Vector3(_targetMachine.VoxelPos.X, _targetMachine.VoxelPos.Y, _targetMachine.VoxelPos.Z)
				* _world.VoxelSize + new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
			info.Distance = (mCenter - cameraPos).Length();

			// Placement-rotation readout (only while holding a machine item).
			FillRotationLine(ref info);
			return info;
		}

		int material = _world.GetVoxel(_targetVoxel.X, _targetVoxel.Y, _targetVoxel.Z);
		if (material == VoxelMaterialIds.Air)
		{
			// Not a machine target but the voxel reads as air: a machine might
			// still be in the line of sight (the fallback FindTargetMachine case).
			// Show its readout so the player sees it before right-clicking.
			if (_targetMachine != null && _targetMachine.Definition != null)
			{
				info.HasTarget = true;
				info.IsMachine = true;
				info.MachineName = BuildMachineName(_targetMachine);
				info.MachineDetails = BuildMachineDetails(_targetMachine);
				Vector3 mCenter2 = new Vector3(_targetMachine.VoxelPos.X, _targetMachine.VoxelPos.Y, _targetMachine.VoxelPos.Z)
					* _world.VoxelSize + new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
				info.Distance = (mCenter2 - cameraPos).Length();
				FillRotationLine(ref info);
				return info;
			}
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

		// A machine may also be in the line of sight (the voxel is nearer, but a
		// machine sits behind it within the perpendicular threshold). Show a
		// compact machine line so the player knows there's a machine there.
		if (_targetMachine != null && _targetMachine.Definition != null)
		{
			info.IsMachine = true;
			info.MachineName = BuildMachineName(_targetMachine);
			info.MachineDetails = BuildMachineDetails(_targetMachine);
		}

		FillRotationLine(ref info);
		return info;
	}

	/// <summary>The machine's display name (falls back to its type name).</summary>
	private static string BuildMachineName(Machine m)
	{
		var def = m.Definition;
		if (def != null && !string.IsNullOrEmpty(def.Name))
		{
			return def.Name;
		}
		return m.Type.ToString();
	}

	/// <summary>Fills the placement-rotation readout (only while holding a machine item).</summary>
	private void FillRotationLine(ref TargetInfoUI.TargetInfo info)
	{
		var held = HeldItem;
		if (!held.IsEmpty && !held.IsTool && held.IsMachine)
		{
			string axis = _rotationAxis switch { 0 => "X", 1 => "Y", _ => "Z" };
			int deg = QuadrantsOf(_currentRotation) * 90;
			info.RotationLine = _currentRotation == MachineRotation.Identity
				? "Rotation: 0°  (R: axis  ↑→↓←: direction  F1: reset)"
				: $"Rotation: {axis} {deg}°  (R: axis  ↑→↓←: direction  F1: reset)";
		}
	}

	/// <summary>
	/// Builds the machine's DETAIL ROWS for the target HUD (shown under the
	/// machine name, before the player right-clicks to open it). Each string is
	/// one row. The rows depend on the machine type:
	///   - power consumers: "Power: ✓" or "Power: ✗ needs power"
	///   - power sources:   "Output: 64/s"
	///   - smelters:        recipe (input → output), progress %, buffer contents
	///   - crafters:        recipe name, progress %, buffer contents
	///   - conveyors:       the item on the belt, if any
	/// This is the at-a-glance status the player sees while looking at the
	/// machine, so they know its state before committing to opening it.
	/// </summary>
	private string[] BuildMachineDetails(Machine m)
	{
		var list = new System.Collections.Generic.List<string>();
		var def = m.Definition;
		if (def == null)
		{
			return System.Array.Empty<string>();
		}

		// POWER: consumers show whether they're powered; sources show their output.
		if (def.PowerConsumption > 0f)
		{
			bool powered = PowerNetwork.IsPowered(_world, m.VoxelPos);
			list.Add(powered ? "Power: ✓" : "Power: ✗ needs power");
		}
		else if (def.PowerOutput > 0f)
		{
			list.Add($"Output: {def.PowerOutput:0.#}/s");
		}

		// TYPE-SPECIFIC details.
		if (m is FactorySim.Machines.SmelterMachine smelter)
		{
			var recipe = smelter.CurrentRecipe;
			if (recipe.Input > 0 && recipe.Output > 0)
			{
				list.Add($"Recipe: {ItemName(recipe.Input)} → {ItemName(recipe.Output)}");
				int pct = Mathf.RoundToInt(smelter.SmeltProgress * 100f);
				list.Add($"Progress: {pct}%");
				list.Add($"Input: {BufferSummary(smelter.InputBuffer)}");
				list.Add($"Output: {BufferSummary(smelter.OutputBuffer)}");
			}
			else
			{
				list.Add("No recipe selected");
			}
		}
		else if (m is FactorySim.Machines.CrafterMachine crafter)
		{
			if (crafter.Recipe != null)
			{
				// The recipe's product name (from the material config); show the
				// output count when it's more than one.
				string prod = ItemName(crafter.Recipe.ProductId);
				list.Add($"Recipe: {prod}{(crafter.Recipe.OutputCount > 1 ? $" x{crafter.Recipe.OutputCount}" : "")}");
				int pct = Mathf.RoundToInt(crafter.CraftProgress * 100f);
				list.Add($"Progress: {pct}%");
				list.Add($"Input: {BufferSummary(crafter.InputBuffer)}");
				list.Add($"Output: {BufferSummary(crafter.OutputBuffer)}");
			}
			else
			{
				list.Add("No recipe selected");
			}
		}
		else if (m is FactorySim.Machines.ConveyorMachine conveyor)
		{
			var slot = conveyor.Slot;
			if (slot != null && !slot.IsEmpty)
			{
				list.Add($"Carrying: {ItemName(slot[0].MaterialId)} x{slot[0].Count}");
			}
			else
			{
				list.Add("Belt: empty");
			}
		}

		return list.ToArray();
	}

	/// <summary>
	/// The display name of a material id (via the world's material config), or
	/// "Item N" when the config is missing / the id is unknown. Mirrors the
	/// MachineUI's item-name lookup so the target HUD and the machine panel
	/// show consistent names.
	/// </summary>
	private string ItemName(int materialId)
	{
		if (_world != null && _world.MaterialConfig != null)
		{
			return ItemRegistry.MaterialName(_world.MaterialConfig, materialId);
		}
		return $"Item {materialId}";
	}

	/// <summary>
	/// Builds the detail rows for a dropped item (shown under the item name in
	/// the target HUD, before the player collects it). A drop is just an item
	/// stack, so the only detail is its count: "x N". (The item name itself is
	/// shown in the primary name line via MachineName.)
	/// </summary>
	private string[] BuildDropDetails(FactorySim.Items.VoxelDrop drop)
	{
		return new string[] { $"x {drop.Count}" };
	}

	/// <summary>
	/// A short one-line summary of a machine buffer's contents, e.g. "3 Coal Ore"
	/// or "empty" or "2x (Coal Ore, Iron Plate)". Used by the machine detail rows.
	/// </summary>
	private string BufferSummary(FactorySim.Machines.MachineBuffer buffer)
	{
		if (buffer == null || buffer.IsEmpty)
		{
			return "empty";
		}
		// Collect the distinct materials + total counts.
		var counts = new System.Collections.Generic.Dictionary<int, int>();
		for (int i = 0; i < buffer.Capacity; i++)
		{
			var s = buffer[i];
			if (!s.IsEmpty)
			{
				counts[s.MaterialId] = counts.TryGetValue(s.MaterialId, out int c) ? c + s.Count : s.Count;
			}
		}
		if (counts.Count == 0)
		{
			return "empty";
		}
		// One material: "N Name". Multiple: "Name, Name" (keep it short).
		var parts = new System.Collections.Generic.List<string>();
		foreach (var kv in counts)
		{
			parts.Add($"{kv.Value} {ItemName(kv.Key)}");
		}
		return string.Join(", ", parts);
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
	/// Returns the placed (non-preview) <see cref="Machine"/> whose <see
	/// cref="Machine.VoxelPos"/> equals <paramref name="v"/>, or null when no
	/// machine occupies that voxel. Iterates the shared "Machines" root node
	/// exactly like <see cref="FindTargetMachine"/> / the spawner's occupancy
	/// check do. Machines live in AIR voxels, so a targeted machine cell reads
	/// as air from the world — this lookup is how the target highlight (and any
	/// other air-voxel query) can tell that the cell is really a machine.
	/// </summary>
	private Machine MachineAtVoxel(Vector3I v)
	{
		var machinesRoot = GetTree()?.Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return null;
		}
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (machinesRoot.GetChild(i) is Machine m && !m.IsPreview && m.VoxelPos == v)
			{
				return m;
			}
		}
		return null;
	}

	/// <summary>
	/// Raycasts the camera ray against every placed machine's FULL voxel AABB
	/// and returns the NEAREST one hit (within <see cref="MaxRaycastDistance"/>).
	/// Machines live in AIR voxels (the spawner refuses to place one into a solid
	/// voxel), so the pure DDA voxel walk never hits them — this test is what
	/// makes a machine targetable. <paramref name="hitVoxel"/> comes back as the
	/// machine's voxel; <paramref name="entryAxis"/> comes back as the INDEX of
	/// the face (0=X, 1=Y, 2=Z) the ray ENTERED through — the axis whose slab
	/// boundary produced the entry t. The caller turns that axis into the face's
	/// outward normal (−axis, pointing back toward the camera), and the placement
	/// offset is then hitVoxel + normal = the empty voxel on the far side of that
	/// face. Deriving the face from the slab (not from the entry point) keeps the
	/// normal exact even when the ray enters through a top/bottom face or a
	/// corner, where an entry-point test would be ambiguous. <paramref
	/// name="hitT"/> is the distance along the ray to the entry point. Returns
	/// false when the ray misses every machine.
	/// </summary>
	private bool TryRaycastMachine(Vector3 from, Vector3 dir, out Vector3I hitVoxel, out int entryAxis, out float hitT)
	{
		hitVoxel = default;
		entryAxis = -1;
		hitT = float.PositiveInfinity;

		var machinesRoot = GetTree().Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return false;
		}

		float vs = _world.VoxelSize;
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (!(machinesRoot.GetChild(i) is Machine m) || m.IsPreview)
			{
				continue;
			}
			// The machine's FULL voxel AABB in world space: min = VoxelPos * vs,
			// max = (VoxelPos + 1) * vs.
			Vector3 boxMin = new Vector3(m.VoxelPos.X, m.VoxelPos.Y, m.VoxelPos.Z) * vs;
			Vector3 boxMax = boxMin + new Vector3(vs, vs, vs);

			// Slab-method ray-vs-AABB: the closest t at which the ray is inside
			// all three axis-aligned slabs is the entry t (and the farthest t is
			// the exit t). t1 < 0 < t2 means the box is behind the ray; t1 > t2
			// means the ray misses.
			// Two independent quantities:
			//   entryT  — the true entry parameter (the largest of the three
			//             slab near-boundaries). This can be NEGATIVE when the
			//             ray origin is already inside the box (the player
			//             stands close to / inside the machine voxel, which is
			//             common). Tracked separately from a clamped value so a
			//             negative entry still records the correct entry face.
			//   entryA  — the AXIS whose near-boundary produced entryT: the
			//             face the ray enters through, which the placement
			//             normal is derived from.
			float entryT = float.NegativeInfinity;
			int entryA = -1;
			float tmax = MaxRaycastDistance;
			bool hit = true;
			for (int axis = 0; axis < 3; axis++)
			{
				float d = dir[axis];
				if (Mathf.Abs(d) < 1e-8f)
				{
					// The ray is PARALLEL to this slab: dividing by ~0 would
					// produce ±infinity and corrupt tmin/tmax. The ray is inside
					// this slab for its whole length, so it constrains t at all —
					// but it only reaches the box at all when the origin's
					// coordinate is already within the slab (inclusive with a
					// tiny epsilon). Otherwise the ray runs alongside the box
					// and misses it.
					if (from[axis] < boxMin[axis] - 1e-8f || from[axis] > boxMax[axis] + 1e-8f)
					{
						hit = false;
						break;
					}
					continue;
				}
				float b1 = (boxMin[axis] - from[axis]) / d;
				float b2 = (boxMax[axis] - from[axis]) / d;
				float t1 = Mathf.Min(b1, b2);
				float t2 = Mathf.Max(b1, b2);
				// Record the axis whose near-boundary is the LARGEST — that is
				// the face the ray enters through (the last boundary crossed on
				// the way into the box). Tracked on entryT (NOT a clamped tmin)
				// so it stays correct even when the entry is negative (origin
				// inside the box).
				if (t1 > entryT)
				{
					entryT = t1;
					entryA = axis;
				}
				tmax = Mathf.Min(tmax, t2);
				if (entryT > tmax)
				{
					hit = false;
					break;
				}
			}
			if (!hit || entryT > MaxRaycastDistance)
			{
				continue;
			}

			// A hit whose entry is behind the origin (entryT < 0) is "around"
			// the player; clamp its reported distance to 0 so it still wins
			// nearest-wins over a far voxel.
			float clampedT = Mathf.Max(entryT, 0f);
			if (clampedT < hitT)
			{
				hitT = clampedT;
				hitVoxel = m.VoxelPos;
				entryAxis = entryA;
			}
		}
		return hitT != float.PositiveInfinity;
	}

	/// <summary>
	/// Raycasts the camera ray against every dropped item's small AABB and
	/// returns the NEAREST hit (within <see cref="MaxRaycastDistance"/>). A drop
	/// is a small object (its mesh is a 0.3-unit box), so its hit box is a small
	/// cube (~0.4 units, a bit larger than the mesh for a forgiving target)
	/// centered on the drop's position. This is what makes a drop targetable:
	/// the DDA voxel walk and the machine raycast both miss a drop (it's a small
	/// object in an air voxel), so without this the HUD couldn't show the drop's
	/// item + count while the player is looking at it. <paramref name="drop"/>
	/// comes back as the hit drop; <paramref name="hitT"/> as the ray distance to
	/// its entry point (clamped to ≥ 0 when the origin is inside the box).
	/// Returns false when the ray misses every drop.
	/// </summary>
	private bool TryRaycastDrop(Vector3 from, Vector3 dir, out FactorySim.Items.VoxelDrop drop, out float hitT)
	{
		drop = null;
		hitT = float.PositiveInfinity;

		const float half = 0.2f; // half-extent of the drop's hit box (~0.4 total)
		foreach (var node in GetTree().GetNodesInGroup("voxel_drops"))
		{
			if (!(node is FactorySim.Items.VoxelDrop d) || d.IsQueuedForDeletion())
			{
				continue;
			}
			Vector3 c = d.GlobalPosition;
			Vector3 boxMin = c - new Vector3(half, half, half);
			Vector3 boxMax = c + new Vector3(half, half, half);

			// Slab-method ray-vs-AABB (same technique as the machine raycast):
			// the closest t inside all three slabs is the entry t.
			float entryT = float.NegativeInfinity;
			float tmax = MaxRaycastDistance;
			bool hit = true;
			for (int axis = 0; axis < 3; axis++)
			{
				float dd = dir[axis];
				if (Mathf.Abs(dd) < 1e-8f)
				{
					// Ray parallel to this slab: inside for its whole length only
					// if the origin's coordinate is within the slab.
					if (from[axis] < boxMin[axis] - 1e-8f || from[axis] > boxMax[axis] + 1e-8f)
					{
						hit = false;
						break;
					}
					continue;
				}
				float b1 = (boxMin[axis] - from[axis]) / dd;
				float b2 = (boxMax[axis] - from[axis]) / dd;
				float t1 = Mathf.Min(b1, b2);
				float t2 = Mathf.Max(b1, b2);
				entryT = Mathf.Max(entryT, t1);
				tmax = Mathf.Min(tmax, t2);
				if (entryT > tmax)
				{
					hit = false;
					break;
				}
			}
			if (!hit || entryT > MaxRaycastDistance)
			{
				continue;
			}
			float clampedT = Mathf.Max(entryT, 0f);
			if (clampedT < hitT)
			{
				hitT = clampedT;
				drop = d;
			}
		}
		return drop != null;
	}

	/// <summary>
	/// The distance from <paramref name="from"/> to the STRUCK FACE of the voxel
	/// a voxel raycast hit: the face on the <paramref name="normal"/> side, i.e.
	/// the voxel's world-space center minus half a voxel along the normal. This
	/// is the ray-distance of the voxel hit, used to compare voxel vs. machine
	/// hits nearest-wins.
	/// </summary>
	private float VoxelHitDistance(Vector3 from, Vector3I voxel, Vector3 normal)
	{
		float vs = _world.VoxelSize;
		Vector3 facePoint = new Vector3(voxel.X, voxel.Y, voxel.Z) * vs
			+ new Vector3(0.5f, 0.5f, 0.5f) * vs
			- normal * (vs * 0.5f);
		return (facePoint - from).Length();
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
	///
	/// When <paramref name="isMachine"/> is true the target voxel is AIR with a
	/// machine inside it (machines live in air voxels), so there is no material
	/// to mesh: the highlight falls back to a PLAIN unit-cube outline (all 12
	/// edges) built from a single solid placeholder voxel, plus the translucent
	/// fill box — an unmistakable marker on the machine cell.
	/// </summary>
	private void EnsureHighlightMesh(Vector3I voxel, bool isMachine)
	{
		if (_meshedTarget == voxel && _highlightWireframe != null)
		{
			return;
		}

		int material = _world.GetVoxel(voxel.X, voxel.Y, voxel.Z);
		if (material <= 0 && !isMachine)
		{
			// Target turned to air: show nothing (the next frame hides it too).
			// (A machine target IS an air voxel, so isMachine suppresses the bail.)
			_highlightMeshInstance.Visible = false;
			_highlightFill.Visible = false;
			return;
		}

		// The translucent fill box marks the targeted block and is always shown
		// for a solid target — even when the wireframe has few or no exposed
		// faces — so the highlight is unmistakable.
		_highlightFill.Visible = true;

		if (isMachine)
		{
			// Machine cell (air voxel): there is no material surface to mesh, so
			// build a plain unit-cube outline instead — a 1×1×1 chunk whose
			// single voxel is a solid PLACEHOLDER material (the color doesn't
			// matter; the highlight material overrides it) with a self-contained
			// solid query that is true ONLY for that exact cell. Every one of the
			// six faces is exposed (all neighbors read as air), so BuildVisual
			// emits all 12 cube edges — a clean full-cube outline around the
			// machine cell. This reuses the existing BuildVisual + BuildEdgeMesh
			// path (which the caller's try/catch guards) and ends in the same
			// wireframe-assignment / visibility code as the solid-voxel path.
			material = VoxelMaterialIds.Stone; // any valid solid id; color is overridden
			var cubeData = new ChunkData(1, 1, 1);
			cubeData.Set(0, 0, 0, (byte)material);

			System.Func<int, int, int, bool> cubeSolidQuery = (lx, y, lz) =>
				lx == 0 && lz == 0 && y == 0; // solid ONLY at the target cell, air for every neighbor

			float cubeVs = _world.VoxelSize;
			_highlightPivotWorld = new Vector3(voxel.X, voxel.Y, voxel.Z) * cubeVs
				+ new Vector3(0.5f, 0.5f, 0.5f) * cubeVs;
			System.Func<Vector2I, ChunkData> cubeNoNeighbors = _ => null;
			var cubeFaces = MeshBuilder.BuildVisual(
				cubeData, voxel.X, voxel.Y, 1f, _world.Palette, cubeSolidQuery,
				neighborChunkQuery: cubeNoNeighbors);
			var cubeWire = BuildEdgeMesh(cubeFaces, _highlightPivotWorld);
			_meshedTarget = voxel;
			if (cubeWire != null)
			{
				// Keep the "wireframe is non-null" invariant the cache check at
				// the top of this method relies on (the non-machine path does
				// the same).
				_highlightWireframe = cubeWire;
				_highlightMeshInstance.Mesh = cubeWire;
				_highlightMeshInstance.MaterialOverride = _highlightMaterial;
				_highlightMeshInstance.Visible = true;
			}
			else
			{
				// No exposed faces (shouldn't happen for a fully-exposed cube):
				// hide the wireframe, but the fill box still marks the cell.
				_highlightWireframe = null;
				_highlightMeshInstance.Visible = false;
			}
			_highlight.Visible = true;
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
