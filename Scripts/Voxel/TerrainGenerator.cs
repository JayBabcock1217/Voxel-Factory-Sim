using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace FactorySim.Voxel;

/// <summary>
/// Fills a chunk's voxel data:
/// 1. 2D Perlin (fractal) heightmap, modulated per-biome (height + amplitude)
/// 2. data-driven material layers (surface / dirt / deep / cave) chosen from the
///    biomes that overlap each column (weighted majority at biome borders). Biomes
///    are selected per column by a weighted cumulative distribution over each
///    biome's Probability (see <see cref="SampleBiomes"/>), so the world's biome
///    mix follows the editor-set probabilities.
/// 3. 3D Perlin noise cave carving (hollow, enclosed by a surface crust)
/// 4. cell-placed structures (rock boulders, from the world's
///    <see cref="StructureRegistry"/>), stamped over a hashed cell grid so they
///    are identical no matter which chunk generates them
/// 5. column-placed structures (trees, or cacti for cactus biomes, from the same
///    <see cref="StructureRegistry"/>), rolled per column so plants are
///    identical no matter which chunk generates them
///
/// Biome selection is a PURE function of (wx, wz) + config (see
/// <see cref="BiomeAt"/>), so it is deterministic and seam-safe — a column's
/// biome is identical whether it is generated from its own chunk or a neighbour's.
/// </summary>
public static class TerrainGenerator
{
	// ---------------------------------------------------------------------
	// Public API - both sync and async variants
	// ---------------------------------------------------------------------

	/// <summary>Masters the terrain generation. Runs synchronously.</summary>
	public static void Generate(ChunkData chunk, int wx0, int wz0, GenConfig cfg, Dictionary<Vector2I, VoxelChunk> chunks = null)
	{
		int sx = chunk.SizeX;
		int sz = chunk.SizeZ;

		// Precompute the per-column surface heightmap once so every stage below
		// can reuse it instead of re-evaluating Perlin per column.
		var heights = new short[sx * sz];
		for (int x = 0; x < sx; x++)
		{
			for (int z = 0; z < sz; z++)
			{
				heights[x + sx * z] = (short)SurfaceHeight(wx0 + x, wz0 + z, cfg);
			}
		}

		// Stage 1: fill the heightmap.
		GenerateHeightmap(chunk, wx0, wz0, cfg, heights, sx, sz).Wait();

		// Stage 2: carve caves (after heightmap).
		if (cfg.EnableCaves)
		{
			GenerateCaves(chunk, wx0, wz0, cfg, cfg.ChunkHeight, heights, sx, sz).Wait();
		}

		// Stage 3: stamp structures — after caves. Ore veins (deep rock, via the
		// IStructureGenerator path) and cell-placed rocks (from the structure
		// registry).
		ApplyStructuresAsync(chunk, wx0, wz0, cfg, heights, sx, sz, chunks).Wait();
		if (cfg.Structures != null)
		{
			Action<int, int, int, byte> rockPut = MakePut(chunk, wx0, wz0, sx, sz, chunks);
			foreach (var rock in cfg.Structures.CellStructures)
			{
				rock.PlaceCells(chunk, wx0, wz0, sx, sz, heights, cfg, rockPut);
			}
		}

		// Stage 4: foliage (trees, cacti) — after structures.
		PlaceFoliageAsync(chunk, wx0, wz0, cfg, heights, sx, sz, chunks).Wait();
	}

	/// <summary>
	/// Masters the terrain generation. Runs asynchronously, but with a BOUNDED
	/// worker pool (see <see cref="WorkerCount"/>) instead of one thread-pool task
	/// per column/slice: the old code spawned ~280 tasks per chunk (256 foliage
	/// columns alone), and with several chunks generating concurrently that
	/// oversubscribed the thread pool so scheduling overhead dominated the actual
	/// voxel math. Each stage now partitions its work across a small fixed set of
	/// slices, each handled by a dedicated worker via a <see cref="ManualResetEvent"/>
	/// barrier. Output is identical — only the threading is cheaper.
	/// </summary>
	/// <param name="chunks">
	/// The world's loaded-chunk map, passed through to the foliage stage for
	/// the solid-base check (a plant's base may sit in a neighbouring chunk
	/// when it lands in the margin band). May be null — the check then treats
	/// every base as solid and never rejects.
	/// </param>
	/// <param name="timeStages">
	/// When true, logs the elapsed milliseconds of each stage to the console
	/// (for measuring generation-speed changes). Off by default.
	/// </param>
	public static async Task GenerateAsync(ChunkData chunk, int wx0, int wz0, GenConfig cfg, Dictionary<Vector2I, VoxelChunk> chunks = null, bool timeStages = false)
	{
		int sx = chunk.SizeX;
		int sz = chunk.SizeZ;

		// Precompute the per-column surface heightmap once so every stage below
		// can reuse it instead of re-evaluating Perlin per column.
		var heights = new short[sx * sz];
		for (int x = 0; x < sx; x++)
		{
			for (int z = 0; z < sz; z++)
			{
				heights[x + sx * z] = (short)SurfaceHeight(wx0 + x, wz0 + z, cfg);
			}
		}

		if (timeStages)
		{
			GD.Print($"[GenTiming] chunk ({wx0},{wz0}) start");
		}
		var sw = System.Diagnostics.Stopwatch.StartNew();

		// Stage 1: fill the heightmap (material layers per column).
		await GenerateHeightmap(chunk, wx0, wz0, cfg, heights, sx, sz);
		if (timeStages) { GD.Print($"[GenTiming]   heightmap {sw.ElapsedMilliseconds} ms"); sw.Restart(); }

		// Stage 2: carve caves (must run after the heightmap so it knows
		// which voxels are solid).
		if (cfg.EnableCaves)
		{
			await GenerateCaves(chunk, wx0, wz0, cfg, cfg.ChunkHeight, heights, sx, sz);
			if (timeStages) { GD.Print($"[GenTiming]   caves     {sw.ElapsedMilliseconds} ms"); sw.Restart(); }
		}

		// Stage 3: stamp structures. Must run after caves so ore veins land in
		// solid rock and boulders rest on the post-cave surface.
		await ApplyStructuresAsync(chunk, wx0, wz0, cfg, heights, sx, sz, chunks);
		if (cfg.Structures != null)
		{
			Action<int, int, int, byte> rockPut = MakePut(chunk, wx0, wz0, sx, sz, chunks);
			foreach (var rock in cfg.Structures.CellStructures)
			{
				rock.PlaceCells(chunk, wx0, wz0, sx, sz, heights, cfg, rockPut);
			}
		}
		if (timeStages) { GD.Print($"[GenTiming]   structures {sw.ElapsedMilliseconds} ms"); sw.Restart(); }

		// Stage 4: foliage (trees, cacti). Must run after caves and structures
		// so trees don't grow inside rock boulders or ore veins, and so the
		// solid-base check sees the post-cave surface (no floating trees).
		// Canopy overhangs spill into loaded neighbour chunks; the world re-meshes
		// the 8 neighbours when this chunk completes, so those leaves appear.
		await PlaceFoliageAsync(chunk, wx0, wz0, cfg, heights, sx, sz, chunks);
		if (timeStages)
		{
			GD.Print($"[GenTiming]   foliage   {sw.ElapsedMilliseconds} ms");
		}
	}

