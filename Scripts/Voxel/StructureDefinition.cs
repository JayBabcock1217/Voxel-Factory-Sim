using Godot;
using System;
using System.Collections.Generic;
using FactorySim;

namespace FactorySim.Voxel;

/// <summary>
/// THE single class everything "grows" in the world is based on: a piece of
/// foliage or a structure (a tree, a bush, a cactus, a rock boulder, a zombie
/// hive, ...). One definition owns BOTH halves of a world object:
///
/// 1. <b>Its Voxel Shape</b> — <see cref="Place"/> deterministically stamps the
///    instance's voxels into a chunk (rooted at the surface, spilling across
///    chunk borders through the shared overhang queue). It is a PURE function
///    of (world column, config), so a full-res chunk and the distant LOD
///    heightfield always agree on where and how big the object is.
///
/// 2. <b>Its Runtime Behavior</b> — <see cref="SpawnRuntimeNode"/> builds the
///    live <see cref="FunctionalFoliage"/> node for a placed instance (the
///    world owns it: it spawns zombies, tracks depletion, ...). A purely
///    static structure (a tree, a bush, a rock) returns <c>null</c>: its
///    voxels ARE the whole object and no live node is needed.
///
/// A world of structures is a <see cref="StructureRegistry"/> (a data-driven
/// list of these definitions). Generation places instances by rolling each
/// column definition against every world column (gen-thread) and each cell
/// definition over the chunk's cell grid (structures stage). The hive is
/// special: it is placed by a per-CHUNK roll on the MAIN thread (its voxels
/// must be written after the chunk's data is ready, to keep the light engine
/// consistent) and is the only definition that is persisted in a save.
///
/// Adding a new world object is now: subclass this, describe its shape in
/// <see cref="Place"/> (and its LOD silhouette in <see cref="LodHeight"/>) and
/// optionally its behavior in <see cref="SpawnRuntimeNode"/> — and register it.
/// No other file needs to know about the specific type.
/// </summary>
[GlobalClass]
public abstract partial class StructureDefinition : Resource
{
	// ------------------------------------------------------------------
	// Placement identity
	// ------------------------------------------------------------------

	/// <summary>
	/// Unique tag for this definition (e.g. "tree", "cactus", "rock", "hive").
	/// Used to identify instances and (for the hive) to persist them.
	/// </summary>
	public abstract string Tag { get; }

	/// <summary>
	/// Whether a given world COLUMN (wx, wz) gets a fresh instance of this
	/// structure, from the column's dominant biome index. A pure, seam-safe
	/// function of (wx, wz, dominant, cfg): the same column always rolls the
	/// same answer, so the layout is identical on full-res and LOD. The default
	/// is false — a definition placed some other way (the hive, by per-chunk
	/// roll; rocks, by per-cell roll) leaves this off.
	/// </summary>
	public virtual bool PlaceAtColumn(int wx, int wz, int dominant, GenConfig cfg) => false;

	/// <summary>
	/// The surface height an instance rooted at (wx, wz) rests ON, in world
	/// voxel Y. For a column-placed structure this is the column's terrain
	/// surface height; the instance's voxels grow from here upward.
	/// </summary>
	public virtual int BaseYAt(int wx, int wz, GenConfig cfg) => TerrainGenerator.SurfaceHeight(wx, wz, cfg);

	/// <summary>
	/// Stamps ONE instance rooted at world column (wx, wz) into <paramref
	/// name="chunk"/>. <paramref name="h"/> is the base surface height (from
	/// <see cref="BaseYAt"/>); the definition decides the voxels. Every voxel is
	/// written through <paramref name="put"/>, which resolves the chunk that
	/// OWNS each world voxel (so the shape may spill across borders) and queues
	/// voxels whose chunk is still generating. The definition must run its own
	/// base checks (solid surface, surface material) before writing anything.
	/// </summary>
	public abstract void Place(
		int wx, int wz, int h, ChunkData chunk, int chunkSizeXZ,
		Dictionary<Vector2I, VoxelChunk> chunks, GenConfig cfg, Action<int, int, int, byte> put);

