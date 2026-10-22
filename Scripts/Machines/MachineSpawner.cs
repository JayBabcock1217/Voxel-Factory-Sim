using Godot;
using FactorySim.Voxel;

namespace FactorySim.Machines;

/// <summary>
/// Spawns <see cref="Machine"/> nodes into the world — the machine counterpart
/// of <see cref="FactorySim.Items.DropSpawner"/>. All machines are parented to
/// a shared "Machines" container node (created on demand if missing), so they
/// are all siblings and can be found/cleared from one place (e.g. when a world
/// slot is unloaded, or for the save system later).
///
/// The spawner is the only place a machine is turned into a live node: it asks
/// the <see cref="MachineRegistry"/> for the right node, parents it to the
/// shared container, positions it at its world voxel, and calls
/// <see cref="Machine.Initialize"/> to build the visual + start its behavior.
/// </summary>
public static class MachineSpawner
{
	/// <summary>
	/// Spawns a machine for <paramref name="itemId"/> at the world voxel
	/// <paramref name="voxelPos"/>. <paramref name="world"/> supplies the voxel
	/// size and a reference into the scene tree. Returns the spawned
	/// <see cref="Machine"/>, or null when the item id isn't a registered
	/// machine (the caller — the place-ability — then falls back to a normal
	/// voxel place, or does nothing).
	/// </summary>
	public static Machine SpawnMachine(VoxelWorld world, int itemId, Vector3I voxelPos)
	{
		if (world == null || !world.IsInsideTree())
		{
			return null;
		}
		if (!MachineRegistry.IsMachineItemId(itemId))
		{
			return null;
		}

		// Bounds check (mirrors VoxelWorld.PlaceVoxel): only place within the
		// world's vertical range. XZ is unbounded (the world is infinite in XZ).
		if (voxelPos.Y < 0 || voxelPos.Y >= world.ChunkHeight)
		{
			return null;
		}

		var machine = MachineRegistry.CreateMachine(itemId, voxelPos);
		if (machine == null)
		{
			return null;
		}

		// Find (or create) the shared "Machines" container under the root,
		// exactly like DropSpawner does for "Drops".
		var root = world.GetTree().Root;
		Node3D machinesRoot = root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			machinesRoot = new Node3D { Name = "Machines" };
			root.AddChild(machinesRoot);
		}

		machinesRoot.AddChild(machine);
		machine.Name = $"Machine_{machine.Type}_{voxelPos.X}_{voxelPos.Y}_{voxelPos.Z}";

		// Initialize AFTER it is in the tree (GlobalPosition needs a tree).
		var def = MachineRegistry.GetByItemId(itemId);
		machine.Initialize(def, voxelPos, world.VoxelSize);

		return machine;
	}
}