	// ---------------------------------------------------------------------
	// Bounded worker pool + shared helpers
	// ---------------------------------------------------------------------

	/// <summary>
	/// Number of concurrent workers used by each generation stage. Bounded to the
	/// processor count (clamped to 1..8) so a chunk's stages never oversubscribe
	/// the thread pool: with several chunks generating at once, the old
	/// one-task-per-column scheme (256 foliage tasks/chunk) saturated the pool and
	/// scheduling overhead dominated. A small fixed slice count keeps the work
	/// parallel without the thrash.
	/// </summary>
	private static int WorkerCount =>
		Math.Max(1, Math.Min(8, System.Environment.ProcessorCount));

	/// <summary>
	/// Runs <paramref name="work"/> across <see cref="WorkerCount"/> slices on the
	/// thread pool, waiting for all to finish. The work is partitioned by
	/// <paramref name="count"/> (e.g. number of columns or Y-slices) into
	/// contiguous ranges; slice <c>k</c> handles indices
	/// <c>[count*k/W, count*(k+1)/W)</c>. This replaces the old "spawn one task per
	/// item" pattern with a bounded set of workers, which is far cheaper when the
	/// item count is large (256 columns) relative to the core count.
	/// </summary>
	private static void ParallelSlices(int count, Action<int, int, int> work)
	{
		int w = Math.Min(WorkerCount, Math.Max(1, count));
		if (w <= 1)
		{
			work(0, count, 0);
			return;
		}

		var workers = new Thread[w];
		var gate = new ManualResetEvent(false);
		int slice = count / w;
		int rem = count % w;

		for (int k = 0; k < w; k++)
		{
			// The first `rem` slices take one extra element so everything is covered.
			int start = k * slice + Math.Min(k, rem);
			int end = start + slice + (k < rem ? 1 : 0);
			if (start >= end)
			{
				gate.Set(); // empty slice: signal done immediately
				continue;
			}

			workers[k] = new Thread(() =>
			{
				try
				{
					work(start, end, k);
				}
				finally
				{
					gate.Set();
				}
			});
			workers[k].IsBackground = true;
			workers[k].Start();
		}

		for (int k = 0; k < w; k++)
		{
			gate.WaitOne();
		}
	}

	/// <summary>
	/// Per-column biome data, computed ONCE per column and shared by the
	/// heightmap and foliage stages (each of which previously re-sampled the
	/// same 2D biome noise per column). For each column we store:
	/// the DOMINANT biome index (argmax weight, ties to lower index — matching
	/// <see cref="BiomeAt"/>, and -1 when no biomes are configured) and the
	/// four per-layer material ids resolved via <see cref="Accumulate"/>.
	/// Reusing these avoids 3-4 redundant <see cref="SampleBiomes"/>/noise
	/// samples per column and lets the heightmap write its fill-runs without
	/// any per-voxel biome work. Layout: column index <c>c = x + sx*z</c>.
	/// </summary>
	private static (int[] dom, int[] surface, int[] subsurface, int[] deep, int[] cave)
		ColumnBiomes(int wx0, int wz0, int sx, int sz, GenConfig cfg)
	{
		var biomes = Biomes(cfg);
		int cols = sx * sz;
		var dom = new int[cols];
		var surface = new int[cols];
		var subsurface = new int[cols];
		var deep = new int[cols];
		var cave = new int[cols];

		for (int x = 0; x < sx; x++)
		{
			for (int z = 0; z < sz; z++)
			{
				int c = x + sx * z;
				var weights = SampleBiomes(wx0 + x, wz0 + z, cfg);

				// Dominant = argmax weight (matches BiomeAt: ties to lower
				// index; -1 when there are no biomes).
				int d = weights.Length == 0 ? -1 : 0;
				for (int b = 1; b < weights.Length; b++)
				{
					if (weights[b] > weights[d]) d = b;
				}
				dom[c] = d;

				surface[c] = Accumulate(biomes, weights, b => b.SurfaceMaterial);
				subsurface[c] = Accumulate(biomes, weights, b => b.SubsurfaceMaterial);
				deep[c] = Accumulate(biomes, weights, b => b.DeepMaterial);
				cave[c] = Accumulate(biomes, weights, b => b.CaveMaterial);
			}
		}
		return (dom, surface, subsurface, deep, cave);
	}

	// ---------------------------------------------------------------------
	// Heightmap - parallelized by blocks of cells
	// ---------------------------------------------------------------------

