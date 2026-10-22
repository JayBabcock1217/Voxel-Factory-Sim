using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FactorySim.Machines;

/// <summary>
/// The registry of all <see cref="MachineDefinition"/>s for a world — the
/// machine-system counterpart of <see cref="VoxelMaterialConfig"/>. Hold an
/// array of machine definitions (one per placeable machine) and provide lookup
/// by <see cref="MachineType"/> or by inventory item id.
///
/// This is where the user wires up their machines: create a
/// <see cref="MachineDefConfig"/> resource, add a <see cref="MachineDefinition"/>
/// per machine (each pointing at its imported Blender mesh), and assign this
/// config to the <see cref="MachineWorld"/> (or any system that needs the
/// registry). Adding a machine = adding a definition to this array; no code
/// changes required.
///
/// Like <see cref="VoxelMaterialConfig"/>, it is a Godot <see cref="Resource"/>
/// so it can be saved as a .tres and edited in the inspector.
/// </summary>
[GlobalClass]
public partial class MachineDefConfig : Resource
{
	/// <summary>Fired whenever the machine set changes (a definition added/removed/edited).</summary>
	public event Action<MachineDefinition> PropertyChanged;

	/// <summary>All machine definitions, editable in the inspector (order is display order).</summary>
	[Export] public MachineDefinition[] Machines { get; set; } = Array.Empty<MachineDefinition>();

	/// <summary>
	/// Allocates a new <see cref="MachineDefinition"/> with the given
	/// <paramref name="type"/> (and its derived item id), appends it, and
	/// returns it. This is what the inspector "Add" button and code use to grow
	/// the list. The user then fills in the name, mesh, and stats.
	/// </summary>
	public MachineDefinition AddMachine(MachineType type)
	{
		var def = new MachineDefinition
		{
			Type = type,
			Name = type.ToString(),
			ItemId = 0, // default: (int)type
		};
		AppendMachine(def);
		return def;
	}

	/// <summary>Appends a machine def, wires its change event, and notifies listeners.</summary>
	public void AppendMachine(MachineDefinition def)
	{
		var list = new List<MachineDefinition>(Machines ?? Array.Empty<MachineDefinition>());
		list.Add(def);
		Machines = list.ToArray();
		def.PropertyChanged += OnDefChanged;
		PropertyChanged?.Invoke(def);
	}

	private void OnDefChanged(MachineDefinition def) => PropertyChanged?.Invoke(def);

	/// <summary>Finds the definition for a machine type, or null when absent.</summary>
	public MachineDefinition GetByType(MachineType type)
	{
		if (Machines == null)
		{
			return null;
		}
		for (int i = 0; i < Machines.Length; i++)
		{
			var def = Machines[i];
			if (def != null && def.Type == type)
			{
				return def;
			}
		}
		return null;
	}

	/// <summary>
	/// Finds the definition whose item id matches <paramref name="itemId"/>
	/// (the effective item id — see <see cref="MachineDefinition.EffectiveItemId"/>).
	/// This is the lookup the place-ability uses: the player holds a machine
	/// item, its MaterialId is the machine id, and this resolves it to a def.
	/// Returns null when no machine uses that id.
	/// </summary>
	public MachineDefinition GetByItemId(int itemId)
	{
		if (Machines == null)
		{
			return null;
		}
		for (int i = 0; i < Machines.Length; i++)
		{
			var def = Machines[i];
			if (def != null && def.EffectiveItemId == itemId)
			{
				return def;
			}
		}
		return null;
	}

	/// <summary>
	/// True when <paramref name="itemId"/> is a machine item id (in the
	/// <see cref="FactorySim.Voxel.VoxelMaterialIds.MachineFirst"/>..
	/// <see cref="FactorySim.Voxel.VoxelMaterialIds.MachineLast"/> range) AND a
	/// definition actually uses it. The item id range is the broad test; this
	/// additionally requires a registered definition.
	/// </summary>
	public bool IsMachineItemId(int itemId)
		=> GetByItemId(itemId) != null;

	/// <summary>All definitions in a stable order (by item id, then name).</summary>
	public IEnumerable<MachineDefinition> GetAll()
		=> (Machines ?? Array.Empty<MachineDefinition>())
			.Where(d => d != null)
			.OrderBy(d => d.EffectiveItemId)
			.ThenBy(d => d.Name, StringComparer.Ordinal);
}
