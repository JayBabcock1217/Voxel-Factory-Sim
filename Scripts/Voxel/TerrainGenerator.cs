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
/// 4. procedural structures (see <see cref="IStructureGenerator"/>)
/// 5. per-biome foliage (trees, or cacti for cactus biomes), placed with a hashed
///    margin scan so plants are identical no matter which chunk generates them
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

		// Stage 3: stamp structures (ores, rocks) — after caves.
		var structureTasks = new List<Task>();
		foreach (var structure in cfg.Structures)
		{
			structureTasks.Add(structure.ApplyAsync(chunk, wx0, wz0, cfg, heights, sx, sz));
		}
		if (structureTasks.Count > 0)
		{
			Task.WaitAll(structureTasks.ToArray());
		}

		// Stage 4: foliage — after structures.
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

		// Stage 3: stamp structures (ores, rocks). Must run after caves so
		// veins are placed into solid rock, not into air carved by caves.
		var structureTasks = new List<Task>();
		foreach (var structure in cfg.Structures)
		{
			structureTasks.Add(structure.ApplyAsync(chunk, wx0, wz0, cfg, heights, sx, sz));
		}
		if (structureTasks.Count > 0)
		{
			await Task.WhenAll(structureTasks);
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
	/// immediately; a neighbour that is still generating (or not loaded yet) can't
	/// be written into, so the voxel is queued here instead. The queue is PERSIST
	/// (an instance of <see cref="OverhangStore"/>, owned by the world, shared by
	/// every chunk's foliage stage): a queued voxel stays in the store until the
	/// target chunk is generated and drains it — surviving the target chunk being
	/// freed and re-generated (the old static, drained-and-discarded on each
	/// (re-)generation, lost overhangs queued for a chunk that fell outside the
	/// 8-chunk in-flight window, clipping border trees). When a chunk finishes
	/// generating, <see cref="DrainOverhangs"/> applies the queued voxels to its
	/// now-ready buffer. This is the "queue a modification for when that chunk
	/// gets generated" half of the border-overhang fix.
	/// </summary>
	private static OverhangStore _overhangs;

	/// <summary>
	/// Binds the world (and its per-world overhang store) for the current
	/// generation episode. Called once per world load / regenerate so every
	/// chunk's foliage stage and the world's drain share ONE store that survives
	/// individual chunk free/re-generate. A fresh store per episode means a new
	/// world (new seed → different trees) never replays a previous world's
	/// overhangs.
	/// </summary>
	public static void BindWorld(VoxelWorld world)
	{
		_overhangs = new OverhangStore(world);
	}

	/// <summary>
	/// The live overhang store, or a throwaway singleton when no world is bound
	/// (defensive: generation is always bound by the world before it runs).
	/// </summary>
	private static OverhangStore Store()
		=> _overhangs ??= new OverhangStore(null);

	/// <summary>
	/// A per-world, persistent collection of pending canopy-overhang voxels,
	/// keyed by target chunk coord. Persists across chunk free/re-generate (the
	/// fix for clipped border trees): a voxel queued for a chunk that is not yet
	/// in the in-flight generation window stays here until that chunk generates
	/// and drains it. Bounded: a voxel is removed when its target drains, and a
	/// world-episode reset clears everything.
	/// </summary>
	private sealed class OverhangStore
	{
		private readonly VoxelWorld _world;
		private readonly System.Collections.Concurrent.ConcurrentDictionary<
			Vector2I, System.Collections.Concurrent.ConcurrentBag<OverhangVoxel>> _map = new();

		public OverhangStore(VoxelWorld world) => _world = world;

		/// <summary>Queue a voxel for a target chunk (creating its bag if needed).</summary>
		public void Enqueue(Vector2I coord, int vx, int vy, int vz, byte mat)
		{
			_map.AddOrUpdate(coord,
				_ => new System.Collections.Concurrent.ConcurrentBag<OverhangVoxel>(
					new OverhangVoxel[] { new OverhangVoxel(vx, vy, vz, mat) }),
				(_, existing) =>
				{
					existing.Add(new OverhangVoxel(vx, vy, vz, mat));
					return existing;
				});
		}

		/// <summary>
		/// Remove + return the queued voxels for <paramref name="coord"/> (null
		/// when none). The caller applies them; removing here (not on apply) means
		/// a chunk that is freed before draining keeps nothing, and a re-generate
		/// of the same coord re-drains any voxels queued in the interim.
		/// </summary>
		public System.Collections.Concurrent.ConcurrentBag<OverhangVoxel> Take(Vector2I coord)
			=> _map.TryRemove(coord, out var bag) ? bag : null;

		/// <summary>Drop a target's entry entirely (used if a chunk is freed and we
		/// want to avoid unbounded growth for coords the player never returns to).
		/// Kept as a no-op by default: the store is small (a handful of border
		/// voxels per queued tree) and a world-episode reset clears it, so we
		/// prefer keeping overhangs over risking a leak-of-the-fix. Exposed for a
		/// future prune if the store ever grows large.</summary>
		public void Forget(Vector2I coord) => _map.TryRemove(coord, out _);
	}

	/// <summary>
	/// Places foliage (trees, cacti) using the bounded worker pool. The 256
	/// columns are partitioned across <see cref="WorkerCount"/> workers (instead
	/// of the old one-Task.Run-per-column, which alone spawned 256 thread-pool
	/// tasks per chunk). The dominant biome per column comes from the shared
	/// <see cref="ColumnBiomes"/> cache, so the 2D biome noise is not re-sampled
	/// here. Behaviour is identical.
	/// </summary>
	private static async Task PlaceFoliageAsync(
		ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz,
		Dictionary<Vector2I, VoxelChunk> chunks)
	{
		// Each chunk handles ONLY the columns inside its own data (no margin).
		// A whole instance is written after its root checks pass; the trunk and
		// the FULL canopy are emitted voxel-by-voxel into whichever chunk owns
		// each voxel (the own chunk plus any loaded neighbour), so a canopy that
		// overhangs a chunk border is drawn in full — not clipped. The root
		// checks read only the own chunk's surface voxel, so the "no floating
		// foliage" guarantee is unchanged: a rejected column leaves nothing
		// behind in the own chunk or its neighbours.
		//
		// Overhang voxels written into a neighbour's buffer are picked up when the
		// world re-meshes the 8 neighbours on generation completion (an overhang
		// can only land in one of those 8), so no extra bookkeeping is needed.
		var (dom, _, _, _, _) = ColumnBiomes(wx0, wz0, sx, sz, cfg);
		int cols = sx * sz;

		await Task.Run(() =>
		{
			ParallelSlices(cols, (cStart, cEnd, _) =>
			{
				for (int c = cStart; c < cEnd; c++)
				{
					int x = c % sx;
					int z = c / sx;
					PlaceFoliageInstance(x, z, dom[c], chunk, wx0, wz0, cfg, heights, sx, sz, chunks);
				}
			});
		});

		// Apply any overhang voxels that earlier chunks queued for THIS chunk
		// (their neighbours were still generating when they ran). The own buffer
		// is now fully built, so these writes land in the correct terrain. (The
		// world drains again on completion to catch a cross-chunk race.)
		var ownCoord = new Vector2I(FloorDiv(wx0, sx), FloorDiv(wz0, sz));
		DrainOverhangs(ownCoord, chunk);
	}

	/// <summary>
	/// Apply every overhang voxel queued for <paramref name="coord"/> into the
	/// <paramref name="chunk"/> buffer (air voxels only), then clear the queue
	/// entry. Returns <c>true</c> when at least one voxel was applied (so the
	/// caller knows the chunk needs a mesh rebuild). Called from the chunk's own
	/// foliage stage (generation thread) and again from the world on generation
	/// completion (main thread) to catch a cross-chunk race where a neighbour
	/// queued its overhang just after this chunk's stage had already drained.
	/// </summary>
	public static bool DrainOverhangs(Vector2I coord, ChunkData chunk)
	{
		var bag = Store().Take(coord);
		if (bag == null)
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
	/// Place one whole foliage instance (cactus column, or trunk + canopy) rooted
	/// at the surface of in-chunk column (x, z). The trunk and the FULL canopy
	/// are written voxel-by-voxel into the chunk that owns each voxel (the own
	/// chunk plus any loaded neighbour), so canopy overhang at a chunk border is
	/// drawn in full. This happens ONLY after every root check passes:
	///   1. the column is on a biome that grows foliage, and the per-column
	///      probability roll passes (<see cref="TreeAt"/>);
	///   2. the surface voxel at height h is SOLID — if a cave carved it to air,
	///      the instance would float, so nothing is written;
	///   3. the surface voxel is the biome's SURFACE material (grass, etc.), not
	///      a rock boulder or cave material.
	/// When any check fails nothing is written. A neighbour that is not loaded or
	/// still generating is simply skipped for its voxels (they appear once that
	/// neighbour has generated), never written into prematurely.
	///
	/// <paramref name="dominant"/> is the column's dominant biome index, taken
	/// from the shared <see cref="ColumnBiomes"/> cache so the 2D biome noise is
	/// not re-sampled here (it was sampled once for the whole chunk).
	/// </summary>
	private static void PlaceFoliageInstance(
		int x, int z, int dominant, ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz,
		Dictionary<Vector2I, VoxelChunk> chunks)
	{
		int wx = wx0 + x;
		int wz = wz0 + z;
		int h = heights[x + sx * z];

		var biome = BiomeOf(cfg, dominant);
		if (biome == null || !biome.EnableFoliage) return;

		// Per-column probability / forest-noise gate. Uses the dominant biome
		// we were given (no re-sampling): the forest-density noise gate and the
		// hashed per-column roll are pure functions of (wx, wz, cfg) + the
		// dominant biome, so this is identical to the old TreeAt(wx, wz, cfg)
		// because `dominant` IS the column's true dominant biome.
		if (!TreeAtDominant(wx, wz, dominant, cfg)) return;

		// Check 1: the surface voxel must be solid (not cave-carved).
		if (h < 0 || h >= chunk.SizeY) return;
		byte surface = chunk.Get(x, h, z);
		if (surface == 0) return;

		// Check 2: the surface must be the biome's surface material (grass
		// etc.), so a tree never sprouts from a rock boulder or cave stone.
		if (surface != (byte)biome.SurfaceMaterial) return;

		int cs = chunk.SizeX;
		int sy = chunk.SizeY;
		int th = TrunkHeight(wx, wz, cfg);

		// Write only into air voxels so we never overwrite terrain or an
		// existing voxel. Resolve the owning chunk per voxel so the instance
		// can spill across chunk borders. A neighbour that is loaded but still
		// generating can't be written into yet, so its voxel is QUEUED and
		// applied when that neighbour finishes (DrainOverhangs). A chunk that
		// is not loaded at all is dropped (it will generate fresh and its own
		// foliage stage handles its rooted instances).
		void PutVoxel(int vx, int vy, int vz, byte mat)
		{
			if (vy < 0 || vy >= sy) return;
			Vector2I coord = new Vector2I(FloorDiv(vx, cs), FloorDiv(vz, cs));
			// Resolve the owning chunk. If it's not loaded (or still generating),
			// QUEUE the voxel in the PERSISTENT overhang store — it is applied
			// when that chunk generates and drains. The old code DROPPED a voxel
			// whose target chunk was not loaded; combined with the 8-chunk
			// in-flight window, that clipped border trees (the canopy overhang
			// was lost when the target chunk fell outside the window and was
			// later re-generated fresh). Persisting fixes it: the voxel survives
			// until the target chunk actually generates.
			if (chunks == null || !chunks.TryGetValue(coord, out VoxelChunk ch) || !ch.DataReady)
			{
				Store().Enqueue(coord, vx, vy, vz, mat);
				return;
			}
			ChunkData o = ch.Data;
			int lx = vx - coord.X * cs;
			int lz = vz - coord.Y * cs;
			if (lx < 0 || lx >= cs || lz < 0 || lz >= cs) return;
			if (o.Get(lx, vy, lz) == 0)
				o.Set(lx, vy, lz, mat);
		}

		if (biome.IsCactus && biome.CactusMaterial > 0)
		{
			// Cactus: a single vertical column, no canopy.
			int cactusTop = h + th;
			for (int y = h + 1; y <= Math.Min(cactusTop, sy - 1); y++)
				PutVoxel(wx, y, wz, (byte)biome.CactusMaterial);
			return;
		}

		if (biome.TrunkMaterial == 0) return; // no trunk material -> no tree

		int lr = LeafRadius(wx, wz, cfg);
		// Canopy centred one voxel above the trunk top so the trunk pokes
		// through the top of the blob (Minecraft style).
		int top = h + th;
		int cy = top + 1;

		// Trunk.
		for (int y = h + 1; y <= Math.Min(top, sy - 1); y++)
			PutVoxel(wx, y, wz, (byte)biome.TrunkMaterial);

		// Canopy: main blob plus an optional lower second tier. Emitted into
		// whatever chunk each leaf voxel belongs to.
		if (biome.LeafMaterial != 0)
		{
			WriteLeafBlobWorld(wx, wz, cy, lr, (byte)biome.LeafMaterial, PutVoxel);
			if (biome.HasLeafTier2 && lr > 1)
			{
				int lr2 = Mathf.Max(1, Mathf.RoundToInt(lr * biome.LeafTier2RadiusFactor));
				WriteLeafBlobWorld(wx, wz, cy - biome.LeafTier2HeightBelow, lr2, (byte)biome.LeafMaterial, PutVoxel);
			}
		}
	}

	/// <summary>
	/// Emit a spherical leaf blob centred at world column (wx, wz) and height
	/// <paramref name="centerY"/> with the given radius, delegating each voxel to
	/// <paramref name="write"/> (which resolves the owning chunk and clips to
	/// air). Used so the canopy can spill across chunk borders.
	/// </summary>
	private static void WriteLeafBlobWorld(
		int wx, int wz, int centerY, int radius, byte leafMaterial,
		Delegate write)
	{
		int r2 = radius * radius;
		var put = (System.Action<int, int, int, byte>)write;
		for (int dx = -radius; dx <= radius; dx++)
		{
			for (int dy = -radius; dy <= radius; dy++)
			{
				for (int dz = -radius; dz <= radius; dz++)
				{
					if (dx * dx + dy * dy + dz * dz > r2) continue;
					put(wx + dx, centerY + dy, wz + dz, leafMaterial);
				}
			}
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

	/// <summary>
	/// Stamps a spherical leaf blob of the given radius into the chunk, centered
	/// at (col.X, centerY, col.Z). Only fills air voxels (never overwrites trunk
	/// or terrain) and only writes voxels that fall inside this chunk.
	/// </summary>
	private static void WriteLeafBlob(ChunkData chunk, Vector3I col, int centerY, int radius, int leafMaterial)
	{
		int r2 = radius * radius;
		for (int dx = -radius; dx <= radius; dx++)
		{
			for (int dy = -radius; dy <= radius; dy++)
			{
				for (int dz = -radius; dz <= radius; dz++)
				{
					if (dx * dx + dy * dy + dz * dz > r2) continue;

					int nx = col.X + dx;
					int ny = centerY + dy;
					int nz = col.Z + dz;
					if (nx < 0 || nx >= chunk.SizeX || ny < 0 || ny >= chunk.SizeY || nz < 0 || nz >= chunk.SizeZ) continue;
					if (chunk.Get(nx, ny, nz) == 0)
						chunk.Set(nx, ny, nz, (byte)leafMaterial);
				}
			}
		}
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
	/// Whether a tree/cactus grows at (wx, wz). The dominant biome must have
	/// foliage enabled and the shared forest-density noise must clear that biome's
	/// threshold; a hashed per-column roll then decides the plant. The global
	/// <see cref="GenConfig.FoliageDensity"/> multiplier (0..1) thins out the
	/// instances inside a forest: it scales the biome's per-column probability
	/// AFTER the noise gate, so the grove layout stays stable and only the tree
	/// count within it changes.
	/// </summary>
	public static bool TreeAt(int wx, int wz, GenConfig cfg)
	{
		var (weights, dominant) = BiomeAt(wx, wz, cfg);
		var biome = BiomeOf(cfg, dominant);
		if (biome == null || !biome.EnableFoliage) return false;
		if (cfg.ForestNoise.GetNoise2D(wx, wz) < biome.DensityThreshold) return false;
		float chance = biome.Probability * Mathf.Clamp(cfg.FoliageDensity, 0f, 1f);
		if (chance <= 0f) return false;
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0x9E37u);
		return VoxelHash.Hash01(h) < chance;
	}

	/// <summary>
	/// <see cref="TreeAt"/> but with the column's dominant biome SUPPLIED rather
	/// than re-sampled (the caller already computed it via
	/// <see cref="ColumnBiomes"/>). Identical result when
	/// <paramref name="dominant"/> is the column's true dominant biome — which it
	/// is, since that's how <see cref="ColumnBiomes"/> computes it — but skips the
	/// redundant 2D biome-noise sample.
	/// </summary>
	private static bool TreeAtDominant(int wx, int wz, int dominant, GenConfig cfg)
	{
		var biome = BiomeOf(cfg, dominant);
		if (biome == null || !biome.EnableFoliage) return false;
		if (cfg.ForestNoise.GetNoise2D(wx, wz) < biome.DensityThreshold) return false;
		float chance = biome.Probability * Mathf.Clamp(cfg.FoliageDensity, 0f, 1f);
		if (chance <= 0f) return false;
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0x9E37u);
		return VoxelHash.Hash01(h) < chance;
	}

	/// <summary>Trunk (or cactus column) height for a column, from the dominant biome's range.</summary>
	public static int TrunkHeight(int wx, int wz, GenConfig cfg)
	{
		var (_, dominant) = BiomeAt(wx, wz, cfg);
		var biome = BiomeOf(cfg, dominant);
		int min = biome != null ? biome.TrunkMinHeight : 3;
		int max = biome != null ? biome.TrunkMaxHeight : 5;
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0x51ADu);
		float r = VoxelHash.Hash01(h);
		return min + (int)(r * (max - min + 1));
	}

	/// <summary>Leaf-blob radius for a column, from the dominant biome's range.</summary>
	public static int LeafRadius(int wx, int wz, GenConfig cfg)
	{
		var (_, dominant) = BiomeAt(wx, wz, cfg);
		var biome = BiomeOf(cfg, dominant);
		int min = biome != null ? biome.LeafMinRadius : 2;
		int max = biome != null ? biome.LeafMaxRadius : 3;
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0xBEEFu);
		float r = VoxelHash.Hash01(h);
		return min + (int)(r * (max - min + 1));
	}

	// ---------------------------------------------------------------------
	// Material-layer helpers
	// ---------------------------------------------------------------------

	/// <summary>Largest leaf radius across biomes (for the foliage margin scan).</summary>
	private static int MaxLeafRadius(GenConfig cfg)
	{
		var biomes = Biomes(cfg);
		int max = 3;
		for (int i = 0; i < biomes.Length; i++)
		{
			if (biomes[i].LeafMaxRadius > max)
			{
				max = biomes[i].LeafMaxRadius;
			}
		}
		return max;
	}

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