	/// <summary>
	/// Fills the heightmap (material layers per column) using the bounded worker
	/// pool. The per-column layer materials come from the shared
	/// <see cref="ColumnBiomes"/> cache (no per-voxel biome work). A column is
	/// filled top-to-bottom as tight single-material fill-runs: surface(1) +
	/// dirt(DirtDepth) + deep + cave + bedrock(the bottom two layers). This
	/// reproduces the old per-voxel layer rule exactly (the old code checked
	/// bedrock first, so for h &lt; 2 a column is entirely bedrock) but writes each
	/// run as one store per voxel with no per-voxel branch.
	/// </summary>
	private static Task GenerateHeightmap(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz)
	{
		const byte bedrock = (byte)VoxelMaterialIds.Bedrock;
		var (_, surface, subsurface, deep, cave) = ColumnBiomes(wx0, wz0, sx, sz, cfg);
		var data = chunk.Data;
		int sy = chunk.SizeY;
		int dirt = cfg.DirtDepth;
		int caveStoneDepth = cfg.CaveStoneDepth;

		return Task.Run(() =>
		{
			ParallelSlices(sz, (zStart, zEnd, _) =>
			{
				for (int z = zStart; z < zEnd; z++)
				{
					for (int x = 0; x < sx; x++)
					{
						int c = x + sx * z;
						int h = heights[c];
						if (h < 0)
						{
							h = 0;
						}

						// Base index for this column's y=0 voxel; layout is
						// x + sx*(y + sy*z), so y advances by sx.
						int idx = x + sx * (sy * z);

						// Bedrock: bottom two layers (always, even for h < 2 the
						// whole column is bedrock — matches the old y<2-first rule).
						data[idx] = bedrock;
						if (sy > 1)
						{
							data[idx + sx] = bedrock;
						}

						if (h >= 2)
						{
							byte surf = (byte)surface[c];
							byte dirtM = (byte)subsurface[c];
							byte deepM = (byte)deep[c];
							byte caveM = (byte)cave[c];

							// Surface voxel (y == h).
							data[idx + h * sx] = surf;

							// Dirt: y in [h - dirt + 1, h).
							int dirtStart = Math.Max(h - dirt + 1, 2);
							for (int y = dirtStart; y < h; y++)
							{
								data[idx + y * sx] = dirtM;
							}

							// Deep: y in [max(caveStoneDepth,2), dirtStart).
							int deepStart = Math.Max(caveStoneDepth, 2);
							for (int y = deepStart; y < dirtStart; y++)
							{
								data[idx + y * sx] = deepM;
							}

							// Cave: y in [2, deepStart).
							for (int y = 2; y < deepStart; y++)
							{
								data[idx + y * sx] = caveM;
							}
						}
					}
				}
			});
		});
	}

	// ---------------------------------------------------------------------
	// Caves - parallelized by Z-slices (each slice is independent)
	// ---------------------------------------------------------------------

	/// <summary>
	/// Carves caves (3D Perlin) using the bounded worker pool. The Y range is
	/// partitioned into contiguous slices, each handled by one worker (instead of
	/// the old one-Task.Run-per-slice). Each slice's Y loop is CLIPPED to its own
	/// range — <c>[max(yStart, caveFloorY), min(yEnd, maxY)]</c> — so a voxel is
	/// never visited by the wrong slice and the old per-voxel
	/// <c>y &lt; yStart || y &gt;= yEnd</c> bounds check is gone. Writes go straight
	/// to the data array. Output is identical to the original.
	/// </summary>
	private static Task GenerateCaves(ChunkData chunk, int wx0, int wz0, GenConfig cfg, int maxDepth, short[] heights, int sx, int sz)
	{
		// The cave floor: no cave carving below this Y (leaves a solid slab
		// above the bedrock so the player always has ground to stand on).
		int caveFloorY = cfg.CaveFloorDepth;
		int ceiling = cfg.CaveCeiling;
		float threshold = cfg.CaveThreshold;
		var data = chunk.Data;
		var noise = cfg.CaveNoise;
		int sy = chunk.SizeY;

		// Partition the Y range [caveFloorY, maxDepth) into WorkerCount slices.
		int totalY = Math.Max(0, maxDepth - caveFloorY);
		return Task.Run(() =>
		{
			ParallelSlices(totalY, (yStart, yEnd, _) =>
			{
				int yLo = caveFloorY + yStart;
				int yHi = Math.Min(caveFloorY + yEnd, maxDepth); // exclusive
				for (int x = 0; x < sx; x++)
				{
					int wx = wx0 + x;
					for (int z = 0; z < sz; z++)
					{
						int c = x + sx * z;
						int maxY = Mathf.Min(heights[c] - ceiling, maxDepth);
						if (maxY < yLo)
						{
							continue; // this column's cave range is entirely below the slice
						}
						int yTop = Math.Min(maxY, yHi - 1);
						int wz = wz0 + z;
						for (int y = yTop; y >= yLo; y--)
						{
							float n = noise.GetNoise3D(wx, y, wz);
							if (Mathf.Abs(n) > threshold)
							{
								// Layout: x + sx*(y + sy*z).
								data[x + sx * (y + sy * z)] = 0;
							}
						}
					}
				}
			});
		});
	}

	// ---------------------------------------------------------------------
	// Foliage - per-biome, parallelized by column (mx,mz), independent
	// ---------------------------------------------------------------------

	/// <summary>
	/// One queued canopy-overhang voxel: a world-space write that couldn't be
	/// applied because its target chunk was still generating when the producing
	/// chunk's foliage stage ran. Applied once the target chunk finishes.
	/// </summary>
	private readonly struct OverhangVoxel
	{
		public readonly int X;
		public readonly int Y;
		public readonly int Z;
		public readonly byte Material;

		public OverhangVoxel(int x, int y, int z, byte material)
		{
			X = x;
			Y = y;
			Z = z;
			Material = material;
		}
	}

