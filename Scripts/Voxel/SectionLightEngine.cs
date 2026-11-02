namespace FactorySim.Voxel;

using System;
using System.Collections.Generic;

/// <summary>
/// PER-SECTION light engine (the Minecraft/Starlight model). Light is stored in
/// per-section 16×16×16 arrays (one per chunk section), but the BFS propagation
/// queue is GLOBAL — it crosses section borders and chunk borders. This is what
/// makes it converge correctly (no hand-off approximation, no per-section
/// inconsistency) while keeping the cost bounded by the light sphere (not the
/// whole 3×3-chunk wide field).
///
/// HOW IT WORKS
///   * STORAGE: each section has an int[4096] array of raw light levels (0..15),
///     indexed localX + 16*(localY + 16*localZ). A section's light is only valid
///     when its data (solid/air + block sources) is loaded.
///   * PROPAGATION: a single BFS over the global queue. When light reaches a
///     section border, it crosses into the neighboring section (same chunk or
///     adjacent chunk) and continues. The queue holds (section, localIndex,
///     level). The BFS visits each voxel at most once per propagation run
///     (increase: only if the new level exceeds the stored level; decrease:
///     only if the stored level exceeds the expected level).
///   * INCREASE: seed the source section at the source level, run the BFS. Light
///     propagates through all connected air, crossing section/chunk borders.
///   * DECREASE: when a source is removed, run a decrease BFS. Voxels that lose
///     their primary source are set to 0 and queued for re-lighting from
///     remaining sources.
///   * COST: a torch (level 14) visits ~15³ = 3,375 voxels — the light sphere —
///     regardless of how many sections/chunks it crosses. Compare to the old
///     1.6M-voxel wide-field delta.
///
/// THIS IS THE INSTANT-EDIT PATH. The async full rebuild re-bakes all section
/// fields from scratch (authoritative convergence); this engine is the
/// same-frame approximation that the full rebuild re-converges.
///
/// Thread safety: main thread only (the instant-edit path runs on the main
/// thread). The async full rebuild re-bakes the fields on the worker thread,
/// but it does NOT call into this engine — it writes the fields directly.
/// </summary>
public sealed class SectionLightEngine
{
	// Floor division: unlike C# integer division (which truncates toward zero),
	// this rounds toward negative infinity. Required for correct chunk/section
	// indices when world coordinates are negative (C#'s `a / b` for negative
	// `a` gives the wrong chunk index, making the local coord negative and
	// out-of-bounds).
	private static int FloorDiv(int a, int b)
	{
		int q = a / b;
		int r = a % b;
		if (r != 0 && ((r < 0) != (b < 0)))
		{
			q--;
		}
		return q;
	}

	// Section key: (chunkX, chunkY, secX, secY, secZ) packed into a long.
	// chunkX: 20 bits, chunkY: 20 bits, secX: 4 bits, secY: 4 bits, secZ: 4 bits.
	private static long SectionKey(int cx, int cy, int sx, int sy, int sz)
	{
		// Bias to handle negative chunk coords.
		int bx = cx + (1 << 19);
		int by = cy + (1 << 19);
		return (long)bx << 32 | (long)by << 12 | (long)sx << 8 | (long)sy << 4 | (long)sz;
	}

	private static void DecodeKey(long key, out int cx, out int cy, out int sx, out int sy, out int sz)
	{
		int bz = (int)(key & 0xF);
		int by2 = (int)((key >> 4) & 0xF);
		int bx2 = (int)((key >> 8) & 0xF);
		int by = (int)((key >> 12) & 0xFFFFF);
		int bx = (int)((key >> 32) & 0xFFFFF);
		cx = bx - (1 << 19);
		cy = by - (1 << 19);
		sx = bx2;
		sy = by2;
		sz = bz;
	}

	// The per-section light fields. Key = packed section key, value = int[4096]
	// of raw light levels (0..15). A section is present only when its field has
	// been computed (by the full rebuild or a prior propagation).
	private readonly Dictionary<long, int[]> _fields = new();

	// The solid/air state per section (for the BFS to know what's opaque).
	// Key = packed section key, value = byte[4096] (1 = solid, 0 = air).
	private readonly Dictionary<long, byte[]> _solid = new();

	// The block-light sources per section (for re-lighting after a decrease).
	// Key = packed section key, value = byte[4096] (source level 0..15, 0 = none).
	private readonly Dictionary<long, byte[]> _sources = new();

