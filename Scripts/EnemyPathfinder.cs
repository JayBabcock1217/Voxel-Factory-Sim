using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim;

/// <summary>
/// A local grid-based A* pathfinder for AI enemies on voxel terrain. Works on
/// a 2D grid of surface heights (the topmost solid voxel at each XZ column),
/// so the path follows the terrain surface — the zombie walks along the ground
/// and never falls into holes.
///
/// Walkability rules (a cell is traversable from cell A to adjacent cell B when):
/// - B has a solid surface (ground exists, not a hole / void).
/// - The step from A's surface to B's surface is at most <c>MaxStepUp</c> voxels
///   (the zombie can climb a 1-voxel step but not a 3-voxel cliff).
/// - The drop from A to B is at most <c>MaxStepDown</c> voxels (the zombie can
///   walk down a 2-voxel ledge but not into a 10-voxel pit).
///
/// The pathfinder searches a bounded radius around the start (to keep the cost
/// down) and returns a list of world-voxel waypoints. The caller (the zombie)
/// follows the waypoints one at a time, re-pathing periodically.
///
/// This is intentionally local and cheap: it does not build a global navmesh,
/// it does not use the Godot NavigationServer, and it does not consider
/// obstacles other than terrain height. It is designed for the common case of
/// "walk toward the player across nearby terrain" where the world is mostly
/// open surface with occasional holes and small cliffs.
/// </summary>
public static class EnemyPathfinder
{
	/// <summary>
	/// The result of a pathfinding attempt: the path (empty when no route was
	/// found) and, when the path is empty, the direction (world-voxel offset)
	/// of the nearest impassable cell blocking the route. The zombie uses
	/// <see cref="BlockedDirection"/> to know which voxel to break.
	/// </summary>
	public readonly struct PathResult
	{
		/// <summary>The path waypoints (empty when no path was found).</summary>
		public List<Vector3I> Path { get; }

		/// <summary>
		/// When <see cref="Path"/> is empty, the world-voxel direction (relative
		/// to the start) of the nearest impassable cell that blocked the route.
		/// This is the voxel the zombie should break to open a path. Zero when
		/// no blocking cell was found (e.g. the goal itself has no ground).
		/// </summary>
		public Vector3I BlockedDirection { get; }

		public PathResult(List<Vector3I> path, Vector3I blockedDirection)
		{
			Path = path;
			BlockedDirection = blockedDirection;
		}
	}

