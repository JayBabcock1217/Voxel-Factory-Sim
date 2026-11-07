using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;
using FactorySim.Machines;
using FactorySim.UI;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// The player's weapon controller. A <see cref="Node3D"/> child of the player
/// (created by <see cref="WeaponSpawner"/>). It owns everything that makes a
/// held weapon *feel* like a weapon:
///
///  * the FIRST-PERSON HELD-ITEM MESH — a simple programmatic mesh that appears
///    in the camera's view when the player selects any item (weapon, tool, or
///    material) in the hotbar, as if they are holding it. Different item kinds
///    get different shapes/colors.
///
///  * the USE/SHOOT ANIMATION — a short programmatic lerp of the held mesh (a
///    recoil kick back-and-down for a weapon, a swing for a tool) played each
///    time the player uses the held item.
///
///  * the AMMO STORE — for each weapon the player owns, the loaded ammo (a
///    single <see cref="ItemStack"/> of the weapon's compatible <see
///    cref="AmmoType"/>). The player loads ammo by moving a stack into the
///    weapon's ammo slot in the inventory UI (see <see cref="LoadAmmo"/>); this
///    class is the authority for how much ammo each weapon has and consumes it
///    on fire.
///
///  * the FIRING LOGIC — each frame it checks whether the held item is a weapon
///    and the primary mouse button is down, respects the weapon's fire rate and
///    ammo, and dispatches to <see cref="WeaponBehavior"/> to actually shoot
///    (raycast damage + tracer, or spawn a projectile).
///
/// This node does NOT own input focus or the mouse — it reads
/// <c>Input.IsMouseButtonPressed</c> and the selected hotbar item. It is the
/// single place that knows "the player is firing weapon X with N ammo left."
/// </summary>
[GlobalClass]
public partial class WeaponController : Node3D
{
	/// <summary>Path to the camera (the held mesh is positioned relative to it).</summary>
	[Export] public NodePath CameraPath { get; set; } = new("../Camera3D");

	/// <summary>Path to the <see cref="VoxelWorld"/> to damage voxels in.</summary>
	[Export] public NodePath VoxelWorldPath { get; set; } = new("../../../Enviornment/VoxelWorld");

	/// <summary>Opacity of the held-item mesh (a faint first-person silhouette).</summary>
	[Export(PropertyHint.Range, "0.05,1,0.05")] public float HeldItemOpacity { get; set; } = 0.85f;

	/// <summary>How far in front of the camera the held item sits (world units).</summary>
	[Export] public float HeldItemDistance { get; set; } = 0.5f;

	/// <summary>
	/// The weapon stats config (per-weapon fire rate, damage, pellets, spread,
	/// blast radius, projectile speed/gravity/lifetime, ammo compatibility,
	/// fire mode, ...). EDITABLE IN THE INSPECTOR: the field is pre-populated
	/// with a fresh <see cref="WeaponBehavior"/> (built-in defaults) so you can
	/// expand its Stats array and tweak any value right in the inspector — no
	/// need to click "New" first. Assign a custom resource to override.
	/// </summary>
	[Export] public WeaponBehavior Config { get; set; } = new();

	private Camera3D _camera;
	private VoxelWorld _world;
	private InventoryUI _ui;
	private WeaponBehavior _config;
	private Inventory _inventory;

	// The held-item mesh (rebuilt when the selected hotbar item changes).
	private MeshInstance3D _heldMesh;
	// The last held item's identity key (its MaterialId, 0 when empty). The mesh
	// is rebuilt only when this key changes, so a plain count change (firing)
	// does not rebuild the mesh every frame.
	private int _lastHeldItemId = -1;

	// The per-hotbar-SLOT ammo store: hotbar slot index (0..HotbarSize-1) ->
	// loaded ammo stack. Keyed by SLOT (not by weapon type) so that two copies
	// of the same weapon in different hotbar slots each have their own,
	// independent ammo — matching the one-ammo-slot-per-hotbar-slot UI. When a
	// weapon is moved between slots, its ammo moves with it (the UI re-keys the
	// store entry on a slot swap; see InventoryUI).
	private readonly Dictionary<int, ItemStack> _ammoBySlot = new();

	// Firing state.
	private float _fireCooldown;
	private bool _wasTriggerDown;

	// One-time diagnostic flags (fire-decision + raycast result).
	private bool _fireDiagLogged;
	private bool _rayDiagLogged;

	/// <summary>RNG for the per-pellet angular spread (raycast weapons).</summary>
	private readonly RandomNumberGenerator _spreadRng = new();

