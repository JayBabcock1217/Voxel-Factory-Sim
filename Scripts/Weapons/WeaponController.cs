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

	private Camera3D _camera;
	private VoxelWorld _world;
	private InventoryUI _ui;
	private Inventory _inventory;

	// The held-item mesh (rebuilt when the selected hotbar item changes).
	private MeshInstance3D _heldMesh;
	private ItemStack _lastHeldItem = ItemStack.From(0);
	private int _lastHeldWeaponId = -1;

	// The per-weapon ammo store: weapon MaterialId -> loaded ammo stack.
	private readonly Dictionary<int, ItemStack> _ammoByWeapon = new();

	// Firing state.
	private float _fireCooldown;
	private bool _wasTriggerDown;

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
		if (held.IsWeapon && held.WeaponType is { } weapon)
		{
			bool continuous = WeaponBehavior.IsContinuous(weapon);
			bool shouldFire = continuous ? triggerDown : (triggerDown && !_wasTriggerDown);

			if (shouldFire && _fireCooldown <= 0f && HasAmmo(weapon))
			{
				// Consume ammo FIRST (so a dry gun can't fire), then shoot.
				_consumeAmmo(weapon, WeaponBehavior.AmmoPerShot(weapon));
				FireWeapon(weapon);
				_fireCooldown = WeaponBehavior.FireInterval(weapon);
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

		if (WeaponBehavior.FireModeFor(weapon) == WeaponBehavior.FireMode.Raycast)
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
	/// </summary>
	private void FireRaycast(WeaponType weapon, Vector3 from, Vector3 dir, Node root)
	{
		int pellets = WeaponBehavior.PelletsPerShot(weapon);
		float spreadDeg = WeaponBehavior.PelletSpreadDegrees(weapon);
		float damage = WeaponBehavior.DamagePerShot(weapon);
		float range = WeaponBehavior.RaycastRange(weapon);
		var tracerColor = new Color(0.9f, 0.95f, 1f);

		for (int i = 0; i < pellets; i++)
		{
			// Add a small random angular spread to the aim direction.
			Vector3 d = dir;
			if (spreadDeg > 0f)
			{
				d = ApplySpread(dir, spreadDeg);
			}

			bool hit = _world.RaycastVoxels(from, d, range, out Vector3I hitVoxel, out Vector3 hitNormal);
			// The impact point: the hit voxel's surface, or the end of the ray.
			float vs = _world.VoxelSize;
			Vector3 impact = hit
				? VoxelSurfacePoint(hitVoxel, hitNormal, vs)
				: from + d * range;

			// Draw the tracer (muzzle to impact) — the visual debug.
			Vector3 muzzle = from + d * 0.4f;
			TracerSpawner.Spawn(root, muzzle, impact, tracerColor);

			if (hit)
			{
				float remaining = _world.DamageVoxel(hitVoxel, damage);
				if (remaining <= 0f)
				{
					_world.BreakVoxel(hitVoxel);
				}
			}
		}
	}

	/// <summary>Projectile firing: spawn a projectile along the aim direction.</summary>
	private void FireProjectile(WeaponType weapon, Vector3 from, Vector3 dir)
	{
		// Launch slightly in front of the camera so the mesh doesn't start inside
		// the player's own collision.
		Vector3 launchPos = from + dir * 0.5f;
		ProjectileSpawner.Spawn(weapon, launchPos, dir, _world);
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
	// Ammo
	// ------------------------------------------------------------------
	/// <summary>True when the weapon has at least one unit of its compatible ammo loaded.</summary>
	public bool HasAmmo(WeaponType weapon)
	{
		int id = (int)weapon;
		if (_ammoByWeapon.TryGetValue(id, out var stack) && !stack.IsEmpty && stack.AmmoType == WeaponBehavior.AmmoForWeapon(weapon))
		{
			return true;
		}
		return false;
	}

	/// <summary>How many units of ammo are currently loaded in the weapon.</summary>
	public int AmmoCount(WeaponType weapon)
	{
		int id = (int)weapon;
		return _ammoByWeapon.TryGetValue(id, out var stack) && !stack.IsEmpty ? stack.Count : 0;
	}

	/// <summary>
	/// Loads (or replaces) the ammo in a weapon's ammo slot. Called by the
	/// inventory UI when the player moves a stack of the weapon's compatible
	/// <see cref="AmmoType"/> into the weapon's ammo slot. Only compatible ammo
	/// is accepted (a bullet weapon rejects rockets, etc.); incompatible stacks
	/// are rejected (the UI bounces them back).
	/// </summary>
	public bool LoadAmmo(WeaponType weapon, ItemStack ammo)
	{
		if (ammo == null || !ammo.IsAmmo || ammo.AmmoType != WeaponBehavior.AmmoForWeapon(weapon))
		{
			return false; // incompatible ammo
		}
		_ammoByWeapon[(int)weapon] = ammo;
		NotifyAmmoChanged();
		return true;
	}

	/// <summary>
	/// Takes all loaded ammo out of a weapon's ammo slot (when the player
	/// removes it). Returns the stack that was loaded (empty if none).
	/// </summary>
	public ItemStack UnloadAmmo(WeaponType weapon)
	{
		int id = (int)weapon;
		ItemStack loaded = _ammoByWeapon.TryGetValue(id, out var s) ? s : ItemStack.From(0);
		_ammoByWeapon.Remove(id);
		if (!loaded.IsEmpty)
		{
			NotifyAmmoChanged();
		}
		return loaded;
	}

	private void _consumeAmmo(WeaponType weapon, int amount)
	{
		int id = (int)weapon;
		if (!_ammoByWeapon.TryGetValue(id, out var stack) || stack.IsEmpty)
		{
			return;
		}
		stack.Count -= amount;
		if (stack.Count <= 0)
		{
			_ammoByWeapon.Remove(id);
		}
		NotifyAmmoChanged();
	}

	/// <summary>Fired whenever any weapon's ammo count changes (the UI updates the ammo slots).</summary>
	public event Action AmmoChanged;

	private void NotifyAmmoChanged() => AmmoChanged?.Invoke();

	// ------------------------------------------------------------------
	// Held-item mesh (the first-person "I am holding this item" view)
	// ------------------------------------------------------------------
	private void UpdateHeldMesh()
	{
		var held = HeldItem;
		int key = held.IsEmpty ? 0 : (held.IsWeapon ? (int)held.WeaponType.Value
			: held.IsTool ? (int)held.ToolType.Value
			: held.IsMachine ? (int)held.MachineType.Value
			: held.IsAmmo ? (int)held.AmmoType.Value
			: held.MaterialId);

		// Rebuild the mesh only when the held item's KIND changes (not every
		// frame, and not on a plain count change).
		if (key != _lastHeldWeaponId)
		{
			_lastHeldWeaponId = key;
			_lastHeldItem = held;
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
		// Free any existing children (the previous mesh).
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

		var (mesh, material) = BuildItemMesh(held);
		if (mesh == null)
		{
			_heldMesh.Visible = false;
			return;
		}
		// A child MeshInstance holds the shape; the controller node stays a bare
		// pivot positioned in front of the camera.
		var mi = new MeshInstance3D
		{
			Name = "ItemShape",
			Mesh = mesh,
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		// A MeshInstance3D with no CollisionShape is a pure visual: it registers
		// no physics geometry and is never hit by the voxel DDA raycast, so the
		// held item sitting in front of the camera never blocks the player's aim.
		_heldMesh.AddChild(mi);
	}

	/// <summary>
	/// Builds a simple programmatic mesh for the held item. Weapons get a
	/// gun-like shape (a body box + a barrel), tools a handle + head, and
	/// materials a small cube of the material's color. The shape is deliberately
	/// simple (primitive meshes, no imported assets) and reads as "something is
	/// in my hand" from a first-person view.
	/// </summary>
	private static (Mesh Mesh, Material Material) BuildItemMesh(ItemStack held)
	{
		// A multi-mesh (a body + a detail) is overkill for a held view; a single
		// representative primitive per kind is cheap and readable.
		if (held.IsWeapon)
		{
			// A gun: an elongated box (the body) reading as a weapon in the hand.
			var body = new BoxMesh
			{
				Size = new Vector3(0.08f, 0.12f, 0.5f),
				Material = WeaponMaterial(held.WeaponType.Value),
			};
			return (body, null);
		}
		if (held.IsTool)
		{
			// A tool: a handle (a thin box) reading as a tool.
			var handle = new BoxMesh
			{
				Size = new Vector3(0.05f, 0.05f, 0.55f),
				Material = ToolMaterial(held.ToolType.Value),
			};
			return (handle, null);
		}
		if (held.IsAmmo)
		{
			// Ammo: a small box reading as a stack of cartridges.
			var box = new BoxMesh
			{
				Size = new Vector3(0.12f, 0.1f, 0.2f),
				Material = AmmoMaterial(held.AmmoType.Value),
			};
			return (box, null);
		}
		if (held.IsMachine)
		{
			// A machine item: a small cube in the machine's color.
			var machineCube = new BoxMesh
			{
				Size = new Vector3(0.25f, 0.25f, 0.25f),
				Material = MachineMaterial(held.MachineType.Value),
			};
			return (machineCube, null);
		}
		// A placeable material: a small cube tinted with the material's color.
		var mat = new StandardMaterial3D
		{
			AlbedoColor = new Color(0.7f, 0.7f, 0.7f),
			Roughness = 0.7f,
		};
		var materialCube = new BoxMesh { Size = new Vector3(0.22f, 0.22f, 0.22f), Material = mat };
		return (materialCube, null);
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
	/// <summary>Snapshots every weapon's loaded ammo as (weaponId, ammoMaterialId, count) triples.</summary>
	public (int WeaponId, int AmmoMaterialId, int Count)[] SnapshotAmmo()
	{
		var list = new List<(int, int, int)>();
		foreach (var (id, stack) in _ammoByWeapon)
		{
			if (!stack.IsEmpty)
			{
				list.Add((id, stack.MaterialId, stack.Count));
			}
		}
		return list.ToArray();
	}

	/// <summary>Restores a weapon's loaded ammo from a snapshot triple.</summary>
	public void RestoreAmmo(int weaponId, int ammoMaterialId, int count)
	{
		if (count <= 0 || ammoMaterialId == 0)
		{
			return;
		}
		// Rebuild the stack with the correct AmmoType so compatibility checks pass.
		if (Enum.TryParse<AmmoType>(ammoMaterialId.ToString(), out var ammoType))
		{
			_ammoByWeapon[weaponId] = ItemStack.FromAmmo(ammoType, count);
		}
	}

	/// <summary>Clears all loaded ammo (e.g. on a fresh world / save switch).</summary>
	public void ClearAllAmmo()
	{
		_ammoByWeapon.Clear();
		NotifyAmmoChanged();
	}

	/// <summary>The <see cref="ItemStack"/> of ammo currently loaded in a weapon (empty if none).</summary>
	public ItemStack GetLoadedAmmo(WeaponType weapon)
	{
		int id = (int)weapon;
		return _ammoByWeapon.TryGetValue(id, out var s) ? s : ItemStack.From(0);
	}
}
