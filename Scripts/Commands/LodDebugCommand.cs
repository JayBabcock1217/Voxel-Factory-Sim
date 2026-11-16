using Godot;
using FactorySim.Voxel;

namespace FactorySim.Commands;

/// <summary>
/// The "loddebug" command: prints a live report of the LOD-1 placeholder
/// system and the hive-spawn gate to the console, for debugging why the
/// placeholder coverage isn't showing / the hives aren't (or are) spawning.
///
/// No arguments. Resolves the live <see cref="VoxelWorld"/> via its static
/// <c>Current</c> handle (set in the world's _Ready) and prints the report
/// through <c>GD.Print</c>. The report is also returned to the UI as feedback
/// (the first line only — the full multi-line report is in the console log).
/// </summary>
public sealed class LodDebugCommand : ConsoleCommand
{
	public override string Name => "loddebug";
	public override string Description => "Print a live report of the LOD-1 placeholder system + hive gate.";
	public override string Usage => "loddebug";

	public override string Execute(string[] args)
	{
		if (args.Length != 0)
		{
			return $"ERR: takes no arguments. Usage: {Usage}";
		}

		// Resolve the live world. The static handle is the fastest path; fall
		// back to walking the tree in case the handle is stale (it is set in the
		// world's _Ready and cleared in _ExitTree).
		VoxelWorld world = VoxelWorld.Current;
		if (world == null || !GodotObject.IsInstanceValid(world))
		{
			var tree = Engine.GetMainLoop() as SceneTree;
			world = tree != null ? FindVoxelWorld(tree.Root) : null;
		}
		if (world == null || !GodotObject.IsInstanceValid(world))
		{
			return "ERR: no live VoxelWorld found in this scene.";
		}

		string report = world.GetLodDebugReport();
		GD.Print(report);
		// Echo the summary lines (everything up to the first "---") to the UI so
		// the player sees a quick verdict; the full per-region detail is in the
		// console log above.
		int cut = report.IndexOf("---", System.StringComparison.Ordinal);
		string summary = cut > 0 ? report.Substring(0, cut).TrimEnd() : report;
		return $"OK: LOD debug printed to console. {summary}";
	}

	/// <summary>Depth-first search for the first VoxelWorld under <paramref name="root"/>.</summary>
	private static VoxelWorld FindVoxelWorld(Node root)
	{
		if (root == null)
		{
			return null;
		}
		if (root is VoxelWorld w)
		{
			return w;
		}
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			var result = FindVoxelWorld(root.GetChild(i));
			if (result != null)
			{
				return result;
			}
		}
		return null;
	}
}
