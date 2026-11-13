using System;
using System.Globalization;
using System.Linq;

namespace FactorySim.Commands;

/// <summary>
/// The "help" command: lists every registered command with its usage and a
/// short description. Takes no arguments (extra tokens are ignored). Always
/// succeeds — it just reports the command table, which is how the player
/// discovers what else they can type.
/// </summary>
public sealed class HelpCommand : ConsoleCommand
{
	public override string Name => "help";
	public override string Description => "List all available commands.";
	public override string Usage => "help";

	public override string Execute(string[] args)
	{
		// Delegate to the registry's formatted listing. Instance is the shared
		// registry this command was registered into.
		return "OK:\n" + CommandRegistry.Instance.BuildHelp();
	}
}