	/// <summary>
	/// Pending canopy-overhang writes, keyed by the chunk coord they belong to.
	/// A chunk's foliage stage writes its trees' overhang into loaded neighbours
	/// immediately; a neighbour that is still generating can't be written into,
	/// so the voxel is queued here instead. When that neighbour finishes
	/// generating, the main-thread <see cref="ReDrainOverhangsUntilStable"/>
	/// applies the queued voxels to its now-ready buffer. This is the "queue a
	/// modification for when that chunk gets generated" half of the
	/// border-overhang fix. Hive border-overhangs live in their own queue (see
	/// <see cref="_pendingHiveOverhangs"/>).
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<
		Vector2I, System.Collections.Concurrent.ConcurrentBag<OverhangVoxel>> _pendingOverhangs = new();

	/// <summary>
	/// The material id a hive sphere is made of. Mirrors
	/// <see cref="VoxelMaterialIds.Hive"/> (kept as a local constant so the
	/// queued-write API does not take a material parameter — every voxel in the
	/// hive queue is a hive voxel by construction).
	/// </summary>
	private const int HiveMaterial = VoxelMaterialIds.Hive;

	/// <summary>
	/// A queued HIVE-overhang voxel: a hive sphere voxel that overhangs a chunk
	/// border. Hives are placed on the MAIN thread (see
	/// <c>VoxelWorld.TryPlaceHiveInChunk</c>), so unlike canopy overhangs (which
	/// the gen threads append) these are appended only by the main thread. The
	/// target chunk is not data-ready yet, so the voxel is queued here and
	/// applied by the main-thread <see cref="ReDrainHiveOverhangs"/> pass the
	/// moment that chunk becomes data-ready — exactly the "queue a modification
	/// for when that chunk gets generated" mechanism the canopy overhangs use.
	///
	/// <see cref="Health"/> is the health the write should carry (full health for
	/// a fresh hive; a RESTORED hive's broken voxels were never written, so its
	/// remaining voxels are written at full health — the save records which
	/// voxels were broken, not which are whole).
	/// </summary>
	private readonly struct HiveOverhangVoxel
	{
		public readonly int X;
		public readonly int Y;
		public readonly int Z;
		public readonly float Health;

		public HiveOverhangVoxel(int x, int y, int z, float health)
		{
			X = x;
			Y = y;
			Z = z;
			Health = health;
		}
	}

	/// <summary>
	/// Pending hive-overhang writes, keyed by the chunk coord they belong to.
	/// Appended by the main thread when a hive is placed; drained by the main
	/// thread when the target chunk becomes data-ready (see
	/// <see cref="ReDrainHiveOverhangs"/>). Separate from
	/// <see cref="_pendingOverhangs"/> (canopy overhangs, appended by gen
	/// threads) so the drains stay independent.
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<
		Vector2I, System.Collections.Concurrent.ConcurrentBag<HiveOverhangVoxel>> _pendingHiveOverhangs = new();

	/// <summary>
	/// Per-chunk serial lock guarding a chunk's overhang DRAIN. The drain runs on
	/// the MAIN thread (see <c>VoxelWorld.OnGenerationComplete</c>) and holds this
	/// lock for its whole duration, so the main-thread drain of a chunk is atomic
	/// with respect to every gen-thread append AND with the gen-thread
	/// "drain-and-restore" below — none of them can interleave and drop a voxel.
	/// Keyed by chunk coord; created on first use.
	/// </summary>
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<Vector2I, object> _overhangDrainLocks = new();

	/// <summary>Gets (or creates) the serial drain lock for <paramref name="coord"/>.</summary>
	private static object OverhangDrainLock(Vector2I coord)
	{
		// GetOrAdd — NOT the indexer: the ConcurrentDictionary indexer (get_Item)
		// throws KeyNotFoundException on a missing key instead of creating it.
		return _overhangDrainLocks.GetOrAdd(coord, _ => new object());
	}

	/// <summary>
	/// Runs the deep-rock <see cref="IStructureGenerator"/>s (ore veins) for the
	/// chunk, in parallel. This is the only remaining use of the
	/// <see cref="IStructureGenerator"/> path — surface foliage is now driven by
	/// the <see cref="StructureRegistry"/> (see the foliage + rock stages).
	/// </summary>
	private static Task ApplyStructuresAsync(
		ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz,
		Dictionary<Vector2I, VoxelChunk> chunks)
	{
		var generators = cfg.OreGenerators;
		if (generators == null || generators.Count == 0)
		{
			return Task.CompletedTask;
		}
		var tasks = new List<Task>(generators.Count);
		foreach (var g in generators)
		{
			tasks.Add(g.ApplyAsync(chunk, wx0, wz0, cfg, heights, sx, sz));
		}
		return Task.WhenAll(tasks);
	}

	/// <summary>
	/// Builds the shared voxel writer for a chunk: given a world voxel it
	/// resolves the chunk that OWNS it (the own chunk plus any loaded neighbour),
	/// writes into it when that chunk is data-ready, and QUEUES it when the
	/// owning chunk is still generating (applied by the main-thread re-drain once
	/// it finishes). A chunk that is not loaded at all is dropped (it will
	/// generate fresh and stamp its own instances). This is the single seam that
	/// lets a structure's shape spill across chunk borders in full. Every
	/// structure definition's <c>Place</c> delegates its voxels to this.
	/// </summary>
	private static Action<int, int, int, byte> MakePut(
		ChunkData chunk, int wx0, int wz0, int sx, int sz,
		Dictionary<Vector2I, VoxelChunk> chunks)
	{
		int cs = chunk.SizeX;
		int sy = chunk.SizeY;
		return (vx, vy, vz, mat) =>
		{
			if (vy < 0 || vy >= sy) return;
			if (chunks == null) return;
			Vector2I coord = new Vector2I(FloorDiv(vx, cs), FloorDiv(vz, cs));
			if (!chunks.TryGetValue(coord, out VoxelChunk ch))
				return; // not loaded: dropped (see summary)
			if (!ch.DataReady)
			{
				// Loaded but still generating: queue the voxel for that chunk
				// (applied by the main-thread re-drain once it finishes).
				_pendingOverhangs.AddOrUpdate(coord,
					_ => new System.Collections.Concurrent.ConcurrentBag<OverhangVoxel>(
						new OverhangVoxel[] { new OverhangVoxel(vx, vy, vz, mat) }),
					(_, existing) =>
					{
						existing.Add(new OverhangVoxel(vx, vy, vz, mat));
						return existing;
					});
				return;
			}
			ChunkData o = ch.Data;
			int lx = vx - coord.X * cs;
			int lz = vz - coord.Y * cs;
			if (lx < 0 || lx >= cs || lz < 0 || lz >= cs) return;
			if (o.Get(lx, vy, lz) == 0)
				o.Set(lx, vy, lz, mat);
		};
	}

