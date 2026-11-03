namespace FactorySim.Voxel;

using Godot;
using System.Linq;

/// <summary>
/// OPT-IN light diagnostic. When <see cref="Enabled"/> is true, every voxel
/// edit and every full-rebuild convergence pass logs a [LightDebug] block to
/// the console: the edit's light-relevant state (old vs new solid + source),
/// the BFS's changed-section set, and a light histogram over the edited
/// chunk's sections (engine light vs the wide field's light, per voxel) so a
/// disagreement between the two is visible as non-zero "diff" voxels.
///
/// This is the tool for tracking down "weird lighting issues": it shows, for
/// each edit, exactly what the instant path wrote into the section engine and
/// how the full rebuild's convergence pass re-baked it, plus whether the
/// engine and the wide field agree after the rebuild.
///
/// Thread safety: main thread only (edits and the convergence pass both run on
/// the main thread). Off by default; purely diagnostic (no behaviour change).
/// </summary>
public static class LightDebug
{
	/// <summary>Master switch. Set true to log every edit + convergence pass.</summary>
	public static bool Enabled { get; set; }

	/// <summary>
	/// Radius (in voxels) of the light histogram around the edited voxel. The
	/// histogram samples a (2r+1)^3 box of the edited chunk's sections (engine)
	/// and the same box of the wide field, and counts how many voxels disagree.
	/// 16 covers a torch's full reach (level 14).
	/// </summary>
	public static int HistogramRadius { get; set; } = 16;

	private static int _editCount;

	/// <summary>
	/// Logs the instant-edit path's state: the edited voxel's old vs new
	/// light-relevant state, the BFS's changed-section set, and a check of
	/// which of the EXPECTED light-reach sections are actually registered.
	/// Called from <c>VoxelWorld._Process</c> right after the BFS runs.
	///
	/// The registration check is the key diagnostic for "light clipped at a
	/// chunk border": the BFS only crosses into a section that is registered.
	/// If a section within the source's reach is UNREGISTERED, the light is
	/// clipped there. This log shows exactly which expected sections are
	/// missing.
	/// </summary>
	public static void LogEdit(
		string label,
		Vector3I worldPos,
		int oldSolid, int newSolid,
		int oldSource, int newSource,
		System.Collections.Generic.HashSet<(int, int, int, int, int)> changedSections,
		SectionLightEngine engine,
		int chunkSizeXZ, int chunkHeight, int sourceLevel)
	{
		if (!Enabled)
		{
			return;
		}
		_editCount++;
		GD.Print($"[LightDebug] === EDIT #{_editCount} ({label}) @ world {worldPos} ===");
		GD.Print($"[LightDebug]   solid: {oldSolid} -> {newSolid}   source: {oldSource} -> {newSource}");
		if (changedSections == null || changedSections.Count == 0)
		{
			GD.Print("[LightDebug]   BFS: no changed sections");
		}
		else
		{
			GD.Print($"[LightDebug]   BFS: {changedSections.Count} section(s) changed");
			var byChunk = new System.Collections.Generic.Dictionary<Vector2I, System.Collections.Generic.List<(int, int, int)>>();
			foreach (var (cx, cy, sx, sy, sz) in changedSections)
			{
				var cc = new Vector2I(cx, cy);
				if (!byChunk.TryGetValue(cc, out var list))
				{
					list = new System.Collections.Generic.List<(int, int, int)>();
					byChunk[cc] = list;
				}
				list.Add((sx, sy, sz));
			}
			foreach (var (cc, list) in byChunk)
			{
				GD.Print($"[LightDebug]     chunk {cc}: {list.Count} section(s) {FormatSections(list)}");
			}
		}

		// Registration check: which of the EXPECTED light-reach sections are
		// actually registered? The source at level L reaches (L-1) voxels. For
		// each chunk within that reach, the relevant section(s) should be
		// registered for the BFS to cross into them. An unregistered section is
		// where the light gets clipped.
		if (sourceLevel > 1)
		{
			int reach = sourceLevel - 1;
			int cs = chunkSizeXZ;
			// The edited chunk + section.
			int ecx = (int)System.Math.Floor((double)worldPos.X / cs);
			int ecz = (int)System.Math.Floor((double)worldPos.Z / cs);
			int esecY = (int)System.Math.Floor((double)worldPos.Y / 16.0);
			// The range of chunks the light can reach.
			int minX = (int)System.Math.Floor((double)(worldPos.X - reach) / cs);
			int maxX = (int)System.Math.Floor((double)(worldPos.X + reach) / cs);
			int minZ = (int)System.Math.Floor((double)(worldPos.Z - reach) / cs);
			int maxZ = (int)System.Math.Floor((double)(worldPos.Z + reach) / cs);
			int minY = (int)System.Math.Floor((double)System.Math.Max(0, worldPos.Y - reach) / 16.0);
			int maxY = (int)System.Math.Floor((double)System.Math.Min(chunkHeight - 1, worldPos.Y + reach) / 16.0);

			var registered = new System.Collections.Generic.List<string>();
			var unregistered = new System.Collections.Generic.List<string>();
			for (int cz = minZ; cz <= maxZ; cz++)
				for (int cx = minX; cx <= maxX; cx++)
					for (int sy = minY; sy <= maxY; sy++)
					{
						// With chunkSizeXZ=16, there's exactly one section per
						// chunk per Y-slab (secX=0, secZ=0). For larger chunks
						// there would be more, but this project uses 16.
						bool reg = engine.IsSectionRegisteredAt(cx, cz, 0, sy, 0);
						var tag = $"chunk({cx},{cz}) secY={sy}";
						if (reg) registered.Add(tag);
						else unregistered.Add(tag);
					}
			GD.Print($"[LightDebug]   registration: {registered.Count} registered, {unregistered.Count} UNREGISTERED (total sections known: {engine.RegisteredSectionCount})");
			if (unregistered.Count > 0)
			{
				GD.Print($"[LightDebug]   !!! UNREGISTERED sections in light reach: {string.Join(", ", unregistered)}");
				GD.Print("[LightDebug]   !!! The BFS clips light at these section borders — this is why the glow is cut off !!!");
			}
		}
	}

