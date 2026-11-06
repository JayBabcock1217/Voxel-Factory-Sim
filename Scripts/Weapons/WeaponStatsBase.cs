using Godot;
using FactorySim.Items;

namespace FactorySim.Weapons;

/// <summary>
/// Base for the per-weapon config resources. Each concrete subclass
/// (<see cref="SmgStats"/>, <see cref="ShotgunStats"/>, <see
/// cref="RocketStats"/>, <see cref="FlamethrowerStats"/>) is a small
/// <see cref="Resource"/> holding ONLY the tunable fields for one weapon.
/// They are assigned as sub-resources on the master <see
/// cref="WeaponBehavior"/> resource, so in the inspector each weapon appears
/// as its own clearly-labeled, collapsible section (not one big flat list).
///
/// A <see cref="Resource"/> (not a <c>GodotObject</c>) is used because
/// resources serialize reliably as sub-resources in .tscn/.tres files — the
/// values you set in the inspector are saved with the scene and restored on
/// load.
/// </summary>
[GlobalClass]
public partial class WeaponStatsBase : Resource
{
	/// <summary>Which firing mode this weapon uses (raycast vs projectile).</summary>
	[Export] public WeaponBehavior.FireMode FireMode { get; set; } = WeaponBehavior.FireMode.Raycast;

	/// <summary>The ammo kind this weapon accepts (type-locked).</summary>
	[Export] public AmmoType AmmoType { get; set; } = AmmoType.Bullet;

	/// <summary>Seconds between shots (rate of fire). Lower = faster.</summary>
	[Export(PropertyHint.Range, "0.01,10,0.01")] public float FireInterval { get; set; } = 0.25f;

	/// <summary>Damage dealt to a voxel per pellet / per impact.</summary>
	[Export(PropertyHint.Range, "0,1000,0.5")] public float DamagePerShot { get; set; } = 20f;
}
