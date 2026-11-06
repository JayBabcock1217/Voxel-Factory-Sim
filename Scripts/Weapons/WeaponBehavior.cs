using Godot;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// The "brain" of the weapon system. A <see cref="Resource"/> (not a static
/// class) so the per-weapon stats are EDITABLE IN THE GODOT INSPECTOR.
///
/// The config is SPLIT PER WEAPON: this master resource holds four sub-resource
/// slots — <see cref="Smg"/>, <see cref="Shotgun"/>, <see cref="Rocket"/>, and
/// <see cref="Flamethrower"/> — each a small <see cref="WeaponStatsBase"/>
/// resource holding only that weapon's tunable fields. In the inspector each
/// weapon appears as its own clearly-labeled, collapsible section (not one big
/// flat list).
///
/// Each sub-resource is pre-populated with a fresh instance (built-in defaults
/// from its constructor) by the field initializer, so the inspector shows
/// populated values immediately — no need to click "New" first. To customize a
/// weapon, edit its section directly; to swap it for a saved variant, assign a
/// different resource to that slot.
///
/// The <see cref="WeaponController"/> and <see cref="Projectile"/> read their
/// stats from this resource via the lookup methods below.
///
/// Two firing modes:
/// <see cref="FireMode.Raycast"/> — an instant hitscan. A ray is cast from the
/// camera; the first solid voxel it strikes takes damage. A short tracer line
/// is drawn from the muzzle to the impact point so the player can SEE where the
/// shot went (the "visual debug" for raycast weapons).
///
/// <see cref="FireMode.Projectile"/> — a physical object is launched along the
/// camera's facing direction. It flies (straight line, or a slight gravity arc
/// for rockets) and, when it strikes a solid voxel, applies its damage
/// (a point hit, or a small sphere for the rocket launcher).
/// </summary>
[GlobalClass]
public partial class WeaponBehavior : Resource
{
	/// <summary>How a weapon delivers its shot.</summary>
	public enum FireMode
	{
		/// <summary>Instant hitscan (raycast). SMG, shotgun.</summary>
		Raycast,
		/// <summary>A physical object that flies and impacts. Rocket launcher, flamethrower.</summary>
		Projectile,
	}

	// ------------------------------------------------------------------
	// Per-weapon config sub-resources (each is its own inspector section).
	// Pre-populated with fresh instances (built-in defaults) so the inspector
	// shows values immediately; assign a different resource to swap.
	// ------------------------------------------------------------------

	/// <summary>The SMG's tunable stats.</summary>
	[Export] public SmgStats Smg { get; set; } = new();

	/// <summary>The shotgun's tunable stats.</summary>
	[Export] public ShotgunStats Shotgun { get; set; } = new();

	/// <summary>The rocket launcher's tunable stats.</summary>
	[Export] public RocketStats Rocket { get; set; } = new();

	/// <summary>The flamethrower's tunable stats.</summary>
	[Export] public FlamethrowerStats Flamethrower { get; set; } = new();

	// ------------------------------------------------------------------
	// Shared defaults (used by weapons that don't carry their own value).
	// ------------------------------------------------------------------
	/// <summary>Default raycast range for weapons that don't specify their own.</summary>
	[Export(PropertyHint.Range, "1,1000,1")] public float DefaultRaycastRange { get; set; } = 60f;

	// ------------------------------------------------------------------
	// Null-safe accessors (a missing sub-resource falls back to a fresh one).
	// ------------------------------------------------------------------
	private SmgStats SmgSafe => Smg ?? new SmgStats();
	private ShotgunStats ShotgunSafe => Shotgun ?? new ShotgunStats();
	private RocketStats RocketSafe => Rocket ?? new RocketStats();
	private FlamethrowerStats FlameSafe => Flamethrower ?? new FlamethrowerStats();

	// ------------------------------------------------------------------
	// Lookup helpers (same names as the old static methods, so call sites
	// change minimally: WeaponBehavior.X(w)  ->  config.X(w)).
	// ------------------------------------------------------------------

