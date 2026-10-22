using Godot;
using System;
using FactorySim.Items;

namespace FactorySim.Machines;

/// <summary>
/// The conveyor machine's behavior — the ONLY item transport in the factory
/// system (there are no inserters). A conveyor has a single item slot and, on a
/// throttled tick, does two things in order:
///
///   1. PULL: if its slot is empty, take one item from an adjacent machine's
///      OUTPUT buffer (a processor's output, or a neighbor conveyor's slot).
///   2. PUSH: if its slot has an item, move it into an adjacent machine's INPUT
///      buffer (a processor's input, or a free neighbor conveyor slot).
///
/// "Adjacent" means any of the six face-neighbors (±X, ±Y, ±Z) — items can
/// enter or leave a machine from any side. A conveyor is a PASSIVE machine: it
/// needs no power, so it always runs.
///
/// This is a <see cref="Machine"/> subclass selected for <see
/// cref="MachineType.Conveyor"/> (see <see cref="MachineBehaviorRegistry"/>). It
/// carries no visual of its own — the mesh comes from the
/// <see cref="MachineDefinition"/> — only the per-tick behavior.
/// </summary>
public partial class ConveyorMachine : Machine
{
	/// <summary>
	/// Seconds between transfer attempts. Lower = faster item flow. 0.25 (4 ops/sec)
	/// is a good default: items visibly travel along the belt without teleporting.
	/// </summary>
	[Export(PropertyHint.Range, "0.02,2,0.01")] public float TransferInterval { get; set; } = 0.25f;

	/// <summary>The conveyor's single item slot (its only buffer).</summary>
	public MachineBuffer Slot { get; private set; }

	private float _transferTimer;

	/// <summary>
	/// A conveyor's "output" is its single slot — an adjacent conveyor can pull
	/// an item out of it (that's how items chain along a belt).
	/// </summary>
	public override MachineBuffer OutputBuffer => Slot;

	/// <summary>
	/// A conveyor's "input" is also its single slot — an adjacent conveyor (or
	/// the pull logic) can push an item into it when it is free. Exposing the
	/// same buffer for both input and output lets a conveyor both receive and
	/// hand off its one item.
	/// </summary>
	public override MachineBuffer InputBuffer => Slot;

	/// <summary>
	/// A conveyor can receive an item only when its single slot is empty (a
	/// conveyor holds at most one item at a time).
	/// </summary>
	public override bool CanReceive() => Slot != null && Slot.IsEmpty;

	/// <summary>A conveyor has an output only when its single slot holds an item.</summary>
	public override bool HasOutput() => Slot != null && !Slot.IsEmpty;

	protected override void StartMachine()
	{
		// The slot capacity is 1 (a conveyor holds one item). The definition's
		// BufferSize is ignored for conveyors — they are always single-slot.
		Slot = new MachineBuffer(1);
		_transferTimer = 0f;
	}

	protected override void ProcessMachine(float delta)
	{
		if (Slot == null)
		{
			return;
		}
		// Conveyors are passive (no power needed) — they always transfer.
		_transferTimer += delta;
		if (_transferTimer < TransferInterval)
		{
			return;
		}
		_transferTimer = 0f;

		TryPull();
		TryPush();
	}

	/// <summary>
	/// If the slot is empty, take one item from an adjacent machine's output
	/// buffer. Tries each of the six neighbors in a fixed order and takes from
	/// the first that has an available output.
	/// </summary>
	private void TryPull()
	{
		if (!Slot.IsEmpty)
		{
			return; // slot full: nothing to pull
		}
		foreach (var neighborPos in NeighborPositions())
		{
			var neighbor = NeighborMachine(neighborPos);
			if (neighbor == null || neighbor == this)
			{
				continue;
			}
			var output = neighbor.OutputBuffer;
			if (output == null || output.IsEmpty)
			{
				continue;
			}
			// Take the first (lowest) stack's item out of the neighbor's output.
			// We pull ONE item so items move one-by-one along the belt.
			var item = output.TryExtract(AnyMaterial, 1);
			if (item.IsEmpty)
			{
				continue;
			}
			int inserted = Slot.TryInsert(item);
			if (inserted > 0)
			{
				// If we couldn't insert the whole item (shouldn't happen for a
				// single item, but be safe), return the remainder.
				if (inserted < item.Count)
				{
					output.TryInsert(ItemStack.From(item.MaterialId, item.Count - inserted));
				}
				return; // pulled one item this tick
			}
		}
	}

	/// <summary>
	/// If the slot has an item, move it into an adjacent machine's input buffer.
	/// Prefers a PROCESSOR (a machine with a real input buffer, i.e. not another
	/// conveyor) so items flow toward machines rather than ping-ponging between
	/// conveyors; only when no processor can take the item does it fall back to
	/// a free neighbor conveyor slot (which is how items chain along a belt).
	/// </summary>
	private void TryPush()
	{
		if (Slot.IsEmpty)
		{
			return; // nothing to push
		}
		var item = Slot.TryExtract(AnyMaterial, 1);
		if (item.IsEmpty)
		{
			return;
		}

		// Pass 1: prefer a processor (a non-conveyor neighbor with an input).
		foreach (var neighborPos in NeighborPositions())
		{
			var neighbor = NeighborMachine(neighborPos);
			if (neighbor == null || neighbor == this || neighbor is ConveyorMachine)
			{
				continue;
			}
			var input = neighbor.InputBuffer;
			if (input == null)
			{
				continue;
			}
			if (PushTo(input, item))
			{
				return;
			}
		}

		// Pass 2: fall back to a free neighbor conveyor slot (belt chaining).
		foreach (var neighborPos in NeighborPositions())
		{
			var neighbor = NeighborMachine(neighborPos);
			if (neighbor == null || neighbor == this)
			{
				continue;
			}
			var input = neighbor.InputBuffer;
			if (input == null)
			{
				continue;
			}
			if (PushTo(input, item))
			{
				return;
			}
		}

		// No neighbor could take the item: keep it in the slot (put it back).
		Slot.TryInsert(item);
	}

	/// <summary>
	/// Inserts <paramref name="item"/> into <paramref name="target"/>, returning
	/// the remainder to the conveyor's slot when it didn't all fit. Returns true
	/// when at least one item was delivered (the item is then considered handled).
	/// </summary>
	private bool PushTo(MachineBuffer target, ItemStack item)
	{
		int inserted = target.TryInsert(item);
		if (inserted > 0)
		{
			if (inserted < item.Count)
			{
				Slot.TryInsert(ItemStack.From(item.MaterialId, item.Count - inserted));
			}
			return true;
		}
		return false;
	}

	/// <summary>Material-id sentinel meaning "any item" (0 = any, see MachineBuffer.TryExtract).</summary>
	private const int AnyMaterial = 0;
}
