using System;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// One configurable voxel material: a stable integer <see cref="Id"/> plus the
/// appearance settings (color, texture, transparency, metallic, roughness,
/// emission) held in a <see cref="VoxelMaterialEntry"/> resource.
///
/// Id 0 is reserved for air; solid materials start at 1. Biomes and structure
/// generators reference materials BY ID, so the material list can grow or
/// shrink freely without touching generation code.
/// </summary>
[GlobalClass]
public partial class VoxelMaterialDef : Resource
{
	/// <summary>Fired whenever the def's entry changes (or the entry resource is swapped).</summary>
	public event Action<VoxelMaterialEntry> PropertyChanged;

	/// <summary>Stable material id (0 = air, 1.. = solid). Assigned by <see cref="VoxelMaterialConfig.AddMaterial"/>.</summary>
	[Export] public int Id { get; set; }

	/// <summary>Display name (editor label only; generation references materials by <see cref="Id"/>).</summary>
	[Export] public string Name { get; set; } = "Material";

	/// <summary>The appearance settings for this material (editable live in the inspector).</summary>
	[Export] public VoxelMaterialEntry Entry { get; set; } = new();

	/// <summary>
	/// Replaces the entry resource, rewiring its change event and notifying listeners.
	/// Mirrors how <see cref="VoxelMaterialConfig"/> wires its entries.
	/// </summary>
	public void SetEntry(VoxelMaterialEntry value)
	{
		if (ReferenceEquals(Entry, value))
		{
			return;
		}

		Unwire(Entry);
		Entry = value ?? new VoxelMaterialEntry();
		Wire(Entry);
		PropertyChanged?.Invoke(Entry);
	}

	/// <summary>
	/// Ensures the entry's change event is wired up. Called by
	/// <see cref="VoxelMaterialConfig"/> after constructing the default list,
	/// so live inspector edits to a def's entry propagate to the world.
	/// </summary>
	public void WireEntry() => Wire(Entry);

	private void Wire(VoxelMaterialEntry entry) => entry.PropertyChanged += OnEntryChanged;
	private void Unwire(VoxelMaterialEntry entry) => entry.PropertyChanged -= OnEntryChanged;
	private void OnEntryChanged(VoxelMaterialEntry entry) => PropertyChanged?.Invoke(entry);
}
