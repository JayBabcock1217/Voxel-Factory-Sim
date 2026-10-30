namespace FactorySim.Voxel;

/// <summary>
/// Per-voxel LIGHT: a flood-fill (BFS) light model combining SKY light and
/// BLOCK light, in the Minecraft style.
///
/// The returned per-air-voxel level (0..15) is the result of seeding the field
/// and then propagating light through air with a single max-BFS:
///
/// SEEDS
///   * SKY: every air voxel with a SOLID-FREE vertical column straight up to
///     the top of the volume (it sees the sky) is seeded at 15. This is a pure
///     per-column function, so it is seam-safe (two adjacent chunks can never
///     disagree about which voxels are sky-seeded). Seeding the ENTIRE open
///     column (not just the surface) means open air is 15 everywhere, so there
///     is no 15/0 hard line at a chunk seam.
///   * BLOCK LIGHT: each light-source voxel (a solid voxel with blockLight > 0,
///     e.g. a torch) emits into its 6 air neighbours at (sourceLevel - 1).
///
/// PROPAGATION — one max-BFS over air, decaying by 1 per voxel:
///   * Every lit voxel propagates to its 6 air neighbours at (level - 1),
///     applied only if it EXCEEDS the neighbour's current value (max semantics).
///   * Effect: light spills OUT of cave openings and fades with 3D distance
///     from the nearest lit air ("fade from the cave opening"), while enclosed
///     air far from any opening reaches 0 (near-black). A torch lights the
///     enclosed air around it that the sky can't reach.
///
/// SEAM SAFETY: the sky seeds are per-column (identical on both sides of a
/// seam) and the BFS is a deterministic max-propagation over the shared
/// 1-voxel padded shell, so the light field is consistent across chunk borders.
/// (This is why the seed must be the pure per-column function — the earlier
/// "0 to 15 across one voxel" artifact came from an inconsistent seed, not from
/// the propagation itself.)
///
/// This is a pure function over a padded volume. It takes a SOLID|AIR byte
/// field (1 = solid, 0 = air) at the padded lattice size and returns a byte
/// light field at the same size. No Godot types, so it is safe to run on the
/// thread pool inside the chunk mesh-build task.
///
/// The padded volume must include a one-voxel shell of real neighbour data on
/// all six sides (the same padding the Marching-Cubes field uses) so the sky
/// column test and the BFS see the true neighbour state at the seam.
/// </summary>
public static class SkyLight
{
	// Scratch buffers reused by ComputeLocal across calls (it runs on the MAIN
	// thread for the instant-edit path, so a static is safe — no concurrency).
	// Reusing them avoids allocating ~140 KB (boxLight float[] + boxLit byte[])
	// on every local recompute, which was the GC churn behind the edit hitch.
	private static float[] _localLightScratch;
	private static byte[] _localLitScratch;

	/// <summary>
	/// Flood-fills light over a padded solid|air field: SKY light (open-sky
	/// columns at 15, decaying through air) plus optional BLOCK light (placed
	/// light-source voxels that emit into their neighbourhood). The returned
	/// per-air-voxel level is <c>max(skyLight, blockLight)</c>.
	/// </summary>
	/// <param name="solid">
	/// Padded solid|air field, 1 = solid, 0 = air, at dimensions (nx, ny, nz),
	/// indexed <c>ix + nx * (iy + ny * iz)</c>. Must include the one-voxel
	/// padded shell of real neighbour data on all six sides.
	/// </param>
	/// <param name="nx">Padded width (X).</param>
	/// <param name="ny">Padded height (Y).</param>
	/// <param name="nz">Padded depth (Z).</param>
	/// <param name="blockLight">
	/// Optional per-voxel BLOCK LIGHT source levels (0..15) at the SAME padded
	/// dimensions as <paramref name="solid"/>. A non-zero value marks that
	/// (solid) voxel as a light source emitting at that level; its 6 air
	/// neighbours are seeded at (level - 1) and light decays from there. Null
	/// (or all-zero) = no block sources, pure sky light.
	/// </param>
	/// <param name="maxLevel">
	/// The maximum light level (open-sky air is seeded at this; light decays by
	/// 1 per voxel, so it reaches zero after <c>maxLevel</c> voxels). Defaults
	/// to 15 (Minecraft). Higher values make light penetrate further (brighter,
	/// deeper caves); the Minecraft default is 15.
	/// </param>
	/// <returns>
	/// A NORMALIZED light field at the same padded dimensions, a continuous
	/// 0..1 float per voxel (level / maxLevel). Storing the field as floats
	/// (not bytes) is what makes the downstream light-response curve
	/// (falloff / dark-gamma / floor) able to produce FINE-GRAINED values: a
	/// byte field would re-quantize the curve's continuous output back onto the
	/// coarse integer light levels, so a small floor change could flip a whole
	/// band of voxels to the next level. Solid voxels are 0. Air voxels that
	/// see open sky (a solid-free column to the top) are 1.0; enclosed air
	/// fades from 1.0 toward 0 over the 3D distance from the nearest lit air;
	/// air near a block source is lit by the source (max with sky); enclosed
	/// air far from any source is 0.
	/// </returns>
	public static float[] Compute(byte[] solid, int nx, int ny, int nz, byte[] blockLight = null, int maxLevel = 15)
		=> Compute(solid, nx, ny, nz, blockLight, maxLevel, out _);

