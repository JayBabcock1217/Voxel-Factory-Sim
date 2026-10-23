using Godot;
using System;
using FactorySim.Items;

namespace FactorySim.Machines;

/// <summary>
/// The conveyor machine's behavior — the ONLY item transport in the factory
/// system (there are no inserters). A conveyor is DIRECTIONAL: it has a single
/// item slot and moves items along its flow axis, which is the machine's LOCAL
/// +Z axis rotated by the placement rotation (the R-key orientation). This
/// matches how the mesh is modeled: the belt's length runs along local +Z, so
/// the machine's "front" is the +Z face.
///
/// On a throttled tick the conveyor does two things, both strictly along its
/// flow axis:
///
///   1. PULL:  if its slot is empty, take one item from the machine on its
///             BACK face (the neighbor at <c>-flow</c>). This is how an item
///             enters the belt and how items chain along a run of conveyors.
///   2. PUSH:  if its slot has an item, move it into the machine on its FRONT
///             face (the neighbor at <c>+flow</c>) — a processor's input, or the
///             next conveyor's slot.
///
/// Because items only ever flow front-to-back (never sideways or backward),
/// there is no ping-pong: a ring of conveyors carries items around the loop in
/// one direction, and a straight run feeds the machine at its end.
///
/// A conveyor is a PASSIVE machine: it needs no power, so it always runs. The
/// flow axis is derived from the placement rotation at spawn (and re-derived on
/// a rotation change), so the player orients each belt by rotating it (R key)
/// before placing.
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

	/// <summary>
	/// The conveyor's flow direction in world-voxel space: the local +Z axis
	/// rotated by the placement rotation. Items are pulled from the machine at
	/// <c>VoxelPos - FlowAxis</c> and pushed into the machine at
	/// <c>VoxelPos + FlowAxis</c>.
	/// </summary>
	public Vector3I FlowAxis { get; private set; } = new(0, 0, 1);

	private float _transferTimer;

	/// <summary>
	/// A conveyor's "output" is its single slot — the machine on its BACK face
	/// pulls an item out of it (that's how items chain along a belt).
	/// </summary>
	public override MachineBuffer OutputBuffer => Slot;

	/// <summary>
	/// A conveyor's "input" is also its single slot — the machine on its FRONT
	/// face pushes an item into it when it is free. Exposing the same buffer for
	/// both input and output lets a conveyor both receive and hand off its one
	/// item.
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
		FlowAxis = ComputeFlowAxis();
	}

	/// <summary>
	/// Recomputes the flow axis from the current placement rotation and stores
	/// it. Called on start and whenever the rotation changes. The flow axis is
	/// the local +Z axis rotated by the placement rotation's basis (the same
	/// basis that turns the machine's visual), so the belt's front face always
	/// matches the mesh's modeled front.
	/// </summary>
	public void UpdateFlowAxis()
	{
		FlowAxis = ComputeFlowAxis();
	}

	/// <summary>
	/// Computes the flow axis (world-voxel direction) for the current placement
	/// rotation: the local +Z unit axis transformed by the rotation's basis,
	/// then snapped to the nearest unit axis. The rotation basis is orthonormal
	/// (a single 90° rotation), so the transformed +Z is exactly one of the six
	/// unit axes — no ambiguity to resolve.
	/// </summary>
	private Vector3I ComputeFlowAxis()
	{
		Basis b = PlacementRotation.BasisFor();
		Vector3 localZ = new(0f, 0f, 1f);
		Vector3 world = b * localZ;
		// Snap to the nearest unit axis (the basis is a 90° rotation, so the
		// result is already a unit axis; the snap just cleans up float error).
		return SnapToUnitAxis(world);
	}

	/// <summary>Snaps a (near) unit vector to the nearest of the six unit axes.</summary>
	private static Vector3I SnapToUnitAxis(Vector3 v)
	{
		// Pick the axis with the largest absolute component, then take its sign.
		float ax = System.Math.Abs(v.X);
		float ay = System.Math.Abs(v.Y);
		float az = System.Math.Abs(v.Z);
		if (ax >= ay && ax >= az)
		{
			return new Vector3I(v.X > 0 ? 1 : -1, 0, 0);
		}
		if (ay >= ax && ay >= az)
		{
			return new Vector3I(0, v.Y > 0 ? 1 : -1, 0);
		}
		return new Vector3I(0, 0, v.Z > 0 ? 1 : -1);
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
	/// If the slot is empty, pull one item from the machine on the conveyor's
	/// BACK face (<c>VoxelPos - FlowAxis</c>). That is the only direction items
	/// enter the belt, so a run of conveyors carries items forward along the
	/// axis and a processor can feed a belt by sitting on its back.
	/// </summary>
	private void TryPull()
	{
		if (!Slot.IsEmpty)
		{
			return; // slot full: nothing to pull
		}
		Vector3I backPos = VoxelPos - FlowAxis;
		var neighbor = NeighborMachine(backPos);
		if (neighbor == null || neighbor == this)
		{
			return;
		}
		var output = neighbor.OutputBuffer;
		if (output == null || output.IsEmpty)
		{
			return;
		}
		// Pull ONE item so items move one-by-one along the belt.
		var item = output.TryExtract(AnyMaterial, 1);
		if (item.IsEmpty)
		{
			return;
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
		}
	}

	/// <summary>
	/// If the slot has an item, push it into the machine on the conveyor's FRONT
	/// face (<c>VoxelPos + FlowAxis</c>). That is the only direction items leave
	/// the belt — a processor's input or the next conveyor's slot. When no
	/// machine (or a full one) is on the front face, the item stays in the slot
	/// (the belt stalls), which is what prevents ping-pong.
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

		Vector3I frontPos = VoxelPos + FlowAxis;
		var neighbor = NeighborMachine(frontPos);
		if (neighbor != null && neighbor != this)
		{
			var input = neighbor.InputBuffer;
			if (input != null && PushTo(input, item))
			{
				return; // delivered to the front-face machine
			}
		}

		// Nothing on the front face could take the item: keep it in the slot
		// (the belt stalls here until the front clears).
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
