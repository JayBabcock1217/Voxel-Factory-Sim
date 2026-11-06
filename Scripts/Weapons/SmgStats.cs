using Godot;
using FactorySim.Items;

namespace FactorySim.Weapons;

/// <summary>
/// Tunable stats for the SMG (a rapid-fire hitscan / raycast weapon). Assigned
/// as the <see cref="WeaponBehavior.Smg"/> sub-resource; edit it in the
/// inspector to tune the SMG without recompiling.
/// </summary>
[GlobalClass]
public partial class SmgStats : WeaponStatsBase
{
	/// <summary>Pellets per shot (the SMG is a single-pellet hitscan).</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int PelletsPerShot { get; set; } = 1;

	/// <summary>Random angular spread (degrees) applied to each pellet (SMG bloom).</summary>
	[Export(PropertyHint.Range, "0,90,0.1")] public float PelletSpreadDegrees { get; set; } = 0.3f;

	/// <summary>Max distance (world units) a shot travels before expiring.</summary>
	[Export(PropertyHint.Range, "1,1000,1")] public float RaycastRange { get; set; } = 60f;

	public SmgStats()
	{
		// Defaults matching the previous hardcoded SMG values.
		FireMode = WeaponBehavior.FireMode.Raycast;
		AmmoType = AmmoType.Bullet;
		FireInterval = 0.09f;   // ~11 shots/sec
		DamagePerShot = 34f;    // ~3 shots to break a 100-health voxel
	}
}
