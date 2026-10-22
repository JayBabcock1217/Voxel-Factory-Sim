namespace FactorySim.Machines;

/// <summary>
/// The kinds of machine the factory design system can represent. This enum is
/// the OPEN extension point for the whole machine system: adding a new machine
/// type (e.g. a Furnace, a Drill, a Solar Panel) starts by adding a value here.
///
/// A <see cref="MachineType"/> value is the identity that links together:
///   - a <see cref="MachineDefinition"/> (its mesh, stats, item id — the data),
///   - the item the player holds to place it (see
///     <see cref="FactorySim.Voxel.VoxelMaterialIds"/> machine ids), and
///   - any behavior a concrete <see cref="Machine"/> subclass implements.
///
/// The underlying int values are the machine item ids
/// (<see cref="FactorySim.Voxel.VoxelMaterialIds.MachineFirst"/> ..
/// <see cref="FactorySim.Voxel.VoxelMaterialIds.MachineLast"/>), so a machine
/// item's <see cref="FactorySim.Items.ItemStack.MaterialId"/> is simply
/// <c>(int)MachineType</c> — the same convention the tool system uses
/// (<see cref="FactorySim.Items.ToolType"/>).
///
/// The first five are the user-requested starter set (smelters, crafters,
/// turrets, defense walls, conveyors). They are intentionally just identities
/// here — the MESHES and per-type behavior are supplied by the user via
/// <see cref="MachineDefinition"/> resources and <see cref="Machine"/>
/// subclasses, so the system is expandable without touching this enum beyond
/// appending new values.
/// </summary>
public enum MachineType
{
	// -- Starter machines (the user's requested factory set) --------------
	Smelter = 200,      // melts ores / processes heat-based recipes
	Crafter = 201,      // assembles products from ingredients
	Turret = 202,       // offensive structure
	DefenseWall = 203,  // passive defensive barrier
	Conveyor = 204,     // moves items between machines

	// -- Future machines append here (e.g. Furnace = 205, Drill = 206, ...) --
}