	// ------------------------------------------------------------------
	// LOD silhouette
	// ------------------------------------------------------------------

	/// <summary>
	/// The LOD silhouette contribution of the (possibly-placed) instance at
	/// (wx, wz): how many voxels to raise the coarse node above the surface
	/// (<paramref name="delta"/>), and which material to flag it with
	/// (<paramref name="material"/>). The LOD heightfield samples this so the
	/// distant view shows the same trees/canopies the full-res view does.
	/// Returns false when the instance has no distant silhouette (the hive and
	/// small rocks are smaller than a coarse cell and never show up).
	/// </summary>
	public virtual bool LodHeight(
		int wx, int wz, int h, GenConfig cfg,
		Dictionary<Vector2I, VoxelChunk> chunks, int chunkSizeXZ,
		out int delta, out int material)
	{
		delta = 0;
		material = 0;
		return false;
	}

	// ------------------------------------------------------------------
	// Runtime behavior (the live node)
	// ------------------------------------------------------------------

	/// <summary>
	/// The voxel material the instance's body is made of (used to identify which
	/// of the surrounding voxels belong to it). 0 = not a single-material body.
	/// </summary>
	public virtual int BodyMaterial { get; protected set; }

	/// <summary>
	/// Builds the live <see cref="FunctionalFoliage"/> node for an instance at
	/// <paramref name="center"/> (its runtime center voxel), or <c>null</c> for
	/// a purely static structure (a tree, a bush, a rock — its voxels ARE the
	/// whole object, so there is no runtime behavior to run). The world takes
	/// ownership of the returned node (adds it to the tree, spawns it, tracks
	/// it, and persists it for saveable definitions).
	/// </summary>
	public virtual FunctionalFoliage SpawnRuntimeNode(Vector3I center, VoxelWorld world) => null;

	/// <summary>
	/// Whether instances of this definition are persisted in a save (restored on
	/// load). Only the hive is, today. A column-placed static structure is
	/// regenerated deterministically and never saved.
	/// </summary>
	public virtual bool IsSaveable => false;

	// ------------------------------------------------------------------
	// Shared helpers
	// ------------------------------------------------------------------

	/// <summary>
	/// Integer division that rounds toward negative infinity (C#'s / truncates
	/// toward zero). Needed to map a WORLD coordinate back to the LOCAL offset
	/// inside its chunk for chunks in negative world space:
	/// <c>x - FloorDiv(x, s) * s</c> is always in [0, s) for any x and any
	/// positive s, whereas a plain <c>x % s</c> keeps the sign of x.
	/// </summary>
	protected static int FloorDiv(int a, int b) => (a - (a % b + b) % b) / b;
}

/// <summary>
/// A TREE: a vertical trunk column topped by one or more spherical leaf
/// canopies (Minecraft-style — the trunk pokes through the top of the main
/// blob, with an optional smaller lower tier). Its shape, per-column gate, and
/// LOD silhouette are all driven by the dominant biome's tree settings (the
/// biome is the source of truth for trunk/leaf materials and the
/// height/radius ranges, so existing worlds keep their exact look).
/// </summary>
[GlobalClass]
public partial class TreeStructure : StructureDefinition
{
	public override string Tag => "tree";

	/// <summary>
	/// The per-column tree gate: the dominant biome must grow foliage, the
	/// shared forest-density noise must clear its threshold, and a hashed
	/// per-column roll must pass (scaled by the global foliage density).
	/// </summary>
	public override bool PlaceAtColumn(int wx, int wz, int dominant, GenConfig cfg) => Gate(wx, wz, dominant, cfg);

