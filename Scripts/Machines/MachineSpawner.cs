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
	/// <paramref name="voxelPos"/>, with the given placement <paramref
	/// name="rotation"/> (the R-key orientation). <paramref name="world"/>
	/// supplies the voxel size and a reference into the scene tree. Returns the
	/// spawned <see cref="Machine"/>, or null when the item id isn't a registered
	/// machine, the cell is out of bounds, or the cell is ALREADY OCCUPIED (see
	/// <see cref="IsCellOccupied"/>).
	/// </summary>
	public static Machine SpawnMachine(VoxelWorld world, int itemId, Vector3I voxelPos,
		MachineRotation rotation = MachineRotation.Identity)
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

		// OCCUPANCY CHECK: a cell can hold at most one machine, and a machine
		// cannot be placed into a cell that already has a solid voxel. This
		// prevents stacking machines in one cell or burying a machine inside
		// terrain. (The preview ghost does not count as occupied — it has no
		// collision and IsPreview is true, so it is excluded below.)
		if (IsCellOccupied(world, voxelPos))
		{
			return null;
		}

		var machine = MachineRegistry.CreateMachine(itemId, voxelPos);
		if (machine == null)
		{
			return null;
		}
		machine.PlacementRotation = rotation;

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

	/// <summary>
	/// True when a machine should NOT be spawned as a transparent PREVIEW at
	/// <paramref name="voxelPos"/> — i.e. when the cell is out of the world's
	/// vertical range or already occupied (by a solid voxel or another machine).
	/// The tool uses this to decide whether to show the ghost. Note this does
	/// NOT check the item id (the tool already knows it's a machine item); it is
	/// the placement-validity half of the preview logic.
	/// </summary>
	public static bool CanPlaceAt(VoxelWorld world, Vector3I voxelPos)
	{
		if (world == null || !world.IsInsideTree())
		{
			return false;
		}
		if (voxelPos.Y < 0 || voxelPos.Y >= world.ChunkHeight)
		{
			return false;
		}
		return !IsCellOccupied(world, voxelPos);
	}

	/// <summary>
	/// True when the world voxel <paramref name="voxelPos"/> is already
	/// occupied — either by a solid voxel (any non-air material) or by another
	/// placed (non-preview) <see cref="Machine"/>. This is the occupancy check
	/// that enforces "one machine per cell, no placing inside blocks".
	/// </summary>
	private static bool IsCellOccupied(VoxelWorld world, Vector3I voxelPos)
	{
		// A solid voxel blocks placement.
		int material = world.GetVoxel(voxelPos.X, voxelPos.Y, voxelPos.Z);
		if (material != FactorySim.Voxel.VoxelMaterialIds.Air)
		{
			return true;
		}

		// A placed (non-preview) machine blocks placement.
		var machinesRoot = world.GetTree().Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return false;
		}
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (machinesRoot.GetChild(i) is Machine m && !m.IsPreview && m.VoxelPos == voxelPos)
			{
				return true;
			}
		}
		return false;
	}
}
