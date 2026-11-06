namespace FactorySim.Items;

/// <summary>
/// The kinds of ammo a weapon can load. Ammo is a stackable, consumable
/// inventory item (see <see cref="ItemStack.AmmoType"/>); it is NOT a tool,
/// machine, or placeable material — its only purpose is to be loaded into a
/// compatible weapon's ammo slot and consumed when the weapon fires.
///
/// Each <see cref="WeaponType"/> accepts exactly one kind of ammo (a bullet
/// weapon takes only bullets, a rocket weapon only rockets, and so on). The
/// compatibility mapping lives in <see cref="WeaponBehavior.AmmoForWeapon"/>.
///
/// The underlying int values are the ammo material ids
/// (<see cref="FactorySim.Voxel.VoxelMaterialIds.AmmoFirst"/> ..
/// <see cref="FactorySim.Voxel.VoxelMaterialIds.AmmoLast"/>), so an ammo
/// stack's <see cref="ItemStack.MaterialId"/> is simply <c>(int)AmmoType</c>.
/// </summary>
public enum AmmoType
{
	/// <summary>Bullets for the SMG.</summary>
	Bullet = 400,
	/// <summary>Shotgun shells.</summary>
	ShotgunShell = 401,
	/// <summary>Rockets for the rocket launcher.</summary>
	Rocket = 402,
	/// <summary>Flamethrower fuel canisters.</summary>
	Fuel = 403,
}
