using System.Collections.Generic;

namespace FactorySim.Machines;

/// <summary>
/// A serializable snapshot of a single placed machine's RUNTIME state — the
/// data the save system needs to fully restore a machine (not just its
/// position/rotation, but its buffer contents and its per-machine settings
/// like the selected smelter/crafter recipe).
///
/// This is the contract between the live <see cref="Machine"/> nodes and the
/// save format: a machine exposes <see cref="Machine.TakeSnapshot"/> to produce
/// one, and <see cref="Machine.RestoreSnapshot"/> to apply one after spawning.
/// The <see cref="WorldSaveFile.MachineRecord"/> (the binary layout) is a thin
/// serialization of this struct.
///
/// The buffers are stored as flat (materialId, count) pairs (one per stack,
/// in slot order) rather than as <see cref="FactorySim.Items.ItemStack"/>
/// objects, so the snapshot is trivially (de)serializable and has no
/// dependency on the live item system. Empty slots are omitted on capture and
/// simply left empty on restore.
/// </summary>
public sealed class MachineSnapshot
{
	/// <summary>The machine's INPUT buffer stacks (materialId, count), in slot order. Empty for machines with no input buffer.</summary>
	public List<(int MaterialId, int Count)> InputSlots { get; set; } = new();

	/// <summary>The machine's OUTPUT buffer stacks (materialId, count), in slot order. Empty for machines with no output buffer.</summary>
	public List<(int MaterialId, int Count)> OutputSlots { get; set; } = new();

	/// <summary>
	/// The machine's selected-recipe index, for machines that have one (a
	/// <see cref="SmelterMachine"/> or <see cref="CrafterMachine"/>). -1 when
	/// the machine has no recipe concept or none is selected. The index is
	/// resolved against the machine's available-recipe list on restore, so it
	/// is robust to the recipe list changing between saves.
	/// </summary>
	public int RecipeIndex { get; set; } = -1;

	/// <summary>True when the snapshot carries a selected recipe (RecipeIndex >= 0).</summary>
	public bool HasRecipe => RecipeIndex >= 0;

	/// <summary>
	/// The in-progress processing fraction (0..1) for a processor (smelter/crafter)
	/// at the moment of the save: how far through the current smelt/craft it was.
	/// 0 when the machine was idle (not mid-operation). Restored by re-seeding the
	/// machine's processing timer so the in-progress operation RESUMES (and
	/// completes) after a reload rather than restarting from scratch. The
	/// consumed ingredients are already out of the input buffer (extracted when the
	/// operation started), so resuming just needs the timer, not the inputs.
	/// </summary>
	public float Progress { get; set; }

	/// <summary>
	/// True when the machine was MID-OPERATION (a smelt/craft in progress) at the
	/// moment of the save. On restore, when true, the machine's in-progress flag
	/// is re-set and its timer seeded from <see cref="Progress"/> so it resumes.
	/// False when the machine was idle (between operations).
	/// </summary>
	public bool IsProcessing { get; set; }
}