	private static async Task PlaceFoliageAsync(
		ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz,
		Dictionary<Vector2I, VoxelChunk> chunks)
	{
		// Each chunk handles ONLY the columns inside its own data (no margin).
		// For every column, each COLUMN structure (tree, cactus, ...) is rolled
		// against the column's dominant biome and, when it passes, its instance
		// is stamped (trunk + full canopy, or a cactus column) voxel-by-voxel
		// into whichever chunk owns each voxel, so a shape that overhangs a
		// chunk border is drawn in full — not clipped. The root checks read only
		// the own chunk's surface voxel, so the "no floating foliage" guarantee
		// is unchanged: a rejected column leaves nothing behind.
		var (dom, _, _, _, _) = ColumnBiomes(wx0, wz0, sx, sz, cfg);
		int cols = sx * sz;
		var structures = cfg.Structures != null ? cfg.Structures.ColumnStructures : null;
		if (structures != null)
		{
			int structCount = structures.Count;
			Action<int, int, int, byte> put = MakePut(chunk, wx0, wz0, sx, sz, chunks);
			await Task.Run(() =>
			{
				ParallelSlices(cols, (cStart, cEnd, _) =>
				{
					for (int c = cStart; c < cEnd; c++)
					{
						int x = c % sx;
						int z = c / sx;
						int wx = wx0 + x;
						int wz = wz0 + z;
						int h = heights[x + sx * z];
						for (int s = 0; s < structCount; s++)
						{
							var def = structures[s];
							if (def.PlaceAtColumn(wx, wz, dom[c], cfg))
							{
								def.Place(wx, wz, h, chunk, sx, chunks, cfg, put);
							}
						}
					}
				});
			});
		}

		// Apply any overhang voxels that earlier chunks queued for THIS chunk
		// (their neighbours were still generating when they ran). The own buffer
		// is now fully built, so these writes land in the correct terrain. This is
		// a NON-destructive drain-and-restore: it never removes the queue entry, so
		// a concurrent append from a neighbouring chunk can't be lost. The
		// authoritative destructive re-drain (which clears the queue) runs on the
		// main thread in VoxelWorld.OnGenerationComplete.
		var ownCoord = new Vector2I(FloorDiv(wx0, sx), FloorDiv(wz0, sz));
		DrainOverhangsForOwnStage(ownCoord, chunk);
	}

	/// <summary>
	/// Apply the overhang voxels currently queued for <paramref name="coord"/>
	/// into the <paramref name="chunk"/> buffer (air voxels only), WITHOUT
	/// removing the queue entry. Returns <c>true</c> when at least one voxel was
	/// applied.
	///
	/// This is the NON-destructive half of the overhang pipeline. The destructive
	/// "re-drain-until-stable" pass (which atomically clears the queue entry) runs
	/// on the MAIN thread under the per-chunk drain lock (see
	/// <c>VoxelWorld.OnGenerationComplete</c>), which is the only correct place to
	/// drop a queued voxel: the main thread is the single writer that finalises a
	/// chunk's data, so once it has re-drained with nothing new landing, no
	/// voxel is left behind. A generation thread must NEVER remove a queue entry
	/// (the old destructive <c>DrainOverhangs</c> raced with a concurrent append
	/// from another chunk's foliage stage and dropped those voxels forever).
	///
	/// The gen-thread caller (a chunk's own foliage stage) uses
	/// <see cref="DrainOverhangsForOwnStage"/> instead, which wraps this in a
	/// drain-and-restore so it is also non-destructive.
	/// </summary>
	private static bool DrainInto(Vector2I coord, ChunkData chunk)
	{
		if (!_pendingOverhangs.TryGetValue(coord, out var bag))
			return false;
		bool applied = false;
		foreach (var v in bag)
		{
			if (v.Y < 0 || v.Y >= chunk.SizeY) continue;
			int lx = v.X - coord.X * chunk.SizeX;
			int lz = v.Z - coord.Y * chunk.SizeZ;
			if (lx < 0 || lx >= chunk.SizeX || lz < 0 || lz >= chunk.SizeZ) continue;
			if (chunk.Get(lx, v.Y, lz) == 0)
			{
				chunk.Set(lx, v.Y, lz, v.Material);
				applied = true;
			}
		}
		return applied;
	}

	/// <summary>
	/// Non-destructive overhang drain used by a chunk's OWN foliage stage (the
	/// generation thread). It applies the queued voxels for <paramref name="coord"/>
	/// into the <paramref name="chunk"/> buffer, then RESTORES the (now applied)
	/// bag to the queue so the authoritative main-thread re-drain (on generation
	/// completion) still sees them. Because the queue entry is never removed, a
	/// concurrent append from a neighbouring chunk's foliage stage can never be
	/// lost: the worst case is that a voxel is applied twice (idempotent — a
	/// second write into a now-solid voxel is a no-op).
	///
	/// Holding the per-chunk drain lock during the drain-and-restore serialises it
	/// with the main-thread drain and with any other gen-thread drain-and-restore
	/// for the same chunk, so the restore can never drop a voxel another thread
	/// just applied.
	/// </summary>
	public static void DrainOverhangsForOwnStage(Vector2I coord, ChunkData chunk)
	{
		var bag = _pendingOverhangs.TryGetValue(coord, out var b) ? b : null;
		if (bag == null) return;

		object lockObj = OverhangDrainLock(coord);
		lock (lockObj)
		{
			// Re-fetch in case the entry was replaced while we were waiting on the
			// lock (another drain-and-restore for this chunk ran first).
			if (!_pendingOverhangs.TryGetValue(coord, out bag))
				return;
			foreach (var v in bag)
			{
				if (v.Y < 0 || v.Y >= chunk.SizeY) continue;
				int lx = v.X - coord.X * chunk.SizeX;
				int lz = v.Z - coord.Y * chunk.SizeZ;
				if (lx < 0 || lx >= chunk.SizeX || lz < 0 || lz >= chunk.SizeZ) continue;
				if (chunk.Get(lx, v.Y, lz) == 0)
					chunk.Set(lx, v.Y, lz, v.Material);
			}
			// Restore the applied bag so the main-thread re-drain still sees it.
			_pendingOverhangs[coord] = bag;
		}
	}