	/// <summary>
	/// The tree's per-column gate, exposed statically so the LOD heightfield and
	/// <see cref="TerrainGenerator.TreeAt"/> can sample it without an instance.
	/// A pure function of (wx, wz, dominant, cfg) — seam-safe.
	/// </summary>
	public static bool Gate(int wx, int wz, int dominant, GenConfig cfg)
	{
		var biome = TerrainGenerator.BiomeOf(cfg, dominant);
		if (biome == null || !biome.EnableFoliage)
		{
			return false;
		}
		float chance = biome.Probability * Mathf.Clamp(cfg.FoliageDensity, 0f, 1f);
		if (chance <= 0f)
		{
			return false; // short-circuit BEFORE touching the forest noise
		}
		if (cfg.ForestNoise == null || cfg.ForestNoise.GetNoise2D(wx, wz) < biome.DensityThreshold)
		{
			return false;
		}
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0x9E37u);
		return VoxelHash.Hash01(h) < chance;
	}

	/// <summary>The trunk (cactus) height for a column, from the dominant biome's range.</summary>
	public static int TrunkHeight(int wx, int wz, GenConfig cfg)
	{
		var (_, dominant) = TerrainGenerator.BiomeAt(wx, wz, cfg);
		var biome = TerrainGenerator.BiomeOf(cfg, dominant);
		int min = biome != null ? biome.TrunkMinHeight : 3;
		int max = biome != null ? biome.TrunkMaxHeight : 5;
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0x51ADu);
		return min + (int)(VoxelHash.Hash01(h) * (max - min + 1));
	}

	/// <summary>The leaf-blob radius for a column, from the dominant biome's range.</summary>
	public static int LeafRadius(int wx, int wz, GenConfig cfg)
	{
		var (_, dominant) = TerrainGenerator.BiomeAt(wx, wz, cfg);
		var biome = TerrainGenerator.BiomeOf(cfg, dominant);
		int min = biome != null ? biome.LeafMinRadius : 2;
		int max = biome != null ? biome.LeafMaxRadius : 3;
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0xBEEFu);
		return min + (int)(VoxelHash.Hash01(h) * (max - min + 1));
	}

	public override void Place(
		int wx, int wz, int h, ChunkData chunk, int chunkSizeXZ,
		Dictionary<Vector2I, VoxelChunk> chunks, GenConfig cfg, Action<int, int, int, byte> put)
	{
		var biome = TerrainGenerator.DominantBiome(wx, wz, cfg);
		if (biome == null)
		{
			return;
		}

		// Base check: the surface voxel must exist, be solid (not cave-carved),
		// and be the biome's surface material, so a tree never sprouts from a
		// cave hollow or a rock boulder. (Own-chunk read; the column is inside
		// this chunk by construction of the foliage stage.)
		if (h < 0 || h >= chunk.SizeY)
		{
			return;
		}
		// wx/wz are WORLD coords; the local read needs the column's offset from
		// the chunk's origin. (A plain `wx % chunk.SizeX` would be wrong for any
		// chunk in negative world space — C#'s % keeps the sign of the dividend —
		// and reads out of bounds, crashing world load-in.)
		int lx = wx - FloorDiv(wx, chunkSizeXZ) * chunkSizeXZ;
		int lz = wz - FloorDiv(wz, chunkSizeXZ) * chunkSizeXZ;
		byte surface = chunk.Get(lx, h, lz);
		if (surface == 0)
		{
			return; // cave-carved: the tree would float.
		}
		if (biome.SurfaceMaterial > 0 && surface != (byte)biome.SurfaceMaterial)
		{
			return; // rocky / cave surface: no tree.
		}

		if (biome.TrunkMaterial == 0)
		{
			return; // no trunk material -> no tree.
		}

		int sy = chunk.SizeY;
		int th = TrunkHeight(wx, wz, cfg);

		int lr = LeafRadius(wx, wz, cfg);
		// Canopy centred one voxel above the trunk top so the trunk pokes
		// through the top of the blob (Minecraft style).
		int top = h + th;
		int cy = top + 1;

		// Trunk.
		for (int y = h + 1; y <= Math.Min(top, sy - 1); y++)
		{
			put(wx, y, wz, (byte)biome.TrunkMaterial);
		}

		// Canopy: main blob plus an optional lower second tier. Emitted into
		// whatever chunk each leaf voxel belongs to (put resolves the owner).
		if (biome.LeafMaterial != 0)
		{
			WriteLeafBlob(wx, wz, cy, lr, (byte)biome.LeafMaterial, put);
			if (biome.HasLeafTier2 && lr > 1)
			{
				int lr2 = Mathf.Max(1, Mathf.RoundToInt(lr * biome.LeafTier2RadiusFactor));
				WriteLeafBlob(wx, wz, cy - biome.LeafTier2HeightBelow, lr2, (byte)biome.LeafMaterial, put);
			}
		}
	}

	/// <summary>
	/// The LOD silhouette: raise the coarse node to the top of the canopy and
	/// flag it with the biome's leaf material, so a distant view reads "forest".
	/// </summary>
	public override bool LodHeight(
		int wx, int wz, int h, GenConfig cfg,
		Dictionary<Vector2I, VoxelChunk> chunks, int chunkSizeXZ,
		out int delta, out int material)
	{
		delta = 0;
		material = 0;
		var biome = TerrainGenerator.DominantBiome(wx, wz, cfg);
		if (biome == null)
		{
			return false;
		}
		if (!TerrainGenerator.TreeAt(wx, wz, cfg))
		{
			return false;
		}
		if (!TerrainGenerator.FoliageBaseSolid(wx, h, wz, chunks, chunkSizeXZ))
		{
			return false;
		}
		delta = TrunkHeight(wx, wz, cfg) + LeafRadius(wx, wz, cfg);
		material = biome.LeafMaterial > 0 ? biome.LeafMaterial : biome.SurfaceMaterial;
		return true;
	}

	/// <summary>
	/// Emit a spherical leaf blob centred at world column (wx, wz) and height
	/// <paramref name="centerY"/> with the given radius, delegating each voxel to
	/// <paramref name="write"/> (which resolves the owning chunk and clips to
	/// air). Used so the canopy can spill across chunk borders.
	/// </summary>
	public static void WriteLeafBlob(
		int wx, int wz, int centerY, int radius, byte leafMaterial,
		Action<int, int, int, byte> write)
	{
		int r2 = radius * radius;
		for (int dx = -radius; dx <= radius; dx++)
		{
			for (int dy = -radius; dy <= radius; dy++)
			{
				for (int dz = -radius; dz <= radius; dz++)
				{
					if (dx * dx + dy * dy + dz * dz > r2)
					{
						continue;
					}
					write(wx + dx, centerY + dy, wz + dz, leafMaterial);
				}
			}
		}
	}
}

