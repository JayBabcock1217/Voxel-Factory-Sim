using Godot;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// The "brain" of the weapon system. A <see cref="Resource"/> (not a static
/// class) so the per-weapon stats are EDITABLE IN THE GODOT INSPECTOR: assign
/// an instance of this resource to the <c>WeaponController.Config</c> export,
/// then tweak every value (fire rate, damage, pellets, spread, blast radius,
/// projectile speed/gravity/lifetime, ammo compatibility, fire mode, ...) in
/// the inspector without recompiling.
///
/// The resource holds one <see cref="WeaponStats"/> entry per
/// <see cref="WeaponType"/> (indexed by the enum value). Each <see
/// cref="WeaponStats"/> is a Godot class whose fields are all <c>[Export]</c>,
/// so they show up as editable rows in the inspector's array editor.
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
///
/// The <see cref="WeaponController"/> owns the default config (created in
/// <see cref="WeaponSpawner"/>); the <see cref="ProjectileSpawner"/> /
/// <see cref="Projectile"/> receive a reference to it at spawn time so a
/// projectile reads its stats from the same editable resource.
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

	/// <summary>
	/// One row of tunable stats for a single weapon. All fields are
	/// <c>[Export]</c> so they are editable in the inspector's array editor.
	/// The array in <see cref="WeaponBehavior.Stats"/> is indexed by
	/// <see cref="WeaponType"/> value (Smg=0, Shotgun=1, RocketLauncher=2,
	/// Flamethrower=3).
	/// </summary>
	[GlobalClass]
	public partial class WeaponStats : GodotObject
	{
		/// <summary>Which firing mode this weapon uses (raycast vs projectile).</summary>
		[Export] public FireMode FireMode { get; set; } = FireMode.Raycast;

		/// <summary>The ammo kind this weapon accepts (type-locked).</summary>
		[Export] public AmmoType AmmoType { get; set; } = AmmoType.Bullet;

		/// <summary>Seconds between shots (rate of fire). Lower = faster.</summary>
		[Export(PropertyHint.Range, "0.01,10,0.01")] public float FireInterval { get; set; } = 0.25f;

		/// <summary>How many pellets a single trigger pull produces (raycast spread).</summary>
		[Export(PropertyHint.Range, "1,64,1")] public int PelletsPerShot { get; set; } = 1;

		/// <summary>The random angular spread (in degrees) applied to each pellet.</summary>
		[Export(PropertyHint.Range, "0,90,0.1")] public float PelletSpreadDegrees { get; set; } = 0f;

		/// <summary>Damage dealt to a voxel per pellet / per projectile impact.</summary>
		[Export(PropertyHint.Range, "0,1000,0.5")] public float DamagePerShot { get; set; } = 20f;

		/// <summary>
		/// For the rocket launcher: the radius (in voxels) of the sphere of
		/// damage applied at the impact point. 0 = point damage only (all other
		/// weapons). The blast damages EVERY voxel in a sphere of this radius
		/// around the impact voxel (see <see cref="ExplosionApplier"/>).
		/// </summary>
		[Export(PropertyHint.Range, "0,16,1")] public int BlastRadius { get; set; } = 0;

		/// <summary>Max distance (world units) a raycast shot travels before expiring.</summary>
		[Export(PropertyHint.Range, "1,1000,1")] public float RaycastRange { get; set; } = 60f;

		/// <summary>Projectile speed (world units / second).</summary>
		[Export(PropertyHint.Range, "0,500,1")] public float ProjectileSpeed { get; set; } = 20f;

		/// <summary>Downward acceleration (m/s²) applied to a projectile. Rockets arc; flame is flat.</summary>
		[Export(PropertyHint.Range, "0,100,0.5")] public float ProjectileGravity { get; set; } = 0f;

		/// <summary>Max lifetime (seconds) of a projectile before it is removed (and, for a rocket, still explodes).</summary>
		[Export(PropertyHint.Range, "0.05,60,0.05")] public float ProjectileLifetime { get; set; } = 4f;

		/// <summary>
		/// How many units of ammo a single trigger pull consumes. The flamethrower
		/// drains fuel fast (a continuous stream); the others consume one unit per
		/// shot.
		/// </summary>
		[Export(PropertyHint.Range, "1,64,1")] public int AmmoPerShot { get; set; } = 1;

		/// <summary>
		/// Whether the weapon fires CONTINUOUSLY while the trigger is held (the
		/// flamethrower) versus a single shot per fresh trigger press (the
		/// others). Continuous weapons fire as fast as <see cref="FireInterval"/>
		/// allows while the button is down AND ammo is loaded.
		/// </summary>
		[Export] public bool IsContinuous { get; set; } = false;
	}

	/// <summary>
	/// One <see cref="WeaponStats"/> per weapon, indexed by <see
	/// cref="WeaponType"/> value. The array is editable in the inspector; each
	/// element expands into its own row of tunable fields. The defaults below
	/// match the previous hardcoded values, so a fresh resource behaves
	/// identically to the old static class.
	/// </summary>
	[Export] public WeaponStats[] Stats { get; set; }

	public WeaponBehavior()
	{
		Stats = new WeaponStats[]
		{
			// Smg (index 0)
			new WeaponStats
			{
				FireMode = FireMode.Raycast,
				AmmoType = AmmoType.Bullet,
				FireInterval = 0.09f,        // ~11 shots/sec
				PelletsPerShot = 1,
				PelletSpreadDegrees = 0.3f,   // a little SMG bloom
				DamagePerShot = 34f,          // ~3 pellets to break a 100-health voxel
				RaycastRange = 60f,
			},
			// Shotgun (index 1)
			new WeaponStats
			{
				FireMode = FireMode.Raycast,
				AmmoType = AmmoType.ShotgunShell,
				FireInterval = 0.85f,         // slow, heavy
				PelletsPerShot = 8,
				PelletSpreadDegrees = 7f,
				DamagePerShot = 16f,          // 8 pellets × 16 = 128 (overkill up close)
				RaycastRange = 60f,
			},
			// RocketLauncher (index 2)
			new WeaponStats
			{
				FireMode = FireMode.Projectile,
				AmmoType = AmmoType.Rocket,
				FireInterval = 1.1f,          // slow, heavy
				DamagePerShot = 100f,         // plus a sphere blast (BlastRadius)
				BlastRadius = 2,
				ProjectileSpeed = 26f,
				ProjectileGravity = 6f,       // a visible arc
				ProjectileLifetime = 4f,
			},
			// Flamethrower (index 3)
			new WeaponStats
			{
				FireMode = FireMode.Projectile,
				AmmoType = AmmoType.Fuel,
				FireInterval = 0.03f,         // very fast, continuous stream
				DamagePerShot = 4f,           // many small impacts add up
				ProjectileSpeed = 22f,
				ProjectileGravity = 0f,       // a flat stream
				ProjectileLifetime = 0.9f,    // flame burns out quickly
				AmmoPerShot = 1,
				IsContinuous = true,
			},
		};
	}

	// ------------------------------------------------------------------
	// Lookup helpers (same names as the old static methods, so call sites
	// change minimally: WeaponBehavior.X(w)  ->  config.X(w)).
	// ------------------------------------------------------------------

	private WeaponStats Get(WeaponType weapon)
	{
		int idx = (int)weapon;
		if (Stats != null && idx >= 0 && idx < Stats.Length && Stats[idx] != null)
		{
			return Stats[idx];
		}
		// Fallback: a safe default so a missing/short array never crashes.
		return new WeaponStats();
	}

	/// <summary>The ammo kind a weapon accepts (type-locked).</summary>
	public AmmoType AmmoForWeapon(WeaponType weapon) => Get(weapon).AmmoType;

	/// <summary>Which firing mode a weapon uses.</summary>
	public FireMode FireModeFor(WeaponType weapon) => Get(weapon).FireMode;

	/// <summary>Seconds between shots (rate of fire).</summary>
	public float FireInterval(WeaponType weapon) => Get(weapon).FireInterval;

	/// <summary>How many pellets a single trigger pull produces.</summary>
	public int PelletsPerShot(WeaponType weapon) => Get(weapon).PelletsPerShot;

	/// <summary>The random angular spread (in degrees) applied to each pellet.</summary>
	public float PelletSpreadDegrees(WeaponType weapon) => Get(weapon).PelletSpreadDegrees;

	/// <summary>Damage dealt to a voxel per pellet / per projectile impact.</summary>
	public float DamagePerShot(WeaponType weapon) => Get(weapon).DamagePerShot;

	/// <summary>The sphere-of-damage radius (voxels) at a rocket's impact; 0 = point.</summary>
	public int BlastRadius(WeaponType weapon) => Get(weapon).BlastRadius;

	/// <summary>Max distance (world units) a raycast shot travels before expiring.</summary>
	public float RaycastRange(WeaponType weapon) => Get(weapon).RaycastRange;

	/// <summary>Projectile speed (world units / second).</summary>
	public float ProjectileSpeed(WeaponType weapon) => Get(weapon).ProjectileSpeed;

	/// <summary>Downward acceleration applied to a projectile.</summary>
	public float ProjectileGravity(WeaponType weapon) => Get(weapon).ProjectileGravity;

	/// <summary>Max lifetime (seconds) of a projectile.</summary>
	public float ProjectileLifetime(WeaponType weapon) => Get(weapon).ProjectileLifetime;

	/// <summary>How many units of ammo a single trigger pull consumes.</summary>
	public int AmmoPerShot(WeaponType weapon) => Get(weapon).AmmoPerShot;

	/// <summary>Whether the weapon fires continuously while the trigger is held.</summary>
	public bool IsContinuous(WeaponType weapon) => Get(weapon).IsContinuous;
}
