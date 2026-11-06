using Godot;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// The "brain" of the weapon system. A <see cref="Resource"/> (not a static
/// class) so the per-weapon stats are EDITABLE IN THE GODOT INSPECTOR. Every
/// tunable value is a flat <c>[Export]</c> field on this resource, grouped by
/// weapon (SMG, Shotgun, RocketLauncher, Flamethrower). Flat fields serialize
/// reliably in .tscn/.tres files (unlike arrays of nested objects), so the
/// values you set in the inspector are saved with the scene and restored on
/// load.
///
/// Assign an instance of this resource to the <c>WeaponController.Config</c>
/// export, then tweak any value in the inspector without recompiling. The
/// <see cref="WeaponController"/> and <see cref="Projectile"/> read their stats
/// from this resource.
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

	// ==================================================================
	// SMG (WeaponType.Smg = 300)
	// ==================================================================
	/// <summary>SMG firing mode (raycast or projectile).</summary>
	[Export] public FireMode SmgFireMode { get; set; } = FireMode.Raycast;
	/// <summary>SMG ammo type (type-locked).</summary>
	[Export] public AmmoType SmgAmmoType { get; set; } = AmmoType.Bullet;
	/// <summary>SMG fire interval (seconds between shots). Lower = faster.</summary>
	[Export(PropertyHint.Range, "0.01,10,0.01")] public float SmgFireInterval { get; set; } = 0.09f;
	/// <summary>SMG pellets per shot.</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int SmgPelletsPerShot { get; set; } = 1;
	/// <summary>SMG pellet spread (degrees).</summary>
	[Export(PropertyHint.Range, "0,90,0.1")] public float SmgPelletSpreadDegrees { get; set; } = 0.3f;
	/// <summary>SMG damage per shot.</summary>
	[Export(PropertyHint.Range, "0,1000,0.5")] public float SmgDamagePerShot { get; set; } = 34f;
	/// <summary>SMG raycast range (world units).</summary>
	[Export(PropertyHint.Range, "1,1000,1")] public float SmgRaycastRange { get; set; } = 60f;

	// ==================================================================
	// Shotgun (WeaponType.Shotgun = 301)
	// ==================================================================
	/// <summary>Shotgun firing mode (raycast or projectile).</summary>
	[Export] public FireMode ShotgunFireMode { get; set; } = FireMode.Raycast;
	/// <summary>Shotgun ammo type (type-locked).</summary>
	[Export] public AmmoType ShotgunAmmoType { get; set; } = AmmoType.ShotgunShell;
	/// <summary>Shotgun fire interval (seconds between shots).</summary>
	[Export(PropertyHint.Range, "0.01,10,0.01")] public float ShotgunFireInterval { get; set; } = 0.85f;
	/// <summary>Shotgun pellets per shot.</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int ShotgunPelletsPerShot { get; set; } = 8;
	/// <summary>Shotgun pellet spread (degrees).</summary>
	[Export(PropertyHint.Range, "0,90,0.1")] public float ShotgunPelletSpreadDegrees { get; set; } = 7f;
	/// <summary>Shotgun damage per pellet.</summary>
	[Export(PropertyHint.Range, "0,1000,0.5")] public float ShotgunDamagePerShot { get; set; } = 16f;
	/// <summary>Shotgun raycast range (world units).</summary>
	[Export(PropertyHint.Range, "1,1000,1")] public float ShotgunRaycastRange { get; set; } = 60f;

	// ==================================================================
	// RocketLauncher (WeaponType.RocketLauncher = 302)
	// ==================================================================
	/// <summary>Rocket launcher firing mode (raycast or projectile).</summary>
	[Export] public FireMode RocketFireMode { get; set; } = FireMode.Projectile;
	/// <summary>Rocket launcher ammo type (type-locked).</summary>
	[Export] public AmmoType RocketAmmoType { get; set; } = AmmoType.Rocket;
	/// <summary>Rocket launcher fire interval (seconds between shots).</summary>
	[Export(PropertyHint.Range, "0.01,10,0.01")] public float RocketFireInterval { get; set; } = 1.1f;
	/// <summary>Rocket launcher damage per shot.</summary>
	[Export(PropertyHint.Range, "0,1000,0.5")] public float RocketDamagePerShot { get; set; } = 100f;
	/// <summary>Rocket launcher blast radius (voxels). 0 = point damage.</summary>
	[Export(PropertyHint.Range, "0,16,1")] public int RocketBlastRadius { get; set; } = 2;
	/// <summary>Rocket launcher projectile speed (world units/sec).</summary>
	[Export(PropertyHint.Range, "0,500,1")] public float RocketProjectileSpeed { get; set; } = 26f;
	/// <summary>Rocket launcher projectile gravity (m/s²).</summary>
	[Export(PropertyHint.Range, "0,100,0.5")] public float RocketProjectileGravity { get; set; } = 6f;
	/// <summary>Rocket launcher projectile lifetime (seconds).</summary>
	[Export(PropertyHint.Range, "0.05,60,0.05")] public float RocketProjectileLifetime { get; set; } = 4f;

	// ==================================================================
	// Flamethrower (WeaponType.Flamethrower = 303)
	// ==================================================================
	/// <summary>Flamethrower firing mode (raycast or projectile).</summary>
	[Export] public FireMode FlameFireMode { get; set; } = FireMode.Projectile;
	/// <summary>Flamethrower ammo type (type-locked).</summary>
	[Export] public AmmoType FlameAmmoType { get; set; } = AmmoType.Fuel;
	/// <summary>Flamethrower fire interval (seconds between ticks). Very fast = continuous stream.</summary>
	[Export(PropertyHint.Range, "0.01,10,0.01")] public float FlameFireInterval { get; set; } = 0.03f;
	/// <summary>Flamethrower damage per tick.</summary>
	[Export(PropertyHint.Range, "0,1000,0.5")] public float FlameDamagePerShot { get; set; } = 4f;
	/// <summary>Flamethrower projectile speed (world units/sec).</summary>
	[Export(PropertyHint.Range, "0,500,1")] public float FlameProjectileSpeed { get; set; } = 22f;
	/// <summary>Flamethrower projectile gravity (m/s²). 0 = flat stream.</summary>
	[Export(PropertyHint.Range, "0,100,0.5")] public float FlameProjectileGravity { get; set; } = 0f;
	/// <summary>Flamethrower projectile lifetime (seconds). Short = burns out quickly.</summary>
	[Export(PropertyHint.Range, "0.05,60,0.05")] public float FlameProjectileLifetime { get; set; } = 0.9f;
	/// <summary>Flamethrower ammo consumed per tick.</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int FlameAmmoPerShot { get; set; } = 1;
	/// <summary>Whether the flamethrower fires continuously while the trigger is held.</summary>
	[Export] public bool FlameIsContinuous { get; set; } = true;

	// ==================================================================
	// Shared defaults (used by weapons that don't have their own field)
	// ==================================================================
	/// <summary>Default raycast range for weapons that don't specify their own.</summary>
	[Export(PropertyHint.Range, "1,1000,1")] public float DefaultRaycastRange { get; set; } = 60f;
	/// <summary>Default projectile speed for weapons that don't specify their own.</summary>
	[Export(PropertyHint.Range, "0,500,1")] public float DefaultProjectileSpeed { get; set; } = 20f;
	/// <summary>Default projectile lifetime for weapons that don't specify their own.</summary>
	[Export(PropertyHint.Range, "0.05,60,0.05")] public float DefaultProjectileLifetime { get; set; } = 4f;

	// ------------------------------------------------------------------
	// Lookup helpers (same names as the old static methods, so call sites
	// change minimally: WeaponBehavior.X(w)  ->  config.X(w)).
	// ------------------------------------------------------------------

	/// <summary>The ammo kind a weapon accepts (type-locked).</summary>
	public AmmoType AmmoForWeapon(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgAmmoType,
		WeaponType.Shotgun        => ShotgunAmmoType,
		WeaponType.RocketLauncher => RocketAmmoType,
		WeaponType.Flamethrower   => FlameAmmoType,
		_ => AmmoType.Bullet,
	};

	/// <summary>Which firing mode a weapon uses.</summary>
	public FireMode FireModeFor(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgFireMode,
		WeaponType.Shotgun        => ShotgunFireMode,
		WeaponType.RocketLauncher => RocketFireMode,
		WeaponType.Flamethrower   => FlameFireMode,
		_ => FireMode.Raycast,
	};

	/// <summary>Seconds between shots (rate of fire).</summary>
	public float FireInterval(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgFireInterval,
		WeaponType.Shotgun        => ShotgunFireInterval,
		WeaponType.RocketLauncher => RocketFireInterval,
		WeaponType.Flamethrower   => FlameFireInterval,
		_ => 0.25f,
	};

	/// <summary>How many pellets a single trigger pull produces.</summary>
	public int PelletsPerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Shotgun => ShotgunPelletsPerShot,
		_ => SmgPelletsPerShot, // SMG = 1; others default to 1
	};

	/// <summary>The random angular spread (in degrees) applied to each pellet.</summary>
	public float PelletSpreadDegrees(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg     => SmgPelletSpreadDegrees,
		WeaponType.Shotgun => ShotgunPelletSpreadDegrees,
		_ => 0f,
	};

	/// <summary>Damage dealt to a voxel per pellet / per projectile impact.</summary>
	public float DamagePerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => SmgDamagePerShot,
		WeaponType.Shotgun        => ShotgunDamagePerShot,
		WeaponType.RocketLauncher => RocketDamagePerShot,
		WeaponType.Flamethrower   => FlameDamagePerShot,
		_ => 20f,
	};

	/// <summary>The sphere-of-damage radius (voxels) at a rocket's impact; 0 = point.</summary>
	public int BlastRadius(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketBlastRadius,
		_ => 0,
	};

	/// <summary>Max distance (world units) a raycast shot travels before expiring.</summary>
	public float RaycastRange(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg     => SmgRaycastRange,
		WeaponType.Shotgun => ShotgunRaycastRange,
		_ => DefaultRaycastRange,
	};

	/// <summary>Projectile speed (world units / second).</summary>
	public float ProjectileSpeed(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketProjectileSpeed,
		WeaponType.Flamethrower   => FlameProjectileSpeed,
		_ => DefaultProjectileSpeed,
	};

	/// <summary>Downward acceleration applied to a projectile.</summary>
	public float ProjectileGravity(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketProjectileGravity,
		WeaponType.Flamethrower   => FlameProjectileGravity,
		_ => 0f,
	};

	/// <summary>Max lifetime (seconds) of a projectile.</summary>
	public float ProjectileLifetime(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => RocketProjectileLifetime,
		WeaponType.Flamethrower   => FlameProjectileLifetime,
		_ => DefaultProjectileLifetime,
	};

	/// <summary>How many units of ammo a single trigger pull consumes.</summary>
	public int AmmoPerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Flamethrower => FlameAmmoPerShot,
		_ => 1,
	};

	/// <summary>Whether the weapon fires continuously while the trigger is held.</summary>
	public bool IsContinuous(WeaponType weapon) => weapon switch
	{
		WeaponType.Flamethrower => FlameIsContinuous,
		_ => false,
	};
}