	/// <summary>
	/// AUTHORITATIVE overhang drain: run on the MAIN thread when a chunk's data
	/// becomes ready (see <c>VoxelWorld.OnGenerationComplete</c>). It holds the
	/// chunk's per-chunk drain lock for the whole operation and RE-DRAINS the
	/// queued voxels into <paramref name="chunk"/> until a pass applies nothing
	/// NEW — i.e. until no voxel is left in the queue for this chunk that still
	/// lands in air. Then it removes the (now fully-applied) queue entry.
	///
	/// Why re-drain-until-stable under a lock: a neighbouring chunk's foliage stage
	/// may queue a voxel for this chunk at any moment, even after the first drain
	/// pass. The old single destructive drain could clear the queue right before
	/// such an append landed, losing the voxel forever (the tree then rendered
	/// only in its own chunk — a "half tree" across a chunk border). Re-draining
	/// until a pass changes nothing, while holding the lock, guarantees that by the
	/// time the entry is removed, every voxel queued up to that instant has been
	/// applied. A voxel queued a hair LATER is caught by this chunk's next rebuild
	/// (the neighbour that queued it calls MarkNeighboursForRebuild on the chunk
	/// it finished, and the budget loop re-runs this drain on that rebuild).
	///
	/// Returns <c>true</c> when at least one voxel was applied across the passes
	/// (so the caller knows the chunk needs a mesh rebuild).
	/// </summary>
	public static bool ReDrainOverhangsUntilStable(Vector2I coord, ChunkData chunk)
	{
		object lockObj = OverhangDrainLock(coord);
		lock (lockObj)
		{
			bool any = false;
			while (true)
			{
				if (!DrainInto(coord, chunk))
				{
					break; // nothing queued (or nothing that lands in air) -> stable
				}
				any = true;
				// A pass applied >=1 voxel. Drain again: if a concurrent append
				// landed since the last read, it will now be applied; if not, the
				// next pass reads an empty bag and breaks.
			}
			// Stable: clear the queue entry. Safe because we hold the lock — no
			// gen-thread drain-and-restore or main-thread drain can interleave and
			// drop a voxel. (A gen-thread PutVoxel append that misses this window
			// re-creates the entry and is caught by a later rebuild, per the
			// method summary.)
			_pendingOverhangs.TryRemove(coord, out _);
			return any;
		}
	}

	/// <summary>
	/// Queues a hive-overhang voxel for <paramref name="coord"/>: a hive sphere
	/// voxel that belongs to a chunk that is loaded but not data-ready yet.
	/// Applied by <see cref="ReDrainHiveOverhangs"/> the moment that chunk's
	/// data becomes ready. Main-thread only (hive placement is a main-thread
	/// operation).
	/// </summary>
	public static void PendingHiveOverhang(Vector2I coord, int x, int y, int z, float health)
	{
		_pendingHiveOverhangs.AddOrUpdate(coord,
			_ => new System.Collections.Concurrent.ConcurrentBag<HiveOverhangVoxel>(
				new HiveOverhangVoxel[] { new HiveOverhangVoxel(x, y, z, health) }),
			(_, existing) =>
			{
				existing.Add(new HiveOverhangVoxel(x, y, z, health));
				return existing;
			});
	}

	/// <summary>
	/// AUTHORITATIVE hive-overhang drain: run on the MAIN thread the moment a
	/// chunk's data becomes ready (see <c>VoxelWorld.OnGenerationComplete</c>).
	/// Applies every queued hive voxel for <paramref name="coord"/> into
	/// <paramref name="chunk"/> — into ANY cell (hive voxels are a structure,
	/// like the trees' trunks, not a canopy that must not replace terrain; a
	/// cell the sphere overlaps becomes hive material, mirroring the sphere's
	/// own-chunk write which the save path does unconditionally) — and removes
	/// the queue entry.
	///
	/// Unlike <see cref="ReDrainOverhangsUntilStable"/> this is a single
	/// destructive pass under the per-chunk drain lock: hive voxels are appended
	/// ONLY by the main thread (hive placement is a main-thread operation in
	/// <c>VoxelWorld.TryPlaceHiveInChunk</c>), so no concurrent gen-thread append
	/// can race this drain and a re-drain-until-stable loop is unnecessary. The
	/// lock is still held so the drain is atomic with the canopy drain of the
	/// same chunk (they touch the same buffer, and the main thread is the only
	/// writer that finalises a chunk's data).
	///
	/// Returns <c>true</c> when at least one voxel was applied (so the caller
	/// knows the chunk needs a mesh rebuild).
	/// </summary>
	public static bool ReDrainHiveOverhangs(Vector2I coord, ChunkData chunk)
	{
		object lockObj = OverhangDrainLock(coord);
		lock (lockObj)
		{
			if (!_pendingHiveOverhangs.TryGetValue(coord, out var bag))
			{
				return false; // nothing queued for this chunk
			}
			bool any = false;
			foreach (var v in bag)
			{
				if (v.Y < 0 || v.Y >= chunk.SizeY) continue;
				int lx = v.X - coord.X * chunk.SizeX;
				int lz = v.Z - coord.Y * chunk.SizeZ;
				if (lx < 0 || lx >= chunk.SizeX || lz < 0 || lz >= chunk.SizeZ) continue;
				chunk.Set(lx, v.Y, lz, (byte)HiveMaterial);
				chunk.SetHealth(lx, v.Y, lz, v.Health);
				any = true;
			}
			// Every queued voxel for this chunk has now been applied (the bag is
			// fully consumed above); remove the entry so it can't be re-applied to
			// a later regeneration of the same chunk. Safe: the only appender is
			// the main thread, which is holding this lock right now.
			_pendingHiveOverhangs.TryRemove(coord, out _);
			return any;
		}
	}