/// <summary>
/// A CACTUS: a single vertical column of cactus voxels, no canopy. Its height
/// follows the dominant biome's trunk-height range. It shares the tree's
/// per-column gate but only grows in a biome configured as cactus with a
/// cactus material.
/// </summary>
[GlobalClass]
public partial class CactusStructure : StructureDefinition
{
	public override string Tag => "cactus";

	public override bool PlaceAtColumn(int wx, int wz, int dominant, GenConfig cfg)
	{
		var biome = TerrainGenerator.BiomeOf(cfg, dominant);
		// Cacti use the SAME per-column gate as trees (the biome's forest gate),
		// but only in a biome configured as cactus with a cactus material.
		if (biome == null || !biome.EnableFoliage || !biome.IsCactus || biome.CactusMaterial <= 0)
		{
			return false;
		}
		float chance = biome.Probability * Mathf.Clamp(cfg.FoliageDensity, 0f, 1f);
		if (chance <= 0f)
		{
			return false; // short-circuit BEFORE touching the forest noise
		}
		if (cfg.ForestNoise == null || cfg.ForestNoise.GetNoise2D(wx, wz) < biome.DensityThreshold)
		{
			return false;
		}
		uint h = VoxelHash.Hash2(wx, wz, (uint)cfg.Seed ^ 0x9E37u);
		return VoxelHash.Hash01(h) < chance;
	}

