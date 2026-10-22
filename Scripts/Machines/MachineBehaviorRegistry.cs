using System;
using System.Collections.Generic;

namespace FactorySim.Machines;

/// <summary>
/// Maps a <see cref="MachineType"/> to the concrete <see cref="Machine"/>
/// subclass that implements its behavior. This is the "which node for which
/// machine" policy for BUILT-IN behaviors (conveyor, crafter, power source).
///
/// A machine type with a registered behavior is instantiated as that behavior
/// subclass (e.g. <see cref="MachineType.Conveyor"/> → <see
/// cref="ConveyorMachine"/>). A type with NO registered behavior uses the base
/// <see cref="Machine"/> (a static, no-logic machine — e.g. a turret or defense
/// wall that is just a placed mesh for now). A custom <see
/// cref="MachineDefinition.MachineSceneOverride"/> scene ALWAYS wins over this
/// registry (it is the escape hatch for fully custom machine nodes).
///
/// To give a new machine type a behavior: add its <see cref="MachineType"/> →
/// behavior-subclass mapping here (one line). No spawner/tool/save changes needed.
/// </summary>
public static class MachineBehaviorRegistry
{
	// Built-in behavior per machine type. The base Machine is the fallback for
	// types not listed (static machines).
	private static readonly Dictionary<MachineType, Func<Machine>> _behaviors = new()
	{
		{ MachineType.Conveyor,    () => new ConveyorMachine() },
		{ MachineType.Crafter,     () => new CrafterMachine() },
		{ MachineType.PowerSource, () => new PowerSourceMachine() },
	};

	/// <summary>
	/// True when a built-in behavior is registered for <paramref name="type"/>.
	/// </summary>
	public static bool HasBehavior(MachineType type) => _behaviors.ContainsKey(type);

	/// <summary>
	/// Creates the <see cref="Machine"/> node for <paramref name="type"/>: the
	/// registered behavior subclass when one exists, else the base
	/// <see cref="Machine"/>. The returned node is NOT in the tree and is not yet
	/// initialized (the spawner does that).
	/// </summary>
	public static Machine CreateForType(MachineType type)
	{
		if (_behaviors.TryGetValue(type, out var factory))
		{
			return factory();
		}
		return new Machine();
	}
}