	/// <summary>
	/// Computes a path from <paramref name="startVoxel"/> to <paramref name="goalVoxel"/>
	/// (world voxel XZ coordinates) on the terrain surface. Returns a
	/// <see cref="PathResult"/> containing the path (a list of world-voxel
	/// waypoints, excluding the start, including the goal) and, when no path is
	/// found, the direction of the blocking cell.
	///
	/// The search is bounded to a square of <c>2 × searchRadius + 1</c> voxels
	/// centered on the start, so the cost is O(R²) where R is the search radius.
	/// For the typical case (player within ~15 m, searchRadius 20) this is at
	/// most 40×40 = 1600 cells — well within budget for a 10 Hz AI tick.
	///
	/// <paramref name="world"/> must be non-null and the chunks covering the
	/// search area must be loaded (GetVoxel returns Air for unloaded chunks,
	/// which the pathfinder treats as void / unwalkable).
	/// </summary>
	public static PathResult FindPath(
		VoxelWorld world,
		Vector3I startVoxel,
		Vector3I goalVoxel,
		int searchRadius,
		int maxStepUp = 2,
		int maxStepDown = 4)
	{
		if (world == null)
		{
			return new PathResult(new List<Vector3I>(), Vector3I.Zero);
		}

		// Snap the goal to the search area: if the goal is outside the search
		// radius, the pathfinder can't reach it directly. We clamp the goal to
		// the search boundary so the path leads toward the player as far as
		// possible (the zombie will re-path on the next tick, closer).
		Vector3I effectiveGoal = ClampToRadius(goalVoxel, startVoxel, searchRadius);

		int startX = startVoxel.X;
		int startZ = startVoxel.Z;

		// Surface height cache: (x, z) → surface Y, or -1 for void.
		// Keyed by a packed long (x << 32 | z) to avoid a Dictionary of tuples.
		var surfaceCache = new Dictionary<long, int>();

		int SurfaceAt(int x, int z)
		{
			long key = (long)x << 32 | (z & 0xFFFFFFFFL);
			if (surfaceCache.TryGetValue(key, out int cached))
			{
				return cached;
			}
			int h = FindSurface(world, x, z);
			surfaceCache[key] = h;
			return h;
		}

		int FindSurface(VoxelWorld w, int x, int z)
		{
			int maxH = w.ChunkHeight;
			for (int y = maxH - 1; y >= 0; y--)
			{
				if (w.GetVoxel(x, y, z) != VoxelMaterialIds.Air)
				{
					return y;
				}
			}
			return -1; // void (no ground)
		}

		// Start and goal must have ground.
		int startH = SurfaceAt(startX, startZ);
		int goalH = SurfaceAt(effectiveGoal.X, effectiveGoal.Z);
		if (startH < 0 || goalH < 0)
		{
			return new PathResult(new List<Vector3I>(), Vector3I.Zero); // no ground at start or goal
		}

		// A* state.
		int sx = startX - startVoxel.X; // offset: start is at (0,0) in local coords
		int sz = startZ - startVoxel.Z;
		int gx = effectiveGoal.X - startVoxel.X;
		int gz = effectiveGoal.Z - startVoxel.Z;

		// Local grid: local (lx, lz) → world (startX + lx, startZ + lz).
		// lx, lz range from -searchRadius to +searchRadius.
		int size = 2 * searchRadius + 1;

		// gScore: cost from start. hScore: heuristic to goal. fScore = g + h.
		// Indexed by (lx + R, lz + R) → 0..size-1 per axis.
		var gScore = new float[size * size];
		var hScore = new float[size * size];
		var fScore = new float[size * size];
		var cameFrom = new int[size * size]; // local index of the predecessor
		var inOpenSet = new bool[size * size];
		var closed = new bool[size * size];

		for (int i = 0; i < gScore.Length; i++)
		{
			gScore[i] = float.PositiveInfinity;
			cameFrom[i] = -1;
		}

		int Idx(int lx, int lz) => (lx + searchRadius) + (lz + searchRadius) * size;

		// Manhattan heuristic (4-directional movement).
		float Heuristic(int lx, int lz) => System.Math.Abs(lx - gx) + System.Math.Abs(lz - gz);

		// 4-directional neighbors.
		int[] DX = { 1, -1, 0, 0 };
		int[] DZ = { 0, 0, 1, -1 };

		// Simple priority queue: a list sorted by fScore. For ≤1600 cells this
		// is fast enough at 10 Hz. A binary heap would be faster for larger
		// searches but the list is simpler and the search is bounded.
		var openSet = new List<int>(); // local indices

		int startIdx = Idx(0, 0);
		gScore[startIdx] = 0f;
		hScore[startIdx] = Heuristic(0, 0);
		fScore[startIdx] = hScore[startIdx];
		openSet.Add(startIdx);
		inOpenSet[startIdx] = true;

		int goalIdx = Idx(gx, gz);

		int found = -1;
		int iterations = 0;
		const int MaxIterations = 100_000; // safety cap

		// Track the rejected cell CLOSEST TO THE START that lies in the
		// direction of the goal, so we can report which voxel is blocking the
		// route when no path is found. The zombie should break the NEAREST
		// wall in its way, not a distant one near the goal.
		//
		// "In the direction of the goal" is approximated by requiring the
		// cell to reduce the Manhattan distance to the goal relative to the
		// start (i.e. it is between the start and the goal, roughly). Among
		// those, we pick the one closest to the start (smallest Manhattan
		// distance from the origin, which is the start in local coords).
		int blockedLx = 0, blockedLz = 0;
		int blockedDist = int.MaxValue;
		bool hasBlocked = false;

		// The total Manhattan distance from start to goal (local coords).
		int totalDistToGoal = System.Math.Abs(gx) + System.Math.Abs(gz);

		void TrackBlocked(int lx, int lz)
		{
			// Distance from this cell to the goal.
			int distToGoal = System.Math.Abs(lx - gx) + System.Math.Abs(lz - gz);
			// Only consider cells that are "in the way": they must be closer
			// to the goal than the start was (distToGoal < totalDistToGoal),
			// meaning they lie roughly between the start and the goal.
			if (distToGoal >= totalDistToGoal)
			{
				return;
			}
			// Among qualifying cells, pick the one closest to the start
			// (local origin). This is the nearest wall in the zombie's path.
			int distFromStart = System.Math.Abs(lx) + System.Math.Abs(lz);
			if (distFromStart < blockedDist)
			{
				blockedDist = distFromStart;
				blockedLx = lx;
				blockedLz = lz;
				hasBlocked = true;
			}
		}

		while (openSet.Count > 0 && iterations < MaxIterations)
		{
			iterations++;

			// Find the node with the lowest fScore in the open set.
			int bestI = 0;
			float bestF = fScore[openSet[0]];
			for (int i = 1; i < openSet.Count; i++)
			{
				float f = fScore[openSet[i]];
				if (f < bestF)
				{
					bestF = f;
					bestI = i;
				}
			}
			int current = openSet[bestI];
			openSet.RemoveAt(bestI);
			inOpenSet[current] = false;
			closed[current] = true;

			if (current == goalIdx)
			{
				found = current;
				break;
			}

			int clx = (current % size) - searchRadius;
			int clz = (current / size) - searchRadius;
			int cWorldX = startX + clx;
			int cWorldZ = startZ + clz;
			int cH = SurfaceAt(cWorldX, cWorldZ);

			for (int d = 0; d < 4; d++)
			{
				int nlx = clx + DX[d];
				int nlz = clz + DZ[d];

				// Bounds: stay within the search radius.
				if (nlx < -searchRadius || nlx >= size - searchRadius
				 || nlz < -searchRadius || nlz >= size - searchRadius)
				{
					continue;
				}

				int nIdx = Idx(nlx, nlz);
				if (closed[nIdx])
				{
					continue;
				}

				int nWorldX = startX + nlx;
				int nWorldZ = startZ + nlz;
				int nH = SurfaceAt(nWorldX, nWorldZ);

				// The neighbor must have ground.
				if (nH < 0)
				{
					TrackBlocked(nlx, nlz);
					continue;
				}

				// Step check: the surface difference must be within limits.
				int stepDiff = nH - cH;
				if (stepDiff > maxStepUp || -stepDiff > maxStepDown)
				{
					TrackBlocked(nlx, nlz);
					continue;
				}

				float tentativeG = gScore[current] + 1f;
				if (tentativeG < gScore[nIdx])
				{
					cameFrom[nIdx] = current;
					gScore[nIdx] = tentativeG;
					hScore[nIdx] = Heuristic(nlx, nlz);
					fScore[nIdx] = tentativeG + hScore[nIdx];

					if (!inOpenSet[nIdx])
					{
						openSet.Add(nIdx);
						inOpenSet[nIdx] = true;
					}
				}
			}
		}

		if (found < 0)
		{
			// No path found. Report the blocking cell (closest to the goal).
			Vector3I blocked = hasBlocked
				? new Vector3I(blockedLx, 0, blockedLz)
				: Vector3I.Zero;
			return new PathResult(new List<Vector3I>(), blocked);
		}

		// Reconstruct the path.
		var path = new List<Vector3I>();
		int node = found;
		while (node != startIdx && node != -1)
		{
			int lx = (node % size) - searchRadius;
			int lz = (node / size) - searchRadius;
			path.Add(new Vector3I(startX + lx, 0, startZ + lz)); // Y=0; the zombie uses surface height
			node = cameFrom[node];
		}
		path.Reverse();

		// Set the Y of each waypoint to the surface height (the topmost solid
		// voxel). The zombie uses this to compute the walk target position.
		for (int i = 0; i < path.Count; i++)
		{
			int h = SurfaceAt(path[i].X, path[i].Z);
			if (h >= 0)
			{
				path[i] = new Vector3I(path[i].X, h, path[i].Z);
			}
		}

		return new PathResult(path, Vector3I.Zero);
	}

	/// <summary>
	/// Clamps a goal voxel to the search radius around the start, so the
	/// pathfinder always has a reachable goal. The clamped point is on the
	/// boundary of the search square, in the direction of the original goal.
	/// </summary>
	private static Vector3I ClampToRadius(Vector3I goal, Vector3I start, int radius)
	{
		int dx = goal.X - start.X;
		int dz = goal.Z - start.Z;
		int maxDx = System.Math.Abs(dx);
		int maxDz = System.Math.Abs(dz);
		int maxD = System.Math.Max(maxDx, maxDz);

		if (maxD <= radius)
		{
			return goal; // already within radius
		}

		// Scale the offset to the boundary.
		float scale = (float)radius / maxD;
		return new Vector3I(
			start.X + (int)System.Math.Round(dx * scale),
			goal.Y,
			start.Z + (int)System.Math.Round(dz * scale));
	}
}
