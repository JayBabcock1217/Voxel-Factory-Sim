namespace FactorySim.Items;

/// <summary>
/// The kinds of weapon the player can hold. A weapon is an inventory item (see
/// <see cref="ItemStack.WeaponType"/>); the one in the selected hotbar slot is
/// the active weapon. Each type fires a different kind of shot — raycast
/// (instant hitscan) or projectile (a physical object that flies and explodes) —
/// and consumes a specific kind of <see cref="AmmoType"/> from its loaded ammo
/// slot. The weapon's firing behavior lives in
/// <see cref="FactorySim.Weapons.WeaponBehavior"/>.
///
/// The underlying int values are the weapon material ids
/// (<see cref="FactorySim.Voxel.VoxelMaterialIds.WeaponFirst"/> ..
/// <see cref="FactorySim.Voxel.VoxelMaterialIds.WeaponLast"/>), so a weapon's
/// <see cref="ItemStack.MaterialId"/> is simply <c>(int)WeaponType</c>.
/// </summary>
public enum WeaponType
{
	/// <summary>A rapid-fire hitscan weapon (raycast based, one pellet per shot).</summary>
	Smg = 300,
	/// <summary>A hitscan weapon that fires a spread of pellets per trigger pull (raycast based).</summary>
	Shotgun = 301,
	/// <summary>A projectile weapon that launches an exploding rocket (projectile based).</summary>
	RocketLauncher = 302,
	/// <summary>A continuous-stream weapon that emits a cone of fast "fluid" projectiles (projectile based).</summary>
	Flamethrower = 303,
}