	/// <summary>
	/// Logs the full-rebuild convergence pass. Called from
	/// <c>VoxelChunk.ApplyBuildResult</c> after the convergence pass.
	///
	/// Two kinds of convergence are logged, and only the SECOND is interesting:
	///  * UNRELATED rebuild (this chunk wasn't the one edited): logged briefly
	///    (one line) so the volume of chunk rebuilds is visible, but no
	///    histogram (there's no edit to center it on).
	///  * EDITED-chunk convergence (this chunk holds the most recent edit):
	///    logged in full — the delta result, a light histogram over a box around
	///    the edited voxel (engine vs wide field), and whether the wide field
	///    actually holds the source. THIS is where a bug shows up: after the
	///    convergence pass the engine and the wide field should AGREE (the
	///    engine was re-seeded from the field), and the wide field should hold
	///    the source (its flood fill read the post-edit chunk data).
	///
	/// <paramref name="isEditedChunk"/> distinguishes the two. When false the
	/// log is a single line (the histogram would be centered on a stale/default
	/// edit position and is misleading). When true the full histogram runs.
	/// </summary>
	public static void LogConvergence(
		VoxelChunk chunk,
		bool isEditedChunk,
		Vector3I worldPos,
		bool deltaRan, int deltaChanged,
		int engineMin, int engineMax,
		int fieldMin, int fieldMax,
		int diffCount,
		int fieldSourceLevel)
	{
		if (!Enabled)
		{
			return;
		}
		if (!isEditedChunk)
		{
			// Unrelated chunk rebuild — one line, no histogram.
			GD.Print($"[LightDebug]   convergence (unrelated) chunk {chunk.Coord}");
			return;
		}
		GD.Print($"[LightDebug] === CONVERGENCE (EDITED) chunk {chunk.Coord} (edit @ {worldPos}) ===");
		GD.Print($"[LightDebug]   delta: {(deltaRan ? $"ran, {deltaChanged} voxel(s) changed" : "!!! NOT RUN — no pending patch; the field was NOT re-baked over the edited voxel !!!")}");
		GD.Print($"[LightDebug]   engine light: min={engineMin} max={engineMax}");
		GD.Print($"[LightDebug]   field  light: min={fieldMin} max={fieldMax}");
		GD.Print($"[LightDebug]   field source @ edit voxel: {fieldSourceLevel}");
		GD.Print($"[LightDebug]   disagreeing voxels (engine != field): {diffCount}");
		if (diffCount > 0)
		{
			GD.Print("[LightDebug]   !!! ENGINE AND WIDE FIELD DISAGREE — this is the bug !!!");
		}
		if (!deltaRan)
		{
			GD.Print("[LightDebug]   !!! DELTA DID NOT RUN — the wide field may be stale (pre-edit) !!!");
		}
		if (fieldSourceLevel == 0)
		{
			GD.Print("[LightDebug]   !!! WIDE FIELD HAS NO SOURCE AT THE EDIT VOXEL — the flood fill didn't see the torch !!!");
		}
	}