	// The BFS queue (reused across calls). Holds (sectionKey, localIndex, level).
	private struct QueueEntry
	{
		public long Section;
		public int Index;
		public int Level;
	}
	private QueueEntry[] _queue = new QueueEntry[8192];
	private int _qHead, _qTail;

	void Enq(long section, int index, int level)
	{
		if (_qTail == _queue.Length)
		{
			// Grow the queue (rare — a torch's light sphere is ~3k voxels).
			var grown = new QueueEntry[_queue.Length * 2];
			Array.Copy(_queue, 0, grown, 0, _qHead);
			for (int i = _qHead; i < _qTail; i++)
				grown[i - _qHead] = _queue[i];
			_qTail -= _qHead;
			_qHead = 0;
			_queue = grown;
		}
		_queue[_qTail++] = new QueueEntry { Section = section, Index = index, Level = level };
	}

	bool Deq(out QueueEntry entry)
	{
		if (_qHead >= _qTail)
		{
			entry = default;
			return false;
		}
		entry = _queue[_qHead++];
		return true;
	}

	void ClearQueue()
	{
		_qHead = 0;
		_qTail = 0;
	}

	/// <summary>
	/// Registers a section's data (solid/air + block sources) with the engine.
	/// Called by the full rebuild after it bakes a section's light field.
	/// </summary>
	public void RegisterSection(
		int chunkX, int chunkY, int secX, int secY, int secZ,
		byte[] solid, byte[] sources, int[] light)
	{
		long key = SectionKey(chunkX, chunkY, secX, secY, secZ);
		_solid[key] = solid;
		_sources[key] = sources;
		_fields[key] = light;
	}

	/// <summary>
	/// Removes a section (chunk unloaded). Its light field is discarded.
	/// </summary>
	public void RemoveSection(int chunkX, int chunkY, int secX, int secY, int secZ)
	{
		long key = SectionKey(chunkX, chunkY, secX, secY, secZ);
		_solid.Remove(key);
		_sources.Remove(key);
		_fields.Remove(key);
	}

	/// <summary>
	/// Decomposes a world position into (chunkX, chunkZ, secX, secY, secZ,
	/// localX, localY, localZ) using FLOOR division (correct for negative
	/// world coordinates).
	/// </summary>
	private static void DecomposeWorldPos(
		int worldX, int worldY, int worldZ, int chunkSizeXZ,
		out int cx, out int cz, out int secX, out int secY, out int secZ,
		out int localX, out int localY, out int localZ)
	{
		cx = FloorDiv(worldX, chunkSizeXZ);
		cz = FloorDiv(worldZ, chunkSizeXZ);
		secY = FloorDiv(worldY, 16);
		int lx = worldX - cx * chunkSizeXZ;
		int lz = worldZ - cz * chunkSizeXZ;
		secX = FloorDiv(lx, 16);
		secZ = FloorDiv(lz, 16);
		localX = lx - secX * 16;
		localY = worldY - secY * 16;
		localZ = lz - secZ * 16;
	}

