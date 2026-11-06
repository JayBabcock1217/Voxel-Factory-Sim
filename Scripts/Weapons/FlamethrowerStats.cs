using Godot;
using FactorySim.Items;

namespace FactorySim.Weapons;

/// <summary>
/// Tunable stats for the flamethrower (a continuous-stream projectile weapon
/// that emits a flat cone of fast "fluid" projectiles). Assigned as the <see
/// cref="WeaponBehavior.Flamethrower"/> sub-resource; edit it in the inspector
/// to tune the flamethrower without recompiling.
/// </summary>
[GlobalClass]
public partial class FlamethrowerStats : WeaponStatsBase
{
	/// <summary>Projectile speed (world units/sec).</summary>
	[Export(PropertyHint.Range, "0,500,1")] public float ProjectileSpeed { get; set; } = 22f;

	/// <summary>Downward acceleration (m/s²). 0 = a flat stream.</summary>
	[Export(PropertyHint.Range, "0,100,0.5")] public float ProjectileGravity { get; set; } = 0f;

	/// <summary>Max lifetime (seconds) of a flame droplet. Short = burns out fast.</summary>
	[Export(PropertyHint.Range, "0.05,60,0.05")] public float ProjectileLifetime { get; set; } = 0.9f;

	/// <summary>How many units of fuel a single tick consumes.</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int AmmoPerShot { get; set; } = 1;

	/// <summary>Whether the flamethrower fires continuously while the trigger is held.</summary>
	[Export] public bool IsContinuous { get; set; } = true;

	public FlamethrowerStats()
	{
		// Defaults matching the previous hardcoded flamethrower values.
		FireMode = WeaponBehavior.FireMode.Projectile;
		AmmoType = AmmoType.Fuel;
		FireInterval = 0.03f;   // very fast, continuous stream
		DamagePerShot = 4f;     // many small impacts add up
	}
}
