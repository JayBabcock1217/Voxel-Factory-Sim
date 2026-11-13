using Godot;
using System.Globalization;

namespace FactorySim.Commands;

/// <summary>
/// The "settime" command: jumps the day/night cycle to a specific time using a
/// standard 24-HOUR CLOCK. Takes a single value in [0, 24] where:
///   * 0  (or 24) = midnight
///   * 6          = sunrise (early morning)
///   * 12         = noon
///   * 18         = sunset (evening)
/// The value is converted to the SkyCycle's phase (phase = hours / 24) and
/// applied via SetPhase. The SkyCycle's sun-arc mapping is built on the same
/// clock convention (phase 0 = midnight, 0.25 = sunrise, 0.5 = noon,
/// 0.75 = sunset), so the sun's angle matches the requested clock time:
/// "settime 12" puts the sun at its zenith (noon), "settime 0" puts it at its
/// nadir (midnight), "settime 6" on the eastern horizon (sunrise), and
/// "settime 18" on the western horizon (sunset).
///
/// Fractional hours are allowed (e.g. "settime 12.5" = 12:30). A value of 24
/// is treated as midnight (it wraps to phase 0). Values outside [0, 24] are
/// rejected with a clear error. This resolves the live SkyCycle by walking the
/// scene tree (the command registry is a plain class with no node context).
/// The cycle continues advancing from the new time after the jump (it is NOT
/// paused), so the world keeps moving in time from wherever you set it.
/// </summary>
public sealed class SetTimeCommand : ConsoleCommand
{
	public override string Name => "settime";
	public override string Description => "Set the time of day on a 24h clock (0/24=midnight, 12=noon).";
	public override string Usage => "settime <hours 0..24>";

	public override string Execute(string[] args)
	{
		if (args.Length != 1)
		{
			return $"ERR: expected 1 argument. Usage: {Usage}";
		}

		// Parse the hour. InvariantCulture so "12.5" is read correctly
		// regardless of the OS locale (a comma-decimal locale would misread it).
		if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float hours)
			|| float.IsNaN(hours) || float.IsInfinity(hours))
		{
			return $"ERR: '{args[0]}' is not a number. Usage: {Usage} (e.g. settime 12)";
		}

		if (hours < 0f || hours > 24f)
		{
			return $"ERR: hours must be in [0, 24]. Got {hours}. Usage: {Usage}";
		}

		// Convert hours → phase. 0 and 24 both map to midnight (phase 0); 12 →
		// noon (phase 0.5). PosMod keeps 24 from wrapping to a tiny epsilon.
		float phase = hours / 24f;
		if (phase >= 1f)
		{
			phase = 0f; // 24 == midnight
		}

		// Find the live SkyCycle. It is a Node in the running scene (Main.tscn);
		// walk the tree to find it. The command runs from the pause menu, which
		// is in the same tree as the world, so this resolves correctly.
		var tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null)
		{
			return "ERR: no active scene tree.";
		}
		var cycle = FindSkyCycle(tree.Root);
		if (cycle == null)
		{
			return "ERR: no SkyCycle found in this scene (nothing to set the time on).";
		}

		cycle.SetPhase(phase);
		string clock = FormatClock(hours);
		string label = TimeLabel(hours);
		GD.Print($"[Commands] settime {hours} -> {clock} ({label})");
		return $"OK: time set to {clock} ({label}).";
	}

	/// <summary>
	/// Formats a [0,24] hour value as a 24h clock string "HH:MM", e.g. 12 →
	/// "12:00", 12.5 → "12:30", 0 → "00:00", 24 → "24:00". Whole minutes are
	/// rounded (fractional hours below a minute are dropped).
	/// </summary>
	private static string FormatClock(float hours)
	{
		int totalMinutes = (int)System.Math.Round(hours * 60f);
		int h = (totalMinutes / 60) % 24;
		int m = totalMinutes % 60;
		return $"{h:00}:{m:00}";
	}

	/// <summary>
	/// Maps a [0,24] hour value to a short human label for the feedback message:
	/// midnight / early morning / morning / noon / afternoon / evening / night.
	/// Purely for the feedback string.
	/// </summary>
	private static string TimeLabel(float hours)
	{
		float h = hours % 24f;
		if (h < 1f || h >= 23f) return "midnight";
		if (h < 5f) return "pre-dawn";
		if (h < 8f) return "early morning";
		if (h < 11f) return "morning";
		if (h < 13f) return "noon";
		if (h < 17f) return "afternoon";
		if (h < 20f) return "evening";
		if (h < 23f) return "night";
		return "midnight";
	}

	/// <summary>Depth-first search for the first SkyCycle under <paramref name="root"/>.</summary>
	private static SkyCycle FindSkyCycle(Node root)
	{
		if (root is SkyCycle cycle)
		{
			return cycle;
		}
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			var result = FindSkyCycle(root.GetChild(i));
			if (result != null)
			{
				return result;
			}
		}
		return null;
	}
}
