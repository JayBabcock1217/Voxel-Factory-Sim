using Godot;
using System;

namespace FactorySim.Machines;

/// <summary>
/// The 12 supported machine placement orientations — four 90° rotations about
/// each of the three axes (X, Y, Z). This is the "R key cycles through the
/// possible rotations" state: the player holds R to step through these 12
/// orientations (wrapping), and the value is PERSISTENT (it survives
/// re-selecting a machine, opening the inventory, etc. — it is the player's
/// current placement orientation, not per-machine state).
///
/// A rotation is applied to a machine's visual by pre-multiplying its
/// <see cref="Basis"/> with the corresponding 90° rotation (see
/// <see cref="BasisFor"/>), so the machine is turned about its own origin
/// (the voxel center).
/// </summary>
public enum MachineRotation
{
	/// <summary>Identity — no rotation.</summary>
	Identity = 0,
	/// <summary>Rotated 90° about the X axis.</summary>
	X90,
	/// <summary>Rotated 180° about the X axis.</summary>
	X180,
	/// <summary>Rotated 270° about the X axis.</summary>
	X270,
	/// <summary>Rotated 90° about the Y axis.</summary>
	Y90,
	/// <summary>Rotated 180° about the Y axis.</summary>
	Y180,
	/// <summary>Rotated 270° about the Y axis.</summary>
	Y270,
	/// <summary>Rotated 90° about the Z axis.</summary>
	Z90,
	/// <summary>Rotated 180° about the Z axis.</summary>
	Z180,
	/// <summary>Rotated 270° about the Z axis.</summary>
	Z270,
}

public static class MachineRotationExtensions
{
	/// <summary>The total number of supported orientations.</summary>
	public const int Count = 12;

	/// <summary>
	/// The next orientation in the cycle order (wrapping), used by the R key to
	/// step through all 12. The order is X90, X180, X270, Y90, ..., Z270, then
	/// back to Identity.
	/// </summary>
	public static MachineRotation Next(this MachineRotation current)
	{
		int next = (int)current + 1;
		if (next >= Count)
		{
			next = 0;
		}
		return (MachineRotation)next;
	}

	/// <summary>
	/// The <see cref="Basis"/> that rotates a machine's local space into the
	/// given orientation (a 90° rotation about the indicated axis, or identity).
	/// Pre-multiplying a mesh's basis by this turns the mesh about its origin
	/// (the voxel center) into the requested orientation.
	/// </summary>
	public static Basis BasisFor(this MachineRotation rotation)
	{
		// Godot C#'s Basis has a (axis, angle) constructor that builds a rotation
		// about the given axis by the given angle (radians). The axis vectors are
		// written as literals (Vector3 has no AxisX/Y/Z members in this SDK, and
		// it is a struct that cannot be const).
		const float half = Mathf.Pi / 2f;
		Vector3 X = new(1f, 0f, 0f);
		Vector3 Y = new(0f, 1f, 0f);
		Vector3 Z = new(0f, 0f, 1f);
		return rotation switch
		{
			MachineRotation.X90 => new Basis(X, half),
			MachineRotation.X180 => new Basis(X, Mathf.Pi),
			MachineRotation.X270 => new Basis(X, -half),
			MachineRotation.Y90 => new Basis(Y, half),
			MachineRotation.Y180 => new Basis(Y, Mathf.Pi),
			MachineRotation.Y270 => new Basis(Y, -half),
			MachineRotation.Z90 => new Basis(Z, half),
			MachineRotation.Z180 => new Basis(Z, Mathf.Pi),
			MachineRotation.Z270 => new Basis(Z, -half),
			_ => Basis.Identity, // Identity
		};
	}

	/// <summary>A short human-readable label (e.g. "X90", "Identity").</summary>
	public static string DisplayName(this MachineRotation rotation)
	{
		return rotation switch
		{
			MachineRotation.Identity => "Identity",
			_ => rotation.ToString(),
		};
	}
}