	/// <summary>
	/// Like <see cref="Compute(byte[], int, int, int, byte[], int)"/> but also
	/// reports, in <paramref name="blockLit"/>, which air voxels carry their
	/// light from a BLOCK source (a torch/glowstone) rather than (only) from
	/// the sky. A voxel is block-lit when a block-source seed reached it with a
	/// value at least as high as any sky seed. The downstream light-response
	/// curve (falloff / dark-gamma) is tuned for SKY light (which sits at high
	/// raw values near openings); applying those same curves to BLOCK light
	/// (which sits at mid-range raw values near a torch) crushes torch light to
	/// the dark floor, so the mesh builder uses this mask to give block-lit
	/// voxels a gentler response. <paramref name="blockLit"/> is a 0/1 byte
	/// field at the same padded dimensions (1 = block-lit, 0 = sky-lit or dark).
	/// </summary>
	public static float[] Compute(
		byte[] solid, int nx, int ny, int nz,
		byte[] blockLight, int maxLevel,
		out byte[] blockLit)
	{
		// Clamp the max level (it indexes the 0..1 normalization; must be >= 1).
		if (maxLevel < 1) maxLevel = 1;
		else if (maxLevel > 255) maxLevel = 255;
		int total = nx * ny * nz;
		var light = new float[total];
		// Assign to a local the local functions (SeedAir / Propagate) can
		// capture; an `out` parameter cannot be used inside a local function.
		var lit = blockLit = new byte[total];

		// ------------------------------------------------------------------
		// SEED 1 — SKY: every air voxel with a SOLID-FREE vertical column
		// straight up to the top of the volume (it sees the sky) starts at 15.
		// ------------------------------------------------------------------
		// This is a PURE per-column function: each (x, z) column is owned by
		// exactly one chunk and depends only on that column's own solid/air
		// state, so two adjacent chunks can NEVER disagree about which voxels
		// are sky-seeded. That is what makes the flood fill below seam-safe —
		// the seeds match across the seam, and the propagation (a deterministic
		// max-BFS) then converges to the same values on both sides.
		//
		// Seeding the ENTIRE open column (not just the surface) is what removes
		// the hard line at chunk edges: open air is 15 everywhere, so there is no
		// 15/0 discontinuity at a seam. The BFS below then spills light INTO
		// cave openings and fades it with 3D distance (the "fade from the cave
		// opening" behaviour), which a pure per-column fade cannot do.
		for (int ix = 0; ix < nx; ix++)
		{
			for (int iz = 0; iz < nz; iz++)
			{
				// Walk the column top-down; air until the first solid is open.
				for (int iy = ny - 1; iy >= 0; iy--)
				{
					int idx = ix + nx * (iy + ny * iz);
					if (solid[idx] != 0)
					{
						break; // hit solid; below is enclosed (seeded only by the BFS)
					}
					light[idx] = 1f; // open-sky air: full light (normalized)
				}
			}
		}

		// BFS frontier + enqueue helper (declared before the seeds so SeedAir
		// can enqueue). A voxel may be enqueued more than once (when its value
		// increases), so the queue grows defensively.
		var queue = new int[total];
		int qHead = 0, qTail = 0;

		void Enqueue(int idx)
		{
			if (qTail >= queue.Length)
			{
				var grown = new int[queue.Length * 2];
				System.Array.Copy(queue, grown, queue.Length);
				queue = grown;
			}
			queue[qTail++] = idx;
		}

		// ------------------------------------------------------------------
		// SEED 2 — BLOCK LIGHT: each light-source voxel (solid, blockLight > 0)
		// emits into its 6 air neighbours at (sourceLevel - 1).
		// ------------------------------------------------------------------
		// A torch in a dark cave seeds its surrounding air, so the BFS below
		// lights the enclosed air the sky can't reach. Max-semantics: a source
		// never downgrades a voxel that is already brighter (e.g. open sky at 15).
		if (blockLight != null)
		{
			for (int idx = 0; idx < total; idx++)
			{
				byte src = blockLight[idx];
				if (src == 0 || solid[idx] == 0)
				{
					continue; // not a source, or not solid (sources are solid blocks)
				}
				float nl = (src > maxLevel ? maxLevel : (src >= 1 ? src - 1 : 0)) / (float)maxLevel; // normalized level just outside the source
				if (nl <= 0f)
				{
					continue;
				}
				int ix = idx % nx;
				int rem = idx / nx;
				int iy = rem % ny;
				int iz = rem / ny;
				// Index layout: idx = ix + nx*(iy + ny*iz). So the neighbour
				// offsets are: ±X -> ±1, ±Y -> ±nx, ±Z -> ±nx*ny.
				SeedAir(idx - 1,         ix > 0,         nl, fromBlock: true);
				SeedAir(idx + 1,         ix < nx - 1,    nl, fromBlock: true);
				SeedAir(idx - nx,        iy > 0,         nl, fromBlock: true);
				SeedAir(idx + nx,        iy < ny - 1,    nl, fromBlock: true);
				SeedAir(idx - nx * ny,   iz > 0,         nl, fromBlock: true);
				SeedAir(idx + nx * ny,   iz < nz - 1,    nl, fromBlock: true);
			}
		}

		void SeedAir(int nidx, bool inBounds, float nl, bool fromBlock = false)
		{
			if (!inBounds || solid[nidx] != 0 || nl <= light[nidx])
			{
				return;
			}
			light[nidx] = nl;
			if (fromBlock)
			{
				lit[nidx] = 1;
			}
			Enqueue(nidx);
		}

		// ------------------------------------------------------------------
		// PROPAGATION — a single max-BFS over air, decaying by 1 per voxel.
		// ------------------------------------------------------------------
		// Every lit voxel (sky-seeded 15 or block-source-seeded) propagates to
		// its 6 air neighbours at (level - 1), applied only if it EXCEEDS the
		// neighbour's current value (max semantics). A voxel is re-enqueued
		// whenever its value INCREASES, so a later, brighter path can lift a
		// dimly-lit voxel. The result: light spills out of cave openings and
		// fades with 3D distance from the nearest lit air, while enclosed air
		// far from any opening reaches 0 (near-black).
		//
		// Seam behaviour: the sky seeds are per-column (identical on both sides
		// of a seam), and the BFS is a deterministic max-propagation over the
		// shared 1-voxel padded shell. Where the light field is open (15) on
		// both sides there is no discontinuity; inside a cave that spans a seam,
		// each chunk propagates from its own openings and the 1-voxel shell lets
		// the values hand off across the border. The neighbour-rebuild (see
		// VoxelWorld.MarkNeighboursForRebuild) re-meshes a chunk when its
		// neighbour generates, so the hand-off is always up to date.
		// Enqueue every sky-seeded (full-light) voxel to start the propagation.
		// (Block sources were already enqueued via SeedAir when they exceeded 0.)
		for (int idx = 0; idx < total; idx++)
		{
			if (light[idx] >= 1f)
			{
				Enqueue(idx);
			}
		}

		while (qHead < qTail)
		{
			int idx = queue[qHead++];
			if (idx < 0 || idx >= total)
			{
				continue;
			}
			float lv = light[idx];
			if (lv <= 1f / maxLevel)
			{
				continue; // can't propagate further (would saturate to 0)
			}
			// Decay by one level and SNAP to the exact level grid (k/maxLevel).
			// The stored value is still a continuous float (so the response
			// curve can be fine-grained), but snapping to the grid makes the BFS
			// deterministic and SEAM-SAFE: two adjacent chunks reaching the same
			// air voxel compute the same integer level k, so they produce
			// bit-identical values (no hairline brightness seam from FP drift).
			int k = (int)System.Math.Round(lv * maxLevel) - 1;
			if (k < 0)
			{
				continue;
			}
			float nl = k / (float)maxLevel;

			int ix = idx % nx;
			int rem = idx / nx;
			int iy = rem % ny;
			int iz = rem / ny;
			// Carry the source voxel's block-lit status forward: light that
			// originated from a block source stays block-lit as it propagates.
			bool srcBlock = blockLit[idx] == 1;

			// Index layout: idx = ix + nx*(iy + ny*iz). Neighbour offsets:
			// ±X -> ±1, ±Y -> ±nx, ±Z -> ±nx*ny.
			Propagate(idx - 1,         ix > 0,         nl, srcBlock);
			Propagate(idx + 1,         ix < nx - 1,    nl, srcBlock);
			Propagate(idx - nx,        iy > 0,         nl, srcBlock);
			Propagate(idx + nx,        iy < ny - 1,    nl, srcBlock);
			Propagate(idx - nx * ny,   iz > 0,         nl, srcBlock);
			Propagate(idx + nx * ny,   iz < nz - 1,    nl, srcBlock);
		}

		void Propagate(int nidx, bool inBounds, float nl, bool fromBlock)
		{
			if (!inBounds || solid[nidx] != 0 || nl <= light[nidx])
			{
				return;
			}
			light[nidx] = nl;
			if (fromBlock)
			{
				lit[nidx] = 1;
			}
			Enqueue(nidx);
		}

		return light;
	}

