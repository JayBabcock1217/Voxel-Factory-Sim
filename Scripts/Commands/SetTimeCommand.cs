using Godot;
using System.Globalization;

namespace FactorySim.Commands;

/// <summary>
/// The "settime" command: jumps the day/night cycle to a specific point in the
/// cycle. Takes a single value in [0, 1] where 0 = midnight, 0.25 = sunrise,
/// 0.5 = noon, 0.75 = sunset. The value is the phase of the SkyCycle (the same
/// 0..1 scale its Phase property uses), so "settime 0.5" puts the world at noon
/// and "settime 0.25" at sunrise.
///
/// This resolves the live SkyCycle node by walking the scene tree (the command
/// registry is a plain class with no node context). If no SkyCycle is present
/// (e.g. a scene without one) it reports an error rather than failing silently.
/// The cycle continues from the new phase after the jump (it is NOT paused), so
/// the world keeps advancing in time from wherever you set it.
/// </summary>
public sealed class SetTimeCommand : ConsoleCommand
{
	public override string Name => "settime";
	public override string Description => "Set the time of day (0=midnight, 0.5=noon, 0.75=sunset).";
	public override string Usage => "settime <0..1>";

	public override string Execute(string[] args)
	{
		if (args.Length != 1)
		{
			return $"ERR: expected 1 argument. Usage: {Usage}";
		}

		// Parse the value. Use InvariantCulture so "0.5" is understood regardless
		// of the OS locale (a comma-decimal locale would otherwise misread it).
		if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
			|| float.IsNaN(value) || float.IsInfinity(value))
		{
			return $"ERR: '{args[0]}' is not a number. Usage: {Usage} (e.g. settime 0.5)";
		}

		if (value < 0f || value > 1f)
		{
			return $"ERR: value must be in [0, 1]. Got {value}. Usage: {Usage}";
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

		cycle.SetPhase(value);
		// Describe the result in terms the player understands (a clock-ish label).
		string label = PhaseLabel(value);
		GD.Print($"[Commands] settime {value} -> {label}");
		return $"OK: time set to {value:F2} ({label}).";
	}

	/// <summary>
	/// Maps a phase in [0,1) to a short human label: 0=midnight, 0.25=sunrise,
	/// 0.5=noon, 0.75=sunset, with "day"/"night" in between. Purely for the
	/// feedback message.
	/// </summary>
	private static string PhaseLabel(float phase)
	{
		if (phase < 0.0625f || phase >= 0.9375f) return "midnight";
		if (phase < 0.3125f) return phase < 0.1875f ? "early morning" : "morning";
		if (phase < 0.4375f) return "midday";
		if (phase < 0.5625f) return "noon";
		if (phase < 0.6875f) return "afternoon";
		if (phase < 0.8125f) return "evening";
		return "late night";
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
