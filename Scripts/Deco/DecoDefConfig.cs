using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using FactorySim.Voxel;

namespace FactorySim.Deco;

/// <summary>
/// The registry of all <see cref="DecoDefinition"/>s for a world — the
/// deco-system counterpart of <see cref="MachineDefConfig"/> /
/// <see cref="VoxelMaterialConfig"/>. Hold an array of deco definitions (one
/// per placeable deco type) and provide lookup by <see cref="DecoType"/> or by
/// inventory item id.
///
/// This is where the user wires up their decos: create a
/// <see cref="DecoDefConfig"/> resource, add a <see cref="DecoDefinition"/>
/// per deco (each pointing at its mesh / material), and assign this config to
/// the <see cref="VoxelWorld.DecoConfig"/> export. Adding a deco = adding a
/// definition to this array; no code changes required.
///
/// Like <see cref="MachineDefConfig"/>, it is a Godot <see cref="Resource"/>
/// so it can be saved as a .tres and edited in the inspector.
/// </summary>
[Tool]
[GlobalClass]
public partial class DecoDefConfig : Resource
{
	/// <summary>Fired whenever the deco set changes (a definition added/removed/edited).</summary>
	public event Action<DecoDefinition> PropertyChanged;

	/// <summary>All deco definitions, editable in the inspector (order is display order).</summary>
	[Export] public DecoDefinition[] Decos { get; set; } = Array.Empty<DecoDefinition>();

	/// <summary>
	/// Allocates a new <see cref="DecoDefinition"/> with the given
	/// <paramref name="type"/> (and its derived item id), appends it, and
	/// returns it. This is what the inspector "Add" button and code use to grow
	/// the list. The user then fills in the name, appearance, and behavior.
	/// </summary>
	public DecoDefinition AddDeco(DecoType type)
	{
		var def = new DecoDefinition
		{
			Type = type,
			Name = type.ToString(),
			ItemId = 0, // default: DecoFirst + (int)type
		};
		AppendDeco(def);
		return def;
	}

	/// <summary>Appends a deco def, wires its change event, and notifies listeners.</summary>
	public void AppendDeco(DecoDefinition def)
	{
		var list = new List<DecoDefinition>(Decos ?? Array.Empty<DecoDefinition>());
		list.Add(def);
		Decos = list.ToArray();
		def.PropertyChanged += OnDefChanged;
		PropertyChanged?.Invoke(def);
	}

	private void OnDefChanged(DecoDefinition def) => PropertyChanged?.Invoke(def);

	/// <summary>Finds the definition for a deco type, or null when absent.</summary>
	public DecoDefinition GetByType(DecoType type)
	{
		if (Decos == null)
		{
			return null;
		}
		for (int i = 0; i < Decos.Length; i++)
		{
			var def = Decos[i];
			if (def != null && def.Type == type)
			{
				return def;
			}
		}
		return null;
	}

	/// <summary>
	/// Finds the definition whose item id matches <paramref name="itemId"/>
	/// (the effective item id — see <see cref="DecoDefinition.EffectiveItemId"/>).
	/// This is the lookup the place-ability uses: the player holds a deco item,
	/// its MaterialId is the deco id, and this resolves it to a def. Returns
	/// null when no deco uses that id.
	/// </summary>
	public DecoDefinition GetByItemId(int itemId)
	{
		if (Decos == null)
		{
			return null;
		}
		for (int i = 0; i < Decos.Length; i++)
		{
			var def = Decos[i];
			if (def != null && def.EffectiveItemId == itemId)
			{
				return def;
			}
		}
		return null;
	}

	/// <summary>
	/// True when <paramref name="itemId"/> is a deco item id (in the
	/// <see cref="VoxelMaterialIds.DecoFirst"/>..
	/// <see cref="VoxelMaterialIds.DecoLast"/> range) AND a definition actually
	/// uses it. The item id range is the broad test; this additionally requires
	/// a registered definition.
	/// </summary>
	public bool IsDecoItemId(int itemId)
		=> GetByItemId(itemId) != null;

	/// <summary>All definitions in a stable order (by item id, then name).</summary>
	public IEnumerable<DecoDefinition> GetAll()
		=> (Decos ?? Array.Empty<DecoDefinition>())
			.Where(d => d != null)
			.OrderBy(d => d.EffectiveItemId)
			.ThenBy(d => d.Name, StringComparer.Ordinal);
}