	// Animation state (a simple lerp-driven kick; 0 = idle, >0 = mid-animation).
	private float _animT;       // 0..1 progress through the current animation
	private float _animDuration;
	private float _animKick;    // the peak recoil offset (world units)
	private Vector3 _animDir;   // the direction the kick goes (local space)
	private bool _hasAnimation;

	public override void _Ready()
	{
		// The InventoryUI finds this controller by group to read/write the
		// per-weapon ammo store (the ammo slot below the hotbar).
		AddToGroup("weapon_controller");
		// The Config field is pre-populated with a fresh WeaponBehavior (built-in
		// defaults) by the field initializer, so it is always non-null. If the
		// inspector assigned a custom resource, that is used instead.
		_config = Config;
		_world = GetNodeOrNull<VoxelWorld>(VoxelWorldPath)
			?? (GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld);
		_camera = GetNodeOrNull<Camera3D>(CameraPath);
		// The inventory UI holds the selected hotbar item; resolve lazily (see
		// ResolveServices) because it joins its group after this node's _Ready.
		ResolveServices();

		// The held-item mesh: a child of the CAMERA (not the player body) so it
		// stays fixed in view while the player looks around. It starts hidden.
		_heldMesh = new MeshInstance3D
		{
			Name = "HeldItem",
			Visible = false,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		if (_camera != null)
		{
			// Attach to the camera so it renders in front of the view.
			_camera.AddChild(_heldMesh);
		}
		PositionHeldItem();
	}

	private void ResolveServices()
	{
		_ui ??= GetTree().GetFirstNodeInGroup("inventory_ui") as InventoryUI;
		_inventory ??= GetTree().GetFirstNodeInGroup("inventory") as Inventory;
	}

	private ItemStack HeldItem
	{
		get
		{
			ResolveServices();
			if (_ui != null)
			{
				return _ui.SelectedHotbarItem;
			}
			return ItemStack.From(0);
		}
	}

	/// <summary>The index of the currently selected hotbar slot (0..HotbarSize-1).</summary>
	private int SelectedSlotIndex
	{
		get
		{
			ResolveServices();
			if (_ui != null)
			{
				return Mathf.Clamp(_ui.SelectedHotbarSlot, 0, Inventory.HotbarSize - 1);
			}
			return 0;
		}
	}

	/// <summary>
	/// The active weapon stats config (never null — the Config field is
	/// pre-populated with a fresh WeaponBehavior by its field initializer).
	/// Exposed so the <see cref="ProjectileSpawner"/> / <see cref="Projectile"/>
	/// and the <see cref="InventoryUI"/> can read the same editable values.
	/// </summary>
	public WeaponBehavior ActiveConfig => _config ?? Config;

	// ------------------------------------------------------------------
	// Per-frame: refresh the held mesh + animation, and fire the weapon.
	// ------------------------------------------------------------------
	public override void _Process(double delta)
	{
		float dt = (float)delta;
		if (_world == null)
		{
			return;
		}

		// Keep the held-item mesh in sync with the selected hotbar item.
		UpdateHeldMesh();

		// Advance the use-animation.
		UpdateAnimation(dt);

		// Fire the held weapon (if any).
		_fireCooldown = Mathf.Max(0f, _fireCooldown - dt);
		bool triggerDown = Input.MouseMode == Input.MouseModeEnum.Captured
			&& Input.IsMouseButtonPressed(MouseButton.Left);

		var held = HeldItem;
		int heldSlot = SelectedSlotIndex;
		if (held.IsWeapon && held.WeaponType is { } weapon)
		{
			bool continuous = ActiveConfig.IsContinuous(weapon);
			bool shouldFire = continuous ? triggerDown : (triggerDown && !_wasTriggerDown);
			bool hasAmmo = HasAmmo(heldSlot, weapon);

			// One-time diagnostic: dump the full fire-decision state on the first
			// frame a weapon is held, so we can see WHY it isn't firing (no ammo?
			// cooldown? wrong fire mode?).
			if (!_fireDiagLogged)
			{
				_fireDiagLogged = true;
				GD.Print($"[WeaponFire] held={held.MaterialId} isWeapon={held.IsWeapon} weapon={weapon} " +
					$"slot={heldSlot} ammoCount={AmmoCount(heldSlot)} hasAmmo={hasAmmo} " +
					$"mode={ActiveConfig.FireModeFor(weapon)} ammoType={ActiveConfig.AmmoForWeapon(weapon)} " +
					$"world={_world != null} camera={_camera != null} triggerDown={triggerDown} cooldown={_fireCooldown}");
			}

			if (shouldFire && _fireCooldown <= 0f && hasAmmo)
			{
				// Consume ammo FIRST (so a dry gun can't fire), then shoot. The
				// ammo is keyed by the SELECTED hotbar slot, so firing always
				// drains the slot the weapon is currently in.
				_consumeAmmo(heldSlot, weapon, ActiveConfig.AmmoPerShot(weapon));
				FireWeapon(weapon);
				_fireCooldown = ActiveConfig.FireInterval(weapon);
				StartUseAnimation(weapon);
			}
		}
		else if (held.IsTool)
		{
			// Tools: play a swing animation on a fresh use (the actual mining is
			// done by the VoxelTool; this is the visual "use" animation the user
			// asked for). The VoxelTool mines on a HELD left button, so a fresh
			// press is the right moment to kick.
			if (triggerDown && !_wasTriggerDown && _hasTargetForAnimation())
			{
				StartUseAnimation(held.ToolType.Value);
			}
		}

		_wasTriggerDown = triggerDown;
	}

	private bool _hasTargetForAnimation() => true; // a tool always swings on use

	// ------------------------------------------------------------------
	// Firing
	// ------------------------------------------------------------------
	private void FireWeapon(WeaponType weapon)
	{
		if (_camera == null)
		{
			return;
		}
		Vector3 from = _camera.GlobalPosition;
		Vector3 dir = -_camera.GlobalTransform.Basis.Z; // cameras look down -Z
		var root = GetTree().Root;

		if (ActiveConfig.FireModeFor(weapon) == WeaponBehavior.FireMode.Raycast)
		{
			FireRaycast(weapon, from, dir, root);
		}
		else
		{
			FireProjectile(weapon, from, dir);
		}
	}

	/// <summary>
	/// Raycast (hitscan) firing: for each pellet, cast a ray from the camera,
	/// damage the first solid voxel it strikes, and draw a tracer line so the
	/// player can see where the shot went (the visual debug for raycast weapons).
	/// The tracer originates AT the camera and extends to the impact point (or
	/// the end of the ray if nothing is hit), so the player can see the shot
	/// leaving the gun and going where they are aiming.
	/// </summary>
	private void FireRaycast(WeaponType weapon, Vector3 from, Vector3 dir, Node root)
	{
		int pellets = ActiveConfig.PelletsPerShot(weapon);
		float spreadDeg = ActiveConfig.PelletSpreadDegrees(weapon);
		float damage = ActiveConfig.DamagePerShot(weapon);
		float range = ActiveConfig.RaycastRange(weapon);
		// A bright, visible tracer color (white-blue with a slight glow).
		var tracerColor = new Color(0.85f, 0.9f, 1f, 0.9f);

		for (int i = 0; i < pellets; i++)
		{
			// Add a small random angular spread to the aim direction.
			Vector3 d = dir;
			if (spreadDeg > 0f)
			{
				d = ApplySpread(dir, spreadDeg);
			}

			// Cast the ray FROM THE CAMERA (not from a muzzle offset) so the
			// tracer clearly originates at the player's viewpoint.
			bool hit = _world.RaycastVoxels(from, d, range, out Vector3I hitVoxel, out Vector3 hitNormal);
			// The impact point: the hit voxel's surface, or the end of the ray.
			float vs = _world.VoxelSize;
			Vector3 impact = hit
				? VoxelSurfacePoint(hitVoxel, hitNormal, vs)
				: from + d * range;

			// Draw the tracer a short distance in FRONT of the camera (the ray
			// still casts from the camera for accurate hit detection) so the
			// line doesn't start right at the viewpoint, where it would be
			// sub-pixel and hard to see.
			TracerSpawner.Spawn(root, from + d * 0.5f, impact, tracerColor);

			if (hit)
			{
				float remaining = _world.DamageVoxel(hitVoxel, damage);
				if (remaining <= 0f)
				{
					_world.BreakVoxel(hitVoxel);
				}
				// One-time diagnostic: confirm the hit voxel, the damage applied,
				// and the resulting remaining health.
				if (!_rayDiagLogged)
				{
					_rayDiagLogged = true;
					int mat = _world.GetVoxel(hitVoxel.X, hitVoxel.Y, hitVoxel.Z);
					GD.Print($"[WeaponRaycast] pellet {i}: HIT {hitVoxel} mat={mat} unbreakable={_world.IsUnbreakable(mat)} " +
						$"dmg={damage} remaining={remaining} from={from} dir={d} range={range}");
				}
			}
			else if (!_rayDiagLogged)
			{
				_rayDiagLogged = true;
				GD.Print($"[WeaponRaycast] pellet {i}: MISS from={from} dir={d} range={range}");
			}
		}
	}

	/// <summary>Projectile firing: spawn a projectile along the aim direction.</summary>
	private void FireProjectile(WeaponType weapon, Vector3 from, Vector3 dir)
	{
		// Launch slightly in front of the camera so the mesh doesn't start inside
		// the player's own collision.
		Vector3 launchPos = from + dir * 0.5f;
		ProjectileSpawner.Spawn(weapon, launchPos, dir, _world, ActiveConfig);
	}

	/// <summary>
	/// Applies a random angular spread (in degrees) to a direction: rotates it
	/// by an angle of at most <paramref name="maxDegrees"/> around a random
	/// perpendicular axis (so each pellet of a shotgun splay out slightly).
	/// </summary>
	private Vector3 ApplySpread(Vector3 dir, float maxDegrees)
	{
		// A random perpendicular axis (cross with a random vector, reject near-parallel).
		var random = new Vector3(_spreadRng.RandfRange(-1f, 1f), _spreadRng.RandfRange(-1f, 1f), _spreadRng.RandfRange(-1f, 1f));
		if (random.LengthSquared() < 0.001f)
		{
			random = new Vector3(1f, 0f, 0f);
		}
		Vector3 axis = dir.Cross(random).Normalized();
		if (axis.LengthSquared() < 0.001f)
		{
			axis = dir.Cross(new Vector3(0f, 1f, 0f)).Normalized();
		}
		float angle = Mathf.DegToRad(_spreadRng.RandfRange(-maxDegrees, maxDegrees));
		return dir.Rotated(axis, angle);
	}

	/// <summary>
	/// The world-space point on a voxel's surface where a ray with the given hit
	/// normal meets it (the impact point for a tracer's end). Approximated as
	/// the voxel center offset by half the voxel size along the normal.
	/// </summary>
	private static Vector3 VoxelSurfacePoint(Vector3I voxel, Vector3 normal, float voxelSize)
	{
		var center = new Vector3(voxel.X, voxel.Y, voxel.Z) * voxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * voxelSize;
		return center + normal * (voxelSize * 0.5f);
	}

	// ------------------------------------------------------------------
	// Ammo (keyed by HOTBAR SLOT INDEX, so each slot's ammo is independent)
	// ------------------------------------------------------------------
	/// <summary>
	/// True when the weapon in hotbar slot <paramref name="slot"/> has at least
	/// one unit of its compatible ammo loaded.
	/// </summary>
	public bool HasAmmo(int slot, WeaponType weapon)
	{
		if (_ammoBySlot.TryGetValue(slot, out var stack) && !stack.IsEmpty && stack.AmmoType == ActiveConfig.AmmoForWeapon(weapon))
		{
			return true;
		}
		return false;
	}

	/// <summary>How many units of ammo are loaded in the weapon in hotbar slot <paramref name="slot"/>.</summary>
	public int AmmoCount(int slot)
	{
		return _ammoBySlot.TryGetValue(slot, out var stack) && !stack.IsEmpty ? stack.Count : 0;
	}

	/// <summary>
	/// Loads (or replaces) the ammo in the ammo slot of hotbar slot
	/// <paramref name="slot"/>. Called by the inventory UI when the player moves
	/// a stack of the slot's weapon's compatible <see cref="AmmoType"/> into that
	/// slot's ammo slot. Only compatible ammo is accepted (a bullet weapon
	/// rejects rockets, etc.); incompatible stacks are rejected (the UI bounces
	/// them back).
	/// </summary>
	public bool LoadAmmo(int slot, WeaponType weapon, ItemStack ammo)
	{
		if (ammo == null || !ammo.IsAmmo || ammo.AmmoType != ActiveConfig.AmmoForWeapon(weapon))
		{
			return false; // incompatible ammo
		}
		_ammoBySlot[slot] = ammo;
		NotifyAmmoChanged();
		return true;
	}

	/// <summary>
	/// Takes all loaded ammo out of the ammo slot of hotbar slot
	/// <paramref name="slot"/>. Returns the stack that was loaded (empty if none).
	/// </summary>
	public ItemStack UnloadAmmo(int slot)
	{
		ItemStack loaded = _ammoBySlot.TryGetValue(slot, out var s) ? s : ItemStack.From(0);
		_ammoBySlot.Remove(slot);
		if (!loaded.IsEmpty)
		{
			NotifyAmmoChanged();
		}
		return loaded;
	}

	/// <summary>
	/// Moves the loaded ammo from hotbar slot <paramref name="from"/> to hotbar
	/// slot <paramref name="to"/> (called when the player moves a weapon between
	/// hotbar slots, so the ammo follows the weapon).
	/// </summary>
	public void MoveAmmo(int from, int to)
	{
		if (from == to)
		{
			return;
		}
		if (_ammoBySlot.TryGetValue(from, out var stack) && !stack.IsEmpty)
		{
			_ammoBySlot[to] = stack;
			_ammoBySlot.Remove(from);
			NotifyAmmoChanged();
		}
	}

	private void _consumeAmmo(int slot, WeaponType weapon, int amount)
	{
		if (!_ammoBySlot.TryGetValue(slot, out var stack) || stack.IsEmpty)
		{
			return;
		}
		stack.Count -= amount;
		if (stack.Count <= 0)
		{
			_ammoBySlot.Remove(slot);
		}
		NotifyAmmoChanged();
	}

	/// <summary>Fired whenever any slot's ammo count changes (the UI updates the ammo slots).</summary>
	public event Action AmmoChanged;

	private void NotifyAmmoChanged() => AmmoChanged?.Invoke();

	/// <summary>
	/// Public wrapper to fire the <see cref="AmmoChanged"/> event after a bulk
	/// restore (e.g. loading a save). The UI subscribes to this event to
	/// refresh the ammo slots; a single fire after the bulk restore is enough
	/// (no need to fire per-slot during the restore loop).
	/// </summary>
	public void NotifyAmmoChangedForRestore() => AmmoChanged?.Invoke();

	// ------------------------------------------------------------------
	// Held-item mesh (the first-person "I am holding this item" view)
	// ------------------------------------------------------------------
	private void UpdateHeldMesh()
	{
		var held = HeldItem;
		// The item's MaterialId is globally unique across every item kind
		// (materials 1..299, tools 100-103, machines 200-299, weapons 300-399,
		// ammo 400-499; 0 = empty), so it is a perfect identity key. Rebuild the
		// mesh only when the selected item's identity changes — not on a plain
		// count change (e.g. firing a weapon decrements its ammo, not the item).
		int key = held.MaterialId;
		if (key != _lastHeldItemId)
		{
			_lastHeldItemId = key;
			BuildHeldMesh(held);
		}
		PositionHeldItem();
	}

	private void BuildHeldMesh(ItemStack held)
	{
		if (_heldMesh == null)
		{
			return;
		}
		// Free any existing children (the previous mesh's parts).
		foreach (var child in _heldMesh.GetChildren())
		{
			child.QueueFree();
		}

		if (held.IsEmpty)
		{
			_heldMesh.Visible = false;
			return;
		}
		_heldMesh.Visible = true;

		// Each item is built from one or more POSITIONED parts (e.g. a gun is a
		// body + barrel + stock, each its own primitive with a local transform).
		// One MeshInstance3D per part; the controller node stays a bare pivot
		// positioned in front of the camera. A MeshInstance3D with no
		// CollisionShape is a pure visual: it registers no physics geometry and
		// is never hit by the voxel DDA raycast, so the held item sitting in
		// front of the camera never blocks the player's aim.
		var parts = BuildItemMesh(held);
		if (parts.Count == 0)
		{
			_heldMesh.Visible = false;
			return;
		}
		for (int i = 0; i < parts.Count; i++)
		{
			var (mesh, xform) = parts[i];
			var mi = new MeshInstance3D
			{
				Name = $"ItemShape_{i}",
				Mesh = mesh,
				Transform = xform,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			};
			_heldMesh.AddChild(mi);
		}
	}

	/// <summary>
	/// Builds a simple programmatic, MULTI-PART mesh for the held item. Each
	/// item gets a DISTINCTIVE set of positioned primitives (a gun = body +
	/// barrel + stock; a shovel = handle + blade; etc.) so that switching the
	/// selected hotbar item visibly changes the held mesh — not just its color.
	/// Shapes are deliberately simple (primitive meshes, no imported assets) and
	/// read as "something is in my hand" from a first-person view. Each returned
	/// part is a (mesh, local transform) pair; the mesh has its material baked
	/// in, and the transform places that part relative to the held-item pivot.
	/// </summary>
	private static List<(Mesh Mesh, Transform3D Transform)> BuildItemMesh(ItemStack held)
	{
		var parts = new List<(Mesh, Transform3D)>();

		if (held.IsWeapon)
		{
			AddWeaponMesh(parts, held.WeaponType.Value);
			return parts;
		}
		if (held.IsTool)
		{
			AddToolMesh(parts, held.ToolType.Value);
			return parts;
		}
		if (held.IsAmmo)
		{
			// Ammo: a small box reading as a stack of cartridges.
			parts.Add((new BoxMesh
			{
				Size = new Vector3(0.12f, 0.1f, 0.2f),
				Material = AmmoMaterial(held.AmmoType.Value),
			}, Transform3D.Identity));
			return parts;
		}
		if (held.IsMachine)
		{
			// A machine item: a small cube in the machine's color.
			parts.Add((new BoxMesh
			{
				Size = new Vector3(0.25f, 0.25f, 0.25f),
				Material = MachineMaterial(held.MachineType.Value),
			}, Transform3D.Identity));
			return parts;
		}
		// A placeable material: a small cube tinted with the material's color.
		var mat = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.7f, 0.7f, 0.7f),
			Roughness = 0.7f,
		};
		parts.Add((new BoxMesh { Size = new Vector3(0.22f, 0.22f, 0.22f), Material = mat }, Transform3D.Identity));
		return parts;
	}

	/// <summary>Builds a Transform3D from a degrees euler rotation + translation.</summary>
	private static Transform3D Xform(Vector3 pos, Vector3 rotDeg)
		=> new Transform3D(Basis.FromEuler(rotDeg * 0.017453292f), pos);

	/// <summary>Add a positioned BoxMesh part to <paramref name="parts"/>.</summary>
	private static void AddBox(List<(Mesh, Transform3D)> parts, Vector3 size, Material m, Vector3 pos, Vector3 rotDeg = default)
	{
		parts.Add((new BoxMesh { Size = size, Material = m }, Xform(pos, rotDeg)));
	}

	/// <summary>Add a positioned CylinderMesh part (axis along Y by default) to <paramref name="parts"/>.</summary>
	private static void AddCyl(List<(Mesh, Transform3D)> parts, float radius, float height, Material m, Vector3 pos, Vector3 rotDeg = default)
	{
		parts.Add((new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height, Material = m }, Xform(pos, rotDeg)));
	}

	/// <summary>
	/// Builds the per-weapon multi-part shape. Each weapon is a small assembly of
	/// boxes/cylinders with a silhouette that is recognizable at a glance: the
	/// SMG is compact with a magazine, the shotgun is long with a pump, the
	/// rocket launcher is a fat tube with a fin, and the flamethrower has a
	/// round fuel tank on a hose to a nozzle.
	/// </summary>
	private static void AddWeaponMesh(List<(Mesh, Transform3D)> parts, WeaponType weapon)
	{
		// Orientation: the held-item pivot sits in front of the camera (local
		// -Z). The weapon points FORWARD, i.e. toward -Z (away from the player,
		// into the scene). So the barrel / muzzle extends toward NEGATIVE Z and
		// the stock / grip (near the hand) sit toward POSITIVE Z (closer to the
		// camera). All Z offsets below follow that convention.
		var m = WeaponMaterial(weapon);
		switch (weapon)
		{
			case WeaponType.Smg:
			{
				// Compact body, short barrel, a grip, and a stubby magazine.
				AddBox(parts, new Vector3(0.07f, 0.09f, 0.30f), m, new Vector3(0f, 0f, -0.05f)); // body
				AddBox(parts, new Vector3(0.04f, 0.04f, 0.20f), m, new Vector3(0f, 0.02f, -0.26f)); // barrel
				AddBox(parts, new Vector3(0.04f, 0.10f, 0.05f), m, new Vector3(0f, -0.08f, 0.12f)); // grip
				AddBox(parts, new Vector3(0.035f, 0.12f, 0.05f), m, new Vector3(0f, -0.09f, 0.02f)); // magazine
				break;
			}
			case WeaponType.Shotgun:
			{
				// A long barrel + a pump foregrip + a body + a long stock.
				AddBox(parts, new Vector3(0.06f, 0.07f, 0.55f), m, new Vector3(0f, 0.02f, -0.15f)); // barrel
				AddBox(parts, new Vector3(0.08f, 0.06f, 0.14f), m, new Vector3(0f, 0f, -0.28f)); // pump
				AddBox(parts, new Vector3(0.07f, 0.09f, 0.22f), m, new Vector3(0f, -0.01f, 0.08f)); // body
				AddBox(parts, new Vector3(0.05f, 0.07f, 0.22f), m, new Vector3(0f, -0.02f, 0.26f)); // stock
				break;
			}
			case WeaponType.RocketLauncher:
			{
				// A fat tube (cylinder, axis along Z), a body, a sight, and a fin.
				AddCyl(parts, 0.07f, 0.5f, m, new Vector3(0f, 0.02f, -0.08f), new Vector3(90f, 0f, 0f)); // tube
				AddBox(parts, new Vector3(0.08f, 0.09f, 0.24f), m, new Vector3(0f, -0.02f, 0.12f)); // body
				AddBox(parts, new Vector3(0.03f, 0.05f, 0.04f), m, new Vector3(0f, 0.10f, 0.02f)); // sight
				AddBox(parts, new Vector3(0.02f, 0.10f, 0.06f), m, new Vector3(0f, 0.04f, -0.34f)); // rear fin
				break;
			}
			case WeaponType.Flamethrower:
			{
				// A round fuel tank (cylinder, axis along Y), a body, a hose, a nozzle.
				AddCyl(parts, 0.09f, 0.18f, m, new Vector3(-0.06f, -0.04f, 0.05f)); // fuel tank
				AddBox(parts, new Vector3(0.07f, 0.08f, 0.30f), m, new Vector3(0f, 0f, -0.05f)); // body
				AddCyl(parts, 0.025f, 0.16f, m, new Vector3(0.02f, -0.02f, -0.24f), new Vector3(90f, 0f, 0f)); // hose
				AddCyl(parts, 0.04f, 0.10f, m, new Vector3(0f, 0f, -0.30f), new Vector3(90f, 0f, 0f)); // nozzle
				break;
			}
		}
	}

	/// <summary>
	/// Builds the per-tool multi-part shape. Each tool is a handle plus a
	/// distinctive head: the shovel has a flat blade, the pickaxe a cross head,
	/// the axe a wide head on one side, and the sword a long tapered blade.
	/// </summary>
	private static void AddToolMesh(List<(Mesh, Transform3D)> parts, ToolType tool)
	{
		// Orientation matches the weapons: the head points FORWARD (-Z, into the
		// scene) and the handle extends toward the hand (+Z, toward the camera).
		var m = ToolMaterial(tool);
		// A shared handle (a thin box) for every tool, then a distinctive head.
		AddBox(parts, new Vector3(0.04f, 0.04f, 0.40f), m, new Vector3(0f, -0.02f, 0.05f)); // handle
		switch (tool)
		{
			case ToolType.Shovel:
			{
				// A flat rectangular blade at the front.
				AddBox(parts, new Vector3(0.10f, 0.02f, 0.18f), m, new Vector3(0f, -0.02f, -0.20f));
				break;
			}
			case ToolType.Pickaxe:
			{
				// A horizontal cross head (two prongs) over the handle tip.
				AddBox(parts, new Vector3(0.22f, 0.03f, 0.04f), m, new Vector3(0f, 0.06f, -0.14f));
				AddBox(parts, new Vector3(0.03f, 0.08f, 0.04f), m, new Vector3(0.10f, 0.02f, -0.14f));
				AddBox(parts, new Vector3(0.03f, 0.08f, 0.04f), m, new Vector3(-0.10f, 0.02f, -0.14f));
				break;
			}
			case ToolType.Axe:
			{
				// A wide head on one side of the handle tip.
				AddBox(parts, new Vector3(0.04f, 0.12f, 0.10f), m, new Vector3(0.07f, 0.04f, -0.14f));
				break;
			}
			case ToolType.Sword:
			{
				// A long tapered blade extending from the handle, plus a crossguard.
				AddBox(parts, new Vector3(0.05f, 0.02f, 0.30f), m, new Vector3(0f, 0.0f, -0.20f)); // blade
				AddBox(parts, new Vector3(0.03f, 0.02f, 0.16f), m, new Vector3(0f, 0.0f, -0.34f)); // blade tip
				AddBox(parts, new Vector3(0.10f, 0.03f, 0.04f), m, new Vector3(0f, 0.0f, -0.05f)); // crossguard
				break;
			}
		}
	}

	private static Material WeaponMaterial(WeaponType weapon)
	{
		var color = ItemRegistry.WeaponColor(weapon);
		return new StandardMaterial3D
		{
			AlbedoColor = color,
			Roughness = 0.4f,
			Metallic = 0.3f,
		};
	}

	private static Material ToolMaterial(ToolType tool)
	{
		var color = ItemRegistry.ToolColor(tool);
		return new StandardMaterial3D
		{
			AlbedoColor = color,
			Roughness = 0.6f,
		};
	}

	private static Material AmmoMaterial(AmmoType ammo)
	{
		var color = ItemRegistry.AmmoColor(ammo);
		return new StandardMaterial3D
		{
			AlbedoColor = color,
			Roughness = 0.5f,
		};
	}

	private static Material MachineMaterial(MachineType machine)
	{
		var color = ItemRegistry.MachineColor(machine);
		return new StandardMaterial3D
		{
			AlbedoColor = color,
			Roughness = 0.6f,
		};
	}

	/// <summary>
	/// Positions the held-item mesh in front of the camera (bottom-right of the
	/// view, like a first-person weapon), offset by the current animation kick.
	/// </summary>
	private void PositionHeldItem()
	{
		if (_heldMesh == null || _camera == null)
		{
			return;
		}
		// Local offset: down and to the right of the camera, in front of it.
		var baseOffset = new Vector3(0.28f, -0.24f, -HeldItemDistance);
		// Apply the animation kick (local space).
		Vector3 offset = baseOffset + _animDir * _animKick * (_hasAnimation ? MathF.Sin(_animT * MathF.PI) : 0f);
		_heldMesh.Position = offset;
	}

	// ------------------------------------------------------------------
	// Use animation (a simple recoil / swing)
	// ------------------------------------------------------------------
	private void StartUseAnimation(WeaponType weapon)
	{
		// A weapon kicks back-and-down (recoil).
		_animDuration = 0.18f;
		_animKick = 0.12f;
		_animDir = new Vector3(0f, -0.4f, 1f); // back (+Z, toward the camera) and down
		_animT = 0f;
		_hasAnimation = true;
	}

	private void StartUseAnimation(ToolType tool)
	{
		// A tool swings down (a mining swing).
		_animDuration = 0.25f;
		_animKick = 0.16f;
		_animDir = new Vector3(0f, -1f, 0.3f); // down and slightly forward
		_animT = 0f;
		_hasAnimation = true;
	}

	private void UpdateAnimation(float dt)
	{
		if (!_hasAnimation)
		{
			return;
		}
		_animT += dt / _animDuration;
		if (_animT >= 1f)
		{
			_animT = 0f;
			_animKick = 0f;
			_hasAnimation = false;
		}
	}

	// ------------------------------------------------------------------
	// Ammo persistence (so loaded ammo survives a save/load)
	// ------------------------------------------------------------------
	/// <summary>
	/// Snapshots every slot's loaded ammo as (slotIndex, ammoMaterialId, count)
	/// triples. Keyed by HOTBAR SLOT (not weapon type) so a save restores each
	/// slot's ammo into the same slot.
	/// </summary>
	public (int Slot, int AmmoMaterialId, int Count)[] SnapshotAmmo()
	{
		var list = new List<(int, int, int)>();
		foreach (var (slot, stack) in _ammoBySlot)
		{
			if (!stack.IsEmpty)
			{
				list.Add((slot, stack.MaterialId, stack.Count));
			}
		}
		return list.ToArray();
	}

	/// <summary>Restores a slot's loaded ammo from a snapshot triple.</summary>
	public void RestoreAmmo(int slot, int ammoMaterialId, int count)
	{
		if (count <= 0 || ammoMaterialId == 0)
		{
			return;
		}
		// Rebuild the stack with the correct AmmoType so compatibility checks pass.
		if (Enum.TryParse<AmmoType>(ammoMaterialId.ToString(), out var ammoType))
		{
			_ammoBySlot[slot] = ItemStack.FromAmmo(ammoType, count);
		}
	}

	/// <summary>Clears all loaded ammo (e.g. on a fresh world / save switch).</summary>
	public void ClearAllAmmo()
	{
		_ammoBySlot.Clear();
		NotifyAmmoChanged();
	}

	/// <summary>
	/// The <see cref="ItemStack"/> of ammo currently loaded in the ammo slot of
	/// hotbar slot <paramref name="slot"/> (empty if none).
	/// </summary>
	public ItemStack GetLoadedAmmo(int slot)
	{
		return _ammoBySlot.TryGetValue(slot, out var s) ? s : ItemStack.From(0);
	}
}
