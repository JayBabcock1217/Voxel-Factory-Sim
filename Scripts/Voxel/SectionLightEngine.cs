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

	// Section key: (chunkX, chunkZ, secX, secY, secZ) packed into a long.
	// chunkX: 20 bits, chunkZ: 20 bits, secX: 4 bits, secY: 4 bits, secZ: 4 bits.
	//
	// NOTE: the second field is the chunk Z COLUMN, not the world Y. Every caller
	// of this method passes (cx, cz, ...) — the Z column is what identifies a
	// chunk on the 2D grid (see VoxelChunk.Coord, a Vector2I of (X column, Z
	// row)). World Y is captured by the section's secY (the 16-tall section
	// index), not by a chunk Y. Packing the Z column here (as the "by" field) is
	// deliberate and matches DecodeKey. Do NOT rename this field to "cy" or
	// repurpose it for world Y — that was the source of a cross-chunk key
	// collision that produced "lines of shadow" along chunk borders.
	private static long SectionKey(int cx, int cz, int sx, int sy, int sz)
	{
		// Bias to handle negative chunk coords.
		int bx = cx + (1 << 19);
		int bz = cz + (1 << 19);
		return (long)bx << 32 | (long)bz << 12 | (long)sx << 8 | (long)sy << 4 | (long)sz;
	}

	private static void DecodeKey(long key, out int cx, out int cz, out int sx, out int sy, out int sz)
	{
		int bz2 = (int)(key & 0xF);
		int by2 = (int)((key >> 4) & 0xF);
		int bx2 = (int)((key >> 8) & 0xF);
		int bz = (int)((key >> 12) & 0xFFFFF);
		int bx = (int)((key >> 32) & 0xFFFFF);
		cx = bx - (1 << 19);
		cz = bz - (1 << 19);
		sx = bx2;
		sy = by2;
		sz = bz2;
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
	/// Decomposes a world position into (sectionKey, localIndex) using FLOOR
	/// division (correct for negative world coordinates). Used by the BFS
	/// methods to enqueue neighbors.
	/// </summary>
	private static void WorldToSectionIndex(
		int worldX, int worldY, int worldZ, int chunkSizeXZ,
		out long key, out int localIndex)
	{
		int cx = FloorDiv(worldX, chunkSizeXZ);
		int cz = FloorDiv(worldZ, chunkSizeXZ);
		int secY = FloorDiv(worldY, 16);
		int lx = worldX - cx * chunkSizeXZ;
		int lz = worldZ - cz * chunkSizeXZ;
		int secX = FloorDiv(lx, 16);
		int secZ = FloorDiv(lz, 16);
		int localX = lx - secX * 16;
		int localY = worldY - secY * 16;
		int localZ = lz - secZ * 16;
		key = SectionKey(cx, cz, secX, secY, secZ);
		localIndex = localX + 16 * (localY + 16 * localZ);
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
	/// True if the section at (chunkX, chunkZ, secX, secY, secZ) is registered
	/// (has a light field). Used by LightDebug to report which of the expected
	/// light-reach sections are actually registered at edit time (an
	/// unregistered section is where the BFS clips the light).
	/// </summary>
	public bool IsSectionRegisteredAt(int chunkX, int chunkZ, int secX, int secY, int secZ)
	{
		long key = SectionKey(chunkX, chunkZ, secX, secY, secZ);
		return _fields.ContainsKey(key);
	}

	/// <summary>
	/// The number of currently-registered sections. Used by LightDebug to
	/// show how many sections the engine knows about (a small number means
	/// most of the world's sections are unregistered, so the BFS clips light
	/// at many borders).
	/// </summary>
	public int RegisteredSectionCount => _fields.Count;

	/// <summary>
	/// True if the voxel is a block-light source (emits its own light) — used
	/// by the mesh builder to brighten a source voxel's own faces to its
	/// source level (the wide field does the same). The wide field treats a
	/// source voxel as solid (so the mesh samples its NEIGHBOURS for light),
	/// but the source voxel's own faces are drawn at its source level.
	/// </summary>
	public bool BlockLightSource(int worldX, int worldY, int worldZ, int chunkSizeXZ)
	{
		DecomposeWorldPos(worldX, worldY, worldZ, chunkSizeXZ,
			out int cx, out int cz, out int secX, out int secY, out int secZ,
			out int localX, out int localY, out int localZ);
		long key = SectionKey(cx, cz, secX, secY, secZ);
		if (!_sources.TryGetValue(key, out byte[] sources))
		{
			return false;
		}
		int idx = localX + 16 * (localY + 16 * localZ);
		return sources[idx] > 0;
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
		// emits INTO the air around it, at level - 1). Clear the queue AND the
		// visited map first: the visited map tracks the best block level each
		// voxel has been reached with (BFS termination), and a fresh run starts
		// with no prior visits.
		ClearQueue();
		_visited.Clear();
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
	/// Used by PropagateIncrease. Marks each seeded neighbor in the visited map
	/// so the BFS (RunBFSIncrease) won't re-process it at the same or lower
	/// block level (termination).
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
			// Is the neighbor's section registered? If not, stop here.
			WorldToSectionIndex(nx, ny, nz, chunkSizeXZ, out long key, out int localIdx);
			if (!_fields.ContainsKey(key))
			{
				continue; // unregistered section — light clipped at border
			}
			// Is the neighbor air?
			if (SampleSolid(nx, ny, nz, chunkSizeXZ) != 0)
			{
				continue; // solid — light doesn't pass
			}
			// TERMINATION: skip if already reached with an equal or higher
			// block level (can't happen on a fresh seed, but guards against
			// re-seeding). Mark the visited level.
			var vkey = (key, localIdx);
			if (_visited.TryGetValue(vkey, out int prevLevel) && level <= prevLevel)
			{
				continue;
			}
			_visited[vkey] = level;
			// The neighbor's current light (combined sky+block). The correct
			// combined light is MAX(current, level) — Minecraft's rule. Bump
			// the stored light up if the block light is brighter, and enqueue
			// at the BLOCK level so the BFS continues propagating.
			int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
			int combined = current > level ? current : level;
			SetLight(nx, ny, nz, combined, chunkSizeXZ);
			Enq(key, localIdx, level);
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
			// Is the neighbor's section registered? If not, stop here.
			WorldToSectionIndex(nx, ny, nz, chunkSizeXZ, out long key, out int localIdx);
			if (!_fields.ContainsKey(key))
			{
				continue; // unregistered section — light clipped at border
			}
			int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
			if (current < level)
			{
				continue; // not lit by this source (or not lit at all)
			}
			// Set to 0 and enqueue for re-lighting.
			SetLight(nx, ny, nz, 0, chunkSizeXZ);
			Enq(key, localIdx, current);
		}
	}

	// Visited map for the increase BFS: tracks the best BLOCK level each
	// (sectionKey, localIndex) has already been reached with. A voxel is only
	// re-enqueued if reached with a STRICTLY HIGHER block level — this is what
	// terminates the BFS (without it, the max(sky, block) logic re-enqueues
	// already-processed voxels forever). Reused across calls (cleared at the
	// start of each RunBFSIncrease / PropagateIncrease).
	private readonly Dictionary<(long, int), int> _visited = new();

	/// <summary>
	/// The increase BFS: propagates block light through air, crossing
	/// section/chunk borders. The stored light is the COMBINED max(sky, block)
	/// (the field holds the wider light); the BFS propagates the BLOCK
	/// component and bumps the stored light up to max(current, blockLevel)
	/// wherever the block light is brighter than the sky.
	///
	/// TERMINATION: a voxel is enqueued only when reached with a block level
	/// STRICTLY HIGHER than any previous visit (tracked in _visited). Because
	/// the block level decreases by 1 at each BFS step and is bounded below by
	/// 1 (level 1 doesn't propagate), each voxel is enqueued at most
	/// (maxLevel) times — in practice once — so the BFS terminates.
	///
	/// DEBUG: when LightDebug.Enabled, counts how many times each gate
	/// (unregistered, solid, already-visited) triggers, plus how many voxels
	/// had their light bumped up.
	/// </summary>
	private void RunBFSIncrease(int chunkSizeXZ, int chunkHeight, HashSet<(int, int, int, int, int)> changed)
	{
		// DEBUG counters (only populated when LightDebug.Enabled).
		int dbgUnregistered = 0, dbgSolid = 0, dbgAlreadyVisited = 0, dbgBumped = 0;
		bool dbg = LightDebug.Enabled;

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
				// Is the neighbor's section registered? If not, stop here —
				// the light is clipped at the section border (the full rebuild
				// will re-bake the light for the unregistered section when it
				// runs). This prevents the BFS from losing light in unregistered
				// sections (SetLight silently fails for unregistered sections).
				WorldToSectionIndex(nx, ny, nz, chunkSizeXZ, out long key, out int localIdx);
				if (!_fields.ContainsKey(key))
				{
					if (dbg) dbgUnregistered++;
					continue; // unregistered section — light clipped at border
				}
				// Is the neighbor air?
				if (SampleSolid(nx, ny, nz, chunkSizeXZ) != 0)
				{
					if (dbg) dbgSolid++;
					continue;
				}
				// TERMINATION: only process the neighbor if we're reaching it
				// with a block level STRICTLY HIGHER than any previous visit.
				// The block level decreases by 1 each step and is bounded below
				// by 1, so each voxel is processed at most (maxLevel) times.
				var vkey = (key, localIdx);
				if (_visited.TryGetValue(vkey, out int prevLevel) && newLevel <= prevLevel)
				{
					if (dbg) dbgAlreadyVisited++;
					continue; // already reached with an equal or higher block level
				}
				_visited[vkey] = newLevel;

				// The neighbor's current light (the combined sky+block value the
				// field holds). The correct combined light after this block light
				// arrives is MAX(current, newLevel) — Minecraft's rule: the
				// final light is the brighter of sky and block. Bump the stored
				// light up if the block light is brighter.
				int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
				int combined = current > newLevel ? current : newLevel;
				bool lightChanged = combined != current;
				SetLight(nx, ny, nz, combined, chunkSizeXZ);
				Enq(key, localIdx, newLevel);
				if (dbg)
				{
					if (lightChanged) dbgBumped++;
					else dbgAlreadyVisited++;
				}
				// Track the changed section only if the light actually changed
				// (a section whose light didn't change doesn't need re-meshing).
				if (lightChanged)
				{
					DecodeKey(key, out int ncx, out int ncz, out int nsecX, out int nsecY, out int nsecZ);
					changed.Add((ncx, ncz, nsecX, nsecY, nsecZ));
				}
			}
		}

		// DEBUG: report the gate counters so a clipped light can be traced to
		// the specific condition that stopped it.
		if (dbg)
		{
			LightDebug.LogBFSGates(dbgUnregistered, dbgSolid, dbgAlreadyVisited, dbgBumped);
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
					WorldToSectionIndex(nx, ny, nz, chunkSizeXZ, out long key, out int localIdx);
					Enq(key, localIdx, current);
					DecodeKey(key, out int ncx, out int ncz, out int nsecX, out int nsecY, out int nsecZ);
					changed.Add((ncx, ncz, nsecX, nsecY, nsecZ));
				}
				else if (current >= oldLevel)
				{
					// The neighbor is as bright or brighter — it may be lit by
					// another source. Queue it for re-lighting (the increase
					// BFS will re-evaluate it).
					WorldToSectionIndex(nx, ny, nz, chunkSizeXZ, out long key, out int localIdx);
					Enq(key, localIdx, current);
					DecodeKey(key, out int ncx, out int ncz, out int nsecX, out int nsecY, out int nsecZ);
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
		// Fresh relight run: clear the visited map (BFS termination tracking).
		_visited.Clear();
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
							WorldToSectionIndex(nx, ny, nz, chunkSizeXZ, out long nkey, out int localIdx);
							if (!_fields.ContainsKey(nkey))
							{
								continue; // unregistered section
							}
							int newLevel = srcLevel - 1;
							// TERMINATION: skip if already reached with an equal
							// or higher block level; otherwise mark it.
							var vkey = (nkey, localIdx);
							if (_visited.TryGetValue(vkey, out int prevLevel) && newLevel <= prevLevel)
							{
								continue;
							}
							_visited[vkey] = newLevel;
							// Bump the stored light to max(current, newLevel)
							// (Minecraft's max(sky, block) rule) and enqueue at
							// the block level.
							int current = SampleLight(nx, ny, nz, chunkSizeXZ, chunkHeight);
							int combined = current > newLevel ? current : newLevel;
							SetLight(nx, ny, nz, combined, chunkSizeXZ);
							Enq(nkey, localIdx, newLevel);
						}
					}
		}
		// Run the increase BFS to propagate the re-lighting.
		// (The increase BFS terminates via the visited map.)
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
	/// Re-seeds this chunk's sections' LIGHT from the freshly baked wide field
	/// (the authoritative, post-edit light). Called by the full rebuild's
	/// convergence pass (VoxelChunk.ApplyBuildResult) AFTER the wide field has
	/// been re-baked from the post-edit data AND the Starlight delta has
	/// converged it over the edited voxel.
	///
	/// WHY THIS EXISTS: the instant-edit path writes the torch's BFS light into
	/// the section fields, but the wide field (_cachedLightField) is only
	/// re-baked from the post-edit data by the full rebuild (plus the
	/// convergence pass's delta). Until that rebuild runs, the two disagree —
	/// the sampler reads the ENGINE's level but the wide field's own values are
	/// pre-edit. The convergence pass re-bakes the wide field and must make the
	/// engine match it, otherwise the engine's BFS starts from a state that
	/// doesn't reflect the flood fill's result, and later propagations (and the
	/// mesh's per-vertex light) diverge from the authoritative field.
	///
	/// After this, the engine's LIGHT and the wide field are derived from the
	/// SAME post-edit data, so they agree. SOLID and SOURCES are NOT re-seeded
	/// here: they were registered from the live chunk data (post-edit) by
	/// RegisterSectionsWithEngine, which is the correct source of truth for
	/// them (the wide volumes' solid/block state can be pre-edit for the
	/// edited voxel — the delta patches the wide volumes but the chunk data is
	/// always post-edit).
	///
	/// <paramref name="maxLight"/> is the session's max light level (e.g. 15) —
	/// the divisor the wide field was normalized by. It recovers the flood fill's
	/// integer raw level from the normalized 0..1 field value (Round(v * maxLight)).
	/// </summary>
	public void ReSeedLightFromWideField(
		int chunkX, int chunkZ,
		float[] wideLight,
		int lpx, int lpy, int lpz,
		int chunkSizeXZ, int chunkHeight, int maxLight)
	{
		int half = lpx / 2 - 1;
		int secsX = System.Math.Max(1, chunkSizeXZ / 16);
		int secsY = System.Math.Max(1, chunkHeight / 16);
		int secsZ = System.Math.Max(1, chunkSizeXZ / 16);

		for (int secZ = 0; secZ < secsZ; secZ++)
			for (int secY = 0; secY < secsY; secY++)
				for (int secX = 0; secX < secsX; secX++)
				{
					long key = SectionKey(chunkX, chunkZ, secX, secY, secZ);
					if (!_fields.TryGetValue(key, out int[] field))
					{
						continue; // section not registered — skip
					}
					int x0 = secX * 16, x1 = System.Math.Min(x0 + 15, chunkSizeXZ - 1);
					int y0 = secY * 16, y1 = System.Math.Min(y0 + 15, chunkHeight - 1);
					int z0 = secZ * 16, z1 = System.Math.Min(z0 + 15, chunkSizeXZ - 1);
					for (int lz = 0; lz <= z1 - z0; lz++)
						for (int ly = 0; ly <= y1 - y0; ly++)
							for (int lx = 0; lx <= x1 - x0; lx++)
							{
								int vx = x0 + lx, vy = y0 + ly, vz = z0 + lz;
								int idx = lx + 16 * (ly + 16 * lz);
								int wxi = vx + half;
								int wyi = vy + 1;
								int wzi = vz + half;
								int g = wxi + lpx * (wyi + lpy * wzi);
								if (g < 0 || g >= wideLight.Length)
								{
									continue;
								}
								// Raw level from the normalized wide field (the flood fill's integer level).
								int raw = System.Math.Clamp(
									(int)System.Math.Round(wideLight[g] * maxLight), 0, maxLight);
								field[idx] = raw;
							}
				}
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
