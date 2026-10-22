namespace FactorySim.Machines;

/// <summary>
/// The power source machine's behavior — a static power generator. It has no
/// per-tick work of its own: the <see cref="PowerNetwork"/> reads its
/// <see cref="MachineDefinition.PowerOutput"/> (the power it produces per
/// second) and feeds that to every consumer machine within
/// <see cref="PowerNetwork.Radius"/>. A power source is a PASSIVE machine — it
/// does not consume power (it produces it) and does not hold items.
///
/// This is a <see cref="Machine"/> subclass selected for <see
/// cref="MachineType.PowerSource"/> (see <see cref="MachineBehaviorRegistry"/>).
/// Its visual comes from the <see cref="MachineDefinition"/> (a mesh the user
/// imports); the behavior is intentionally minimal.
/// </summary>
public partial class PowerSourceMachine : Machine
{
	// A power source does nothing per-tick: the PowerNetwork reads its
	// PowerOutput directly. No overrides are needed — the base Machine hooks
	// are already correct for a passive, item-less machine.
}