	public override void Place(
		int wx, int wz, int h, ChunkData chunk, int chunkSizeXZ,
		Dictionary<Vector2I, VoxelChunk> chunks, GenConfig cfg, Action<int, int, int, byte> put)
	{
		var biome = TerrainGenerator.DominantBiome(wx, wz, cfg);
		if (biome == null || !biome.IsCactus || biome.CactusMaterial <= 0)
		{
			return;
		}
		// Same base checks as the tree (solid, surface-material). wx/wz are
		// world coords; the local read needs the offset from the chunk origin
		// (see TreeStructure.Place for why a plain % is wrong in negative space).
		if (h < 0 || h >= chunk.SizeY)
		{
			return;
		}
		int lx = wx - FloorDiv(wx, chunkSizeXZ) * chunkSizeXZ;
		int lz = wz - FloorDiv(wz, chunkSizeXZ) * chunkSizeXZ;
		byte surface = chunk.Get(lx, h, lz);
		if (surface == 0)
		{
			return;
		}
		if (biome.SurfaceMaterial > 0 && surface != (byte)biome.SurfaceMaterial)
		{
			return;
		}

		int sy = chunk.SizeY;
		int th = TreeStructure.TrunkHeight(wx, wz, cfg);
		int cactusTop = h + th;
		for (int y = h + 1; y <= Math.Min(cactusTop, sy - 1); y++)
		{
			put(wx, y, wz, (byte)biome.CactusMaterial);
		}
	}
}

/// <summary>
/// A ROCK BOULDER: a squashed ellipsoid of the biome's rock material resting on
/// the surface. Placed on a hashed cell grid (one candidate per cell) so
/// placement is stable across chunk borders — the same cell rolls the same
/// boulder no matter which chunk generates it. It is a per-CELL structure (the
/// structures stage calls <see cref="PlaceCells"/> over the chunk's cell grid)
/// rather than a per-column one, so <see cref="StructureDefinition.PlaceAtColumn"/>
/// is not used for it.
/// </summary>
[GlobalClass]
public partial class RockStructure : StructureDefinition
{
	public override string Tag => "rock";

	public float Probability { get; set; } = 0.35f;
	public int CellSize { get; set; } = 8;
	public int MinRadius { get; set; } = 2;
	public int MaxRadius { get; set; } = 4;

	/// <summary>Vertical squash of the boulder (&lt; 1 flattens it into the ground).</summary>
	public float Squash { get; set; } = 0.7f;

	/// <summary>
	/// Stamps every rock boulder whose hashed cell overlaps the chunk
	/// [wx0..wx0+sx) × [wz0..wz0+sz). <paramref name="heights"/> is the
	/// per-column surface-height cache for the chunk (indexed
	/// <c>heights[x + sx*z]</c>); out-of-bounds columns fall back to
	/// <see cref="TerrainGenerator.SurfaceHeight"/>.
	/// </summary>
	public void PlaceCells(
		ChunkData chunk, int wx0, int wz0, int sx, int sz, short[] heights, GenConfig cfg,
		Action<int, int, int, byte> put)
	{
		int sy = chunk.SizeY;
		int cellMinX = FloorDiv(wx0, CellSize);
		int cellMaxX = FloorDiv(wx0 + sx - 1, CellSize);
		int cellMinZ = FloorDiv(wz0, CellSize);
		int cellMaxZ = FloorDiv(wz0 + sz - 1, CellSize);

		for (int cx = cellMinX; cx <= cellMaxX; cx++)
		{
			for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
			{
				uint h = VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0x51A5u);
				if (VoxelHash.Hash01(h) > Probability)
				{
					continue;
				}

				int wx = cx * CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xA111u)) * (CellSize - 1));
				int wz = cz * CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cz, cx, (uint)cfg.Seed ^ 0xB222u)) * (CellSize - 1));

				int ground = (wx >= wx0 && wx < wx0 + sx && wz >= wz0 && wz < wz0 + sz)
					? heights[(wx - wx0) + sx * (wz - wz0)]
					: TerrainGenerator.SurfaceHeight(wx, wz, cfg);

				int r = MinRadius + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xC333u)) * (MaxRadius - MinRadius + 1));
				var rockBiome = TerrainGenerator.DominantBiome(wx, wz, cfg);
				byte rockMat = (byte)(rockBiome != null && rockBiome.RockMaterial > 0 ? rockBiome.RockMaterial : VoxelMaterialIds.Rock);

				float invR2 = 1f / (r * r);
				float invRy2 = 1f / (r * r * Squash * Squash);
				int cy = ground + 1;
				for (int dx = -r; dx <= r; dx++)
				{
					for (int dy = -r; dy <= r; dy++)
					{
						for (int dz = -r; dz <= r; dz++)
						{
							if (dx * dx * invR2 + dy * dy * invRy2 + dz * dz * invR2 > 1f)
							{
								continue;
							}
							int ny = cy + dy;
							if (ny < 0 || ny >= sy)
							{
								continue;
							}
							put(wx + dx, ny, wz + dz, rockMat);
						}
					}
				}
			}
		}
	}

	public override void Place(
		int wx, int wz, int h, ChunkData chunk, int chunkSizeXZ,
		Dictionary<Vector2I, VoxelChunk> chunks, GenConfig cfg, Action<int, int, int, byte> put)
	{
		// Rocks are placed per cell (see PlaceCells), not per column. This is a
		// no-op if called directly.
	}
}

