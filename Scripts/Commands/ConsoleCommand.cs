using Godot;

namespace FactorySim.Commands;

/// <summary>
/// Base class for a typed in-game console command. Each command is its own
/// script (a <see cref="ConsoleCommand"/> subclass) with a name, a human-readable
/// description, a usage string, and an <see cref="Execute"/> method that runs
/// when the player types the command into the pause-menu command input.
///
/// A command is registered with the <see cref="CommandRegistry"/> (usually from
/// the registry's static constructor or from a scene's _Ready). When the player
/// submits text, the registry splits it into the command name + arguments and
/// calls the matching command's <see cref="Execute"/>. The command returns a
/// short result message (success or error) that the UI shows as feedback, so the
/// player gets immediate confirmation without having to watch the world.
/// </summary>
public abstract class ConsoleCommand
{
	/// <summary>
	/// The command's keyword, as the player types it (lowercase). E.g. "settime".
	/// Matching is case-insensitive, so the player can type "SetTime" too.
	/// </summary>
	public abstract string Name { get; }

	/// <summary>
	/// A one-line human-readable description of what the command does. Shown in
	/// a "help" listing and as the input's placeholder hint.
	/// </summary>
	public abstract string Description { get; }

	/// <summary>
	/// The argument syntax, e.g. "settime &lt;0..1&gt;". Shown when the command is
	/// used with the wrong number of arguments so the player knows the format.
	/// </summary>
	public abstract string Usage { get; }

	/// <summary>
	/// Runs the command. <paramref name="args"/> is the submitted text split on
	/// whitespace with the command NAME removed (so for "settime 0.5" the args
	/// are ["0.5"]). Returns a short feedback message for the UI: start with
	/// "OK:" for success or "ERR:" for a problem (the UI colours them
	/// accordingly). Do not throw — return an "ERR:" string instead, so one bad
	/// command never crashes the menu.
	/// </summary>
	public abstract string Execute(string[] args);
}
