namespace FactorySim.Voxel;

using System;
using System.Collections.Generic;

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

	// Scratch state reused by ComputeDelta across calls (main thread only, same
	// safety argument as the buffers above). The queues/changed lists are
	// scratch (only live for the duration of one ComputeDelta call). The RAW
	// integer light level (0..maxLevel) per wide lattice voxel — the delta's
	// working state — is a PER-CHUNK buffer (passed in as <c>raw</c>): each
	// chunk has its own wide field, so the raw state must be per-chunk (a
	// single static buffer would be clobbered when a different chunk's field is
	// committed while another chunk is being edited). The caller keeps it in
	// sync with its normalized field via RecomputeDeltaRaw.
	private static int[] _deltaQueue;
	private static List<int> _deltaChanged;

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

	/// <summary>
	/// STARLIGHT-STYLE incremental light update (the instant-edit path). Unlike
	/// <see cref="ComputeLocal"/> — which re-solves an entire box from scratch —
	/// this propagates ONLY the delta caused by one edited voxel, touching only
	/// the voxels whose light actually changes. It is the per-voxel analogue of
	/// Starlight's light propagation:
	/// <list type="bullet">
	///   <item>INCREASE: a voxel whose light rises to L offers each air neighbour
	///   L - 1 (solid = opacity 1); if that exceeds the neighbour's level, set it
	///   and propagate further (one light read per neighbour; the solid read only
	///   when actually setting).</item>
	///   <item>DECREASE: a voxel whose light drops (or is removed) marks its
	///   neighbours stale; each stale neighbour RE-DERIVES its true level from
	///   its other neighbours + its own sky seed + block sources; if it drops,
	///   it cascades the decrease; if it stays, propagation stops (its old value
	///   was still valid, so nothing further on that path changes).</item>
	///   <item>SKY: open-sky columns are a pure per-column function of the solid
	///   field. When an edit changes a column's openness, the column's sky values
	///   are recomputed in place (increase from the first changed voxel down to
	///   the solid, decrease upward) and the new values feed the propagation.</item>
	/// </list>
	///
	/// CORRECTNESS (bit-identical to Compute + ApplyLightResponse):
	/// <list type="bullet">
	///   <item>The RAW light is propagated in INTEGER levels 0..maxLevel, exactly
	///   the grid the max-BFS snaps to (level = max of sky-column seed,
	///   sourceLevel-1 at a source's face, and neighbour-1 over the 6-adjacency —
	///   the same max-plus-1 relaxation both solvers compute, so the converged
	///   fields are the same integers).</item>
	///   <item>The blockLit flag matches ComputeLocal (the production
	///   reference): its max-BFS marks a voxel block-lit whenever ANY
	///   propagation (sky or block) raises it, so the only voxels that stay
	///   sky-lit (blockLit 0) are the ones directly seeded by the open-sky
	///   per-column scan (air with a solid-free path to the field top) and the
	///   dark voxels. The write-back therefore sets blockLit = 1 iff the voxel
	///   is lit (raw &gt; 0) and NOT open-sky-seeded, and 0 for solid voxels —
	///   exactly what ComputeLocal leaves in the mask over the box.</item>
	///   <item>Every changed voxel's CURVED value is written by applying the
	///   caller's exact curve expression (same code path as
	///   ApplyLightResponse/ApplyLightResponseBox) to its new (level, blockLit)
	///   pair — the curve is a pure per-voxel function, so the resulting
	///   field is bit-identical to the full pipeline's.</item>
	///   <item>The decrease re-derivation uses the TRUE column sky seed
	///   (recomputed, not the possibly-stale cached value), so a column that
	///   just became open re-derives its voxels at the new sky level.</item>
	/// </list>
	/// The full <see cref="Compute"/> flood fill stays the reference and the
	/// async full rebuild re-converges the field (safety net); this method only
	/// needs to be correct for the single-voxel-edit deltas the instant path
	/// feeds it.
	///
	/// The raw-level working state is a per-chunk buffer (the <c>raw</c>
	/// parameter) and is re-synced by <see cref="RecomputeDeltaRaw"/> whenever
	/// the chunk's field is committed (a full rebuild) or the world is rebuilt;
	/// the caller owns the buffer and must keep it in sync (see VoxelChunk).
	/// </summary>
	/// <param name="solid">Padded solid|air field (1 = solid, opacity 1; 0 = air, opacity 0).</param>
	/// <param name="blockLight">Per-voxel block-light SOURCE levels (0 = none) at the same dims.</param>
	/// <param name="light">The CURRENT normalized (0..1, already response-curved) field. Changed voxels are re-curved in place.</param>
	/// <param name="blockLit">The CURRENT block-lit mask (updated at changed voxels).</param>
	/// <param name="nx,ny,nz">Padded field dimensions.</param>
	/// <param name="ex">Edited voxel's wide index (the voxel whose solid state / source level changed; its sky column is recomputed in place).</param>
	/// <param name="maxLevel">Max light level (normalization divisor).</param>
	/// <param name="curve">The caller's response-curve closure (exactly the ApplyLightResponseBox per-voxel expression), applied to each changed voxel's new raw level.</param>
	/// <param name="raw">The per-chunk RAW integer light level (0..maxLevel) working state (length = nx*ny*nz), kept in sync with the chunk's NORMALIZED field by <see cref="RecomputeDeltaRaw"/>. Updated in place by the propagation.</param>
	/// <returns>The wide-lattice indices whose CURVED light changed (empty if nothing changed).</returns>
	public static List<int> ComputeDelta(
		byte[] solid, byte[] blockLight, float[] light, byte[] blockLit,
		int nx, int ny, int nz,
		int ex,
		int maxLevel,
		Func<int, byte, float> curve,
		int[] raw)
	{
		if (maxLevel < 1) maxLevel = 1;
		else if (maxLevel > 255) maxLevel = 255;
		int total = nx * ny * nz;
		if (raw == null || raw.Length < total)
		{
			throw new System.ArgumentException("ComputeDelta: raw buffer too small", nameof(raw));
		}
		if (_deltaQueue == null || _deltaQueue.Length < total)
		{
			_deltaQueue = new int[total];
		}
		_deltaChanged ??= new List<int>();
		_deltaChanged.Clear();
		var changed = _deltaChanged;
		int nxy = nx * ny;
		int G(int x, int y, int z) => x + nx * (y + ny * z);

		// ------------------------------------------------------------------
		// Propagation queue. _deltaQueue is the main queue; it is seeded below
		// with the edited voxel's 6 air neighbours (a source added/removed
		// re-seeds its face air) and the edited column's sky-changed voxels,
		// and grown via Enq() as the propagation discovers changes.
		// ------------------------------------------------------------------
		var queue = _deltaQueue;
		int qHead = 0, qTail = 0;
		void Enq(int g)
		{
			if (qTail >= queue.Length)
			{
				var grown = new int[queue.Length * 2];
				System.Array.Copy(queue, grown, queue.Length);
				queue = grown;
			}
			queue[qTail++] = g;
		}

		// ------------------------------------------------------------------
		// (1) The edited column's SKY values are a pure function of the solid
		// field; an edit to (ex) can change exactly this column's openness.
		// Recompute the column's true sky values and update the changed voxels
		// in place (increase where it opened, decrease where it blocked).
		// ------------------------------------------------------------------
		int exx = ex % nx;
		int exrem = ex / nx;
		int exy = exrem % ny;
		int exz = exrem / ny;
		// topOpen = the LOWEST air voxel with a solid-free path to the top of
		// the wide field (walk down from the top; stops at the first solid).
		// Air at y >= topOpen is open to the sky (maxLevel) — this matches the
		// reference `Compute` SEED 1, which seeds air from the top down to the
		// first solid. Air below the first solid (y < topOpen) is enclosed and
		// gets no direct sky light; it is lit only by the BFS spilling in
		// sideways from neighbouring open columns, so we re-derive it from its
		// neighbours (below).
		int topOpen = ny;
		for (int yy = ny - 1; yy >= 0; yy--)
		{
			if (solid[G(exx, yy, exz)] != 0) break;
			topOpen = yy;
		}
		// Record the column voxels whose sky value changed, so we can re-derive
		// them (from their neighbours) once the helper functions are in scope
		// below. A column that just got BLOCKED drops its open-sky air to
		// enclosed (0), but that enclosed air is still lit by the neighbouring
		// open columns (light leaks in sideways), so the true level is
		// max(sky, neighbour, source) — we must re-derive, not just set the
		// sky value.
		var colChanged = new List<int>();
		for (int yy = 0; yy < ny; yy++)
		{
			int g = G(exx, yy, exz);
			if (solid[g] != 0) continue;
			int trueLv = yy >= topOpen ? maxLevel : 0;
			if (raw[g] == trueLv) continue;
			raw[g] = trueLv;
			blockLit[g] = 0; // sky is never block-lit; a later block offer re-sets it
			colChanged.Add(g);
			// Record the change for the caller: this voxel's sky light changed
			// (e.g. the column just opened → maxLevel, or just got blocked →
			// enclosed 0). The Rederive below may further adjust it (sideways
			// leak for newly-enclosed air), but the sky change itself must be
			// reported so the caller re-meshes and re-writes the curved value.
			changed.Add(g);
		}
		// The edited voxel itself: if it just became SOLID, its light drops to
		// 0 (solid carries no light), so record it as changed (the write-back
		// uses raw 0 for solid voxels). If it became air, the sky-column pass
		// already handled it (or it's open sky and unchanged).
		if (solid[ex] != 0)
		{
			changed.Add(ex);
		}

		// The edited voxel's own source state can change: re-offer into its
		// 6 air neighbours (a newly placed source seeds its face air; a removed
		// or SWAPPED source leaves its face air to be re-derived). Record the
		// source's face-air neighbours so we can re-derive them (to their new
		// level) once the helpers are in scope below — this is what makes a
		// source SWAP (level drop) propagate the decrease: the face air is at
		// the OLD (higher) level and must drop to the NEW level before the
		// cascade can continue.
		var srcFaceAir = new List<int>();
		byte src = (blockLight != null) ? blockLight[ex] : (byte)0;
		int exx2 = ex % nx, exy2 = (ex / nx) % ny, exz2 = (ex / (nx * ny)) % nz;
		CheckSrcFace(ex - 1, exx2 > 0);
		CheckSrcFace(ex + 1, exx2 < nx - 1);
		CheckSrcFace(ex - nx, exy2 > 0);
		CheckSrcFace(ex + nx, exy2 < ny - 1);
		CheckSrcFace(ex - nxy, exz2 > 0);
		CheckSrcFace(ex + nxy, exz2 < nz - 1);
		void CheckSrcFace(int g, bool inb)
		{
			if (!inb) return;
			if (solid[g] != 0) return;
			srcFaceAir.Add(g);
			Enq(g);
		}

		// ------------------------------------------------------------------
		// PROPAGATION LOOP (Starlight-style). A queued voxel re-evaluates its
		// neighbours in two phases:
		//   INCREASE: offer (level - 1) to each air neighbour (solid = opacity 1
		//   -> target L-1; air = opacity 0 -> also L-1 in this engine). A
		//   strictly higher offer updates the neighbour (block-lit when a block
		//   path offers; a strictly brighter sky offer clears a stale block flag)
		//   and enqueues it.
		//   DECREASE: any air neighbour that was OFFERED this voxel's (stale,
		//   pre-change) level is potentially stale (it may have been lit by us)
		//   and is re-derived: newLevel = max(ownSky, ownSourceFace, best neighbour
		//   offer). If it drops, the new (lower) value propagates; if it stays
		//   (any neighbour still offers the old level, or its own seed covers
		//   it), propagation stops on that path.
		// ------------------------------------------------------------------
		// Per-neighbour offer computation (increase): reads solid only when
		// actually setting (one light read per neighbour, per the spec).
		bool TryIncrease(int nidx, bool inb, int lv, bool srcBlock)
		{
			if (!inb) return false;
			if (solid[nidx] != 0) return false;
			if (lv <= raw[nidx]) return false;
			raw[nidx] = lv;
			if (srcBlock)
			{
				blockLit[nidx] = 1;
			}
			else if (blockLit[nidx] == 1)
			{
				blockLit[nidx] = 0; // a strictly-brighter sky path wins
			}
			changed.Add(nidx);
			Enq(nidx);
			return true;
		}

		// Per-neighbour re-derivation (decrease): true when the neighbour's
		// level dropped (it was enqueued for further decrease propagation).
		bool Rederive(int nidx)
		{
						int ixn = nidx % nx;
			if (ixn == 0 || ixn == nx - 1) return false;
			int re = nidx / nx;
			int iyn = re % ny;
			if (iyn == 0 || iyn == ny - 1) return false;
			int izn = re / ny;
			if (izn == 0 || izn == nz - 1) return false;
			if (solid[nidx] != 0) return false;
			int oldLv = raw[nidx];
			// Own seeds: the TRUE sky value of its column (the edited column was
			// recomputed in place above; every other column is unchanged, so its
			// sky value is the true one) and its own block-source face.
			int ownSky = 0;
			// The voxel's TRUE sky seed: maxLevel iff it has a solid-free path
			// straight up to the top of the volume (the same per-column rule as
			// the sky-column pass). Walk UP from the voxel (exclusive) to the
			// top; if every voxel above is air, the column is open here.
			// (The edited column was recomputed in place above; every other
			// column is unchanged, so this walk is exact for all columns.)
			{
				// Walk up from the voxel (exclusive) to the top; the column is
				// open here only if EVERY voxel above is air (reaches the top).
				int yy = iyn + 1;
				while (yy < ny && solid[nidx + nx * (yy - iyn)] == 0)
				{
					yy++;
				}
				if (yy >= ny)
				{
					ownSky = maxLevel; // reached the top: solid-free path to sky
				}
			}
			int ownSrc = 0;
			if (blockLight != null)
			{
				// A block source adjacent to this air voxel emits (level - 1)
				// into it: check the 6 neighbours (a solid source next to us).
				// (Cheap: only 6 reads, and only on a stale air voxel.)
				ownSrc = MaxSourceFace(nidx, ixn, iyn, izn);
			}
			int best = 0;
			int bestBlock = 0;
			// Neighbour offers: each air neighbour offers (its level - 1) unless
			// it is solid (offers nothing). Track the best (and, for the flag,
			// whether a block path achieves the best).
			best = OfferFrom(nidx - 1, ixn > 0, best, ref bestBlock);
			best = OfferFrom(nidx + 1, ixn < nx - 1, best, ref bestBlock);
			best = OfferFrom(nidx - nx, iyn > 0, best, ref bestBlock);
			best = OfferFrom(nidx + nx, iyn < ny - 1, best, ref bestBlock);
			best = OfferFrom(nidx - nxy, izn > 0, best, ref bestBlock);
			best = OfferFrom(nidx + nxy, izn < nz - 1, best, ref bestBlock);
			if (ownSky > best)
			{
				best = ownSky;
				bestBlock = 0; // sky is never block-lit
			}
			else if (ownSky == best && best > 0)
			{
				// Tie: keep the flag (a later equal write never clears it — the
				// max-BFS only SETs on ties, so keeping matches its result).
			}
			if (ownSrc > best)
			{
				best = ownSrc;
				bestBlock = 1;
			}
			else if (ownSrc == best && best > 0)
			{
				bestBlock = 1; // block path ties -> block-lit (max-BFS sets on tie)
			}
			if (best != oldLv)
			{
				raw[nidx] = best;
				blockLit[nidx] = (byte)bestBlock;
				changed.Add(nidx);
				Enq(nidx);
				return true;
			}
			return false;
		}

		int MaxSourceFace(int nidx, int ixn, int iyn, int izn)
		{
			int best = 0;
			CheckSource(nidx - 1, ixn > 0);
			CheckSource(nidx + 1, ixn < nx - 1);
			CheckSource(nidx - nx, iyn > 0);
			CheckSource(nidx + nx, iyn < ny - 1);
			CheckSource(nidx - nxy, izn > 0);
			CheckSource(nidx + nxy, izn < nz - 1);
			return best;
			void CheckSource(int g, bool inb)
			{
				if (!inb || blockLight == null) return;
				byte s = blockLight[g];
				if (s == 0 || solid[g] == 0) return;
				int l = (s > maxLevel) ? maxLevel : (s >= 1 ? s - 1 : 0);
				if (l > best) best = l;
			}
		}

		int OfferFrom(int nidx, bool inb, int best, ref int bestBlock)
		{
			if (!inb) return best;
			if (solid[nidx] != 0) return best; // a solid neighbour offers nothing
			int l = raw[nidx];
			if (l <= 1) return best; // would saturate to 0 (can't offer)
			int offer = l - 1;
			if (offer > best)
			{
				best = offer;
				bestBlock = (blockLit[nidx] == 1) ? 1 : 0;
			}
			else if (offer == best && best > 0 && blockLit[nidx] == 1)
			{
				bestBlock = 1; // block path ties the best -> block-lit (set on tie)
			}
			return best;
		}

		// Re-derive the column voxels whose sky value changed: their true level
		// is max(sky, neighbour, source), so a just-blocked column's enclosed air
		// is re-lit from the neighbouring open columns (light leaks in sideways).
		// Also re-derive the edited source's face-air neighbours: a source SWAP
		// (level drop) leaves them at the OLD (higher) level, and they must drop
		// to the NEW level before the decrease cascade can propagate. This must
		// happen AFTER the helpers are defined and BEFORE the main loop, so the
		// cascade starts from the correct levels.
		for (int i = 0; i < colChanged.Count; i++)
		{
			int g = colChanged[i];
			Rederive(g);
			Enq(g);
		}
		for (int i = 0; i < srcFaceAir.Count; i++)
		{
			int g = srcFaceAir[i];
			Rederive(g);
			Enq(g);
		}

		while (qHead < qTail)
		{
			int idx = queue[qHead++];
			if (idx < 0 || idx >= total) continue;
			int ix = idx % nx;
			if (ix == 0 || ix == nx - 1) continue;
			int rem = idx / nx;
			int iy = rem % ny;
			if (iy == 0 || iy == ny - 1) continue;
			int iz = rem / ny;
			if (iz == 0 || iz == nz - 1) continue;
			if (solid[idx] != 0) continue;
			int lv = raw[idx];
			// INCREASE: a voxel that just rose offers (lv - 1) to its neighbours.
			// (A voxel that just DROPPED does not offer its new lower value —
			// that can only reduce a neighbour, which the re-derivation below
			// handles.) A level-0 voxel offers nothing.
			if (lv >= 2)
			{
				bool srcBlock = blockLit[idx] == 1;
				TryIncrease(idx - 1, ix > 0, lv - 1, srcBlock);
				TryIncrease(idx + 1, ix < nx - 1, lv - 1, srcBlock);
				TryIncrease(idx - nx, iy > 0, lv - 1, srcBlock);
				TryIncrease(idx + nx, iy < ny - 1, lv - 1, srcBlock);
				TryIncrease(idx - nxy, iz > 0, lv - 1, srcBlock);
				TryIncrease(idx + nxy, iz < nz - 1, lv - 1, srcBlock);
			}
			// DECREASE CASCADE: this voxel's level may have DROPPED (a sky column
			// got blocked, or a source was removed and it re-derived lower). Any
			// neighbour that was lit at a level this voxel can no longer support
			// (its current level > lv - 1, i.e. it was lit by a path at a level
			// >= lv) is potentially stale: re-derive its true level from its
			// OTHER neighbours + its own sky seed + block sources. If it drops,
			// it is enqueued and cascades; if it stays, propagation stops there.
			// (A neighbour at exactly lv - 1 could still be lit by us, but its
			// level is unchanged either way, so it is left alone.)
			StepDecrease(idx - 1, ix > 0, lv);
			StepDecrease(idx + 1, ix < nx - 1, lv);
			StepDecrease(idx - nx, iy > 0, lv);
			StepDecrease(idx + nx, iy < ny - 1, lv);
			StepDecrease(idx - nxy, iz > 0, lv);
			StepDecrease(idx + nxy, iz < nz - 1, lv);
		}

		void StepDecrease(int nidx, bool inb, int lv)
		{
			if (!inb) return;
			// Only re-derive a neighbour that is STRICTLY higher than what this
			// (possibly dropped) voxel can offer (lv - 1): it was lit by a path
			// at a level >= lv, so if this voxel dropped, it may be stale.
			if (solid[nidx] == 0 && lv >= 1 && raw[nidx] > lv - 1)
			{
				Rederive(nidx);
			}
		}

		// ------------------------------------------------------------------
		// WRITE the changed voxels' CURVED values. The curve is a pure
		// per-voxel function of (raw level, blockLit), so applying it to each
		// changed voxel's new pair reproduces the full pipeline's field exactly.
		//
		// BLOCK-LIT SEMANTICS (must match ComputeLocal, the production
		// reference): ComputeLocal's max-BFS marks a voxel block-lit whenever
		// ANY propagation (sky OR block) raises it — the only voxels that stay
		// sky-lit (blockLit 0) are the ones directly seeded by the open-sky
		// per-column scan (air with a solid-free path to the field top) and the
		// dark voxels. So the rule is: blockLit = 1 iff the voxel is lit
		// (raw > 0) AND it is NOT open-sky-seeded. This is what makes a torch
		// under an open column keep its sky-lit neighbours un-curved while the
		// enclosed air around the torch is block-lit (gentle curve).
		// ------------------------------------------------------------------
		for (int i = 0; i < changed.Count; i++)
		{
			int g = changed[i];
			if (solid[g] != 0)
			{
				// Solid carries no light: the reference field value is the
				// curve of raw 0 (the floor), and a solid voxel is never
				// block-lit (ComputeLocal leaves its mask at 0).
				blockLit[g] = 0;
				float cs = curve(0, (byte)0);
				if (cs != light[g])
				{
					light[g] = cs;
				}
				continue;
			}
			int rawLv = raw[g];
			// Open-sky-seeded? Air with a solid-free vertical path from this
			// voxel to the top of the field. For the edited column, topOpen was
			// already computed (a voxel is open-sky-seeded iff y >= topOpen);
			// for every other column the solid is unchanged, so walk up from the
			// voxel to the top (exact, and short in practice).
			int wx = g % nx;
			int wy = (g / nx) % ny;
			bool openSky;
			if (wx == exx && (g / (nx * ny)) == exz)
			{
				openSky = wy >= topOpen;
			}
			else
			{
				openSky = true;
				for (int yy = wy; yy < ny; yy++)
				{
					if (solid[g + nx * (yy - wy)] != 0)
					{
						openSky = false;
						break;
					}
				}
			}
			byte litB = (!openSky && rawLv > 0) ? (byte)1 : (byte)0;
			blockLit[g] = litB;
			float cs2 = curve(rawLv, litB);
			if (cs2 != light[g])
			{
				light[g] = cs2;
			}
		}
		return changed;
	}

	/// <summary>
	/// Re-synchronizes a per-chunk raw-level working buffer with a freshly
	/// computed NORMALIZED (0..1, NOT response-curved) light field: raw =
	/// round(norm * maxLevel), clamped to 0..maxLevel. The input MUST be the
	/// pre-curve field — the response curve (falloff^exp, dark-gamma, floor) is
	/// NOT the inverse of normalization, so deriving raw from the curved field
	/// would be wrong for every sub-full voxel. Call it with the field BEFORE
	/// <c>ApplyLightResponse</c> (or on a copy). The buffer is grown if needed.
	/// Safe to call from any thread (it only writes the caller's buffer).
	/// </summary>
	public static void RecomputeDeltaRaw(float[] normalizedLight, int maxLevel, int[] raw)
	{
		if (maxLevel < 1) maxLevel = 1;
		else if (maxLevel > 255) maxLevel = 255;
		if (raw == null || raw.Length < normalizedLight.Length)
		{
			throw new System.ArgumentException("RecomputeDeltaRaw: raw buffer too small", nameof(raw));
		}
		for (int i = 0; i < normalizedLight.Length; i++)
		{
			int k = (int)System.Math.Round(normalizedLight[i] * maxLevel);
			if (k < 0) k = 0;
			else if (k > maxLevel) k = maxLevel;
			raw[i] = k;
		}
	}
}
