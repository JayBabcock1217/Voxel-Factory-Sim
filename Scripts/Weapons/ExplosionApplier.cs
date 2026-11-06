using Godot;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// Applies area damage to the voxel world. The rocket launcher uses this to
/// damage EVERY voxel in a small sphere around its impact point (see
/// <see cref="WeaponBehavior.BlastRadius"/>). It works on the voxel world's
/// stored health (<see cref="VoxelWorld.DamageVoxel"/>): a voxel that drops to
/// 0 health is broken (via <see cref="VoxelWorld.BreakVoxel"/>, which fires the
/// <c>VoxelBroken</c> event so the normal drop pipeline still runs).
///
/// Unbreakable voxels (bedrock) are skipped — they can't be damaged or broken,
/// the same rule the miner and <see cref="VoxelWorld.RemoveSphere"/> use.
/// </summary>
public static class ExplosionApplier
{
	/// <summary>
	/// Damages every voxel within <paramref name="radius"/> (inclusive, a sphere
	/// of voxels) of <paramref name="center"/> by <paramref name="amount"/>
	/// health points, and breaks any that reach 0. Returns the number of voxels
	/// actually broken. A radius of 0 damages/breaks just the center voxel.
	/// </summary>
	public static int Apply(VoxelWorld world, Vector3I center, int radius, float amount)
	{
		if (world == null)
		{
			return 0;
		}

		int broken = 0;
		int r = Mathf.Max(0, radius);
		for (int dx = -r; dx <= r; dx++)
		for (int dy = -r; dy <= r; dy++)
		for (int dz = -r; dz <= r; dz++)
		{
			// Only voxels inside the sphere (a true ball, not a cube).
			if (dx * dx + dy * dy + dz * dz > r * r)
			{
				continue;
			}

			var v = new Vector3I(center.X + dx, center.Y + dy, center.Z + dz);
			int material = world.GetVoxel(v.X, v.Y, v.Z);
			if (material == VoxelMaterialIds.Air || world.IsUnbreakable(material))
			{
				continue;
			}

			float remaining = world.DamageVoxel(v, amount);
			if (remaining <= 0f)
			{
				world.BreakVoxel(v);
				broken++;
			}
		}
		return broken;
	}
}
