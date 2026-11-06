using Godot;

namespace FactorySim.Machines;

/// <summary>
/// What a machine's face does with items — the per-direction config the DEV
/// sets on a <see cref="MachineDefinition"/> (one value per each of the six
/// cube faces: ±X, ±Y, ±Z). This is NOT per-instance player state and NOT
/// saved in the world file: it is a definition-level setting (like
/// <see cref="MachineDefinition.PowerConsumption"/>), edited in the inspector
/// and shared by every placed machine of that type.
///
/// The value drives the in-game FACE INDICATORS: a face marked
/// <see cref="Input"/> shows an input-colored indicator, a face marked
/// <see cref="Output"/> shows an output-colored indicator, and a face marked
/// <see cref="Disabled"/> shows nothing. (Per the design, the indicators are
/// the visible manifestation of the config — the actual item flow is left
/// unchanged.) The indicators can be toggled globally visible/invisible with
/// the F2 key (see <see cref="Machine.SetIndicatorsVisible"/>).
/// </summary>
public enum MachinePort
{
	/// <summary>The face is disabled — no item port, no indicator.</summary>
	Disabled = 0,

	/// <summary>The face is an INPUT — items enter the machine here. Shows an input-colored indicator.</summary>
	Input = 1,

	/// <summary>The face is an OUTPUT — items leave the machine here. Shows an output-colored indicator.</summary>
	Output = 2,
}

/// <summary>
/// Helpers for the six machine face directions and their canonical index order.
/// The order matches <see cref="PowerNetwork.Neighbors"/> so a face index lines
/// up with the neighbor-lookup order used elsewhere in the machine system.
/// </summary>
public static class MachineFace
{
	/// <summary>The number of cube faces (six).</summary>
	public const int Count = 6;

	/// <summary>
	/// The six face direction vectors in the canonical index order (index 0..5):
	///   0 = +X, 1 = -X, 2 = +Y, 3 = -Y, 4 = +Z, 5 = -Z.
	/// This is the same order <see cref="PowerNetwork.Neighbors"/> returns, so
	/// <c>FaceDirections[i]</c> is the offset from a machine's voxel to the
	/// neighbor on face <c>i</c>.
	/// </summary>
	public static readonly Vector3I[] FaceDirections =
	{
		new Vector3I(1, 0, 0),
		new Vector3I(-1, 0, 0),
		new Vector3I(0, 1, 0),
		new Vector3I(0, -1, 0),
		new Vector3I(0, 0, 1),
		new Vector3I(0, 0, -1),
	};

	/// <summary>
	/// The canonical face index for a (unit) direction vector, or -1 when the
	/// vector is not one of the six face axes. Used to look up a face's config
	/// from a direction.
	/// </summary>
	public static int IndexOf(Vector3I dir)
	{
		for (int i = 0; i < Count; i++)
		{
			if (FaceDirections[i] == dir)
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>
	/// A short label for a face direction (e.g. "+X", "-Y") for inspector /
	/// debug display.
	/// </summary>
	public static string Label(Vector3I dir)
	{
		if (dir == FaceDirections[0]) return "+X";
		if (dir == FaceDirections[1]) return "-X";
		if (dir == FaceDirections[2]) return "+Y";
		if (dir == FaceDirections[3]) return "-Y";
		if (dir == FaceDirections[4]) return "+Z";
		if (dir == FaceDirections[5]) return "-Z";
		return "?";
	}
}
