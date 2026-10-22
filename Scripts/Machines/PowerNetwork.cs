using Godot;
using FactorySim.Voxel;

namespace FactorySim.Machines;

/// <summary>
/// The range-based power network. Power is NOT carried on wires or conveyors —
/// a <see cref="MachineType.PowerSource"/> machine produces a fixed rate of power
/// (its definition's <see cref="MachineDefinition.PowerOutput"/>) and that power
/// is available to ANY consumer machine within <see cref="Radius"/> blocks (a
/// simple Euclidean distance in world-voxel units, so it works "in any
/// direction" — no cables to lay).
///
/// Model (the simplest fair one): for a given consumer machine, the available
/// power is the sum of every PowerSource within radius MINUS the sum of every
/// OTHER consumer's <see cref="MachineDefinition.PowerConsumption"/> within the
/// same radius. A machine is "powered" when its own <see
/// cref="MachineDefinition.PowerConsumption"/> is met by that available amount.
/// This means a single source powers a cluster of machines until its output is
/// exhausted, and adding more machines to the cluster can brown out the whole
/// area — exactly the "too many machines, not enough power" tension of a
/// factory sim, but computed with no graph.
///
/// This is a static service (like <see cref="DropSpawner"/>): it walks the
/// shared "Machines" container in the scene tree and is stateless, so it is
/// always consistent with the live set of placed machines.
/// </summary>
public static class PowerNetwork
{
	/// <summary>
	/// The default radius (in world-voxel units) over which a power source feeds
	/// nearby machines. A source at the origin powers any machine whose voxel is
	/// within this distance. ~8 is a good default: a small cluster of machines
	/// around a single generator. This is a fallback; the live value is
	/// <see cref="Radius"/>, which the VoxelWorld can override via its
	/// <c>PowerRadius</c> export (set through <see cref="SetRadius"/>).
	/// </summary>
	public const float DefaultRadius = 8f;

	private static float _radius = DefaultRadius;

	/// <summary>
	/// The live power radius (world-voxel units). Defaults to
	/// <see cref="DefaultRadius"/>; the VoxelWorld sets it from its
	/// <c>PowerRadius</c> export so the range is tunable in the inspector.
	/// </summary>
	public static float Radius => _radius;

	/// <summary>Overrides the live power radius (the VoxelWorld calls this).</summary>
	public static void SetRadius(float radius)
	{
		_radius = radius < 0.5f ? DefaultRadius : radius;
	}

	/// <summary>
	/// True when the machine at <paramref name="voxelPos"/> has enough power to
	/// run. A machine with <c>PowerConsumption <= 0</c> (a passive machine like a
	/// conveyor, or a power source) always returns true — it needs no power.
	/// Otherwise it is powered when the net available power in its radius meets
	/// or exceeds its consumption.
	/// </summary>
	public static bool IsPowered(VoxelWorld world, Vector3I voxelPos)
	{
		if (world == null)
		{
			return false;
		}
		var machine = MachineAt(world, voxelPos);
		if (machine == null || machine.Definition == null)
		{
			return true; // no machine there (or unknown) → don't gate on power
		}

		float consumption = machine.Definition.PowerConsumption;
		if (consumption <= 0f)
		{
			return true; // passive machine: no power needed
		}

		return AvailablePowerAt(world, voxelPos) >= consumption;
	}

	/// <summary>
	/// The net power available at a voxel position: total production from every
	/// power source within <see cref="Radius"/> minus total consumption from
	/// every consumer machine within that radius (excluding the position
	/// itself, so a machine is not double-counted against itself). Clamped at 0.
	/// </summary>
	public static float AvailablePowerAt(VoxelWorld world, Vector3I at)
	{
		if (world == null)
		{
			return 0f;
		}
		float production = 0f;
		float consumption = 0f;

		var machinesRoot = world.GetTree().Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return 0f;
		}
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (!(machinesRoot.GetChild(i) is Machine m) || m.IsPreview || m.Definition == null)
			{
				continue;
			}
			float dist = VoxelDistance(m.VoxelPos, at);
			if (dist > Radius)
			{
				continue;
			}
			if (m.Definition.PowerOutput > 0f)
			{
				production += m.Definition.PowerOutput;
			}
			else if (m.Definition.PowerConsumption > 0f)
			{
				// Exclude the machine AT 'at' (the one being queried) so it isn't
				// subtracted from its own available power.
				if (m.VoxelPos != at)
				{
					consumption += m.Definition.PowerConsumption;
				}
			}
		}

		return Mathf.Max(0f, production - consumption);
	}

	/// <summary>
	/// Finds the (non-preview) machine occupying the world voxel
	/// <paramref name="voxelPos"/>, or null when none is there. This is the
	/// shared "what machine is at this cell" lookup used by the power network,
	/// conveyors, and the tool.
	/// </summary>
	public static Machine MachineAt(VoxelWorld world, Vector3I voxelPos)
	{
		if (world == null)
		{
			return null;
		}
		var machinesRoot = world.GetTree().Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return null;
		}
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (machinesRoot.GetChild(i) is Machine m && !m.IsPreview && m.VoxelPos == voxelPos)
			{
				return m;
			}
		}
		return null;
	}

	/// <summary>
	/// The six face-adjacent neighbor positions of a voxel (±X, ±Y, ±Z) — the
	/// "any side" neighbor set conveyors use to pull from / push into adjacent
	/// machines.
	/// </summary>
	public static Vector3I[] Neighbors(Vector3I v) => new[]
	{
		v + new Vector3I(1, 0, 0),
		v + new Vector3I(-1, 0, 0),
		v + new Vector3I(0, 1, 0),
		v + new Vector3I(0, -1, 0),
		v + new Vector3I(0, 0, 1),
		v + new Vector3I(0, 0, -1),
	};

	/// <summary>Euclidean distance between two voxel positions (in voxel units).</summary>
	private static float VoxelDistance(Vector3I a, Vector3I b)
	{
		return new Vector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z).Length();
	}
}
