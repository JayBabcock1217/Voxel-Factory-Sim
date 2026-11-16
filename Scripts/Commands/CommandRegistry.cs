using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FactorySim.Commands;

/// <summary>
/// Holds the set of registered <see cref="ConsoleCommand"/>s and dispatches
/// submitted text to the matching command. The pause-menu command input calls
/// <see cref="Run"/> with the raw text the player typed; the registry parses it
/// (first token = command name, rest = args), finds the command, and returns the
/// command's feedback string.
///
/// Commands register themselves in <see cref="RegisterDefaults"/> (called once
/// from the registry's static constructor), so adding a new command is just:
/// create a ConsoleCommand subclass and add one line in RegisterDefaults. The
/// registry is a plain class (not a node) — it needs no tree access; commands
/// that need a live node (e.g. SkyCycle) resolve it from the SceneTree inside
/// their Execute.
/// </summary>
public sealed class CommandRegistry
{
	/// <summary>
	/// The shared registry. Commands are registered once (lazily) so the pause
	/// menu and any future command UI share the same command set.
	/// </summary>
	public static CommandRegistry Instance { get; } = new();

	private readonly Dictionary<string, ConsoleCommand> _commands =
		new(StringComparer.OrdinalIgnoreCase);

	private static bool _registered;

	private CommandRegistry()
	{
		RegisterDefaults();
	}

	/// <summary>
	/// Registers all the built-in commands. Adding a new command is a one-line
	/// change here. Guarded so it only runs once even though Instance's
	/// constructor and any explicit re-init both call it.
	/// </summary>
	private void RegisterDefaults()
	{
		if (_registered)
		{
			return;
		}
		_registered = true;
		Register(new HelpCommand());
		Register(new SetTimeCommand());
		Register(new LodDebugCommand());
		Register(new LodDistanceCommand());
	}

	/// <summary>Registers a command (replaces any prior command with the same name).</summary>
	public void Register(ConsoleCommand command)
	{
		if (command == null)
		{
			return;
		}
		_commands[command.Name] = command;
	}

	/// <summary>All registered commands, in registration order (for a "help" listing).</summary>
	public IEnumerable<ConsoleCommand> Commands => _commands.Values;

	/// <summary>
	/// True when <paramref name="name"/> matches a registered command
	/// (case-insensitive). Used by the UI to show a usage hint.
	/// </summary>
	public bool TryGet(string name, out ConsoleCommand command)
	{
		return _commands.TryGetValue(name, out command);
	}

	/// <summary>
	/// Parses and runs a submitted command line. Returns a feedback string for
	/// the UI. Handles: empty input (no-op), unknown commands, and per-command
	/// argument errors. Never throws — all failures come back as "ERR:" strings.
	/// </summary>
	public string Run(string input)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return "OK: (empty)";
		}

		// Split on any run of whitespace (space, tab). The first token is the
		// command name; the rest are its arguments. RemoveEmptyEntries collapses
		// runs of whitespace so "settime   0.5" parses the same as "settime 0.5".
		char[] whitespace = { ' ', '\t' };
		var tokens = input.Trim().Split(whitespace, StringSplitOptions.RemoveEmptyEntries);
		if (tokens.Length == 0)
		{
			return "OK: (empty)";
		}

		string name = tokens[0];
		if (!TryGet(name, out var command))
		{
			return $"ERR: unknown command '{name}'. Type 'help' to list commands.";
		}

		var args = new string[tokens.Length - 1];
		Array.Copy(tokens, 1, args, 0, args.Length);

		try
		{
			return command.Execute(args);
		}
		catch (Exception e)
		{
			// A command should not throw, but if one does, surface it as an error
			// so the menu never crashes.
			GD.PushError($"[Commands] '{name}' threw: {e.Message}");
			return $"ERR: '{name}' failed: {e.Message}";
		}
	}

	/// <summary>
	/// Builds a multi-line "help" string listing every command with its usage and
	/// description. The pause menu shows this when the player types "help".
	/// </summary>
	public string BuildHelp()
	{
		var lines = new List<string> { "Commands:" };
		foreach (var c in _commands.Values)
		{
			lines.Add($"  {c.Usage}  —  {c.Description}");
		}
		return string.Join("\n", lines);
	}
}