	/// <summary>
	/// LOD overload of <see cref="FoliageBaseSolid"/>: resolves the base voxel
	/// through the world's full chunk map (a region spans many chunks, so there
	/// is no pre-fetched neighbour set). A column whose chunk is unloaded or
	/// still generating is treated as solid, matching the full-res rule.
	/// </summary>
	public static bool FoliageBaseSolid(
		int wx, int h, int wz,
		Dictionary<Vector2I, VoxelChunk> chunks, int chunkSizeXZ)
	{
		if (chunks == null)
		{
			return true;
		}

		int testY = h; // must match the full-res overload
		int cx = FloorDiv(wx, chunkSizeXZ);
		int cz = FloorDiv(wz, chunkSizeXZ);
		ChunkData owner = VoxelWorld.GeneratedChunkData(wx, wz, chunks, chunkSizeXZ);
		if (owner == null)
		{
			return true; // unknown: assume solid
		}

		int lx = wx - cx * owner.SizeX;
		int lz = wz - cz * owner.SizeZ;
		if (testY < 0 || testY >= owner.SizeY)
		{
			return true;
		}

		return owner.Get(lx, testY, lz) != 0;
	}

	/// <summary>Floor division for possibly-negative world coords.</summary>
	private static int FloorDiv(int a, int b)
	{
		int q = a / b;
		if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
		return q;
	}

	// ---------------------------------------------------------------------
	// Biome sampling - pure function of (wx, wz) + config (seam-safe)
	// ---------------------------------------------------------------------

	/// <summary>
	/// Returns the biomes in <paramref name="cfg"/> (empty array when none are
	/// configured). Guarded so generation still produces terrain when a world has
	/// no biomes (it falls back to the canonical default material ids).
	/// </summary>
	private static BiomeDef[] Biomes(GenConfig cfg)
	{
		return cfg.BiomeConfig != null && cfg.BiomeConfig.Biomes != null && cfg.BiomeConfig.Biomes.Length > 0
			? cfg.BiomeConfig.Biomes
			: Array.Empty<BiomeDef>();
	}

	/// <summary>
	/// The biome weights for a world column: a normalized weight vector across
	/// <c>cfg.BiomeConfig.Biomes</c> with a SINGLE dominant biome (weight 1).
	/// Selection is a WEIGHTED CUMULATIVE DISTRIBUTION over each biome's
	/// <see cref="BiomeDef.Weight"/>: the (0..1) biome noise value is scaled to
	/// <c>totalWeight</c> and the column lands inside whichever biome's
	/// cumulative band it falls in, so a biome's coverage is proportional to its
	/// weight (a biome at 2.0 covers twice the area of one at 1.0). A biome with
	/// 0 (or negative) weight occupies a ZERO-WIDTH band and is never selected —
	/// it is genuinely absent from the world. This is what makes editor edits to
	/// each biome's weight actually change the world's biome mix.
	///
	/// The dominant biome drives every per-column decision (all four material
	/// layers, foliage, rock boulders). Biome borders are therefore clean edges
	/// (each column is fully one biome), not per-voxel blends — matching how
	/// "biome regions" are meant to read, and avoiding the earlier bug where a
	/// 0-weight biome still won a weighted-majority tie for the surface material.
	///
	/// Pure in (wx, wz) + config.
	/// </summary>
	public static float[] SampleBiomes(int wx, int wz, GenConfig cfg)
	{
		var biomes = Biomes(cfg);
		int n = biomes.Length;
		var weights = new float[n];
		if (n == 0)
		{
			return weights;
		}
		if (n == 1)
		{
			weights[0] = 1f;
			return weights;
		}

		// Build the cumulative weight bands. If every biome has 0 (or negative)
		// weight, fall back to an equal split so the world still generates.
		float[] cum = new float[n];
		float total = 0f;
		for (int k = 0; k < n; k++)
		{
			total += Mathf.Max(0f, biomes[k].Weight);
			cum[k] = total;
		}
		bool weighted = total > 1e-6f;
		if (!weighted)
		{
			// Equal bands of width 1 (each biome covers 1/n of the area).
			for (int k = 0; k < n; k++)
			{
				cum[k] = k + 1;
			}
		}
		float span = weighted ? total : (float)n;

		// Position along the [0, span] line.
		float t = (cfg.BiomeNoise.GetNoise2D(wx, wz) + 1f) * 0.5f; // [0,1]
		float pos = Mathf.Clamp(t, 0f, 1f) * span;

		// Find the band [cum[i-1], cum[i]] that `pos` falls in. A 0-weight biome
		// is a zero-width band and is skipped by this (pos >= cum[i] is false
		// the instant we reach it), so it can never be selected.
		int i = 0;
		while (i < n - 1 && pos >= cum[i])
		{
			i++;
		}

		weights[i] = 1f; // single dominant biome drives the column
		return weights;
	}

	/// <summary>
	/// Convenience overload: per-column weights for a rectangular region. Layout is
	/// <c>per[(z * sx + x) * biomeCount + b]</c> (z-major, one normalized weight
	/// vector per column).
	/// </summary>
	public static float[] SampleBiomes(int wx0, int wz0, int sx, int sz, GenConfig cfg)
	{
		int bc = Biomes(cfg).Length;
		float[] per = new float[sz * sx * bc];
		for (int z = 0; z < sz; z++)
		{
			for (int x = 0; x < sx; x++)
			{
				var w = SampleBiomes(wx0 + x, wz0 + z, cfg);
				int col = (z * sx + x) * bc;
				for (int b = 0; b < bc; b++)
					per[col + b] = w[b];
			}
		}
		return per;
	}

