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
	/// A byte light field at the same padded dimensions, 0..maxLevel per voxel.
	/// Solid voxels are 0. Air voxels that see open sky (a solid-free column to
	/// the top) are maxLevel; enclosed air fades from maxLevel toward 0 over the
	/// 3D distance from the nearest lit air; air near a block source is lit by
	/// the source (max with sky); enclosed air far from any source is 0.
	/// </returns>
	public static byte[] Compute(byte[] solid, int nx, int ny, int nz, byte[] blockLight = null, int maxLevel = 15)
	{
		// Clamp the max level: the light is a byte (0..255) and must be >= 1.
		if (maxLevel < 1) maxLevel = 1;
		else if (maxLevel > 255) maxLevel = 255;
		int total = nx * ny * nz;
		var light = new byte[total];

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
					light[idx] = (byte)maxLevel;
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
				byte nl = (byte)(src > maxLevel ? maxLevel : (src >= 1 ? src - 1 : 0)); // level just outside the source
				if (nl <= 0)
				{
					continue;
				}
				int ix = idx % nx;
				int rem = idx / nx;
				int iy = rem % ny;
				int iz = rem / ny;
				// Index layout: idx = ix + nx*(iy + ny*iz). So the neighbour
				// offsets are: ±X -> ±1, ±Y -> ±nx, ±Z -> ±nx*ny.
				SeedAir(idx - 1,         ix > 0,         nl);
				SeedAir(idx + 1,         ix < nx - 1,    nl);
				SeedAir(idx - nx,        iy > 0,         nl);
				SeedAir(idx + nx,        iy < ny - 1,    nl);
				SeedAir(idx - nx * ny,   iz > 0,         nl);
				SeedAir(idx + nx * ny,   iz < nz - 1,    nl);
			}
		}

		void SeedAir(int nidx, bool inBounds, byte nl)
		{
			if (!inBounds || solid[nidx] != 0 || nl <= light[nidx])
			{
				return;
			}
			light[nidx] = nl;
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
		// Enqueue every sky-seeded (15) voxel to start the propagation. (Block
		// sources were already enqueued via SeedAir when they exceeded 0.)
		for (int idx = 0; idx < total; idx++)
		{
			if (light[idx] >= maxLevel)
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
			byte lv = light[idx];
			if (lv <= 1)
			{
				continue; // can't propagate further (would saturate to 0)
			}
			byte nl = (byte)(lv - 1);

			int ix = idx % nx;
			int rem = idx / nx;
			int iy = rem % ny;
			int iz = rem / ny;

			// Index layout: idx = ix + nx*(iy + ny*iz). Neighbour offsets:
			// ±X -> ±1, ±Y -> ±nx, ±Z -> ±nx*ny.
			Propagate(idx - 1,         ix > 0,         nl);
			Propagate(idx + 1,         ix < nx - 1,    nl);
			Propagate(idx - nx,        iy > 0,         nl);
			Propagate(idx + nx,        iy < ny - 1,    nl);
			Propagate(idx - nx * ny,   iz > 0,         nl);
			Propagate(idx + nx * ny,   iz < nz - 1,    nl);
		}

		void Propagate(int nidx, bool inBounds, byte nl)
		{
			if (!inBounds || solid[nidx] != 0 || nl <= light[nidx])
			{
				return;
			}
			light[nidx] = nl;
			Enqueue(nidx);
		}

		return light;
	}
}
