using Godot;
using System.Globalization;
using FactorySim.Voxel;

namespace FactorySim.Commands;

/// <summary>
/// The "loddist" command: read or set the far-LOD render distance
/// (<see cref="VoxelWorld.LodRenderDistanceChunks"/>) at runtime, for tuning
/// the distant-horizon load without editing the scene.
///
///   loddist            → prints the current value
///   loddist 24         → sets it to 24 base chunks
///
/// The far LOD region count scales with the SQUARE of this value (and its
/// per-frame scan is O(distance²)), so a large value (e.g. 128) creates
/// thousands of regions that saturate the thread pool at load-in — starving
/// the near full-res chunks and the LOD-1 placeholder. Dropping it (e.g. to
/// 24–48) makes the horizon load quickly so the near terrain fills in fast.
/// The change takes effect on the next frame's UpdateLod (existing regions
/// beyond the new distance are freed, nearer ones built).
/// </summary>
public sealed class LodDistanceCommand : ConsoleCommand
{
	public override string Name => "loddist";
	public override string Description => "Read/set the far-LOD render distance (LodRenderDistanceChunks).";
	public override string Usage => "loddist [chunks 1..256]";

	public override string Execute(string[] args)
	{
		VoxelWorld world = ResolveWorld();
		if (world == null)
		{
			return "ERR: no live VoxelWorld found in this scene.";
		}

		if (args.Length == 0)
		{
			return $"OK: LodRenderDistanceChunks={world.LodRenderDistanceChunks} (LodLevels={world.LodLevels}, LodRes={world.LodMeshResolution}).";
		}

		if (args.Length != 1)
		{
			return $"ERR: expected 0 or 1 argument. Usage: {Usage}";
		}
		if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < 1 || v > 256)
		{
			return $"ERR: '{args[0]}' is not an integer in [1,256]. Usage: {Usage}";
		}

		world.LodRenderDistanceChunks = v;
		return $"OK: LodRenderDistanceChunks set to {v}. (Region count scales with the square — expect ~{(v * v) / 4}+ far-LOD regions.)";
	}

	private static VoxelWorld ResolveWorld()
	{
		VoxelWorld world = VoxelWorld.Current;
		if (world != null && GodotObject.IsInstanceValid(world))
		{
			return world;
		}
		var tree = Engine.GetMainLoop() as SceneTree;
		return tree != null ? FindVoxelWorld(tree.Root) : null;
	}

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
			var r = FindVoxelWorld(root.GetChild(i));
			if (r != null)
			{
				return r;
			}
		}
		return null;
	}
}