/// <summary>
/// A ZOMBIE HIVE: a solid sphere of <see cref="VoxelMaterialIds.Hive"/> voxels
/// grown on the surface that periodically SPAWNS ZOMBIES around it. It is the
/// one structure whose placement is a per-CHUNK roll (not a per-column one) and
/// whose voxels are written on the MAIN thread after its chunk's data is ready
/// (so the light engine never sees a half-built chunk). It is also the only
/// saveable definition: the world persists each placed instance
/// (center + radius + depleted) in the save's hive section.
///
/// Its runtime behavior lives in the <see cref="Hive"/> node (the
/// <see cref="FunctionalFoliage"/> subclass that tracks its voxel count,
/// depletes when enough of the sphere is broken, and spawns zombies on an
/// interval). <see cref="SpawnRuntimeNode"/> builds that node.
/// </summary>
[GlobalClass]
public partial class HiveStructure : StructureDefinition
{
	public override string Tag => "hive";

	/// <summary>
	/// The scene instantiated for each hive's functional node (the
	/// <c>FactorySim.Hive</c> node that spawns zombies). Leave null to disable
	/// hive runtime behavior (the voxels still generate, but no zombies come
	/// out of them).
	/// </summary>
	public PackedScene HiveScene { get; set; }

	private int _radius = 2;
	/// <summary>The hive sphere radius (voxels). Set by the world on build.</summary>
	public int Radius => _radius;

	public override int BodyMaterial => VoxelMaterialIds.Hive;
	public override bool IsSaveable => true;

	public void SetRadius(int r) => _radius = Mathf.Max(1, r);

	/// <summary>
	/// Enumerates the hive sphere's voxel offsets (relative to its center). The
	/// same solid sphere the old <c>WriteHiveVoxelsDirect</c> stamped — kept here
	/// so the world's main-thread writer and the runtime node's footprint agree.
	/// </summary>
	public IEnumerable<Vector3I> SphereVoxels()
	{
		int r2 = _radius * _radius;
		for (int dx = -_radius; dx <= _radius; dx++)
		{
			for (int dy = -_radius; dy <= _radius; dy++)
			{
				for (int dz = -_radius; dz <= _radius; dz++)
				{
					if (dx * dx + dy * dy + dz * dz > r2)
					{
						continue;
					}
					yield return new Vector3I(dx, dy, dz);
				}
			}
		}
	}

