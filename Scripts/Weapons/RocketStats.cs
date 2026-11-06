using Godot;
using FactorySim.Items;

namespace FactorySim.Weapons;

/// <summary>
/// Tunable stats for the rocket launcher (a projectile weapon that launches an
/// exploding rocket with a sphere blast). Assigned as the <see
/// cref="WeaponBehavior.Rocket"/> sub-resource; edit it in the inspector to
/// tune the rocket launcher without recompiling.
/// </summary>
[GlobalClass]
public partial class RocketStats : WeaponStatsBase
{
	/// <summary>Radius (voxels) of the sphere of damage at the impact point. 0 = point.</summary>
	[Export(PropertyHint.Range, "0,16,1")] public int BlastRadius { get; set; } = 2;

	/// <summary>Projectile speed (world units/sec).</summary>
	[Export(PropertyHint.Range, "0,500,1")] public float ProjectileSpeed { get; set; } = 26f;

	/// <summary>Downward acceleration (m/s²). Rockets arc.</summary>
	[Export(PropertyHint.Range, "0,100,0.5")] public float ProjectileGravity { get; set; } = 6f;

	/// <summary>Max lifetime (seconds) of the projectile.</summary>
	[Export(PropertyHint.Range, "0.05,60,0.05")] public float ProjectileLifetime { get; set; } = 4f;

	public RocketStats()
	{
		// Defaults matching the previous hardcoded rocket values.
		FireMode = WeaponBehavior.FireMode.Projectile;
		AmmoType = AmmoType.Rocket;
		FireInterval = 1.1f;    // slow, heavy
		DamagePerShot = 100f;   // plus a sphere blast (BlastRadius)
	}
}