	/// <summary>
	/// Samples the light level (0..15) at a world position. Returns 0 if the
	/// section is not registered.
	/// </summary>
	public int SampleLight(int worldX, int worldY, int worldZ, int chunkSizeXZ, int chunkHeight)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		if (!_fields.TryGetValue(key, out int[] field))
		{
			return 0;
		}
		int idx = localX + 16 * (localY + 16 * localZ);
		return field[idx];
	}

	/// <summary>
	/// Reads the solid/air state at a world position. Returns 0 (air) if the
	/// section is not registered.
	/// </summary>
	public int SampleSolid(int worldX, int worldY, int worldZ, int chunkSizeXZ)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		if (!_solid.TryGetValue(key, out byte[] solid))
		{
			return 0; // unloaded = air
		}
		int idx = localX + 16 * (localY + 16 * localZ);
		return solid[idx];
	}

	/// <summary>
	/// Reads the block-light source level at a world position. Returns 0 if the
	/// section is not registered or the voxel has no source.
	/// </summary>
	public int SampleSource(int worldX, int worldY, int worldZ, int chunkSizeXZ)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		if (!_sources.TryGetValue(key, out byte[] sources))
		{
			return 0;
		}
		int idx = localX + 16 * (localY + 16 * localZ);
		return sources[idx];
	}

	/// <summary>
	/// True if the section containing (worldX, worldY, worldZ) is registered
	/// (has a light field). Used to distinguish "unregistered section" (fall
	/// back to the wide field) from "registered section with light level 0"
	/// (dark — use the engine's value).
	/// </summary>
	public bool IsSectionRegistered(int worldX, int worldY, int worldZ, int chunkSizeXZ)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out _, out _, out _);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		return _fields.ContainsKey(key);
	}

	/// <summary>
	/// Updates a single voxel's solid/air + block source state in the section
	/// data (called after SetVoxel). This ensures the BFS sees the new state.
	/// </summary>
	public void UpdateVoxel(int worldX, int worldY, int worldZ, int chunkSizeXZ, byte solid, byte source)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		if (!_solid.TryGetValue(key, out byte[] solidArr))
		{
			return; // section not registered — the full rebuild will handle it
		}
		int idx = localX + 16 * (localY + 16 * localZ);
		solidArr[idx] = solid;
		if (_sources.TryGetValue(key, out byte[] sources))
		{
			sources[idx] = source;
		}
	}

	/// <summary>
	/// PROPAGATES A LIGHT INCREASE: a light source was added (or brightened) at
	/// (worldX, worldY, worldZ) with level <paramref name="level"/>. Runs a BFS
	/// from the source through all connected air, crossing section/chunk
	/// borders. Returns the set of sections whose light field changed (the
	/// caller re-meshes the sub-cells in those sections).
	///
	/// The BFS is the Starlight increase algorithm: for each queued voxel,
	/// propagate to its 6 air neighbors at (level - 1), only if the new level
	/// exceeds the stored level. This visits each voxel at most once (the first
	/// visit sets the highest level, subsequent visits are no-ops).
	/// </summary>
	public HashSet<(int cx, int cy, int sx, int sy, int sz)> PropagateIncrease(
		int worldX, int worldY, int worldZ, int level, int chunkSizeXZ, int chunkHeight)
	{
		var changed = new HashSet<(int, int, int, int, int)>();
		if (level <= 0)
		{
			return changed;
		}

		// The source's section + local index (floor division for negative coords).
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);

		// Seed the source's air neighbors (the source itself is solid, so light
		// emits INTO the air around it, at level - 1).
		ClearQueue();
		SeedAirNeighbors(cx, cz, secX, secY, secZ, localX, localY, localZ, level - 1, chunkSizeXZ, chunkHeight);

		// Run the BFS.
		RunBFSIncrease(chunkSizeXZ, chunkHeight, changed);

		return changed;
	}

	/// <summary>
	/// PROPAGATES A LIGHT DECREASE: a light source was removed (or darkened) at
	/// (worldX, worldY, worldZ). Runs a decrease BFS: voxels that were lit by
	/// the removed source are set to 0 and queued for re-lighting from
	/// remaining sources. Returns the set of sections whose light field changed.
	/// </summary>
	public HashSet<(int cx, int cy, int sx, int sy, int sz)> PropagateDecrease(
		int worldX, int worldY, int worldZ, int oldLevel, int chunkSizeXZ, int chunkHeight)
	{
		var changed = new HashSet<(int, int, int, int, int)>();
		if (oldLevel <= 0)
		{
			return changed;
		}

		// The source's section + local index.
		// The source's section + local index (floor division for negative coords).
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);

		// Decrease BFS: set the source's air neighbors to 0 (if they were lit by
		// this source) and queue them for re-lighting.
		ClearQueue();
		SeedDecrease(cx, cz, secX, secY, secZ, localX, localY, localZ, oldLevel - 1, chunkSizeXZ, chunkHeight);

		// Run the decrease BFS.
		RunBFSDecrease(chunkSizeXZ, chunkHeight, changed);

		// After the decrease, re-light from remaining sources: run an increase
		// BFS from all remaining sources in the affected sections.
		RunBFSRelight(changed, chunkSizeXZ, chunkHeight);

		return changed;
	}

	/// <summary>
	/// Seeds the air neighbors of a solid source voxel at the given level.
	/// Used by PropagateIncrease.
	/// </summary>
	private void SeedAirNeighbors(
		int cx, int cz, int secX, int secY, int secZ,
		int localX, int localY, int localZ,
		int level, int chunkSizeXZ, int chunkHeight)
	{
		if (level <= 0)
		{
			return;
		}
		// The 6 neighbors of the source voxel (in world coords).
		int wx = cx * chunkSizeXZ + secX * 16 + localX;
		int wy = secY * 16 + localY;
		int wz = cz * chunkSizeXZ + secZ * 16 + localZ;
		int[] dxs = { -1, 1, 0, 0, 0, 0 };
		int[] dys = { 0, 0, -1, 1, 0, 0 };
		int[] dzs = { 0, 0, 0, 0, -1, 1 };
		for (int i = 0; i < 6; i++)
		{
			int nx = wx + dxs[i];
			int ny = wy + dys[i];
			int nz = wz + dzs[i];
			if (ny < 0 || ny >= chunkHeight)
			{
				continue;
			}
			// Is the neighbor air?
			if (SampleSolid(nx, ny, nz, chunkSizeXZ) != 0)
			{
				continue; // solid — light doesn't pass
			}
			// Is the neighbor's current light less than the new level?
			int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
			if (current >= level)
			{
				continue; // already as bright or brighter
			}
			// Set the neighbor's light and enqueue it.
			SetLight(nx, ny, nz, level, chunkSizeXZ);
			int ncx = nx / chunkSizeXZ;
			int ncz = nz / chunkSizeXZ;
			int nsecY = ny / 16;
			int nlx = nx - ncx * chunkSizeXZ;
			int nlz = nz - ncz * chunkSizeXZ;
			int nsecX = nlx / 16;
			int nsecZ = nlz / 16;
			int nlocalX = nlx - nsecX * 16;
			int nlocalY = ny - nsecY * 16;
			int nlocalZ = nlz - nsecZ * 16;
			long key = SectionKey(ncx, ncz, nsecX, nsecY, nsecZ);
			Enq(key, nlocalX + 16 * (nlocalY + 16 * nlocalZ), level);
		}
	}

	/// <summary>
	/// Seeds the decrease BFS: for each air neighbor of the removed source, if
	/// its current light level is >= the removed source's contribution, set it
	/// to 0 and enqueue it.
	/// </summary>
	private void SeedDecrease(
		int cx, int cz, int secX, int secY, int secZ,
		int localX, int localY, int localZ,
		int level, int chunkSizeXZ, int chunkHeight)
	{
		if (level <= 0)
		{
			return;
		}
		int wx = cx * chunkSizeXZ + secX * 16 + localX;
		int wy = secY * 16 + localY;
		int wz = cz * chunkSizeXZ + secZ * 16 + localZ;
		int[] dxs = { -1, 1, 0, 0, 0, 0 };
		int[] dys = { 0, 0, -1, 1, 0, 0 };
		int[] dzs = { 0, 0, 0, 0, -1, 1 };
		for (int i = 0; i < 6; i++)
		{
			int nx = wx + dxs[i];
			int ny = wy + dys[i];
			int nz = wz + dzs[i];
			if (ny < 0 || ny >= chunkHeight)
			{
				continue;
			}
			int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
			if (current < level)
			{
				continue; // not lit by this source (or not lit at all)
			}
			// Set to 0 and enqueue for re-lighting.
			SetLight(nx, ny, nz, 0, chunkSizeXZ);
			int ncx = nx / chunkSizeXZ;
			int ncz = nz / chunkSizeXZ;
			int nsecY = ny / 16;
			int nlx = nx - ncx * chunkSizeXZ;
			int nlz = nz - ncz * chunkSizeXZ;
			int nsecX = nlx / 16;
			int nsecZ = nlz / 16;
			int nlocalX = nlx - nsecX * 16;
			int nlocalY = ny - nsecY * 16;
			int nlocalZ = nlz - nsecZ * 16;
			long key = SectionKey(ncx, ncz, nsecX, nsecY, nsecZ);
			Enq(key, nlocalX + 16 * (nlocalY + 16 * nlocalZ), current);
		}
	}

	/// <summary>
	/// The increase BFS: propagates light through air, crossing section/chunk
	/// borders. Visits each voxel at most once (the first visit sets the
	/// highest level).
	/// </summary>
	private void RunBFSIncrease(int chunkSizeXZ, int chunkHeight, HashSet<(int, int, int, int, int)> changed)
	{
		while (Deq(out var entry))
		{
			int level = entry.Level;
			if (level <= 1)
			{
				continue; // level 1 doesn't propagate (same as Starlight)
			}
			int newLevel = level - 1;

			// Decode the section + local index.
			DecodeKey(entry.Section, out int cx, out int cz, out int secX, out int secY, out int secZ);
			int localX = entry.Index % 16;
			int rem = entry.Index / 16;
			int localY = rem % 16;
			int localZ = rem / 16;

			// World coords of this voxel.
			int wx = cx * chunkSizeXZ + secX * 16 + localX;
			int wy = secY * 16 + localY;
			int wz = cz * chunkSizeXZ + secZ * 16 + localZ;

			// The 6 neighbors.
			int[] dxs = { -1, 1, 0, 0, 0, 0 };
			int[] dys = { 0, 0, -1, 1, 0, 0 };
			int[] dzs = { 0, 0, 0, 0, -1, 1 };
			for (int i = 0; i < 6; i++)
			{
				int nx = wx + dxs[i];
				int ny = wy + dys[i];
				int nz = wz + dzs[i];
				if (ny < 0 || ny >= chunkHeight)
				{
					continue;
				}
				// Is the neighbor air?
				if (SampleSolid(nx, ny, nz, chunkSizeXZ) != 0)
				{
					continue;
				}
				// Is the neighbor's current light less than the new level?
				int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
				if (current >= newLevel)
				{
					continue; // already as bright or brighter
				}
				// Set the neighbor's light and enqueue it.
				SetLight(nx, ny, nz, newLevel, chunkSizeXZ);
				int ncx = nx / chunkSizeXZ;
				int ncz = nz / chunkSizeXZ;
				int nsecY = ny / 16;
				int nlx = nx - ncx * chunkSizeXZ;
				int nlz = nz - ncz * chunkSizeXZ;
				int nsecX = nlx / 16;
				int nsecZ = nlz / 16;
				int nlocalX = nlx - nsecX * 16;
				int nlocalY = ny - nsecY * 16;
				int nlocalZ = nlz - nsecZ * 16;
				long key = SectionKey(ncx, ncz, nsecX, nsecY, nsecZ);
				Enq(key, nlocalX + 16 * (nlocalY + 16 * nlocalZ), newLevel);
				changed.Add((ncx, ncz, nsecX, nsecY, nsecZ));
			}
		}
	}

	/// <summary>
	/// The decrease BFS: propagates the removal through air, crossing
	/// section/chunk borders. Voxels that lose their light are set to 0 and
	/// queued for re-lighting.
	/// </summary>
	private void RunBFSDecrease(int chunkSizeXZ, int chunkHeight, HashSet<(int, int, int, int, int)> changed)
	{
		while (Deq(out var entry))
		{
			int oldLevel = entry.Level;
			if (oldLevel <= 0)
			{
				continue;
			}

			// Decode the section + local index.
			DecodeKey(entry.Section, out int cx, out int cz, out int secX, out int secY, out int secZ);
			int localX = entry.Index % 16;
			int rem = entry.Index / 16;
			int localY = rem % 16;
			int localZ = rem / 16;

			int wx = cx * chunkSizeXZ + secX * 16 + localX;
			int wy = secY * 16 + localY;
			int wz = cz * chunkSizeXZ + secZ * 16 + localZ;

			// The 6 neighbors.
			int[] dxs = { -1, 1, 0, 0, 0, 0 };
			int[] dys = { 0, 0, -1, 1, 0, 0 };
			int[] dzs = { 0, 0, 0, 0, -1, 1 };
			for (int i = 0; i < 6; i++)
			{
				int nx = wx + dxs[i];
				int ny = wy + dys[i];
				int nz = wz + dzs[i];
				if (ny < 0 || ny >= chunkHeight)
				{
					continue;
				}
				int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
				if (current == 0)
				{
					continue; // already dark
				}
				// If the neighbor's light is less than the level we're
				// propagating, it was lit by the removed source — set to 0.
				if (current < oldLevel)
				{
					SetLight(nx, ny, nz, 0, chunkSizeXZ);
					int ncx = nx / chunkSizeXZ;
					int ncz = nz / chunkSizeXZ;
					int nsecY = ny / 16;
					int nlx = nx - ncx * chunkSizeXZ;
					int nlz = nz - ncz * chunkSizeXZ;
					int nsecX = nlx / 16;
					int nsecZ = nlz / 16;
					int nlocalX = nlx - nsecX * 16;
					int nlocalY = ny - nsecY * 16;
					int nlocalZ = nlz - nsecZ * 16;
					long key = SectionKey(ncx, ncz, nsecX, nsecY, nsecZ);
					Enq(key, nlocalX + 16 * (nlocalY + 16 * nlocalZ), current);
					changed.Add((ncx, ncz, nsecX, nsecY, nsecZ));
				}
				else if (current >= oldLevel)
				{
					// The neighbor is as bright or brighter — it may be lit by
					// another source. Queue it for re-lighting (the increase
					// BFS will re-evaluate it).
					int ncx = nx / chunkSizeXZ;
					int ncz = nz / chunkSizeXZ;
					int nsecY = ny / 16;
					int nlx = nx - ncx * chunkSizeXZ;
					int nlz = nz - ncz * chunkSizeXZ;
					int nsecX = nlx / 16;
					int nsecZ = nlz / 16;
					int nlocalX = nlx - nsecX * 16;
					int nlocalY = ny - nsecY * 16;
					int nlocalZ = nlz - nsecZ * 16;
					long key = SectionKey(ncx, ncz, nsecX, nsecY, nsecZ);
					Enq(key, nlocalX + 16 * (nlocalY + 16 * nlocalZ), current);
					changed.Add((ncx, ncz, nsecX, nsecY, nsecZ));
				}
			}
		}
	}

	/// <summary>
	/// After a decrease, re-lights the affected sections from their remaining
	/// block sources. Runs an increase BFS from each source in the affected
	/// sections.
	/// </summary>
	private void RunBFSRelight(HashSet<(int, int, int, int, int)> affected, int chunkSizeXZ, int chunkHeight)
	{
		ClearQueue();
		foreach (var (cx, cz, secX, secY, secZ) in affected)
		{
			long key = SectionKey(cx, cz, secX, secY, secZ);
			if (!_sources.TryGetValue(key, out byte[] sources))
			{
				continue;
			}
			if (!_fields.TryGetValue(key, out int[] field))
			{
				continue;
			}
			// Find all sources in this section.
			for (int lz = 0; lz < 16; lz++)
				for (int ly = 0; ly < 16; ly++)
					for (int lx = 0; lx < 16; lx++)
					{
						int idx = lx + 16 * (ly + 16 * lz);
						int srcLevel = sources[idx];
						if (srcLevel <= 1)
						{
							continue;
						}
						// The source emits into its air neighbors at (srcLevel - 1).
						// Seed the air neighbors (if they're dark, they need
						// re-lighting).
						int wx = cx * chunkSizeXZ + secX * 16 + lx;
						int wy = secY * 16 + ly;
						int wz = cz * chunkSizeXZ + secZ * 16 + lz;
						int[] dxs = { -1, 1, 0, 0, 0, 0 };
						int[] dys = { 0, 0, -1, 1, 0, 0 };
						int[] dzs = { 0, 0, 0, 0, -1, 1 };
						for (int i = 0; i < 6; i++)
						{
							int nx = wx + dxs[i];
							int ny = wy + dys[i];
							int nz = wz + dzs[i];
							if (ny < 0 || ny >= chunkHeight)
							{
								continue;
							}
							if (SampleSolid(nx, ny, nz, chunkSizeXZ) != 0)
							{
								continue;
							}
							int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
							int newLevel = srcLevel - 1;
							if (current >= newLevel)
							{
								continue;
							}
							SetLight(nx, ny, nz, newLevel, chunkSizeXZ);
							int ncx = nx / chunkSizeXZ;
							int ncz = nz / chunkSizeXZ;
							int nsecY = ny / 16;
							int nlx = nx - ncx * chunkSizeXZ;
							int nlz = nz - ncz * chunkSizeXZ;
							int nsecX = nlx / 16;
							int nsecZ = nlz / 16;
							int nlocalX = nlx - nsecX * 16;
							int nlocalY = ny - nsecY * 16;
							int nlocalZ = nlz - nsecZ * 16;
							long nkey = SectionKey(ncx, ncz, nsecX, nsecY, nsecZ);
							Enq(nkey, nlocalX + 16 * (nlocalY + 16 * nlocalZ), newLevel);
						}
					}
		}
		// Run the increase BFS to propagate the re-lighting.
		// (The increase BFS will visit each voxel at most once.)
		var relightChanged = new HashSet<(int, int, int, int, int)>();
		RunBFSIncrease(chunkSizeXZ, chunkHeight, relightChanged);
		foreach (var s in relightChanged)
		{
			affected.Add(s);
		}
	}

	/// <summary>
	/// Sets the light level at a world position (writes to the section field).
	/// </summary>
	private void SetLight(int worldX, int worldY, int worldZ, int level, int chunkSizeXZ)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		if (!_fields.TryGetValue(key, out int[] field))
		{
			return; // section not registered — the full rebuild will handle it
		}
		int idx = localX + 16 * (localY + 16 * localZ);
		field[idx] = level;
	}

	/// <summary>
	/// Clears all sections (world reset / new game).
	/// </summary>
	public void ClearAll()
	{
		_fields.Clear();
		_solid.Clear();
		_sources.Clear();
		ClearQueue();
	}
}
