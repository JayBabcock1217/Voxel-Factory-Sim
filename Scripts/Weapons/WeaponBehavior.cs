using Godot;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// The static "brain" of the weapon system. It holds the per-<see cref="WeaponType"/>
/// stats (fire rate, damage, ammo compatibility, projectile vs raycast) and the
/// methods the <see cref="WeaponController"/> (a child of the player, created by
/// <see cref="WeaponSpawner"/>) calls each frame to decide what to do.
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
public static class WeaponBehavior
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
	/// The ammo kind a weapon accepts. A bullet weapon takes only bullets, a
	/// rocket weapon only rockets, and so on. Loading the wrong kind is rejected
	/// by the inventory UI (see the ammo feature).
	/// </summary>
	public static AmmoType AmmoForWeapon(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => AmmoType.Bullet,
		WeaponType.Shotgun        => AmmoType.ShotgunShell,
		WeaponType.RocketLauncher => AmmoType.Rocket,
		WeaponType.Flamethrower   => AmmoType.Fuel,
		_ => AmmoType.Bullet,
	};

	/// <summary>Which firing mode a weapon uses.</summary>
	public static FireMode FireModeFor(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => FireMode.Raycast,
		WeaponType.Shotgun        => FireMode.Raycast,
		WeaponType.RocketLauncher => FireMode.Projectile,
		WeaponType.Flamethrower   => FireMode.Projectile,
		_ => FireMode.Raycast,
	};

	/// <summary>Seconds between shots (rate of fire). Lower = faster.</summary>
	public static float FireInterval(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => 0.09f,  // ~11 shots/sec
		WeaponType.Shotgun        => 0.85f,  // slow, heavy
		WeaponType.RocketLauncher => 1.1f,   // slow, heavy
		WeaponType.Flamethrower   => 0.03f,  // very fast, continuous stream
		_ => 0.25f,
	};

	/// <summary>How many pellets a single trigger pull produces (raycast spread).</summary>
	public static int PelletsPerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Shotgun => 8,
		_ => 1,
	};

	/// <summary>The random angular spread (in degrees) applied to each pellet.</summary>
	public static float PelletSpreadDegrees(WeaponType weapon) => weapon switch
	{
		WeaponType.Shotgun => 7f,
		_ => 0.3f, // a little SMG bloom
	};

	/// <summary>Damage dealt to a voxel per pellet / per projectile impact.</summary>
	public static float DamagePerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Smg            => 34f,   // ~3 pellets to break a 100-health voxel
		WeaponType.Shotgun        => 16f,   // 8 pellets × 16 = 128 (overkill up close)
		WeaponType.RocketLauncher => 100f,  // plus a sphere blast (see BlastRadius)
		WeaponType.Flamethrower   => 4f,    // many small impacts add up
		_ => 20f,
	};

	/// <summary>
	/// For the rocket launcher: the radius (in voxels) of the sphere of damage
	/// applied at the impact point. 0 = point damage only (all other weapons).
	/// The blast damages EVERY voxel in a sphere of this radius around the
	/// impact voxel (see <see cref="ExplosionApplier"/>).
	/// </summary>
	public static int BlastRadius(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => 2,
		_ => 0,
	};

	/// <summary>Max distance (world units) a raycast shot travels before expiring.</summary>
	public static float RaycastRange(WeaponType weapon) => weapon switch
	{
		_ => 60f,
	};

	/// <summary>Projectile speed (world units / second).</summary>
	public static float ProjectileSpeed(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => 26f,
		WeaponType.Flamethrower   => 22f,
		_ => 20f,
	};

	/// <summary>Downward acceleration (m/s²) applied to a projectile. Rockets arc; flame is nearly straight.</summary>
	public static float ProjectileGravity(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => 6f,   // a visible arc
		WeaponType.Flamethrower   => 0f,   // a flat stream
		_ => 0f,
	};

	/// <summary>Max lifetime (seconds) of a projectile before it is removed (and, for a rocket, still explodes).</summary>
	public static float ProjectileLifetime(WeaponType weapon) => weapon switch
	{
		WeaponType.RocketLauncher => 4f,
		WeaponType.Flamethrower   => 0.9f, // flame burns out quickly
		_ => 4f,
	};

	/// <summary>
	/// How many units of ammo a single trigger pull consumes. The flamethrower
	/// drains fuel fast (it is a continuous stream); the others consume one
	/// unit per shot.
	/// </summary>
	public static int AmmoPerShot(WeaponType weapon) => weapon switch
	{
		WeaponType.Flamethrower => 1, // per fast "tick" of the stream
		_ => 1,
	};

	/// <summary>
	/// Whether the weapon fires CONTINUOUSLY while the trigger is held (the
	/// flamethrower) versus a single shot per fresh trigger press (the others).
	/// Continuous weapons fire as fast as <see cref="FireInterval"/> allows while
	/// the button is down AND ammo is loaded.
	/// </summary>
	public static bool IsContinuous(WeaponType weapon) => weapon switch
	{
		WeaponType.Flamethrower => true,
		_ => false,
	};
}