	/// <summary>The ammo kind a weapon accepts (type-locked).</summary>
	public AmmoType AmmoForWeapon(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgSafe.AmmoType,
		WeaponType.Shotgun        => ShotgunSafe.AmmoType,
		WeaponType.RocketLauncher => RocketSafe.AmmoType,
		WeaponType.Flamethrower   => FlameSafe.AmmoType,
		_ => AmmoType.Bullet,
	};

	/// <summary>Which firing mode a weapon uses.</summary>
	public FireMode FireModeFor(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgSafe.FireMode,
		WeaponType.Shotgun        => ShotgunSafe.FireMode,
		WeaponType.RocketLauncher => RocketSafe.FireMode,
		WeaponType.Flamethrower   => FlameSafe.FireMode,
		_ => FireMode.Raycast,
	};

	/// <summary>Seconds between shots (rate of fire).</summary>
	public float FireInterval(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgSafe.FireInterval,
		WeaponType.Shotgun        => ShotgunSafe.FireInterval,
		WeaponType.RocketLauncher => RocketSafe.FireInterval,
		WeaponType.Flamethrower   => FlameSafe.FireInterval,
		_ => 0.25f,
	};

	/// <summary>How many pellets a single trigger pull produces.</summary>
	public int PelletsPerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg     => SmgSafe.PelletsPerShot,
		WeaponType.Shotgun => ShotgunSafe.PelletsPerShot,
		_ => 1,
	};

	/// <summary>The random angular spread (in degrees) applied to each pellet.</summary>
	public float PelletSpreadDegrees(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg     => SmgSafe.PelletSpreadDegrees,
		WeaponType.Shotgun => ShotgunSafe.PelletSpreadDegrees,
		_ => 0f,
	};

	/// <summary>Damage dealt to a voxel per pellet / per projectile impact.</summary>
	public float DamagePerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgSafe.DamagePerShot,
		WeaponType.Shotgun        => ShotgunSafe.DamagePerShot,
		WeaponType.RocketLauncher => RocketSafe.DamagePerShot,
		WeaponType.Flamethrower   => FlameSafe.DamagePerShot,
		_ => 20f,
	};

	/// <summary>The sphere-of-damage radius (voxels) at a rocket's impact; 0 = point.</summary>
	public int BlastRadius(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketSafe.BlastRadius,
		_ => 0,
	};

	/// <summary>Max distance (world units) a raycast shot travels before expiring.</summary>
	public float RaycastRange(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg     => SmgSafe.RaycastRange,
		WeaponType.Shotgun => ShotgunSafe.RaycastRange,
		_ => DefaultRaycastRange,
	};

	/// <summary>Projectile speed (world units / second).</summary>
	public float ProjectileSpeed(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketSafe.ProjectileSpeed,
		WeaponType.Flamethrower   => FlameSafe.ProjectileSpeed,
		_ => 20f,
	};

	/// <summary>Downward acceleration (m/s²) applied to a projectile.</summary>
	public float ProjectileGravity(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketSafe.ProjectileGravity,
		WeaponType.Flamethrower   => FlameSafe.ProjectileGravity,
		_ => 0f,
	};

	/// <summary>Max lifetime (seconds) of a projectile before it is removed.</summary>
	public float ProjectileLifetime(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketSafe.ProjectileLifetime,
		WeaponType.Flamethrower   => FlameSafe.ProjectileLifetime,
		_ => 4f,
	};

	/// <summary>How many units of ammo a single trigger pull consumes.</summary>
	public int AmmoPerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Flamethrower => FlameSafe.AmmoPerShot,
		_ => 1,
	};

	/// <summary>Whether the weapon fires continuously while the trigger is held.</summary>
	public bool IsContinuous(WeaponType weapon) => weapon switch
	{
		WeaponType.Flamethrower => FlameSafe.IsContinuous,
		_ => false,
	};
}