	/// <summary>
	/// Computes the light histogram over a (2r+1)^3 box around <paramref
	/// name="worldPos"/> within <paramref name="chunk"/>. Samples the section
	/// engine's light (the instant path's state) and the chunk's wide field
	/// (the full rebuild's state) at every air voxel in the box, and returns
	/// (engineMin, engineMax, fieldMin, fieldMax, diffCount) where diffCount is
	/// the number of voxels where the two disagree by more than an epsilon.
	///
	/// A voxel is "air" when the chunk's solid data says so (the light is only
	/// meaningful on air; solid voxels have 0 light by definition). The box is
	/// clamped to the chunk's bounds.
	/// </summary>
	public static (int engineMin, int engineMax, int fieldMin, int fieldMax, int diffCount) Histogram(
		VoxelChunk chunk, Vector3I worldPos, int radius,
		int chunkSizeXZ, int chunkHeight, int maxLight,
		SectionLightEngine engine, float[] wideLight)
	{
		int cs = chunkSizeXZ, ch = chunkHeight;
		int cx0 = worldPos.X - radius, cx1 = worldPos.X + radius;
		int cy0 = System.Math.Max(0, worldPos.Y - radius), cy1 = System.Math.Min(ch - 1, worldPos.Y + radius);
		int cz0 = worldPos.Z - radius, cz1 = worldPos.Z + radius;

		int lpx = 2 * cs + 2 * maxLight + 2;
		int lpy = ch + 2;
		int lpz = 2 * cs + 2 * maxLight + 2;
		int half = lpx / 2 - 1;

		int engineMin = int.MaxValue, engineMax = int.MinValue;
		int fieldMin = int.MaxValue, fieldMax = int.MinValue;
		int diffCount = 0;
		int sampled = 0;

		for (int wz = cz0; wz <= cz1; wz++)
			for (int wy = cy0; wy <= cy1; wy++)
				for (int wx = cx0; wx <= cx1; wx++)
				{
					// Chunk-local coords.
					int lx = wx - chunk.Coord.X * cs;
					int ly = wy;
					int lz = wz - chunk.Coord.Y * cs;
					if (lx < 0 || lx >= cs || ly < 0 || ly >= ch || lz < 0 || lz >= cs)
					{
						continue; // outside this chunk
					}
					// Only air voxels carry light.
					if (chunk.Data.Get(lx, ly, lz) != 0)
					{
						continue;
					}
					// Engine light (raw 0..maxLight).
					int e = engine.SampleLight(wx, wy, wz, cs, ch);
					if (e < engineMin) engineMin = e;
					if (e > engineMax) engineMax = e;
					// Wide field light (normalized 0..1 -> raw).
					int fx = wx - chunk.Coord.X * cs + half;
					int fy = wy + 1;
					int fz = wz - chunk.Coord.Y * cs + half;
					int g = fx + lpx * (fy + lpy * fz);
					int f = 0;
					if (wideLight != null && g >= 0 && g < wideLight.Length)
					{
						f = System.Math.Clamp((int)System.Math.Round(wideLight[g] * maxLight), 0, maxLight);
					}
					if (f < fieldMin) fieldMin = f;
					if (f > fieldMax) fieldMax = f;
					// Disagree if the raw levels differ by >= 1 (a full light step).
					if (System.Math.Abs(e - f) >= 1)
					{
						diffCount++;
					}
					sampled++;
				}

		if (sampled == 0)
		{
			return (0, 0, 0, 0, 0);
		}
		return (engineMin, engineMax, fieldMin, fieldMax, diffCount);
	}

