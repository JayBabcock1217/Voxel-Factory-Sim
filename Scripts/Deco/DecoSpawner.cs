using Godot;
using FactorySim.Voxel;

namespace FactorySim.Deco;

/// <summary>
/// Spawns <see cref="DecoVoxel"/> nodes into the world — the spawner for all
/// non-terrain voxels (grass, walls, doors, windows, and any future
/// structural/decorative object). Mirrors <see cref="Machines.MachineSpawner"/>
/// but is lighter-weight (no machine definitions, no behavior registry — just
/// a simple factory for the DecoVoxel node).
///
/// All DecoVoxels are parented to a shared "DecoVoxels" container (created on
/// demand by VoxelWorld.EnsureDecoRoot), so they are all siblings and can be
/// found/cleared from one place (e.g. when a world slot is unloaded).
///
/// The spawner is the only place a DecoVoxel is turned into a live node: it
/// creates the right node (via the <see cref="CreateDeco"/> factory), parents
/// it to the shared container, positions it at its world voxel, and calls
/// <see cref="DecoVoxel.Initialize"/> to build the visual.
/// </summary>
public static class DecoSpawner
{
	/// <summary>
	/// Spawns a DecoVoxel of the given <paramref name="type"/> at the world
	/// voxel <paramref name="voxelPos"/>. <paramref name="world"/> supplies the
	/// voxel size and a reference into the scene tree. <paramref
	/// name="dropItemId"/> is the item the DecoVoxel drops when broken (0 = no
	/// drop). <paramref name="preview"/> is true for the placement ghost (no
	/// collision, transparent material, not counted for occupancy).
	///
	/// Returns the spawned <see cref="DecoVoxel"/>, or null when the cell is
	/// out of bounds or already occupied (by a solid voxel or another
	/// DecoVoxel).
	/// </summary>
	public static DecoVoxel SpawnDeco(
		VoxelWorld world,
		DecoType type,
		Vector3I voxelPos,
		int dropItemId = 0,
		bool preview = false)
	{
		if (world == null || !world.IsInsideTree())
		{
			return null;
		}

		// Bounds check (mirrors VoxelWorld.PlaceVoxel): only place within the
		// world's vertical range. XZ is unbounded (the world is infinite in XZ).
		if (voxelPos.Y < 0 || voxelPos.Y >= world.ChunkHeight)
		{
			return null;
		}

		// OCCUPANCY CHECK: a cell can hold at most one DecoVoxel, and a
		// DecoVoxel cannot be placed into a cell that already has a solid voxel
		// (terrain). (A preview ghost does not count as occupied — it has no
		// collision and IsPreview is true, so it is excluded below.)
		if (!preview && IsCellOccupied(world, voxelPos))
		{
			return null;
		}

		var deco = CreateDeco(type);
		if (deco == null)
		{
			return null;
		}
		deco.IsPreview = preview;
		deco.Name = $"Deco_{type}_{voxelPos.X}_{voxelPos.Y}_{voxelPos.Z}";

		// Add to the world (which parents it to the "DecoVoxels" container and
		// indexes it in _decoVoxels).
		world.RegisterDecoVoxel(deco);

		// Initialize AFTER it is in the tree (GlobalPosition needs a tree).
		deco.Initialize(voxelPos, world.VoxelSize, world, dropItemId);

		return deco;
	}

	/// <summary>
	/// True when the world voxel <paramref name="voxelPos"/> can accept a
	/// DecoVoxel — i.e. it's within the world's vertical range AND not already
	/// occupied (by a solid voxel or another DecoVoxel). The tool uses this to
	/// decide whether to show the placement ghost.
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
	/// occupied — either by a solid voxel (any non-air material in ChunkData)
	/// or by another placed (non-preview) <see cref="DecoVoxel"/>. This is the
	/// occupancy check that enforces "one DecoVoxel per cell, no placing inside
	/// blocks".
	/// </summary>
	private static bool IsCellOccupied(VoxelWorld world, Vector3I voxelPos)
	{
		// A solid voxel blocks placement.
		int material = world.GetVoxel(voxelPos.X, voxelPos.Y, voxelPos.Z);
		if (material != VoxelMaterialIds.Air)
		{
			return true;
		}

		// A placed (non-preview) DecoVoxel blocks placement.
		var existing = world.GetDecoVoxelAt(voxelPos);
		if (existing != null && !existing.IsPreview)
		{
			return true;
		}

		return false;
	}

	/// <summary>
	/// Creates the DecoVoxel node for the given type. This is the factory the
	/// spawner uses to instantiate the right node. Add a new DecoVoxel type by
	/// adding a case here (and a new value to the <see cref="DecoType"/> enum).
	/// </summary>
	public static DecoVoxel CreateDeco(DecoType type)
	{
		return type switch
		{
			DecoType.Grass => new GrassDeco(),
			// Future: DecoType.Wall => new WallDeco(),
			//         DecoType.Door => new DoorDeco(),
			//         DecoType.Window => new WindowDeco(),
			_ => null,
		};
	}
}

/// <summary>
/// The kinds of DecoVoxels that can be spawned. Add a new kind by adding a
/// value here and a case in <see cref="DecoSpawner.CreateDeco"/>.
/// </summary>
public enum DecoType
{
	/// <summary>
	/// No specific deco type. The base <see cref="DecoVoxel"/> reports this when
	/// it is used directly (not through a concrete subclass), so no
	/// <see cref="DecoDefinition"/> is looked up. Not a spawnable kind.
	/// </summary>
	None = -1,

	/// <summary>Grass: two crossed planes on the surface, walk-through, light-passing.</summary>
	Grass = 0,

	// Future:
	// Wall = 1,     // A structural wall (blocks light + collision).
	// Door = 2,     // A sliding door (blocks light + collision, can open).
	// Window = 3,   // A transparent window (passes light, blocks collision).
}