	/// <summary>
	/// LOCAL light recompute (Option B lighting): recompute the light only over a
	/// bounded box <c>[x0..x1] × [y0..y1] × [z0..z1]</c> of the padded field
	/// (inclusive, lattice coords). The box interior is solved from scratch by a
	/// max-BFS seeded from its SURROUNDING (the fixed boundary conditions), which
	/// makes it far cheaper than a full <see cref="Compute"/> flood fill (which
	/// scans the whole 3×3-chunk region) and is what makes an edit's light update
	/// instant.
	///
	/// BOUNDARY CONDITIONS (the surrounding, which is unchanged by the edit as
	/// long as the box is one light-range past it):
	///   * SKY — a column open to the field top seeds full light (the same
	///     per-column rule as <see cref="Compute"/>); a column blocked above the
	///     box hand-offs from the existing value just above it. This is what lets
	///     a broken block re-light the whole open column above it instantly (the
	///     sky hand-off seeds the box top, the BFS propagates down).
	///   * BLOCK — every light source in or adjacent to the box seeds its air
	///     neighbours at (level - 1), exactly as <see cref="Compute"/> does.
	///
	/// CORRECTNESS: light is a monotone MAX propagation (each voxel = the max of
	/// its own seeds and its neighbours' propagated values, decaying 1/voxel), so
	/// a max-BFS driven by the true surrounding converges to the SAME values the
	/// full flood fill would produce inside the box. The interior is started at 0
	/// (never seeded from the stale field), so a stale interior value can only
	/// make the result LESS correct — it is dropped, not trusted.
	///
	/// Writes the recomputed (NORMALIZED 0..1) light back into
	/// <paramref name="light"/> in place over the box, and recomputes
	/// <paramref name="blockLit"/> over the box (a voxel is block-lit when a
	/// block-source path reached it — a removed torch therefore drops its stale
	/// flags, and a sky hand-off keeps the boundary's flag).
	/// </summary>
	/// <param name="solid">Padded solid|air field (1 = solid) at (nx,ny,nz).</param>
	/// <param name="blockLight">Per-voxel block-light SOURCE levels (0 = none) at the same dims.</param>
	/// <param name="light">The CURRENT normalized light field (boundary values are read as fixed conditions; the box interior is overwritten).</param>
	/// <param name="blockLit">The CURRENT block-lit mask (updated over the box).</param>
	/// <param name="nx,ny,nz">Padded field dimensions.</param>
	/// <param name="x0,y0,z0,x1,y1,z1">Inclusive box bounds (lattice coords, clamped to the field).</param>
	/// <param name="maxLevel">Max light level (normalization divisor).</param>
	public static void ComputeLocal(
		byte[] solid, byte[] blockLight, float[] light, byte[] blockLit,
		int nx, int ny, int nz,
		int x0, int y0, int z0, int x1, int y1, int z1,
		int maxLevel)
	{
		if (maxLevel < 1) maxLevel = 1;
		else if (maxLevel > 255) maxLevel = 255;

		// Clamp the box to the field.
		if (x0 < 0) x0 = 0; if (y0 < 0) y0 = 0; if (z0 < 0) z0 = 0;
		if (x1 >= nx) x1 = nx - 1; if (y1 >= ny) y1 = ny - 1; if (z1 >= nz) z1 = nz - 1;
		if (x0 > x1 || y0 > y1 || z0 > z1) return;

		int bx = x1 - x0 + 1, by = y1 - y0 + 1, bz = z1 - z0 + 1;
		int total = bx * by * bz;
		// Reuse the scratch buffers (grow if needed) so a local recompute doesn't
		// allocate ~140 KB on every call. Zero the box so the interior is solved
		// from scratch (no stale interior value or block-lit flag survives).
		if (_localLightScratch == null || _localLightScratch.Length < total)
		{
			_localLightScratch = new float[total];
		}
		if (_localLitScratch == null || _localLitScratch.Length < total)
		{
			_localLitScratch = new byte[total];
		}
		var boxLight = _localLightScratch;
		var boxLit = _localLitScratch;
		System.Array.Clear(boxLight, 0, total);
		System.Array.Clear(boxLit, 0, total);
		int G(int x, int y, int z) => x + nx * (y + ny * z);
		int L(int lx, int ly, int lz) => lx + bx * (ly + by * lz);

		// ------------------------------------------------------------------
		// FIXED BOUNDARY CONDITIONS. The box interior is solved by a max-BFS
		// seeded from its SURROUNDING, so the result equals what the full flood
		// fill would produce inside the box (the surrounding values are
		// unchanged by the edit — the box is one light-range past it). Two
		// kinds of surrounding:
		//   (a) SKY — a column open to the field top seeds 1f; a column blocked
		//       above the box hand-offs from the stale value just above it
		//       (already correct, and unaffected by the box).
		//   (b) BLOCK — every source in or adjacent to the box seeds its air
		//       neighbours at (level - 1), exactly as the full Compute does.
		// The interior is otherwise started at 0, so only the true conditions
		// drive the solve (a stale interior value could only make it LESS
		// correct, so it is never seeded).
		// ------------------------------------------------------------------

		// (a) Sky hand-off + open-sky seed, per (x, z) column of the box.
		for (int lx = 0; lx < bx; lx++)
			for (int lz = 0; lz < bz; lz++)
			{
				int x = x0 + lx, z = z0 + lz;
				if (y0 > 0)
				{
					// The column is blocked above the box: hand off from the
					// stale value just above the box (correct + edit-invariant).
					int gAbove = G(x, y0 - 1, z);
					if (solid[gAbove] == 0 && light[gAbove] > 0f)
					{
						int gl = G(x, y0, z);
						if (solid[gl] == 0)
						{
							boxLight[L(lx, 0, lz)] = light[gAbove];
							boxLit[L(lx, 0, lz)] = blockLit[gAbove];
						}
					}
				}
				else
				{
					// Box reaches the field top: a solid-free column up to the
					// top is open sky -> full light (same per-column rule as the
					// full Compute). Scan from the top of the box down.
					for (int y = y1; y >= y0; y--)
					{
						int g = G(x, y, z);
						if (solid[g] != 0) break;
						boxLight[L(lx, y - y0, lz)] = 1f;
					}
				}
			}

		// (b) Block-light sources: every solid source with a level, in the box
		//     OR on its 1-voxel shell (so a source just outside the box still
		//     seeds into the box, and a source inside seeds to the boundary).
		if (blockLight != null)
		{
			for (int lz = -1; lz <= bz; lz++)
				for (int ly = -1; ly <= by; ly++)
					for (int lx = -1; lx <= bx; lx++)
					{
						int x = x0 + lx, y = y0 + ly, z = z0 + lz;
						if (x < 0 || y < 0 || z < 0 || x >= nx || y >= ny || z >= nz) continue;
						int g = G(x, y, z);
						byte src = blockLight[g];
						if (src == 0 || solid[g] == 0) continue;
						float nl = (src > maxLevel ? maxLevel : (src >= 1 ? src - 1 : 0)) / (float)maxLevel;
						if (nl <= 0f) continue;
						// Seed the 6 air neighbours that land inside the box.
						Seed(lx + 1, ly, lz, nl);
						Seed(lx - 1, ly, lz, nl);
						Seed(lx, ly + 1, lz, nl);
						Seed(lx, ly - 1, lz, nl);
						Seed(lx, ly, lz + 1, nl);
						Seed(lx, ly, lz - 1, nl);
					}
		}

		void Seed(int lx, int ly, int lz, float nl)
		{
			if (lx < 0 || ly < 0 || lz < 0 || lx >= bx || ly >= by || lz >= bz) return;
			int g = G(x0 + lx, y0 + ly, z0 + lz);
			if (solid[g] != 0) return;
			int l = L(lx, ly, lz);
			if (nl > boxLight[l])
			{
				boxLight[l] = nl;
				boxLit[l] = 1; // block-lit (a block-source path reached it)
			}
		}

		// ------------------------------------------------------------------
		// PROPAGATION — a max-BFS over the box, decaying 1/voxel and snapping
		// to the level grid, exactly as the full Compute (deterministic +
		// seam-safe). Every voxel that has light from the boundary conditions
		// above is enqueued, then re-enqueued whenever a brighter path lifts it.
		// ------------------------------------------------------------------
		var queue = new int[total];
		int qHead = 0, qTail = 0;
		void Enq(int idx)
		{
			if (qTail >= queue.Length)
			{
				var grown = new int[queue.Length * 2];
				System.Array.Copy(queue, grown, queue.Length);
				queue = grown;
			}
			queue[qTail++] = idx;
		}
		for (int l = 0; l < total; l++)
		{
			if (boxLight[l] > 0f) Enq(l);
		}

		while (qHead < qTail)
		{
			int l = queue[qHead++];
			float lv = boxLight[l];
			if (lv <= 1f / maxLevel) continue; // can't propagate further
			int k = (int)System.Math.Round(lv * maxLevel) - 1;
			if (k < 0) continue;
			float nl = k / (float)maxLevel;
			int lx = l % bx;
			int rem = l / bx;
			int ly = rem % by;
			int lz = rem / by;
			PropStep(lx + 1, ly, lz, nl);
			PropStep(lx - 1, ly, lz, nl);
			PropStep(lx, ly + 1, lz, nl);
			PropStep(lx, ly - 1, lz, nl);
			PropStep(lx, ly, lz + 1, nl);
			PropStep(lx, ly, lz - 1, nl);
		}

		void PropStep(int lx, int ly, int lz, float nl)
		{
			if (lx < 0 || ly < 0 || lz < 0 || lx >= bx || ly >= by || lz >= bz) return;
			int g = G(x0 + lx, y0 + ly, z0 + lz);
			if (solid[g] != 0) return;
			int l = L(lx, ly, lz);
			if (nl <= boxLight[l]) return;
			boxLight[l] = nl;
			boxLit[l] = 1; // a block or hand-off path reached it
			Enq(l);
		}

		// Write the solved box back into the global field.
		for (int lz = 0; lz < bz; lz++)
			for (int ly = 0; ly < by; ly++)
				for (int lx = 0; lx < bx; lx++)
				{
					int l = L(lx, ly, lz);
					int g = G(x0 + lx, y0 + ly, z0 + lz);
					light[g] = boxLight[l];
					blockLit[g] = boxLit[l];
				}
	}
}
