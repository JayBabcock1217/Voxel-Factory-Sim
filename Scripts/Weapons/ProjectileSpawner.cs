using Godot;
using FactorySim.Items;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// Spawns <see cref="Projectile"/> nodes. Mirrors the pattern of
/// <see cref="Machines.MachineSpawner"/> and <see cref="Items.DropSpawner"/>:
/// it finds (or creates) a shared "Projectiles" container under the scene root,
/// adds the projectile there (so it survives independently of the player), and
/// initializes it with the launch parameters. Keeping the root at the scene root
/// (like Machines and Drops) means projectiles are not affected by the player's
/// transform and are cleaned up with the world, not the player.
/// </summary>
public static class ProjectileSpawner
{
	/// <summary>
	/// Launches a projectile of <paramref name="weapon"/> type from
	/// <paramref name="from"/> along <paramref name="direction"/> (which need
	/// not be normalized). Returns the new projectile, or null when the world is
	/// unavailable.
	/// </summary>
	public static Projectile Spawn(WeaponType weapon, Vector3 from, Vector3 direction, VoxelWorld world)
	{
		if (world == null || !world.IsInsideTree())
		{
			return null;
		}

		var root = world.GetTree().Root;
		Node3D projectilesRoot = root.GetNodeOrNull<Node3D>("Projectiles");
		if (projectilesRoot == null)
		{
			projectilesRoot = new Node3D { Name = "Projectiles" };
			root.AddChild(projectilesRoot);
		}

		var projectile = new Projectile { Name = "Projectile" };
		projectilesRoot.AddChild(projectile);
		projectile.GlobalPosition = from;
		projectile.Initialize(weapon, from, direction, world);
		return projectile;
	}
}
