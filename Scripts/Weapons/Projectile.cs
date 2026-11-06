using Godot;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// A single flying projectile (a rocket, or one "droplet" of the flamethrower
/// stream). It is a plain <see cref="Node3D"/> (NOT a physics body — moving it by
/// hand along a direction vector is far cheaper and more predictable than
/// integrating a <see cref="RigidBody3D"/> through the physics server, which
/// matters for the flamethrower's high projectile count).
///
/// Each frame it advances along its velocity (optionally arcing under gravity),
/// raycasts the voxel grid a short step ahead, and on the first solid voxel it
/// strikes it applies its damage — a point hit, or a sphere blast for the rocket
/// launcher (see <see cref="ExplosionApplier"/>). It shows a small mesh so the
/// player can SEE the projectile in flight, and a debug line for the rocket's
/// trajectory. It removes itself when it runs out of life or hits something.
///
/// One node is spawned per projectile, parented to the shared "Projectiles"
/// container under the scene root (created by <see cref="ProjectileSpawner"/>),
/// so they survive independently of the player.
/// </summary>
[GlobalClass]
public partial class Projectile : Node3D
{
	/// <summary>The weapon type that launched this projectile (drives speed/gravity/lifetime/damage).</summary>
	public WeaponType OwnerWeapon { get; private set; }

	/// <summary>Current velocity (world units / second).</summary>
	public Vector3 Velocity { get; set; }

	private VoxelWorld _world;
	private float _age;
	private float _lifetime;
	private float _gravity;
	private float _damage;
	private int _blastRadius;
	private bool _exploded;
	private MeshInstance3D _mesh;
	private MeshInstance3D _debugLine;

	// Shared, cheap, unshaded emissive mesh instances (one per kind, created
	// lazily and reused). Each spawned projectile CLONES the mesh (a new
	// MeshInstance3D sharing the same Mesh + material) so it can be freed
	// independently while the material/mesh stay shared.
	private static MeshInstance3D _rocketMeshInstance;
	private static MeshInstance3D _flameMeshInstance;

	private static MeshInstance3D GetSharedMesh(WeaponType type)
	{
		if (type == WeaponType.RocketLauncher && _rocketMeshInstance == null)
		{
			var mat = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.9f, 0.5f, 0.2f),
				EmissionEnabled = true,
				Emission = new Color(1.0f, 0.6f, 0.2f),
				EmissionEnergyMultiplier = 1.5f,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			};
			// A small capsule reads as a rocket.
			var capsule = new CapsuleMesh
			{
				Radius = 0.09f,
				Height = 0.35f,
				Material = mat,
			};
			_rocketMeshInstance = new MeshInstance3D { Mesh = capsule, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		}
		if (type == WeaponType.Flamethrower && _flameMeshInstance == null)
		{
			var mat = new StandardMaterial3D
			{
				AlbedoColor = new Color(1.0f, 0.6f, 0.15f, 0.85f),
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				EmissionEnabled = true,
				Emission = new Color(1.0f, 0.55f, 0.1f),
				EmissionEnergyMultiplier = 1.8f,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			};
			// A small sphere reads as a droplet of flame.
			var sphere = new SphereMesh
			{
				Radius = 0.07f,
				Height = 0.14f,
				Material = mat,
			};
			_flameMeshInstance = new MeshInstance3D { Mesh = sphere, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
		}
		if (type == WeaponType.RocketLauncher)
		{
			return _rocketMeshInstance;
		}
		return _flameMeshInstance;
	}