	private static string FormatSections(System.Collections.Generic.List<(int, int, int)> list)
	{
		if (list.Count <= 8)
		{
			return string.Join(", ", list.ConvertAll(s => $"({s.Item1},{s.Item2},{s.Item3})"));
		}
		// Too many to list — show the first few + count.
		var head = string.Join(", ", list.Take(5).Select(s => $"({s.Item1},{s.Item2},{s.Item3})"));
		return $"{head}, ... ({list.Count - 5} more)";
	}

	/// <summary>
	/// Logs the BFS gate counters (called from RunBFSIncrease when
	/// LightDebug.Enabled). Shows how many times the BFS stopped propagation
	/// due to each condition: unregistered section (light clipped at border),
	/// solid voxel (light blocked), already-bright (no change needed), and how
	/// many voxels were actually propagated. A high "unregistered" count means
	/// the light is being clipped at section borders (the sections aren't
	/// registered). A high "solid" count means the light is being blocked by
	/// terrain.
	/// </summary>
	public static void LogBFSGates(int unregistered, int solid, int alreadyBright, int propagated)
	{
		if (!Enabled)
		{
			return;
		}
		GD.Print($"[LightDebug]   BFS gates: unregistered={unregistered}, solid={solid}, already-bright={alreadyBright}, propagated={propagated}");
		if (unregistered > 0)
		{
			GD.Print("[LightDebug]   !!! BFS was clipped by UNREGISTERED sections — the light stopped at section borders !!!");
		}
		if (solid > 50)
		{
			GD.Print($"[LightDebug]   note: {solid} solid-voxel blocks (expected for terrain — the light is hitting blocks)");
		}
	}

	/// <summary>
	/// Logs a chunk-border light seam: the engine's light and the wide field's
	/// light at a border voxel disagree by more than a level. This is the
	/// "harsh shadow line on chunk edges" diagnostic — if the engine's per-chunk
	/// fields disagree at the shared border, the mesh shows a hard light
	/// transition exactly on the chunk edge. Rate-limited: at most one line per
	/// 500 border samples so a seam doesn't flood the log.
	/// </summary>
	private static int _borderSeamCount;
	public static void LogBorderSeam(Vector2I chunk, int lx, int ly, int lz, int engineRaw, int wideRaw, bool fromEngine)
	{
		if (!Enabled)
		{
			return;
		}
		_borderSeamCount++;
		if (_borderSeamCount % 500 != 1)
		{
			return; // rate-limit: 1 line per 500 border seams
		}
		// fromEngine: the section is REGISTERED, so the mesh uses the ENGINE's
		// light (engineRaw) for this voxel. The wide field (wideRaw) is only a
		// fallback for unregistered sections. A seam with fromEngine=true means
		// the ENGINE and the WIDE FIELD disagree at the border — the engine is
		// the authoritative (rendered) value, and the wide field is the stale
		// one (it doesn't carry the torch's glow to the border yet).
		GD.Print($"[LightDebug]   BORDER SEAM chunk ({chunk.X},{chunk.Y}) local ({lx},{ly},{lz}): rendered(engine)={engineRaw} wide={wideRaw} (diff {System.Math.Abs(engineRaw - wideRaw)}) [section {(fromEngine ? "registered→engine" : "unregistered→wide")}");
	}
}
