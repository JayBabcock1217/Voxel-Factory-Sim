using Godot;
using FactorySim.Items;

namespace FactorySim.Weapons;

/// <summary>
/// Tunable stats for the shotgun (a hitscan / raycast weapon that fires a
/// spread of pellets per trigger pull). Assigned as the <see
/// cref="WeaponBehavior.Shotgun"/> sub-resource; edit it in the inspector to
/// tune the shotgun without recompiling.
/// </summary>
[GlobalClass]
public partial class ShotgunStats : WeaponStatsBase
{
	/// <summary>Pellets per shot (the shotgun's signature spread).</summary>
	[Export(PropertyHint.Range, "1,64,1")] public int PelletsPerShot { get; set; } = 8;

	/// <summary>Random angular spread (degrees) applied to each pellet.</summary>
	[Export(PropertyHint.Range, "0,90,0.1")] public float PelletSpreadDegrees { get; set; } = 7f;

	/// <summary>Max distance (world units) a shot travels before expiring.</summary>
	[Export(PropertyHint.Range, "1,1000,1")] public float RaycastRange { get; set; } = 60f;

	public ShotgunStats()
	{
		// Defaults matching the previous hardcoded shotgun values.
		FireMode = WeaponBehavior.FireMode.Raycast;
		AmmoType = AmmoType.ShotgunShell;
		FireInterval = 0.85f;   // slow, heavy
		DamagePerShot = 16f;    // 8 pellets × 16 = 128 (overkill up close)
	}
}