	public void Initialize(WeaponType weapon, Vector3 from, Vector3 direction, VoxelWorld world, WeaponBehavior config)
	{
		OwnerWeapon = weapon;
		_world = world;
		// Read the projectile's stats from the editable config resource (the same
		// one the WeaponController uses), so inspector tweaks apply to projectiles
		// too. Fall back to a fresh config if null (defensive).
		var cfg = config ?? new WeaponBehavior();
		Gravity = cfg.ProjectileGravity(weapon);
		_lifetime = cfg.ProjectileLifetime(weapon);
		_damage = cfg.DamagePerShot(weapon);
		_blastRadius = cfg.BlastRadius(weapon);
		Velocity = direction.Normalized() * cfg.ProjectileSpeed(weapon);

		// Orient the node so its local +Y (a capsule's long axis) points along
		// the direction of travel. LookAt makes +Y face the target: aiming at a
		// point one unit along the travel direction from the origin does exactly
		// that. (A flame sphere is round, so its orientation is cosmetic.)
		if (direction != Vector3.Zero)
		{
			LookAt(new Vector3(0f, 0f, 0f) + direction.Normalized(), Vector3.Up);
		}

		// Visual mesh (a shared, cheap instance cloned per projectile so each can
		// be freed independently while sharing the material).
		var shared = GetSharedMesh(weapon);
		if (shared != null && shared.Mesh != null)
		{
			_mesh = new MeshInstance3D
			{
				Name = "Mesh",
				Mesh = shared.Mesh,
				MaterialOverride = shared.MaterialOverride,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			};
			AddChild(_mesh);
		}

		// Debug line for the rocket: a thin box along the travel direction so the
		// player can SEE the trajectory (the "visual debug" the user asked for).
		if (weapon == WeaponType.RocketLauncher)
		{
			BuildDebugLine();
		}
	}

	private void BuildDebugLine()
	{
		var mat = new StandardMaterial3D
		{
			AlbedoColor = new Color(1f, 0.8f, 0.2f),
			EmissionEnabled = true,
			Emission = new Color(1f, 0.8f, 0.2f),
			EmissionEnergyMultiplier = 2f,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};
		var box = new BoxMesh { Size = new Vector3(0.03f, 0.6f, 0.03f), Material = mat };
		_debugLine = new MeshInstance3D
		{
			Name = "DebugLine",
			Mesh = box,
			MaterialOverride = mat,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_debugLine);
	}

	public override void _Process(double delta)
	{
		if (_world == null || _exploded)
		{
			return;
		}
		float dt = (float)delta;
		_age += dt;

		// Gravity arc (rockets).
		if (_gravity != 0f)
		{
			Velocity += new Vector3(0f, -_gravity, 0f) * dt;
		}

		Vector3 nextPos = GlobalPosition + Velocity * dt;

		// Raycast a short step ahead to find the voxel we are about to enter.
		Vector3 dir = Velocity.LengthSquared() > 0f ? Velocity / Velocity.Length() : Vector3.Forward;
		if (_world.RaycastVoxels(GlobalPosition, dir, dt * Velocity.Length() + 0.5f, out Vector3I hitVoxel, out Vector3 hitNormal))
		{
			// Move to just before the impact (stop at the voxel face).
			GlobalPosition = nextPos;
			Impact(hitVoxel, hitNormal);
			return;
		}

		GlobalPosition = nextPos;

		// Update the debug line to point along the current velocity.
		if (_debugLine != null && Velocity.LengthSquared() > 0f)
		{
			var travel = Velocity.Normalized();
			// A box's long axis here is local Y; LookAt points local +Y along the
			// travel direction (same trick used for the rocket mesh).
			_debugLine.Position = travel * 0.3f;
			_debugLine.LookAt(_debugLine.GlobalPosition + travel, Vector3.Up);
		}

		if (_age >= _lifetime)
		{
			// Expired: a rocket still explodes at the end of its flight; a flame
			// droplet just burns out.
			if (OwnerWeapon == WeaponType.RocketLauncher)
			{
				Impact(new Vector3I(
				 Mathf.FloorToInt(GlobalPosition.X / _world.VoxelSize),
				 Mathf.FloorToInt(GlobalPosition.Y / _world.VoxelSize),
				 Mathf.FloorToInt(GlobalPosition.Z / _world.VoxelSize)), Vector3.Up);
			}
			else
			{
				RemoveProjectile();
			}
		}
	}

	private void Impact(Vector3I hitVoxel, Vector3 hitNormal)
	{
		_exploded = true;
		// Point damage at the impact voxel, then a sphere blast for the rocket.
		float remaining = _world.DamageVoxel(hitVoxel, _damage);
		if (remaining <= 0f)
		{
			_world.BreakVoxel(hitVoxel);
		}
		if (_blastRadius > 0)
		{
			ExplosionApplier.Apply(_world, hitVoxel, _blastRadius, _damage);
		}
		RemoveProjectile();
	}

	private void RemoveProjectile()
	{
		if (IsQueuedForDeletion())
		{
			return;
		}
		QueueFree();
	}

	private float Gravity { get; set; }
}