	/// <summary>
	/// Stamps the hive sphere's voxels. <paramref name="h"/> is the surface
	/// height the sphere rests on; the center is <c>(wx, h + radius, wz)</c> so
	/// the sphere's bottom point sits AT the surface (its lowest cell is the
	/// ground's top voxel, which the air-fill leaves as ground). Unlike the
	/// column structures, a hive is a STRUCTURE, not a canopy: it writes into
	/// ANY cell it overlaps (a restored hive's partially-broken sphere must be
	/// re-stamped exactly as the save describes it), not only air.
	/// </summary>
	public override void Place(
		int wx, int wz, int h, ChunkData chunk, int chunkSizeXZ,
		Dictionary<Vector2I, VoxelChunk> chunks, GenConfig cfg, Action<int, int, int, byte> put)
	{
		int cy = h + _radius;
		foreach (var o in SphereVoxels())
		{
			put(wx + o.X, cy + o.Y, wz + o.Z, (byte)VoxelMaterialIds.Hive);
		}
	}

	/// <summary>
	/// Builds the live <see cref="Hive"/> node for a placed instance at
	/// <paramref name="center"/>. Returns null when no hive scene is assigned.
	/// </summary>
	public override FunctionalFoliage SpawnRuntimeNode(Vector3I center, VoxelWorld world)
	{
		if (HiveScene == null)
		{
			return null;
		}
		var hive = HiveScene.Instantiate<Hive>();
		if (hive == null)
		{
			return null;
		}
		hive.CenterVoxel = center;
		hive.Radius = _radius;
		hive.MaterialId = VoxelMaterialIds.Hive;
		return hive;
	}
}

/// <summary>
/// The world's list of <see cref="StructureDefinition"/>s: everything that
/// grows in the world, in one place. The world builds this once (from its
/// biome/rock/hive settings) and hands it to the generation stages.
///
/// <see cref="ColumnStructures"/> are the per-column definitions (trees,
/// cacti) rolled against every column in the gen-thread foliage stage;
/// <see cref="CellStructures"/> are the per-cell definitions (rocks) stamped
/// over the chunk's cell grid in the structures stage. The hive is NOT in
/// either list — it is placed by a per-chunk main-thread roll (see
/// <see cref="HiveStructure"/>), which is why it keeps its own direct-write
/// path.
/// </summary>
public sealed class StructureRegistry
{
	private readonly List<StructureDefinition> _column = new();
	private readonly List<RockStructure> _cells = new();

	/// <summary>Per-column structures (trees, cacti) — rolled per column.</summary>
	public IReadOnlyList<StructureDefinition> ColumnStructures => _column;

	/// <summary>Per-cell structures (rocks) — stamped over the cell grid.</summary>
	public IReadOnlyList<RockStructure> CellStructures => _cells;

	/// <summary>The hive definition (nullable; null when hives are disabled).</summary>
	public HiveStructure Hive { get; private set; }

	/// <summary>Adds a per-column structure (tree / cactus).</summary>
	public void AddColumn(StructureDefinition def) => _column.Add(def);

	/// <summary>Adds a per-cell structure (rock).</summary>
	public void AddCell(RockStructure def) => _cells.Add(def);

	/// <summary>Sets the hive definition (its radius must be set by the caller).</summary>
	public void SetHive(HiveStructure def) => Hive = def;

	public static StructureRegistry Build(GenConfig cfg, bool enableRocks, int rockCellSize,
		float rockProbability, int rockMinRadius, int rockMaxRadius, float rockSquash,
		int hiveRadius, PackedScene hiveScene, bool hivesEnabled)
	{
		var reg = new StructureRegistry();
		reg.AddColumn(new TreeStructure());
		reg.AddColumn(new CactusStructure());
		if (enableRocks)
		{
			reg.AddCell(new RockStructure
			{
				Probability = rockProbability,
				CellSize = rockCellSize,
				MinRadius = rockMinRadius,
				MaxRadius = rockMaxRadius,
				Squash = rockSquash,
			});
		}
		if (hivesEnabled)
		{
			var hive = new HiveStructure { HiveScene = hiveScene };
			hive.SetRadius(hiveRadius);
			reg.SetHive(hive);
		}
		return reg;
	}
}