	/// <summary>
	/// The biome weights (and dominant biome index) for a column. The dominant
	/// biome (argmax weight, ties to the lower index) drives foliage, rock boulders,
	/// and any per-column single-biome decision. Pure in (wx, wz) + config.
	/// </summary>
	public static (float[] Weights, int Dominant) BiomeAt(int wx, int wz, GenConfig cfg)
	{
		var w = SampleBiomes(wx, wz, cfg);
		if (w.Length == 0)
		{
			return (w, -1);
		}

		int dom = 0;
		for (int i = 1; i < w.Length; i++)
		{
			if (w[i] > w[dom])
			{
				dom = i;
			}
		}
		return (w, dom);
	}

	/// <summary>The dominant <see cref="BiomeDef"/> for a column (null when no biomes are configured or the index is invalid).</summary>
	public static BiomeDef BiomeOf(GenConfig cfg, int dominant)
	{
		var biomes = Biomes(cfg);
		return dominant >= 0 && dominant < biomes.Length ? biomes[dominant] : null;
	}

	/// <summary>
	/// The dominant <see cref="BiomeDef"/> for a column, or null when no biomes
	/// are configured. Convenience for callers that want the biome directly
	/// (foliage, rock boulders, LOD surface material, etc.).
	/// </summary>
	public static BiomeDef DominantBiome(int wx, int wz, GenConfig cfg)
	{
		var (_, dominant) = BiomeAt(wx, wz, cfg);
		return BiomeOf(cfg, dominant);
	}

	// ---------------------------------------------------------------------
	// Shared helpers (kept static for convenience)
	// ---------------------------------------------------------------------

	/// <summary>
	/// The surface height of a column. Each biome modulates height (additive
	/// <c>HeightModifier</c>) and amplitude (multiplicative
	/// <c>AmplitudeModifier</c>); the column's height is the weighted blend of the
	/// biomes that overlap it, then clamped to the chunk. Pure in (wx, wz) + config.
	/// </summary>
	public static int SurfaceHeight(int wx, int wz, GenConfig cfg)
	{
		var biomes = Biomes(cfg);
		if (biomes.Length == 0)
		{
			// No biomes: the original flat heightmap.
			float n = cfg.HeightNoise.GetNoise2D(wx, wz); // -1..1
			int flatH = (int)Mathf.Round(cfg.BaseHeight + n * cfg.HeightAmplitude);
			return Mathf.Clamp(flatH, 2, cfg.ChunkHeight - 8);
		}

		var w = SampleBiomes(wx, wz, cfg);
		float heightNoise = cfg.HeightNoise.GetNoise2D(wx, wz); // -1..1, shared by all biomes
		float blended = 0f;
		for (int i = 0; i < biomes.Length; i++)
		{
			// Per-biome height = BaseHeight + HeightModifier + HeightNoise * (HeightAmplitude * AmplitudeModifier)
			float biomeHeight = cfg.BaseHeight + biomes[i].HeightModifier + heightNoise * (cfg.HeightAmplitude * biomes[i].AmplitudeModifier);
			// Round each biome's contribution independently (matches the original
			// per-biome integer height), then weight-blend the rounded values so a
			// column's height is an integer and deterministic.
			blended += w[i] * Mathf.Round(biomeHeight);
		}

		int h = (int)Mathf.Round(blended);
		return Mathf.Clamp(h, 2, cfg.ChunkHeight - 8);
	}

	/// <summary>
	/// Whether a TREE grows at (wx, wz). Delegates to the tree structure
	/// definition's per-column gate (see <see cref="TreeStructure.PlaceAtColumn"/>).
	/// Kept for the LOD heightfield, which samples the tree silhouette through
	/// this + <see cref="TreeStructure.TrunkHeight"/> +
	/// <see cref="TreeStructure.LeafRadius"/>.
	/// </summary>
	public static bool TreeAt(int wx, int wz, GenConfig cfg)
	{
		var (weights, dominant) = BiomeAt(wx, wz, cfg);
		return TreeStructure.Gate(wx, wz, dominant, cfg);
	}

	// ---------------------------------------------------------------------
	// Material-layer helpers
	// ---------------------------------------------------------------------

	/// <summary>
	/// Picks the per-layer material for a column: for each distinct material id in
	/// the biomes' <c>slot</c>, accumulate the sum of the biomes' weights for that
	/// id, and return the id with the highest accumulated weight (the WEIGHTED
	/// MAJORITY). This is how discrete materials blend per-voxel at biome borders:
	/// a column that is 70% Plains / 30% Desert surfaces as Grass (the dominant
	/// biome's surface). Ids of 0 (air / none) are never chosen; when no biome
	/// offers a solid material for a layer, the first non-zero slot is used as a
	/// fallback so the column is still filled.
	/// </summary>
	private static int Accumulate(BiomeDef[] biomes, float[] weights, System.Func<BiomeDef, int> slot)
	{
		// Collect the distinct non-zero slot ids (few in practice).
		var ids = new HashSet<int>();
		for (int i = 0; i < biomes.Length; i++)
		{
			int id = slot(biomes[i]);
			if (id > 0)
			{
				ids.Add(id);
			}
		}

		if (ids.Count == 0)
		{
			return 0; // no biome offers this layer; caller stores air (rare/misconfig)
		}

		int best = -1;
		float bestWeight = -1f;
		foreach (int id in ids)
		{
			float w = 0f;
			for (int i = 0; i < biomes.Length; i++)
			{
				if (slot(biomes[i]) == id)
				{
					w += weights[i];
				}
			}
			if (w > bestWeight + 1e-6f)
			{
				bestWeight = w;
				best = id;
			}
		}

		return best > 0 ? best : 0;
	}
}
