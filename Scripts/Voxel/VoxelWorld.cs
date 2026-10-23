using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FactorySim.Items;

namespace FactorySim.Voxel;

/// <summary>
/// Visual meshing style. "Faces" is the classic hidden-face voxel mesh
/// (exact voxel faces, matches the collision mesh). "MarchingCubes" is the
/// classic smooth per-material isosurface (beveled edges/corners, inset half
/// a voxel, open chunk seams). Toggle in the inspector; all loaded chunks
/// rebuild live.
/// </summary>
public enum VoxelMeshMode
{
	Faces,
	MarchingCubes,
}

/// <summary>
/// Chunk-based voxel terrain manager.
///
/// - Generates terrain around the player up to <see cref="VoxelWorld.RenderDistanceChunks"/>.
/// - Exposes every generation parameter in the inspector for testing.
/// - Provides sphere add/remove edits (non-persistent) and a DDA voxel raycast
///   used by the player's tools.
/// - Press F (configurable) to regenerate all loaded chunks after changing parameters.
/// </summary>
[GlobalClass]
public partial class VoxelWorld : Node3D
{
	[ExportGroup("Chunks")]
	/// <summary>Chunk width/depth in voxels (XZ).</summary>
	[Export(PropertyHint.Range, "8,64,8")]
	public int ChunkSizeXZ { get; set; } = 16;

	/// <summary>Chunk height in voxels (Y).</summary>
	[Export(PropertyHint.Range, "16,256,8")]
	public int ChunkHeight { get; set; } = 48;

	/// <summary>How many chunks around the player to keep loaded.</summary>
	[Export(PropertyHint.Range, "1,32,1")]
	public int RenderDistanceChunks { get; set; } = 4;

	/// <summary>How many chunk meshes to (re)build per frame.</summary>
	[Export(PropertyHint.Range, "1,32,1")]
	public int ChunksPerFrame { get; set; } = 3;

	[ExportGroup("World")]
	/// <summary>Random seed for all generation.</summary>
	[Export]
	public int Seed { get; set; } = 1337;

	/// <summary>Edge length of one voxel in world units (meters).</summary>
	[Export(PropertyHint.Range, "0.01,10,0.01")]
	public float VoxelSize { get; set; } = 1.0f;

	/// <summary>Height of the player's origin above the terrain surface when spawning.</summary>
	[Export(PropertyHint.Range, "0.5,5,0.05")]
	public float SpawnOffset { get; set; } = 1.0f;

	[ExportGroup("Heightmap (2D Perlin)")]
	/// <summary>Base terrain height in voxels.</summary>
	[Export(PropertyHint.Range, "0,256,1")]
	public float BaseHeight { get; set; } = 16f;

	[Export(PropertyHint.Range, "0,256,0.5")]
	public float HeightAmplitude { get; set; } = 9f;

	/// <summary>Perlin frequency (lower = larger features).</summary>
	[Export(PropertyHint.Range, "0.001,0.2,0.001")]
	public float HeightFrequency { get; set; } = 0.018f;

	[Export(PropertyHint.Range, "1,8,1")]
	public int HeightOctaves { get; set; } = 4;

	[Export(PropertyHint.Range, "0,1,0.01")]
	public float HeightPersistence { get; set; } = 0.5f;

	[Export(PropertyHint.Range, "1,4,0.05")]
	public float HeightLacunarity { get; set; } = 2.0f;

	/// <summary>Thickness of the dirt layer under the grass.</summary>
	[Export(PropertyHint.Range, "0,32,1")]
	public int DirtDepth { get; set; } = 3;

	/// <summary>Voxels below this Y are cave stone.</summary>
	[Export(PropertyHint.Range, "0,32,1")]
	public int CaveStoneDepth { get; set; } = 5;

	[ExportGroup("Caves (3D Perlin)")]
	[Export]
	public bool EnableCaves { get; set; } = true;

	/// <summary>Perlin frequency of the 3D cave field.</summary>
	[Export(PropertyHint.Range, "0.01,0.5,0.001")]
	public float CaveFrequency { get; set; } = 0.07f;

	[Export(PropertyHint.Range, "1,8,1")]
	public int CaveOctaves { get; set; } = 3;

	/// <summary>Fractal gain of the cave noise (lower = smoother, less detailed).</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float CavePersistence { get; set; } = 0.5f;

	/// <summary>Fractal lacunarity of the cave noise.</summary>
	[Export(PropertyHint.Range, "1,4,0.05")]
	public float CaveLacunarity { get; set; } = 2.0f;

	/// <summary>|noise| above this value is carved out.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float CaveThreshold { get; set; } = 0.5f;

	/// <summary>Minimum voxels of terrain kept above a cave.</summary>
	[Export(PropertyHint.Range, "0,32,1")]
	public int CaveCeiling { get; set; } = 4;

	/// <summary>
	/// Minimum voxels of solid rock kept below the cave network (the "cave floor").
	/// Caves will not carve below this Y, leaving a solid slab above the bedrock
	/// so the player always has ground to stand on. Defaults to 7 (5 above the
	/// 2-voxel bedrock layer).
	/// </summary>
	[Export(PropertyHint.Range, "2,32,1")]
	public int CaveFloorDepth { get; set; } = 7;

	[ExportGroup("Biomes")]
	/// <summary>
	/// The list of biomes and how they're chosen across the world. Each
	/// <see cref="BiomeDef"/> is fully editor-modifiable (height, amplitude,
	/// materials, foliage, weight). Biome selection is a weighted cumulative
	/// distribution over each biome's Weight (see the Biome noise settings
	/// below), so the world's biome mix follows the editor-set weights.
	///
	/// Persistence: this is best a reference to a saved .tres (point the export
	/// at <see cref="BiomeConfigPath"/>), because the code default is a fresh
	/// in-memory instance that would discard inspector edits on re-run. When it
	/// is empty/unset, <see cref="SaveBiomeConfig"/> (run at startup) writes the
	/// current config to that path so the next run loads real values.
	/// </summary>
	[Export]
	public BiomeConfig BiomeConfig { get; private set; } = new();

	/// <summary>
	/// The .tres the <see cref="BiomeConfig"/> is serialized to/from. Edit the
	/// biomes in the inspector (or call <see cref="SaveBiomeConfig"/>) to
	/// (re)write this file, then re-run: the scene loads your values instead of
	/// the code defaults. This is what makes biome edits persist.
	/// </summary>
	[Export]
	public string BiomeConfigPath { get; set; } = "res://Resources/BiomeConfig.tres";

	/// <summary>
	/// 2D biome-selection noise frequency (lower = larger biome regions).
	/// Independent from the terrain height noise.
	/// </summary>
	[Export(PropertyHint.Range, "0.001,0.1,0.001")]
	public float BiomeNoiseFrequency { get; set; } = 0.004f;

	[Export(PropertyHint.Range, "1,8,1")]
	public int BiomeOctaves { get; set; } = 2;

	/// <summary>Fractal gain of the biome-selection noise (lower = smoother biome borders).</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float BiomePersistence { get; set; } = 0.5f;

	/// <summary>Fractal lacunarity of the biome-selection noise.</summary>
	[Export(PropertyHint.Range, "1,4,0.05")]
	public float BiomeLacunarity { get; set; } = 2.0f;

	[ExportGroup("Foliage (forest-density noise)")]
	/// <summary>
	/// 2D forest-density noise that gates whether the dominant biome actually
	/// places a tree at a column. Per-biome probability/trunk/leaf settings
	/// live on each <see cref="BiomeDef"/>.
	/// </summary>
	[Export(PropertyHint.Range, "0.001,0.1,0.001")]
	public float ForestNoiseFrequency { get; set; } = 0.008f;

	[Export(PropertyHint.Range, "1,8,1")]
	public int ForestOctaves { get; set; } = 2;

	/// <summary>Fractal gain of the forest-density noise (lower = smoother transitions).</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float ForestPersistence { get; set; } = 0.5f;

	/// <summary>Fractal lacunarity of the forest-density noise.</summary>
	[Export(PropertyHint.Range, "1,4,0.05")]
	public float ForestLacunarity { get; set; } = 2.0f;

	/// <summary>
	/// Global multiplier (0..1) on foliage instance density WITHIN a forest.
	/// 1.0 = each biome plants at its own Probability; lower values thin out
	/// the trees/cacti inside the same groves (the grove layout from the
	/// forest-density noise is unchanged — only how many plants land per
	/// column). 0.0 disables all foliage.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float FoliageDensity { get; set; } = 1.0f;

	[ExportGroup("Structures: Ores")]
	/// <summary>
	/// The list of ores (material id, placement probability, cell size, vein
	/// size, height range). Each <see cref="OreDef"/> is fully editor-modifiable;
	/// add new ores with the "Add Ore" button (or edit a saved .tres). Ore
	/// placement is a pure function of (cell, seed), so veins crossing chunk
	/// borders are identical no matter which chunk generates them.
	/// </summary>
	[Export]
	public OreConfig OreConfig { get; private set; } = new();

	[ExportGroup("Structures: Rocks")]
	/// <summary>Masters the rock structure generator (extend via IStructureGenerator).</summary>
	[Export]
	public bool EnableRocks { get; set; } = true;

	/// <summary>Chance per rock cell (cellSize x cellSize) to place a boulder.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float RockProbability { get; set; } = 0.35f;

	/// <summary>Grid cell size used for deterministic rock placement.</summary>
	[Export(PropertyHint.Range, "2,64,1")]
	public int RockCellSize { get; set; } = 8;

	[Export(PropertyHint.Range, "1,16,1")]
	public int RockMinRadius { get; set; } = 2;

	[Export(PropertyHint.Range, "1,16,1")]
	public int RockMaxRadius { get; set; } = 4;

	/// <summary>Vertical squash of boulders (&lt; 1 flattens them into the ground, &gt; 1 stretches them up).</summary>
	[Export(PropertyHint.Range, "0.1,2,0.05")]
	public float RockSquash { get; set; } = 0.7f;

	[ExportGroup("Meshing")]
	/// <summary>Visual mesh style; switching rebuilds all loaded chunks live.</summary>
	[Export]
	public VoxelMeshMode MeshMode { get; set; } = VoxelMeshMode.Faces;

	[ExportGroup("LOD (Distant)")]
	/// <summary>
	/// Masters the Distant-Horizons-style distant level-of-detail: simplified
	/// low-res heightfield terrain rendered beyond <see cref="RenderDistanceChunks"/>
	/// (visual only, no collision).
	/// </summary>
	[Export]
	public bool LodEnabled { get; set; } = true;

	/// <summary>Number of LOD levels beyond full-res (1 = finest, higher = coarser/farther).</summary>
	[Export(PropertyHint.Range, "1,8,1")]
	public int LodLevels { get; set; } = 4;

	/// <summary>Farthest LOD render distance, in base-chunk units.</summary>
	[Export(PropertyHint.Range, "1,256,1")]
	public int LodRenderDistanceChunks { get; set; } = 48;

	/// <summary>
	/// Geometric growth factor of the LOD distance bands (Distant Horizons'
	/// "quadratic base"): level k covers distances in
	/// [R * base^(k-1), R * base^k) where R is <see cref="RenderDistanceChunks"/>.
	/// </summary>
	[Export(PropertyHint.Range, "1.1,4,0.05")]
	public float LodDistanceBase { get; set; } = 2.0f;

	/// <summary>Coarse heightfield grid cells per side of an LOD region.</summary>
	[Export(PropertyHint.Range, "2,32,1")]
	public int LodMeshResolution { get; set; } = 8;

	[ExportGroup("Materials")]
	/// <summary>
	/// Per-material appearance (color, texture, transparency, metallic, roughness,
	/// emission). Assign a <see cref="VoxelMaterialConfig"/> resource here (create
	/// one in the FileSystem dock, right-click it to Save as a .tres, then edit
	/// each material's Entry — including its Texture — in the inspector). When a
	/// material's Entry has a Texture set, that same texture is used both on the
	/// terrain AND as the item icon in the inventory / hotbar / crafting UI.
	/// Leaving it unset falls back to a freshly built default config.
	/// </summary>
	[Export]
	public VoxelMaterialConfig MaterialConfig { get; set; } = new();

	/// <summary>
	/// The .tres the <see cref="MaterialConfig"/> is serialized to/from. Edit the
	/// materials in the inspector (or call <see cref="SaveMaterialConfig"/>) to
	/// (re)write this file, then re-run: the scene loads your values instead of
	/// the code defaults. This is what makes material/texture edits persist.
	/// </summary>
	[Export]
	public string MaterialConfigPath { get; set; } = "res://Resources/MaterialConfig.tres";

	[ExportGroup("Machines")]
	/// <summary>
	/// The registry of machine definitions for this world — the factory design
	/// system's data. Assign a <see cref="FactorySim.Machines.MachineDefConfig"/>
	/// resource here (create one, add a <see cref="FactorySim.Machines.MachineDefinition"/>
	/// per machine, each pointing at an imported Blender mesh). When a player
	/// places a machine item, the world spawns a <see cref="FactorySim.Machines.Machine"/>
	/// node using this registry. Leave unset (null) to disable machines; the
	/// dev panel still lists machine items with fallback names.
	/// </summary>
	[Export]
	public FactorySim.Machines.MachineDefConfig MachineConfig { get; private set; }

	/// <summary>
	/// The radius (in world-voxel units) over which a power source machine feeds
	/// nearby consumer machines. Tunable here; forwarded to
	/// <see cref="FactorySim.Machines.PowerNetwork"/> in <see cref="_Ready"/>.
	/// A value under 0.5 falls back to the network's default (8).
	/// </summary>
	[Export(PropertyHint.Range, "0,64,0.5")]
	public float PowerRadius { get; set; } = FactorySim.Machines.PowerNetwork.DefaultRadius;

	[ExportGroup("Debug")]
	/// <summary>Pressing this key regenerates all loaded chunks with the current parameters.</summary>
	[Export]
	public bool EnableRegenerateShortcut { get; set; } = true;
	[Export]
	public Key RegenerateKey { get; set; } = Key.F;

	// ------------------------------------------------------------------

	public VoxelMaterialPalette Palette { get; private set; } = null!;

	/// <summary>
	/// Fired for every voxel that a PLAYER EDIT removes (the pickaxe breaking a
	/// single voxel, the remove tool carving a sphere) with the world voxel
	/// position and the removed material. Terrain generation and the add tool
	/// never raise it. <c>VoxelTool</c> subscribes to queue item drops — the
	/// drops themselves spawn only once the chunk update for the edited chunk
	/// has finished (see <see cref="PendingDrops"/>).
	/// </summary>
	public event Action<Vector3I, int> VoxelBroken;

	/// <summary>
	/// One pending item drop. <see cref="VoxelPos"/>/<see cref="MaterialId"/>
	/// describe the broken voxel; <see cref="WaitStamp"/> is the
	/// <see cref="_editPriorityCounter"/> value stamped on the edited region at
	/// the moment the drop was queued, and <see cref="WaitChunk"/> is the chunk
	/// the removed voxel sat in.
	///
	/// A drop is released only once the chunk update it was waiting on has
	/// FINISHED, where "finished" is checked SELF-CONTAINEDLY against the stamp:
	/// the drop's own chunk has <c>NeedsMesh == false</c>, no mesh build is in
	/// flight, and its <c>EditPriority >= WaitStamp</c> (i.e. every edit made
	/// while the drop waited has already been rebuilt). Waiting on the whole
	/// 1-chunk ring is deliberately NOT done — a ring chunk can be kept perpetually
	/// dirty by unrelated work (neighbour seams, LOD, other edits), which would
	/// hold the drop forever. The staleness cap below additionally guarantees a
	/// drop can never be held indefinitely.
	/// </summary>
	public readonly struct PendingDrop
	{
		public readonly Vector3I VoxelPos;
		public readonly int MaterialId;
		public readonly int WaitStamp;
		public readonly Vector2I WaitChunk;
		public readonly double QueuedTime;

		public PendingDrop(Vector3I voxelPos, int materialId, int waitStamp, Vector2I waitChunk, double queuedTime)
		{
			VoxelPos = voxelPos;
			MaterialId = materialId;
			WaitStamp = waitStamp;
			WaitChunk = waitChunk;
			QueuedTime = queuedTime;
		}
	}

	/// <summary>
	/// Pending item drops: one <see cref="PendingDrop"/> per voxel a player edit
	/// removed. <c>VoxelTool</c> enqueues them (via the <c>VoxelBroken</c>
	/// event); <see cref="_Process"/> (in <see cref="FlushPendingDrops"/>) spawns
	/// each one once its chunk update has finished, re-raising <c>VoxelBroken</c>
	/// so the tool actually creates the drop node.
	///
	/// This keeps a drop from visibly resting in mid-air: a broken voxel's
	/// surface is not re-meshed (and its collision not rebuilt) until the chunk
	/// update finishes, so the drop can only land on solid, visible terrain once
	/// that update has completed.
	/// </summary>
	public List<PendingDrop> PendingDrops { get; } = new();

	/// <summary>
	/// A pending drop older than this (seconds) is spawned regardless of chunk
	/// state: a safety net so a drop can never be held forever by a chunk that
	/// will never rebuild (e.g. freed mid-wait, or stuck dirty by unrelated
	/// work). Generous enough to be invisible in normal play (a chunk rebuild
	/// finishes in a frame or two).
	/// </summary>
	private const double MaxPendingDropAgeSeconds = 5.0;

	private GenConfig _config;
	private Node3D _chunkRoot;
	private readonly Dictionary<Vector2I, VoxelChunk> _chunks = new();
	private bool _playerSpawned;

	/// <summary>
	/// Every voxel the player has edited this session, keyed by world voxel
	/// (latest value wins). This is the persistence layer: <c>SetVoxel</c> is
	/// the single choke point all edits route through, so recording here
	/// captures adds, breaks, and pickaxe removals alike. Saved to / loaded
	/// from the <c>WorldSave</c> service; a material id of 0 (air) means removed.
	/// </summary>
	private readonly Dictionary<Vector3I, byte> _edits = new();

	/// <summary>
	/// Health for every voxel the player has damaged that is still solid, keyed
	/// by world voxel. Complementary to <see cref="_edits"/>: edits reset a
	/// voxel to full health, so only voxels that were damaged (not replaced)
	/// live here. Saved to / loaded from the <c>WorldSave</c> service.
	/// </summary>
	private readonly Dictionary<Vector3I, float> _damagedHealth = new();

	/// <summary>
	/// The <c>WorldSave</c> service node (looked up lazily). Owns the pending
	/// snapshot loaded from disk and the save triggers. Null when there is no
	/// save node in the scene (the world then runs without persistence).
	/// </summary>
	private WorldSave _save;
	private VoxelMeshMode _lastBuiltMode = (VoxelMeshMode)(-1);
	/// <summary>
	/// Monotonic stamp for <see cref="VoxelChunk.EditPriority">: each dirty ring
	/// from an edit gets a newer timestamp, so the most recently edited chunks
	/// win the budget-loop sort tie-breaks.
	/// </summary>
	private int _editPriorityCounter;

	/// <summary>
	/// The current value of <see cref="_editPriorityCounter"/> (read-only view).
	/// <c>VoxelTool</c> captures this the frame it queues a pending drop; a
	/// chunk's <c>EditPriority</c> only reaches at least this value once the
	/// chunk update the drop was waiting on has been rebuilt, which is how the
	/// drop gate knows to release the drop (see <see cref="FlushPendingDrops"/>).
	/// </summary>
	public int EditPriorityCounter => _editPriorityCounter;

	/// <summary>
	/// Number of chunk generations currently in flight on the thread pool.
	/// Bumped in <see cref="StartGeneration"/>, released in
	/// <see cref="OnGenerationComplete"/>. Used together with
	/// <see cref="_genQueue"/> to cap concurrent generation and to keep the
	/// in-flight set to the nearest unfinished chunks (see
	/// <see cref="PumpGenerationQueue"/>).
	/// </summary>
	private int _generatingBudget;

	/// <summary>
	/// Nearest-first queue of chunk coords that need generating but are not yet
	/// started. A chunk is enqueued by the load loop and dequeued (nearest
	/// first) by <see cref="PumpGenerationQueue"/> whenever a generation slot
	/// frees up. This is what gives generation *priority ordering*: the thread
	/// pool runs tasks in whatever order it pleases, so without this gate a far
	/// chunk could be mid-generation while nearer chunks wait — the in-flight
	/// set is always the nearest <see cref="MaxConcurrentGenerations"/>
	/// unfinished chunks, so completions always come from the nearest ring.
	/// Ordered by squared distance to the player at enqueue time; the player
	/// moves slowly relative to a chunk, so a stale ordering is acceptable and
	/// re-queued on the next frame if still pending.
	/// </summary>
	private readonly System.Collections.Generic.List<(Vector2I Coord, float DistSq)> _genQueue = new();

	/// <summary>
	/// True while a coord is in <see cref="_genQueue"/> (not yet started), so the
	/// load loop doesn't enqueue the same chunk twice in one frame.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<Vector2I> _genQueued = new();

	/// <summary>
	/// Maximum number of chunks to have their terrain generating at once. Higher
	/// = faster overall population; the nearest-first queue (see
	/// <see cref="_genQueue"/>) keeps the in-flight set to the nearest
	/// unfinished chunks regardless of this value.
	/// </summary>
	private const int MaxConcurrentGenerations = 8;

	/// <summary>
	/// Chunks within this many chunk-cells (Chebyshev distance) of the player carry a
	/// live collision shape; farther chunks have their shape nulled so the broad phase
	/// only ever tests a small constant number of colliders. The player can only reach
	/// nearby terrain, so distant chunks need no collider — they exist for visual culling.
	/// </summary>
	private const int CollisionRadiusChunks = 3;

	/// <summary>
	/// Distant-Horizons-style LOD state: the root node holding every
	/// <see cref="LodChunk"/> and the (level, region-coord) -> chunk map.
	/// Managed by <see cref="UpdateLod"/>; deliberately separate from the
	/// full-resolution <see cref="_chunks"/> (LOD is visual-only and never
	/// generates full-res voxel data or collision).
	/// </summary>
	private Node3D _lodRoot;
	private readonly Dictionary<(int, Vector2I), LodChunk> _lodChunks = new();

	/// <summary>Maximum LOD region meshes to build concurrently on the thread pool.</summary>
	private const int MaxConcurrentLodBuilds = 4;

	public override void _Ready()
	{
		// Safety net for stale scene data: a `= null` export override loads as 0/false,
		// which would give zero-sized chunks (NaN coords) and — with ChunksPerFrame = 0 —
		// meshes that never build, so the spawn gate never opens.
		SanitizeExports();

		// VoxelTool (and any other systems) can locate us via this group.
		AddToGroup("voxel_world");

		// Seed the biome .tres if the export is still the empty in-memory
		// default: a fresh project has no saved biome file, so writing the code
		// defaults gives the user a real file to edit + re-run against. If the
		// export already points at a saved .tres, that value is loaded as-is
		// (the user's edits) and is NOT overwritten.
		if (BiomeConfig != null && BiomeConfig.Count == 0)
		{
			SaveBiomeConfig();
		}

		// A "New World" session or a directly-run scene may still be on the in-memory
		// default MaterialConfig (no textures). Seed MaterialConfigPath with it so the
		// next run loads a real file the user can edit (textures, colors, ...). Only
		// seed when the export is the empty in-memory default (Materials has no entries)
		// — the SAME rule as BiomeConfig above. If the export already points at a saved
		// .tres (the user's edits, possibly with textures), it is NOT touched: re-writing
		// it from the in-memory default here would silently wipe the user's textures.
		if (MaterialConfig != null && (MaterialConfig.Materials == null || MaterialConfig.Materials.Length == 0))
		{
			SaveMaterialConfig();
		}

		// A "New World" session carries a user-chosen seed (published by the
		// main menu's slot picker and read by this scene's WorldSave node in its
		// own _Ready, which runs first — WorldSave is an earlier sibling).
		// Apply it to Seed BEFORE BuildConfig() so the height/cave/forest/biome
		// noise sources and GenConfig.Seed (ores/rocks) all pick it up. A load
		// session (no session seed) keeps the export/scene seed, and a directly-
		// run scene (no WorldSave node) also keeps the export seed.
		var sessionSave = GetSaveService();
		if (sessionSave != null && sessionSave.HasSessionSeed)
		{
			Seed = sessionSave.ActiveSessionSeed;
			GD.Print($"[VoxelWorld] Applying session seed {Seed} for this world.");
		}

		_config = BuildConfig();
		Palette = new VoxelMaterialPalette();
		_lastBuiltMode = MeshMode;
		_chunkRoot = new Node3D { Name = "Chunks" };
		AddChild(_chunkRoot);

		_lodRoot = new Node3D { Name = "LodChunks" };
		AddChild(_lodRoot);

		// Apply the material config once, then follow live inspector edits.
		ApplyMaterials();
		if (MaterialConfig != null)
		{
			MaterialConfig.PropertyChanged -= OnMaterialConfigChanged; // dedupe on re-ready
			MaterialConfig.PropertyChanged += OnMaterialConfigChanged;
			MaterialConfig.BlendChanged -= OnBlendChanged; // dedupe on re-ready
			MaterialConfig.BlendChanged += OnBlendChanged;
		}

		// Publish the machine registry to the static MachineRegistry (and any
		// change to the config re-publishes). Null = machines disabled.
		FactorySim.Machines.MachineRegistry.SetConfig(MachineConfig);
		// Forward the tunable power radius to the range-based power network.
		FactorySim.Machines.PowerNetwork.SetRadius(PowerRadius);
		if (BiomeConfig != null)
		{
			BiomeConfig.PropertyChanged -= OnBiomeConfigChanged; // dedupe on re-ready
			BiomeConfig.PropertyChanged += OnBiomeConfigChanged;
		}
		if (OreConfig != null)
		{
			OreConfig.PropertyChanged -= OnOreConfigChanged; // dedupe on re-ready
			OreConfig.PropertyChanged += OnOreConfigChanged;
		}

		// Player spawning is deferred to the first _Process: the player joins
		// its group in its own _Ready, which can run after this node's.
	}

	public void SetMaterialConfig(VoxelMaterialConfig value)
	{
		if (MaterialConfig != null)
		{
			MaterialConfig.PropertyChanged -= OnMaterialConfigChanged;
			MaterialConfig.BlendChanged -= OnBlendChanged;
		}

		MaterialConfig = value ?? new VoxelMaterialConfig();

		if (IsInsideTree())
		{
			MaterialConfig.PropertyChanged += OnMaterialConfigChanged;
			MaterialConfig.BlendChanged += OnBlendChanged;
			ApplyMaterials();
		}
	}

	/// <summary>
	/// Sets (or clears) the machine registry and re-publishes it to the static
	/// <see cref="FactorySim.Machines.MachineRegistry"/>. The inspector calls this
	/// when the export is reassigned; <c>null</c> disables machines.
	/// </summary>
	public void SetMachineConfig(FactorySim.Machines.MachineDefConfig value)
	{
		MachineConfig = value;
		FactorySim.Machines.MachineRegistry.SetConfig(value);
	}

	/// <summary>
	/// Rebuilds the auto-generated box collision on every machine currently in
	/// the scene tree. Called by the editor's "Rebuild Machine Collisions"
	/// button (via the <see cref="FactorySim.Editor.VoxelWorldPlugin"/>) and by
	/// the config panel's "Rebuild All Collisions" button.
	///
	/// Works in both the editor and play mode:
	///   - **Play mode**: finds the "Machines" container (where spawned machines
	///     live) and rebuilds each non-preview machine's collision.
	///   - **Editor**: searches the entire scene tree for any <see cref="FactorySim.Machines.Machine"/>
	///     nodes (e.g. machines placed in a test scene) and rebuilds those.
	///     If no machine nodes are found, logs a hint that machines are
	///     spawned at runtime.
	///
	/// Use this after re-importing or re-scaling a Blender mesh so the collider
	/// matches the new visuals.
	/// </summary>
	public void RebuildAllMachineCollisions()
	{
		var tree = GetTree();
		if (tree == null)
		{
			GD.PushWarning("[VoxelWorld] Not in a scene tree — cannot rebuild collisions.");
			return;
		}

		int rebuilt = 0;

		// Primary path: the "Machines" container (where spawned machines live
		// at runtime).
		var machinesRoot = tree.Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot != null)
		{
			for (int i = 0; i < machinesRoot.GetChildCount(); i++)
			{
				if (machinesRoot.GetChild(i) is FactorySim.Machines.Machine m && !m.IsPreview)
				{
					m.RebuildCollision();
					rebuilt++;
				}
			}
		}

		// Secondary path (editor): search the whole tree for any Machine nodes
		// that aren't already in the "Machines" container (e.g. machines placed
		// directly in a test scene).
		if (rebuilt == 0)
		{
			foreach (var node in tree.Root.FindChildren("*", "Machine", true, false))
			{
				if (node is FactorySim.Machines.Machine m && !m.IsPreview)
				{
					m.RebuildCollision();
					rebuilt++;
				}
			}
			if (rebuilt == 0)
			{
				GD.Print("[VoxelWorld] No machine nodes found in the scene tree. " +
					"Machines are spawned at runtime; to test collision in the editor, " +
					"place a Machine node in a test scene and select it, or run the game.");
			}
		}

		if (rebuilt > 0)
		{
			GD.Print($"[VoxelWorld] Rebuilt collision on {rebuilt} machine(s).");
		}
	}

	/// <summary>
	/// Opens the in-game machine-config panel (the list of all registered
	/// machine definitions with add/remove). Called by the editor's
	/// "Open Machine Config" button (via the <see cref="FactorySim.Editor.VoxelWorldPlugin"/>).
	/// The panel is a CanvasLayer that toggles on/off.
	/// </summary>
	public void OpenMachineConfigPanel()
	{
		var tree = GetTree();
		if (tree == null)
		{
			return;
		}

		// Find or create the MachineConfigPanel CanvasLayer in the scene.
		var panel = tree.Root.FindChild("MachineConfigPanel", true, false) as FactorySim.UI.MachineConfigPanel;
		if (panel == null)
		{
			panel = new FactorySim.UI.MachineConfigPanel { Name = "MachineConfigPanel" };
			tree.Root.AddChild(panel);
			panel.Configure(MachineConfig);
		}
		panel.Toggle();
	}

	private void OnMaterialConfigChanged(VoxelMaterialEntry _) => ApplyMaterials();

	/// <summary>
	/// A blend parameter (width / roughness / metallic / strength) changed in the
	/// config: re-push the palette (which clears its cached blend shader
	/// materials) and remesh — the blend uniforms and the MC boundary ramp both
	/// depend on the values.
	/// </summary>
	private void OnBlendChanged() => ApplyMaterials();

	public void SetBiomeConfig(BiomeConfig value)
	{
		if (BiomeConfig != null)
		{
			BiomeConfig.PropertyChanged -= OnBiomeConfigChanged;
		}

		BiomeConfig = value ?? new BiomeConfig();

		if (IsInsideTree())
		{
			BiomeConfig.PropertyChanged += OnBiomeConfigChanged;
			// Biome edits change terrain, not just appearance: regenerate the
			// config and queue every loaded chunk for a rebuild.
			RegenerateWorld();
		}
	}

	private void OnBiomeConfigChanged(string _) => RegenerateWorld();

	/// <summary>
	/// Writes the current <see cref="BiomeConfig"/> to <see cref="BiomeConfigPath"/>
	/// (creating the folder if needed). Call this after editing the biomes in the
	/// inspector so the values PERSIST: the next scene run (and <c>Load</c> on
	/// demand) reads the saved file instead of the code defaults. Also invoked
	/// automatically at startup when the export is still the empty in-memory
	/// default, so a fresh project seeds a usable file on first run.
	/// </summary>
	public void SaveBiomeConfig()
	{
		BiomeConfig?.SaveTo(BiomeConfigPath);
	}

	/// <summary>
	/// Writes the current <see cref="MaterialConfig"/> to <see cref="MaterialConfigPath"/>
	/// (creating the folder if needed). Call this after editing the materials in the
	/// inspector so the values PERSIST: the next scene run (and <c>Load</c> on
	/// demand) reads the saved file instead of the code defaults. Also invoked
	/// automatically at startup so a fresh project seeds a usable file on first
	/// run (the in-memory default has no textures).
	/// </summary>
	public void SaveMaterialConfig()
	{
		MaterialConfig?.SaveTo(MaterialConfigPath);
	}

	public void SetOreConfig(OreConfig value)
	{
		if (OreConfig != null)
		{
			OreConfig.PropertyChanged -= OnOreConfigChanged;
		}

		OreConfig = value ?? new OreConfig();

		if (IsInsideTree())
		{
			OreConfig.PropertyChanged += OnOreConfigChanged;
			// Ore edits change terrain, not just appearance: regenerate the
			// config and queue every loaded chunk for a rebuild.
			RegenerateWorld();
		}
	}

	private void OnOreConfigChanged(string _) => RegenerateWorld();

	/// <summary>
	/// Pushes the material config onto the shared palette instances in place.
	/// Color/texture/transparency/metallic/etc. update every built chunk with no
	/// remesh; only a texture-tiling (UV) change or a blend-parameter change
	/// needs the chunks rebuilt, which is budget-managed through ChunksPerFrame
	/// so it never hitches.
	/// </summary>
	private void ApplyMaterials()
	{
		if (Palette == null || MaterialConfig == null)
		{
			return;
		}

		int newCount = MaterialConfig.Materials.Length;
		// Growing or shrinking the material list changes the palette's material
		// slots (mesh material indices are id-1), so every chunk must remesh.
		bool countChanged = Palette.MaterialCount != newCount;
		bool tilingChanged = false;
		for (int i = 0; i < Palette.TextureScales.Length && i < newCount; i++)
		{
			if (!Mathf.IsEqualApprox(Palette.TextureScales[i], MaterialConfig.Materials[i].Entry.TextureScale))
			{
				tilingChanged = true;
				break;
			}
		}

		// A blend-parameter change needs a remesh: the cached blend shader
		// materials bake in the roughness / metallic / strength uniforms and the
		// MarchingCubes boundary ramp depends on the width. Capture the previous
		// params BEFORE pushing the new ones so we can detect the change.
		var prevBlend = Palette.BlendParams;
		bool blendChanged =
			!Mathf.IsEqualApprox(prevBlend.width, MaterialConfig.BlendWidth) ||
			!Mathf.IsEqualApprox(prevBlend.roughness, MaterialConfig.BlendRoughness) ||
			!Mathf.IsEqualApprox(prevBlend.metallic, MaterialConfig.BlendMetallic) ||
			!Mathf.IsEqualApprox(prevBlend.strength, MaterialConfig.BlendStrength);

		Palette.Resize(newCount);
		Palette.Apply(MaterialConfig);
		Palette.SetBlendParams(
			MaterialConfig.BlendWidth,
			MaterialConfig.BlendRoughness,
			MaterialConfig.BlendMetallic,
			MaterialConfig.BlendStrength);

		if (tilingChanged || countChanged || blendChanged)
		{
			foreach (VoxelChunk chunk in _chunks.Values)
			{
				chunk.NeedsMesh = true;
			}
			// LOD UVs are tiled by the same per-material TextureScales (and its
			// per-material slot count), so either change invalidates the cached
			// LOD meshes too. (Blend params don't reach the LOD meshes, but
			// invalidating here keeps the single code path simple.)
			foreach (LodChunk lod in _lodChunks.Values)
			{
				lod.Invalidate();
			}
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (!EnableRegenerateShortcut)
		{
			return;
		}

		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == RegenerateKey)
		{
			RegenerateWorld();
			GD.Print("[VoxelWorld] Regenerated all loaded chunks.");
		}
	}

	public override void _Process(double delta)
	{
		// Inspector toggle: when the mesh style changes, queue every loaded
		// chunk for a rebuild (happens within the normal per-frame budget).
		if (MeshMode != _lastBuiltMode)
		{
			_lastBuiltMode = MeshMode;
			foreach (VoxelChunk chunk in _chunks.Values)
			{
				chunk.NeedsMesh = true;
			}
		}

		if (GetTree().GetFirstNodeInGroup("player") is not Node3D playerNode)
		{
			return;
		}

		Vector2I playerChunk = ChunkCoordOf(playerNode.GlobalPosition);

		// Enqueue every in-range chunk that isn't already loaded or queued, then
		// pump the nearest-first generation queue. The queue (not the thread
		// pool) decides which chunk generates next, so the in-flight set is
		// always the nearest MaxConcurrentGenerations unfinished chunks and a far
		// chunk can never be mid-generation while nearer ones wait. SpiralCoords
		// is only used to *enumerate* in-range coords; the queue re-sorts them by
		// true distance to the player each frame.
		foreach (Vector2I coord in SpiralCoords(playerChunk, RenderDistanceChunks))
		{
			if (_chunks.ContainsKey(coord) || _genQueued.Contains(coord))
			{
				continue;
			}

			_genQueued.Add(coord);
			_genQueue.Add((coord, DistanceSq(coord, playerChunk)));
		}

		PumpGenerationQueue(playerChunk);

		// One-time spawn: voxel data is now generated asynchronously, but mesh +
		// collision are only built in Rebuild() under the ChunksPerFrame budget.
		// Wait until the chunk at (0,0) — the one under the player's origin —
		// has its data generated (DataReady) and is solid (mesh + collision
		// built), then place the player on the terrain. The player is frozen
		// meanwhile so it can't fall through the not-yet-solid ground (its
		// movement lives in _PhysicsProcess).
		if (!_playerSpawned)
		{
			playerNode.SetPhysicsProcess(false);
			if (_chunks.TryGetValue(Vector2I.Zero, out VoxelChunk spawnChunk)
				&& spawnChunk.DataReady && !spawnChunk.NeedsMesh
				&& !HasPendingEdits
				&& SpawnOrRestorePlayer(playerNode))
			{
				_playerSpawned = true;
				playerNode.SetPhysicsProcess(true);
			}
		}

		// Unload chunks well out of range.
		int margin = RenderDistanceChunks + 1;
		foreach (VoxelChunk chunk in _chunks.Values.ToList())
		{
			if (Mathf.Abs(chunk.Coord.X - playerChunk.X) > margin || Mathf.Abs(chunk.Coord.Y - playerChunk.Y) > margin)
			{
				FreeChunk(chunk);
			}
		}

		// Build/rebuild meshes within a per-frame budget. Order: edited
		// chunks first (most recently edited wins ties), then nearest to the
		// player. Edited chunks (EditPriority > 0) sort before never-edited ones
		// at the same distance. Chunks whose data isn't generated yet are
		// skipped — Rebuild would no-op on them anyway.
		var pending = new List<VoxelChunk>();
		foreach (VoxelChunk chunk in _chunks.Values)
		{
			if (chunk.NeedsMesh && chunk.DataReady)
			{
				pending.Add(chunk);
			}
		}

		// OrderBy/ThenBy is a stable sort, so equal (priority, distance) keys
		// keep their original order; the edit-priority stamp plus distance is
		// a deterministic total order over the edited area.
		pending = pending
			.OrderByDescending(c => c.EditPriority)
			.ThenBy(c => DistanceSq(c.Coord, playerChunk))
			.ToList();

		for (int i = 0; i < pending.Count && i < ChunksPerFrame; i++)
		{
			pending[i].Rebuild(this);
		}

		// Item drops from edited voxels only spawn once the chunk update for
		// the edited chunk has finished (see PendingDrops) — otherwise the drop
		// would visibly rest in mid-air until the surface re-meshes.
		FlushPendingDrops(playerChunk);

		// Enable per-chunk collision for the player's vicinity: chunks within
		// CollisionRadiusChunks (plus any just-rebuilt chunk) keep their shape
		// so the player never stands on invisible ground; the rest have theirs
		// nulled so the broad phase only ever tests a small constant number of
		// colliders.
		UpdateLocalCollision(playerChunk);

		// Distant-Horizons-style LOD beyond the normal render distance
		// (visual-only heightfields, own budget — never routed through
		// ChunksPerFrame). When disabled, free anything still alive.
		if (LodEnabled)
		{
			UpdateLod(playerNode.GlobalPosition);
		}
		else if (_lodChunks.Count > 0)
		{
			FreeAllLod();
		}
	}

	// ------------------------------------------------------------------
	// Public API used by the tools
	// ------------------------------------------------------------------

	/// <summary>
	/// The chunk coordinate for a world voxel's XZ. Public so <c>VoxelTool</c>
	/// can record which chunk a pending drop is waiting on (see
	/// <see cref="PendingDrop.WaitChunk"/>).
	/// </summary>
	public Vector2I VoxelChunkCoord(Vector3I v)
	{
		return ChunkCoordOf(v.X, v.Z);
	}

	/// <summary>Reads a voxel id in world voxel coordinates (0 = air if the chunk is not loaded).</summary>
	public int GetVoxel(int x, int y, int z)
	{
		if (y < 0 || y >= ChunkHeight)
		{
			return VoxelMaterialIds.Air;
		}

		if (!_chunks.TryGetValue(ChunkCoordOf(x, z), out VoxelChunk chunk))
		{
			return VoxelMaterialIds.Air;
		}

		return chunk.Data.Get(x - chunk.Coord.X * ChunkSizeXZ, y, z - chunk.Coord.Y * ChunkSizeXZ);
	}

	/// <summary>
	/// True when a material is flagged as unbreakable in the material config
	/// (e.g. bedrock). Unbreakable voxels are skipped by the pickaxe and the
	/// remove tool.
	/// </summary>
	public bool IsUnbreakable(int materialId)
	{
		if (MaterialConfig == null || materialId <= 0)
		{
			return false;
		}

		var def = MaterialConfig.GetDef(materialId);
		return def != null && def.Entry != null && def.Entry.Unbreakable;
	}

	// ------------------------------------------------------------------
	// Voxel health (player damage tracking)
	// ------------------------------------------------------------------

	/// <summary>
	/// Reads a voxel's health (0..100) in world voxel coordinates. Unloaded
	/// chunks and air report full health (100) — generated voxels are always
	/// considered undamaged until the player damages them.
	/// </summary>
	public float GetVoxelHealth(Vector3I v)
	{
		if (v.Y < 0 || v.Y >= ChunkHeight)
		{
			return VoxelHealth.MaxHealth;
		}

		if (!_chunks.TryGetValue(ChunkCoordOf(v.X, v.Z), out VoxelChunk chunk))
		{
			return VoxelHealth.MaxHealth;
		}

		return chunk.Data.GetHealth(v.X - chunk.Coord.X * ChunkSizeXZ, v.Y, v.Z - chunk.Coord.Y * ChunkSizeXZ);
	}

	// ------------------------------------------------------------------
	// World persistence (edits + health capture / replay)
	// ------------------------------------------------------------------

	/// <summary>
	/// Returns a copy of every voxel the player has edited this session
	/// (world voxel → material id, 0 = air/removed). The <c>WorldSave</c>
	/// service iterates this when collecting a snapshot.
	/// </summary>
	public IReadOnlyDictionary<Vector3I, int> GetEdits()
	{
		var copy = new Dictionary<Vector3I, int>(_edits.Count);
		foreach (var kv in _edits)
		{
			copy[kv.Key] = kv.Value;
		}
		return copy;
	}

	/// <summary>
	/// Returns a copy of every damaged-voxel health this session (world voxel →
	/// remaining health 0..100). The <c>WorldSave</c> service iterates this when
	/// collecting a snapshot (and filters out voxels that have since been removed).
	/// </summary>
	public IReadOnlyDictionary<Vector3I, float> GetDamagedHealth()
	{
		var copy = new Dictionary<Vector3I, float>(_damagedHealth.Count);
		foreach (var kv in _damagedHealth)
		{
			copy[kv.Key] = kv.Value;
		}
		return copy;
	}

	/// <summary>
	/// Re-applies a set of saved voxel edits to the world, for one chunk. Each
	/// (world voxel, material) pair is written via <see cref="SetVoxel"/> into
	/// the chunk it belongs to (voxels outside the chunk or out of range are
	/// ignored — they belong to a neighbour). Does NOT re-mesh: the caller is
	/// expected to flag the chunk + its 1-ring for a rebuild (see
	/// <see cref="ApplyLoadedChunk"/>). Used on load to restore the player's
	/// edits onto freshly generated terrain.
	/// </summary>
	public void ApplyEdits(IReadOnlyList<(Vector3I Voxel, int Material)> edits)
	{
		if (edits == null)
		{
			return;
		}

		foreach (var (v, material) in edits)
		{
			SetVoxel(v, material);
		}
	}

	/// <summary>
	/// Re-applies a set of saved damaged-voxel health values to the world, for
	/// one chunk. Health is written with <c>ChunkData.SetHealth</c> directly (no
	/// re-mesh, since health is invisible until a voxel breaks). Voxels in a
	/// neighbour chunk or out of range are ignored.
	/// </summary>
	public void ApplyHealth(IReadOnlyList<(Vector3I Voxel, float Health)> health)
	{
		if (health == null)
		{
			return;
		}

		foreach (var (v, h) in health)
		{
			if (v.Y < 0 || v.Y >= ChunkHeight)
			{
				continue;
			}

			if (!_chunks.TryGetValue(ChunkCoordOf(v.X, v.Z), out VoxelChunk chunk))
			{
				continue;
			}

			int lx = v.X - chunk.Coord.X * ChunkSizeXZ;
			int lz = v.Z - chunk.Coord.Y * ChunkSizeXZ;
			chunk.Data.SetHealth(lx, v.Y, lz, h);
		}
	}

	/// <summary>
	/// The load-time hook: apply one chunk's saved edits + health (pulled from
	/// the <c>WorldSave</c> service's pending snapshot) and flag the chunk plus
	/// its 1-chunk ring for a rebuild with fresh edit priority, so the budget
	/// loop meshes the edited chunk first. Called by <see cref="OnGenerationComplete"/>
	/// the frame a chunk's data finishes generating. A no-op when there is no
	/// pending snapshot (a fresh world).
	/// </summary>
	public void ApplyLoadedChunk(VoxelChunk chunk)
	{
		var save = GetSaveService();
		if (save == null)
		{
			return;
		}

		// Safety net: chunk generation can finish BEFORE the spawn gate runs
		// EnsureLoadedForSession, so this node's PendingEdits can be null even
		// though a slot was chosen. Load its own active slot directly so the
		// saved edits are not silently dropped.
		if (save.PendingEdits == null && save.EnsureLoadedForSession())
		{
			// Re-check after the reload; the reload may have found no data.
		}
		if (save.PendingEdits == null)
		{
			return;
		}

		bool anything = false;

		var edits = save.ConsumePendingEdits(chunk.Coord, ChunkSizeXZ);
		if (edits.Count > 0)
		{
			ApplyEdits(edits);
			anything = true;
		}

		var health = save.ConsumePendingHealth(chunk.Coord, ChunkSizeXZ);
		if (health.Count > 0)
		{
			ApplyHealth(health);
			anything = true;
		}

		if (!anything)
		{
			return;
		}

		// The edited chunk AND its 1-ring must re-mesh: border faces are culled
		// against the neighbour's state, so the neighbours re-cull too. A fresh
		// edit priority makes the budget loop rebuild this area before the
		// surrounding (never-edited) chunks.
		chunk.NeedsMesh = true;
		chunk.EditPriority = ++_editPriorityCounter;
		MarkNeighboursForRebuild(chunk.Coord);
	}

	/// <summary>
	/// Restores the player's saved transform (position + yaw/pitch) in place of
	/// the terrain spawn. Returns true when a saved player was applied (the
	/// caller should NOT also run <c>SpawnPlayerOnTerrain</c>); false when there
	/// was no saved player (fresh world — the normal terrain spawn applies).
	/// </summary>
	public bool RestorePlayerFromSave(Node3D player)
	{
		var save = GetSaveService();
		if (save == null || !save.ConsumePendingPlayer(out Vector3 pos, out float yaw, out float pitch))
		{
			return false;
		}

		if (player is Player p)
		{
			p.RestoreFromSave(pos, yaw, pitch);
			GD.Print($"[VoxelWorld] Restored player from save at {pos} (yaw={yaw:F2}, pitch={pitch:F2}).");
		}
		else
		{
			player.GlobalPosition = pos;
		}
		return true;
	}

	/// <summary>
	/// True while the <c>WorldSave</c> service still holds unapplied saved edits
	/// or health (i.e. the world has not finished re-applying the saved world).
	/// Used to keep the spawn gate honest: when the saved player position sits in
	/// a chunk other than (0,0), the gate waits until those chunks load and their
	/// edits are applied before the player is placed. A fresh world (no save, or
	/// a save with nothing to apply) reports false immediately.
	/// </summary>
	public bool HasPendingEdits
	{
		get
		{
			var save = GetSaveService();
			return save != null && save.HasPendingEdits;
		}
	}

	/// <summary>
	/// Looks up (and caches) the <c>WorldSave</c> service node for THIS scene,
	/// or null when the scene has none (the world then runs without persistence).
	///
	/// The lookup prefers the node that is a DESCENDANT of the world's own scene
	/// root (so it never binds to the main menu's WorldSave), then falls back to
	/// any WorldSave in the tree. It re-resolves whenever the cached node leaves
	/// the tree (a scene change) so a stale reference is never served.
	/// </summary>
	private WorldSave GetSaveService()
	{
		if (_save != null && _save.IsInsideTree())
		{
			return _save;
		}

		// The cached node is gone (or was never set) — re-resolve. This can be
		// reached from a deferred callback during teardown, when the world is no
		// longer in the tree; GetTree() throws in that case, so bail out early.
		_save = null;
		if (!IsInsideTree())
		{
			return null;
		}
		var tree = GetTree();
		if (tree == null)
		{
			return null;
		}

		// Strategy 1 (preferred): find the WorldSave under the world's own scene
		// root. GetParent() walks up to the top-level scene instance, so this is
		// scoped to THIS scene and can never match the menu's node.
		var sceneRoot = GetParent();
		while (sceneRoot != null && sceneRoot.GetParent() != null && sceneRoot.GetParent() is not Window)
		{
			sceneRoot = sceneRoot.GetParent();
		}
		if (sceneRoot != null)
		{
			_save = sceneRoot.FindChild("WorldSave", recursive: true) as WorldSave;
			if (_save != null)
			{
				return _save;
			}
		}

		// Strategy 2 (fallback): any WorldSave in the tree, preferring one that is
		// currently in the tree and belongs to the active scene.
		foreach (Node child in tree.Root.GetChildren())
		{
			if (child is Window)
			{
				continue;
			}
			var candidate = child.FindChild("WorldSave", recursive: true) as WorldSave;
			if (candidate != null && candidate.IsInsideTree())
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Subtracts <paramref name="amount"/> (percentage points) from a voxel's
	/// health, clamped at 0. Returns the voxel's health after the damage (full
	/// health for unloaded chunks / air, where the damage is dropped).
	/// </summary>
	public float DamageVoxel(Vector3I v, float amount)
	{
		if (v.Y < 0 || v.Y >= ChunkHeight || amount <= 0f)
		{
			return GetVoxelHealth(v);
		}

		if (!_chunks.TryGetValue(ChunkCoordOf(v.X, v.Z), out VoxelChunk chunk))
		{
			return GetVoxelHealth(v);
		}

		chunk.Data.Damage(v.X - chunk.Coord.X * ChunkSizeXZ, v.Y, v.Z - chunk.Coord.Y * ChunkSizeXZ, amount);
		float health = chunk.Data.GetHealth(v.X - chunk.Coord.X * ChunkSizeXZ, v.Y, v.Z - chunk.Coord.Y * ChunkSizeXZ);

		// Record the damage for persistence: only when the voxel is still solid
		// and below full health. (A broken voxel is removed via SetVoxel, which
		// handles its own persistence.) Full health clears the entry.
		if (health < VoxelHealth.MaxHealth)
		{
			_damagedHealth[v] = health;
		}
		else
		{
			_damagedHealth.Remove(v);
		}

		return health;
	}

	/// <summary>
	/// The display name of a material id from the material config (falls back to
	/// "Unknown" / "Air" for unconfigured ids). Used by the tool's debug HUD.
	/// </summary>
	public string GetVoxelName(int id)
	{
		if (id == VoxelMaterialIds.Air)
		{
			return "Air";
		}

		if (MaterialConfig != null && MaterialConfig.GetDef(id) is { } def)
		{
			return string.IsNullOrEmpty(def.Name) ? $"Material {id}" : def.Name;
		}

		return $"Material {id}";
	}

	/// <summary>
	/// DDA raycast through the voxel grid.
	/// Returns the first solid voxel within <paramref name="maxDistance"/> world units.
	/// </summary>
	public bool RaycastVoxels(Vector3 from, Vector3 dir, float maxDistance, out Vector3I hitVoxel, out Vector3 hitNormal)
	{
		hitVoxel = default;
		hitNormal = default;

		if (dir.IsZeroApprox())
		{
			return false;
		}

		dir = dir.Normalized();
		Vector3 pos = from / VoxelSize;

		int ix = Mathf.FloorToInt(pos.X);
		int iy = Mathf.FloorToInt(pos.Y);
		int iz = Mathf.FloorToInt(pos.Z);

		int stepX = dir.X >= 0f ? 1 : -1;
		int stepY = dir.Y >= 0f ? 1 : -1;
		int stepZ = dir.Z >= 0f ? 1 : -1;

		float tMaxX = NextBoundary(pos.X, dir.X, stepX);
		float tMaxY = NextBoundary(pos.Y, dir.Y, stepY);
		float tMaxZ = NextBoundary(pos.Z, dir.Z, stepZ);
		float tDeltaX = tMaxX != 1e30f ? 1f / Mathf.Abs(dir.X) : 1e30f;
		float tDeltaY = tMaxY != 1e30f ? 1f / Mathf.Abs(dir.Y) : 1e30f;
		float tDeltaZ = tMaxZ != 1e30f ? 1f / Mathf.Abs(dir.Z) : 1e30f;

		float maxT = maxDistance / VoxelSize;
		float t = 0f;

		for (int guard = 0; guard < 2048 && t <= maxT; guard++)
		{
			int normalAxis = -1;
			int normalSign = 1;

			if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
			{
				ix += stepX;
				t = tMaxX;
				tMaxX += tDeltaX;
				normalAxis = 0;
				normalSign = -stepX;
			}
			else if (tMaxY <= tMaxZ)
			{
				iy += stepY;
				t = tMaxY;
				tMaxY += tDeltaY;
				normalAxis = 1;
				normalSign = -stepY;
			}
			else
			{
				iz += stepZ;
				t = tMaxZ;
				tMaxZ += tDeltaZ;
				normalAxis = 2;
				normalSign = -stepZ;
			}

			if (GetVoxel(ix, iy, iz) != VoxelMaterialIds.Air)
			{
				hitVoxel = new Vector3I(ix, iy, iz);
				hitNormal = normalAxis switch
				{
					0 => new Vector3(normalSign, 0, 0),
					1 => new Vector3(0, normalSign, 0),
					_ => new Vector3(0, 0, normalSign),
				};
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Adds a sphere of voxels (material <paramref name="material"/>) centered on
	/// <paramref name="center"/> (world voxel coordinates). Non-persistent.
	/// </summary>
	public void AddSphere(Vector3I center, int radius, int material)
	{
		FillSphere(center, radius, material);
	}

	/// <summary>
	/// Removes (sets to air) a sphere of voxels centered on
	/// <paramref name="center"/> (world voxel coordinates). Non-persistent.
	/// Every non-air voxel removed raises <see cref="VoxelBroken"/> so item
	/// drops match what was actually carved out.
	/// </summary>
	public void RemoveSphere(Vector3I center, int radius)
	{
		int r2 = radius * radius;
		var removed = new List<(Vector3I Pos, int Material)>();
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

					Vector3I v = center + new Vector3I(dx, dy, dz);
					int material = GetVoxel(v.X, v.Y, v.Z);
					if (material != VoxelMaterialIds.Air && !IsUnbreakable(material))
					{
						removed.Add((v, material));
					}
				}
			}
		}

		FillSphereRespectingUnbreakable(center, radius);

		// Report what was removed AFTER the data is gone, so a handler that
		// reads the world mid-callback sees the post-edit state.
		foreach (var (pos, material) in removed)
		{
			VoxelBroken?.Invoke(pos, material);
		}
	}

	/// <summary>
	/// Sets a single voxel to air (world voxel coordinates), resetting its
	/// health and rebuilding the affected chunks. Used when a voxel's health
	/// reaches zero (the voxel "breaks"). Raises <see cref="VoxelBroken"/>
	/// with the removed material so the tool can spawn an item drop.
	/// </summary>
	public void BreakVoxel(Vector3I v)
	{
		int material = GetVoxel(v.X, v.Y, v.Z);
		if (material == VoxelMaterialIds.Air || IsUnbreakable(material))
		{
			return;
		}
		SetVoxel(v, VoxelMaterialIds.Air);
		MarkChunkDirty(v, 0);
		VoxelBroken?.Invoke(v, material);
	}

	/// <summary>
	/// Places a single <paramref name="material"/> voxel at <paramref name="v"/>
	/// (world voxel coordinates), overwriting whatever was there. Returns true
	/// when the voxel was actually written (the target cell was air and the
	/// chunk was loaded), false when it was a no-op (already solid, out of range,
	/// or an unloaded chunk). This is the world-side of the player's default
	/// place-ability: the tool calls it with the held material and the cell
	/// adjacent to the targeted face.
	///
	/// The edited chunk AND its 1-chunk ring are flagged for a rebuild (border
	/// faces are culled against neighbour state, so bordering chunks re-cull),
	/// matching the edit path. The write is captured by <see cref="SetVoxel"/>
	/// into <c>_edits</c> for persistence.
	/// </summary>
	public bool PlaceVoxel(Vector3I v, int material)
	{
		if (material == VoxelMaterialIds.Air || IsUnbreakable(material))
		{
			return false;
		}
		if (v.Y < 0 || v.Y >= ChunkHeight)
		{
			return false;
		}
		// Only place into an empty cell (don't overwrite existing terrain).
		if (GetVoxel(v.X, v.Y, v.Z) != VoxelMaterialIds.Air)
		{
			return false;
		}

		SetVoxel(v, material);
		MarkChunkDirty(v, 0);
		return true;
	}

	/// <summary>
	/// Moves the player so its origin sits <see cref="SpawnOffset"/> above the
	/// highest solid voxel in the column at the player's XZ.
	/// Returns false if the column could not be resolved (retry next frame).
	/// </summary>
	private bool SpawnPlayerOnTerrain(Node3D player)
	{
		Vector3 pos = player.GlobalPosition;
		int vx = Mathf.FloorToInt(pos.X / VoxelSize);
		int vz = Mathf.FloorToInt(pos.Z / VoxelSize);

		// Scan the loaded chunk top-down: accounts for caves (surface may be
		// lower) and rocks/trees (surface may be higher).
		int surfaceY = -1;
		if (_chunks.TryGetValue(ChunkCoordOf(vx, vz), out VoxelChunk chunk))
		{
			int lx = vx - chunk.Coord.X * ChunkSizeXZ;
			int lz = vz - chunk.Coord.Y * ChunkSizeXZ;
			for (int y = ChunkHeight - 1; y >= 0; y--)
			{
				if (chunk.Data.Get(lx, y, lz) != 0)
				{
					surfaceY = y;
					break;
				}
			}
		}

		// Fallback: the heightmap surface (always solid by construction).
		if (surfaceY < 0)
		{
			surfaceY = TerrainGenerator.SurfaceHeight(vx, vz, _config);
		}

		player.GlobalPosition = new Vector3(pos.X, (surfaceY + 1) * VoxelSize + SpawnOffset, pos.Z);
		GD.Print($"[VoxelWorld] Spawned player at {player.GlobalPosition} (surface voxel y={surfaceY}).");
		return true;
	}

	/// <summary>
	/// Spawns the player: restores the saved transform when a save exists
	/// (via <see cref="RestorePlayerFromSave"/>), otherwise places the player on
	/// the generated terrain (via <see cref="SpawnPlayerOnTerrain"/>). After a
	/// successful restore, the saved item drops are re-spawned onto the now-solid
	/// spawn chunk. Returns false when the save has no player (so the caller
	/// falls through to the terrain spawn).
	/// </summary>
	private bool SpawnOrRestorePlayer(Node3D player)
	{
		var save = GetSaveService();
		if (save == null)
		{
			return SpawnPlayerOnTerrain(player);
		}

		// Safety net: the world's WorldSave node and the menu's are DIFFERENT
		// instances, so this one can arrive with a null snapshot even though a
		// slot was chosen. If so, load its own active slot directly from disk.
		if (save.Snapshot == null)
		{
			save.EnsureLoadedForSession();
		}

		if (save.Snapshot != null)
		{
			// A save exists: restore the player's saved transform (instead of the
			// terrain spawn) and the saved inventory. The saved item drops are
			// re-spawned from the ON-DISK snapshot (the authoritative copy), not
			// the in-memory Snapshot, so a fresh world with a pending player
			// still restores the drops that were on disk.
			RestorePlayerFromSave(player);
			RestoreInventoryFromSave(save.Snapshot);
			save.ApplyDrops(save.Snapshot);
			save.ApplyMachines(save.Snapshot);
			return true;
		}
		return SpawnPlayerOnTerrain(player);
	}

	/// <summary>
	/// Restores the saved inventory (all 82 slots) from <paramref name="data"/>.
	/// A no-op when there is no inventory node or the snapshot held no slots.
	/// Called once, when the player is restored from a save.
	/// </summary>
	private void RestoreInventoryFromSave(WorldSaveFile.Data data)
	{
		if (data == null || data.Inventory.Count == 0)
		{
			return;
		}

		// Guard against an out-of-tree call (GetTree() throws when the world has
		// left the tree); a no-op then.
		if (!IsInsideTree())
		{
			return;
		}

		if (GetTree().GetFirstNodeInGroup("inventory") is Inventory inventory)
		{
			var ids = new int[data.Inventory.Count];
			var counts = new int[data.Inventory.Count];
			for (int i = 0; i < data.Inventory.Count; i++)
			{
				ids[i] = data.Inventory[i].MaterialId;
				counts[i] = data.Inventory[i].Count;
			}
			inventory.LoadState(ids, counts);
		}
	}

	/// <summary>Regenerates the data of every loaded chunk (for parameter testing).</summary>
	public void RegenerateWorld()
	{
		// Rebuild the config so inspector edits take effect immediately.
		_config = BuildConfig();
		foreach (VoxelChunk chunk in _chunks.Values)
		{
			TerrainGenerator.Generate(chunk.Data, chunk.Coord.X * ChunkSizeXZ, chunk.Coord.Y * ChunkSizeXZ, _config, _chunks);
			// Apply any overhang voxels queued for this chunk, then re-mesh it and
			// its 8 neighbours (an overhang may have landed in a neighbour buffer).
			TerrainGenerator.DrainOverhangs(chunk.Coord, chunk.Data);
			chunk.NeedsMesh = true;
			MarkNeighboursForRebuild(chunk.Coord);
		}

		// LOD regions are derived from the same terrain functions, so they are
		// stale too — invalidate them so they rebuild from the new config
		// (their gap-fill keeps the parent visible until each rebuild lands).
		foreach (LodChunk lod in _lodChunks.Values)
		{
			lod.Invalidate();
		}
	}

	// ------------------------------------------------------------------
	// Test-only seams (used by WorldSaveTest; never called in gameplay)
	// ------------------------------------------------------------------

	/// <summary>
	/// Test-only: returns the current generation config without rebuilding it.
	/// Lets <c>WorldSaveTest</c> generate a chunk synchronously against the same
	/// config the world would use, so the terrain is deterministic per seed.
	/// </summary>
	public GenConfig BuildConfigForTest()
	{
		if (_config == null)
		{
			_config = BuildConfig();
		}
		return _config;
	}

	/// <summary>
	/// Test-only: inserts an already-generated chunk into <see cref="_chunks"/>
	/// (bypassing the async generation pipeline) so the world's edit/query
	/// paths treat it as a loaded, ready chunk.
	/// </summary>
	public void RegisterChunkForTest(Vector2I coord, VoxelChunk chunk)
	{
		_chunks[coord] = chunk;
	}

	/// <summary>Test-only: writes a single voxel (the same path gameplay uses).</summary>
	public void SetVoxelForTest(Vector3I v, int material)
	{
		SetVoxel(v, material);
	}

	/// <summary>Test-only: damages a voxel (the same path gameplay uses).</summary>
	public float DamageVoxelForTest(Vector3I v, float amount)
	{
		return DamageVoxel(v, amount);
	}

	/// <summary>
	/// True when a chunk is loaded AND its voxel data has finished generating.
	/// A freshly created chunk is inserted into <see cref="_chunks"/>
	/// immediately (so it is a valid seam-culling neighbour) but its data is an
	/// all-air buffer until async generation completes and sets
	/// <see cref="VoxelChunk.DataReady"/>. Queries must treat the in-between
	/// state as UNLOADED: reading the empty buffer would see solid air and a
	/// neighbour rebuilding against it would emit a full one-sided wall across
	/// the whole shared edge, visible for however long the seam-rebuild is
	/// queued behind the ChunksPerFrame budget.
	/// </summary>
	private bool TryGetGeneratedChunk(Vector2I coord, out VoxelChunk chunk)
	{
		if (_chunks.TryGetValue(coord, out chunk) && chunk.DataReady)
		{
			return true;
		}

		chunk = null;
		return false;
	}

	/// <summary>
	/// Solid query for mesh culling: unloaded (or not-yet-generated) chunks
	/// count as SOLID so the outer edge of the terrain at the render-distance
	/// boundary is left OPEN (no wall) — matching the collision query and the
	/// Marching-Cubes open edges. A newly generated neighbour then closes the
	/// seam cleanly.
	/// </summary>
	public bool VoxelSolidForMesh(int x, int y, int z)
	{
		if (y < 0)
		{
			return true;
		}

		if (y >= ChunkHeight)
		{
			return false;
		}

		if (!TryGetGeneratedChunk(ChunkCoordOf(x, z), out VoxelChunk chunk))
		{
			return true; // unloaded / generating neighbour: open edge (no wall)
		}

		return chunk.Data.Get(x - chunk.Coord.X * ChunkSizeXZ, y, z - chunk.Coord.Y * ChunkSizeXZ) != 0;
	}

	/// <summary>
	/// Solid query for collision: unloaded (or not-yet-generated) chunks
	/// count as solid so we never generate interior walls at chunk borders.
	/// </summary>
	public bool VoxelSolidForCollision(int x, int y, int z)
	{
		if (y < 0)
		{
			return true;
		}

		if (y >= ChunkHeight)
		{
			return false;
		}

		if (!TryGetGeneratedChunk(ChunkCoordOf(x, z), out VoxelChunk chunk))
		{
			return true;
		}

		return chunk.Data.Get(x - chunk.Coord.X * ChunkSizeXZ, y, z - chunk.Coord.Y * ChunkSizeXZ) != 0;
	}

	// ------------------------------------------------------------------
	// Internals
	// ------------------------------------------------------------------

	private void FillSphere(Vector3I center, int radius, int material)
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

					SetVoxel(center + new Vector3I(dx, dy, dz), material);
				}
			}
		}

		MarkDirtyRegion(center - new Vector3I(radius, radius, radius),
			center + new Vector3I(radius, radius, radius));
	}

	/// <summary>
	/// Like <see cref="FillSphere"/> but skips unbreakable voxels (bedrock).
	/// Used by <see cref="RemoveSphere"/> so unbreakable materials survive the
	/// carve.
	/// </summary>
	private void FillSphereRespectingUnbreakable(Vector3I center, int radius)
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

					Vector3I v = center + new Vector3I(dx, dy, dz);
					int existing = GetVoxel(v.X, v.Y, v.Z);
					if (existing == VoxelMaterialIds.Air || IsUnbreakable(existing))
					{
						continue;
					}

					SetVoxel(v, VoxelMaterialIds.Air);
				}
			}
		}

		MarkDirtyRegion(center - new Vector3I(radius, radius, radius),
			center + new Vector3I(radius, radius, radius));
	}

	/// <summary>
	/// Marks a single voxel's chunk (and its 1-chunk ring) for a rebuild with
	/// fresh edit priority. Used by <see cref="BreakVoxel"/> (a single-voxel
	/// edit) so the rebuild path is shared with the sphere edits.
	/// </summary>
	private void MarkChunkDirty(Vector3I v, int radius)
	{
		MarkDirtyRegion(v - new Vector3I(radius, radius, radius),
			v + new Vector3I(radius, radius, radius));
	}

	/// <summary>
	/// Queue every chunk the edited region (plus 1-chunk ring) touches for a
	/// rebuild. Rebuilds are now budget-managed through the `_Process`
	/// ChunksPerFrame loop (edited-first, then nearest), so a big edit
	/// never hitches: only per-voxel SetVoxel work runs in this call.
	/// </summary>
	private void MarkDirtyRegion(Vector3I min, Vector3I max)
	{
		int cMinX = FloorDiv(min.X, ChunkSizeXZ);
		int cMaxX = FloorDiv(max.X, ChunkSizeXZ);
		int cMinZ = FloorDiv(min.Z, ChunkSizeXZ);
		int cMaxZ = FloorDiv(max.Z, ChunkSizeXZ);

		// One extra chunk ring: faces on chunk borders are culled by the
		// neighbour's state, so bordering chunks need a re-cull too. Mark each
		// dirty chunk and stamp it with a newer edit priority, so the budget
		// loop rebuilds them over the next few frames — the chunk the player is
		// editing (closest, highest priority) goes first on the very next frame.
		for (int cx = cMinX - 1; cx <= cMaxX + 1; cx++)
		{
			for (int cz = cMinZ - 1; cz <= cMaxZ + 1; cz++)
			{
				if (_chunks.TryGetValue(new Vector2I(cx, cz), out VoxelChunk chunk))
				{
					chunk.NeedsMesh = true;
					chunk.EditPriority = ++_editPriorityCounter;
				}
			}
		}
	}

	/// <summary>
	/// True when <paramref name="drop"/> may spawn: the chunk update it was
	/// waiting on has finished. Checked SELF-CONTAINEDLY against the drop's
	/// <see cref="PendingDrop.WaitStamp"/> — the drop's own chunk must have
	/// <c>NeedsMesh == false</c>, no mesh build in flight, and an
	/// <c>EditPriority</c> at least as new as the stamp (meaning every edit made
	/// while the drop waited has already been rebuilt). A missing chunk (freed
	/// while waiting) counts as finished so a drop can never wait on a chunk that
	/// will never rebuild — it is simply dropped with the chunk.
	/// </summary>
	private bool ChunkUpdateFinished(PendingDrop drop)
	{
		if (!_chunks.TryGetValue(drop.WaitChunk, out VoxelChunk chunk))
		{
			return true;
		}
		return !chunk.NeedsMesh && !chunk.MeshBuildInFlight && chunk.EditPriority >= drop.WaitStamp;
	}

	/// <summary>
	/// Spawns every pending item drop whose chunk update has finished. Each drop
	/// waits on the chunk the removed voxel sat in (checked self-containedly
	/// against the edit stamp it was queued under), so the drop appears only
	/// once that chunk's surface is solid and visible — never resting in
	/// mid-air. A drop older than <see cref="MaxPendingDropAgeSeconds"/> is
	/// spawned unconditionally so it can never be held forever. Called from
	/// <see cref="_Process"/> after the per-frame rebuild budget runs.
	/// </summary>
	private void FlushPendingDrops(Vector2I playerChunk)
	{
		if (PendingDrops.Count == 0)
		{
			return;
		}

		double now = Time.GetTicksMsec() / 1000.0;

		for (int i = PendingDrops.Count - 1; i >= 0; i--)
		{
			var drop = PendingDrops[i];

			bool ready = ChunkUpdateFinished(drop);

			// Staleness safety net: never hold a drop longer than the cap, even
			// if its chunk is stuck (freed, or kept dirty by unrelated work).
			if (!ready && (now - drop.QueuedTime) > MaxPendingDropAgeSeconds)
			{
				ready = true;
			}

			if (!ready)
			{
				continue;
			}

			PendingDrops.RemoveAt(i);
			VoxelBroken?.Invoke(drop.VoxelPos, drop.MaterialId);
		}
	}

	private void SetVoxel(Vector3I v, int material)
	{
		if (v.Y < 0 || v.Y >= ChunkHeight)
		{
			return;
		}

		Vector2I coord = ChunkCoordOf(v.X, v.Z);
		if (!_chunks.TryGetValue(coord, out VoxelChunk chunk))
		{
			return;
		}

		int lx = v.X - chunk.Coord.X * ChunkSizeXZ;
		int lz = v.Z - chunk.Coord.Y * ChunkSizeXZ;
		chunk.Data.Set(lx, v.Y, lz, (byte)material);
		// A placed/broken voxel starts at full health.
		if (material == VoxelMaterialIds.Air)
		{
			chunk.Data.SetHealth(lx, v.Y, lz, 0f); // clears the stored (damaged) value
		}
		else
		{
			chunk.Data.SetHealth(lx, v.Y, lz, VoxelHealth.MaxHealth);
		}

		// Record the edit for persistence (latest value wins). SetVoxel is the
		// single choke point: AddSphere/RemoveSphere/BreakVoxel all funnel
		// through it, so this one write captures every player edit. A replaced
		// voxel is at full health again, so drop any stale damaged entry.
		_edits[v] = (byte)material;
		_damagedHealth.Remove(v);
	}

	/// <summary>
	/// Creates a chunk node and kicks off its terrain generation on the thread
	/// pool. The chunk node is added to the tree and <c>_chunks</c> immediately
	/// (so it is a valid neighbour sample for seam culling) and its budget is
	/// counted as in-flight, but its voxel data is filled asynchronously.
	/// <c>Rebuild</c> will no-op on it until <c>DataReady</c> becomes true.
	/// </summary>
	private void StartGeneration(Vector2I coord)
	{
		var data = new ChunkData(ChunkSizeXZ, ChunkHeight, ChunkSizeXZ);
		var chunk = new VoxelChunk { Name = $"Chunk_{coord.X}_{coord.Y}" };
		chunk.Initialize(coord, data);
		_chunkRoot.AddChild(chunk);
		_chunks[coord] = chunk;
		_generatingBudget++;

		int wx0 = coord.X * ChunkSizeXZ;
		int wz0 = coord.Y * ChunkSizeXZ;
		TerrainGenerator.GenerateAsync(data, wx0, wz0, _config, _chunks).ContinueWith(t =>
		{
			if (t.IsFaulted)
			{
				GD.PushError($"[VoxelWorld] Terrain generation failed for chunk {coord}: {t.Exception}");
				return;
			}
			// Marshal back to the main thread: the scene-tree and <c>_chunks</c>
			// state below must be touched on the engine thread. `CallDeferred`
			// runs the named instance method on the next process frame with
			// the supplied arguments.
			CallDeferred(nameof(OnGenerationComplete), new Variant[] { coord, chunk });
		}, TaskScheduler.Default);
	}

	/// <summary>
	/// Starts the nearest pending chunk generations to fill any free concurrent
	/// slots. Pops from <see cref="_genQueue"/> in nearest-first order (re-sorting
	/// by the player's CURRENT position, since the player may have moved since the
	/// coord was enqueued) and calls <see cref="CreateChunk"/> until either the
	/// queue is empty or <see cref="MaxConcurrentGenerations"/> are in flight.
	/// Called every frame by the load loop and immediately from
	/// <see cref="OnGenerationComplete"/> so a freed slot is refilled the same frame
	/// a generation finishes — this is what keeps the in-flight set pinned to the
	/// nearest unfinished chunks.
	/// </summary>
	private void PumpGenerationQueue(Vector2I playerChunk)
	{
		if (_genQueue.Count == 0)
		{
			return;
		}

		// Re-sort by the player's current position (the queue may hold coords
		// enqueued a few frames ago at a different player location). Cheap: the
		// queue is bounded by the render distance and is drained as fast as it
		// fills.
		_genQueue.Sort((a, b) => DistanceSq(a.Coord, playerChunk).CompareTo(DistanceSq(b.Coord, playerChunk)));

		while (_genQueue.Count > 0 && _generatingBudget < MaxConcurrentGenerations)
		{
			// Take the nearest, but skip any that became loaded (e.g. freed and
			// re-created, or a stale entry) — they are already handled.
			var (coord, _) = _genQueue[0];
			_genQueue.RemoveAt(0);
			_genQueued.Remove(coord);
			if (_chunks.ContainsKey(coord))
			{
				continue;
			}
			CreateChunk(coord);
		}
	}

	/// <summary>
	/// Invoked on the main thread (via <c>CallDeferred</c>) when a chunk's
	/// async terrain generation finishes. Marks the chunk's data ready, re-applies
	/// any saved edits that belong to it (world persistence), and re-meshes its
	/// neighbours so they close the MC seam with real data.
	/// </summary>
	private void OnGenerationComplete(Vector2I coord, VoxelChunk chunk)
	{
		// This runs on the main thread via CallDeferred, so it can fire AFTER
		// the world has left the tree (e.g. the player quit to the menu while a
		// chunk was still generating). GetTree() throws on a node that is not in
		// the tree, so guard with IsInsideTree() and bail out: there is nothing
		// left to mesh or persist into once the world is gone.
		if (!IsInsideTree())
		{
			_generatingBudget = Mathf.Max(0, _generatingBudget - 1);
			return;
		}

		_generatingBudget = Mathf.Max(0, _generatingBudget - 1);
		// A slot just freed up: immediately start the next nearest pending chunk
		// so the in-flight set stays pinned to the nearest unfinished chunks
		// without waiting a full frame.
		var playerForPump = GetTree()?.GetFirstNodeInGroup("player") as Node3D;
		PumpGenerationQueue(ChunkCoordOf(playerForPump?.GlobalPosition ?? Vector3.Zero));
		if (!_chunks.TryGetValue(coord, out VoxelChunk current) || current != chunk)
		{
			// Chunk was freed while generation was in flight.
			return;
		}
		current.DataReady = true;

		// World persistence: re-apply this chunk's saved edits + health NOW,
		// while its data is ready and before the budget loop meshes it. This
		// stamps the chunk + 1-ring with a fresh edit priority so the edited
		// area rebuilds first. A no-op when there is no pending snapshot
		// (a fresh world with no save).
		ApplyLoadedChunk(current);

		// A neighbour's foliage stage may have queued canopy-overhang voxels for
		// THIS chunk (it was still generating when they ran). Apply any that are
		// left — the own chunk's foliage stage already drained once, so this
		// second pass only catches a cross-chunk race where a neighbour queued
		// just after. If anything landed, the chunk needs a mesh rebuild.
		if (TerrainGenerator.DrainOverhangs(coord, current.Data))
		{
			current.NeedsMesh = true;
			current.EditPriority = ++_editPriorityCounter;
		}

		// Re-mesh the 8 neighbours so they close the MC seam with real data AND
		// pick up any canopy overhang voxels this chunk's foliage stage wrote
		// into their buffers (an overhang can only land in one of these 8).
		MarkNeighboursForRebuild(coord);
	}

	/// <summary>
	/// Creates a chunk and kicks off its terrain generation on the thread pool
	/// (see <see cref="StartGeneration"/>). Called by the <c>_Process</c> load
	/// loop; the body is now a single delegation to it.
	/// </summary>
	private void CreateChunk(Vector2I coord)
	{
		StartGeneration(coord);
	}

	/// <summary>
	/// Yields chunk coordinates around <paramref name="center"/> in spiral order:
	/// the center chunk first, then each expanding square ring (Chebyshev radius
	/// 1, 2, … up to <paramref name="radius"/>) traversed clockwise. This is the
	/// load order used by <c>_Process</c> so the chunk under the player generates
	/// before its neighbours and nearer rings generate before farther ones.
	/// </summary>
	private static IEnumerable<Vector2I> SpiralCoords(Vector2I center, int radius)
	{
		yield return center;

		for (int r = 1; r <= radius; r++)
		{
			// Top edge: (-r, -r) → (r, -r)
			for (int x = -r; x <= r; x++)
			{
				yield return center + new Vector2I(x, -r);
			}

			// Right edge: (r, -r+1) → (r, r)
			for (int z = -r + 1; z <= r; z++)
			{
				yield return center + new Vector2I(r, z);
			}

			// Bottom edge: (r-1, r) → (-r, r)
			for (int x = r - 1; x >= -r; x--)
			{
				yield return center + new Vector2I(x, r);
			}

			// Left edge: (-r, r-1) → (-r, -r+1)
			for (int z = r - 1; z >= -r + 1; z--)
			{
				yield return center + new Vector2I(-r, z);
			}
		}
	}

	private void FreeChunk(VoxelChunk chunk)
	{
		Vector2I coord = chunk.Coord;
		// This chunk's data is about to disappear: any loaded neighbour whose
		// seam referenced it must re-mesh to drop the (now stale) wall and
		// re-open that edge at the boundary.
		MarkNeighboursForRebuild(coord);
		_chunks.Remove(coord);
		// Drop any pending generation-queue entry for this coord so it can be
		// re-enqueued if it comes back in range.
		_genQueued.Remove(coord);
		for (int i = _genQueue.Count - 1; i >= 0; i--)
		{
			if (_genQueue[i].Coord == coord)
			{
				_genQueue.RemoveAt(i);
			}
		}
		chunk.QueueFree();
	}

	/// <summary>
	/// Gives a collision shape to chunks within <see cref="CollisionRadiusChunks"/> of
	/// the player and clears it from the rest.
	///
	/// Incremental: only the chunks inside the collision radius can possibly have
	/// collision enabled, so only they are touched each frame. The per-frame work is
	/// O(radius²) — a constant ~49 chunks — instead of O(loaded chunks), which at a
	/// 16-chunk render distance was ~580 chunks and (with the per-chunk NeedsMesh
	/// property read crossing the Godot binding) cost ~13 ms/frame. Far chunks are
	/// left as-is (they stay disabled) and are only re-enabled when they enter the
	/// radius or are freed (FreeChunk → MarkNeighboursForRebuild path).
	/// </summary>
	private void UpdateLocalCollision(Vector2I playerChunk)
	{
		for (int dx = -CollisionRadiusChunks; dx <= CollisionRadiusChunks; dx++)
		{
			for (int dz = -CollisionRadiusChunks; dz <= CollisionRadiusChunks; dz++)
			{
				if (!_chunks.TryGetValue(playerChunk + new Vector2I(dx, dz), out VoxelChunk chunk))
				{
					continue;
				}

				// A near chunk always has collision ON: either it's within the
				// radius (the whole point) or it was just rebuilt and is still
				// flagged NeedsMesh (the player must never stand on invisible
				// ground while its mesh/collision is in flight).
				chunk.SetCollisionEnabled(true);
			}
		}
	}

	// ------------------------------------------------------------------
	// Distant-Horizons-style LOD (visual-only distant terrain)
	// ------------------------------------------------------------------

	/// <summary>
	/// The LOD level the player wants at <paramref name="distChunks"/>
	/// (base-chunk distance from the player): 0 inside the normal render
	/// distance (full-res chunks own it), otherwise the logarithmic
	/// Distant-Horizons band — level k covers [R * B^(k-1), R * B^k) where
	/// R is <see cref="RenderDistanceChunks"/> and B is
	/// <see cref="LodDistanceBase"/>. Clamped to [1, <see cref="LodLevels"/>].
	/// </summary>
	private int DesiredLodLevel(float distChunks)
	{
		if (distChunks < RenderDistanceChunks)
		{
			return 0;
		}

		// log_B(dist / R) via natural logs (System.Math.Log with a base is
		// not in the Godot Mathf surface we rely on).
		float t = (float)(System.Math.Log(distChunks / RenderDistanceChunks) / System.Math.Log(LodDistanceBase));
		return Mathf.Clamp(Mathf.CeilToInt(t), 1, LodLevels);
	}

	/// <summary>
	/// Keeps the distant LOD regions in sync with the player: builds the
	/// regions the logarithmic distance rule wants, hides the ones a finer
	/// level now covers (gap-fill), and frees the ones well out of range.
	///
	/// Levels are scanned FINEST-FIRST (ascending k). A coarser region's
	/// "are my finer children all built?" gap-fill check therefore runs after
	/// those children (level k-1) were decided this frame, so the parent
	/// covers any gap until the finer level loads and then hides — the classic
	/// coarse-to-fine reveal with no holes.
	///
	/// The scan window is the ANNULUS of the LOD band, not a box out to the
	/// LOD render distance: a region is only ever rendered while its center
	/// is in [RenderDistanceChunks, LodRenderDistanceChunks), so anything
	/// inside the inner radius (full-res chunks own that space — including
	/// the area just behind the player as they move) or beyond the outer
	/// radius is never touched here. Scanning a full box out to
	/// LodRenderDistanceChunks would (a) create/keep level-1 regions whose
	/// centers sit 1–2 chunks inside the normal render radius — low-res
	/// heightfields popping in right behind the player and z-fighting the
	/// full-res chunks — and (b) visit O(LodRenderDistanceChunks²) region
	/// cells per level per frame for the handful in the band.
	///
	/// The annulus is walked as the four edges of its bounding square, whose
	/// inner/outer radii are the tight Chebyshev bounds of the Euclidean band
	/// (see the corner-margin derivation in the body): every cell inside the
	/// inner square is provably within the normal render distance and every
	/// cell outside the outer square is provably beyond the LOD render
	/// distance, so the interior/exterior are never visited without being in
	/// the same "no render" state the band test would assign them.
	/// </summary>
	private void UpdateLod(Vector3 playerPos)
	{
		// Player position in base-chunk FLOAT units so the distance bands
		// transition smoothly as the player moves within a chunk.
		Vector3 v = playerPos / VoxelSize / ChunkSizeXZ;
		float px = v.X, pz = v.Z;

		// Budget: count the builds already in flight, then cap new starts.
		int inFlight = 0;
		foreach (LodChunk lod in _lodChunks.Values)
		{
			if (lod.BuildInFlight)
			{
				inFlight++;
			}
		}

		// Build candidates gathered during the per-level visibility walk below,
		// then scheduled NEAREST-FIRST across all levels (see the scheduling
		// block at the end of this method). Deferring the build start is what
		// makes distance order win over level/ring-walk order.
		var buildCandidates = new List<(LodChunk Lod, float DistSq)>();

		for (int k = 1; k <= LodLevels; k++)
		{
			int regionSizeChunks = 1 << k; // base chunks per side at this level
			int prx = Mathf.FloorToInt(px / regionSizeChunks);
			int prz = Mathf.FloorToInt(pz / regionSizeChunks);

			// Level-k band [R*B^(k-1), R*B^k) — the Euclidean distances this
			// level is responsible for. BOUND THE WALK BY THIS LEVEL'S OWN BAND
			// EDGES, not the global LodRenderDistanceChunks: a coarser level's
			// band ends well before the LOD distance, and walking every level
			// out to the LOD distance (the old code) made the per-frame scan
			// O(LodDistance²) per level — thousands of out-of-band cells visited
			// every frame for no reason.
			float bandInner = RenderDistanceChunks * (float)System.Math.Pow(LodDistanceBase, k - 1);
			float bandOuter = RenderDistanceChunks * (float)System.Math.Pow(LodDistanceBase, k);

			// Chebyshev span of the annulus the player's level-k band can
			// occupy (see method docs). The player sits at most
			// size*sqrt(2)/2 base-chunks from their own region's center
			// (region center = player + (size/2 - fx, size/2 - fz), with
			// fx, fz in [0, size)), so the band's Chebyshev reach is the
			// Euclidean band [bandInner, bandOuter] inflated/deflated by that
			// worst-case offset — exactly what the corner margins below compute:
			//   inner: largest radius r with (r+1)*size - corner < bandInner
			//          (every inner-square cell is provably < bandInner away).
			//   outer: smallest radius r with (r+1)*size - corner >= bandOuter
			//          (every outer-square cell is provably >= bandOuter away).
			// No band cell falls outside the walk, and no in-band region is
			// missed — including the ring just behind the normal render
			// radius, which the old box scan used to leave hidden.
			float corner = regionSizeChunks * (float)System.Math.Sqrt(2.0) * 0.5f;
			int inner = Mathf.FloorToInt((bandInner - corner) / regionSizeChunks) - 1;
			if (inner < 0)
			{
				inner = 0;
			}
			int outer = Mathf.CeilToInt((bandOuter + corner) / regionSizeChunks) - 1;

			// Walk only the four edges of the annulus's bounding square —
			// the band's radial thickness covers every interior cell's state,
			// so the O(span²) interior is skipped entirely. Corners are
			// emitted by two edges; dedupe with a small seen set.
			var seen = new HashSet<Vector2I>();
			for (int d = inner; d <= outer; d++)
			{
				// Top edge: (d, d) → (-d, d)
				for (int ox = d; ox >= -d; ox--)
				{
					VisitLodCell(k, prx + ox, prz + d, regionSizeChunks, px, pz, seen, buildCandidates);
				}
				// Right edge: (-d, d-1) → (-d, -d)
				for (int oz = d - 1; oz >= -d; oz--)
				{
					VisitLodCell(k, prx - d, prz + oz, regionSizeChunks, px, pz, seen, buildCandidates);
				}
				// Bottom edge: (-d+1, -d) → (d, -d)
				for (int ox = -d + 1; ox <= d; ox++)
				{
					VisitLodCell(k, prx + ox, prz - d, regionSizeChunks, px, pz, seen, buildCandidates);
				}
				// Left edge: (d, -d+1) → (d, d-1)
				for (int oz = -d + 1; oz <= d - 1; oz++)
				{
					VisitLodCell(k, prx + d, prz + oz, regionSizeChunks, px, pz, seen, buildCandidates);
				}
			}
		}

		// Free regions that no longer need to exist. A region's CENTER sits
		// size/2 from each edge, so its FARTHEST point is
		// centerDistance + size/2 from the player. Two cases:
		//  1. Beyond the LOD render distance (far side): even the NEAREST
		//     edge (centerDistance - size/2) is > LodRenderDistanceChunks away
		//     — past the horizon.
		//  2. Fully inside the normal render distance (behind the player): the
		//     FARTHEST point (centerDistance + size/2) is < RenderDistanceChunks
		//     away, so the WHOLE region is inside the full-res area. The walk's
		//     shouldRender is false for such a region (d2 < normalDistSq), so it
		//     can never render and only overlaps (z-fights) the full-res chunks.
		//
		// The behind test uses the FARTHEST point (centerDistance + size/2), NOT
		// the near corner (centerDistance - size/2). Using the near corner was
		// buggy: it freed regions the walk was creating at the same time
		// (shouldRender=false, centerDistance ≈ RenderDistanceChunks, where
		// nearCorner < R but farthest > R), so every LOD region was freed the
		// frame it spawned and LOD never rendered. centerDistance + size/2 < R
		// matches the walk's own geometry exactly — those regions are provably
		// never rendered, so freeing them oscillates nothing.
		// Enumerate a copy: FreeLod mutates the dictionary.
		foreach (KeyValuePair<(int, Vector2I), LodChunk> kv in _lodChunks.ToList())
		{
			int level = kv.Key.Item1;
			int regionSizeChunks = 1 << level;
			float half = regionSizeChunks * 0.5f;
			float cx = kv.Key.Item2.X * regionSizeChunks + regionSizeChunks / 2f;
			float cz = kv.Key.Item2.Y * regionSizeChunks + regionSizeChunks / 2f;
			float centerDist = (float)System.Math.Sqrt((cx - px) * (cx - px) + (cz - pz) * (cz - pz));
			bool far = centerDist - half > LodRenderDistanceChunks;
			bool fullyInsideFullRes = centerDist + half < RenderDistanceChunks;
			if (far || fullyInsideFullRes)
			{
				FreeLod(level, kv.Key.Item2);
			}
		}

		// Schedule this frame's LOD builds NEAREST-FIRST across all levels. The
		// visibility walk above recorded every region that needs a build; sorting
		// them by squared distance to the player (stable) and starting up to the
		// budget in that order is what makes the rings fill outward from the
		// player instead of by level/ring-walk order. A coarser (farther) region
		// can no longer grab a build slot before a nearer region that also needs
		// one. (The free pass ran first, so a region freed this frame is not
		// built.)
		if (buildCandidates.Count > 0)
		{
			buildCandidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
			foreach (var (lod, _) in buildCandidates)
			{
				if (inFlight >= MaxConcurrentLodBuilds)
				{
					break;
				}
				// The region may have been freed or already built since it was
				// recorded; only start a build for a live, unbuilt region.
				if (!lod.IsInsideTree() || lod.Built || lod.BuildInFlight)
				{
					continue;
				}
				lod.Rebuild(ChunkSizeXZ, _config, Palette, VoxelSize, LodMeshResolution, _chunks);
				inFlight++;
			}
		}
	}

	/// <summary>
	/// Decides and applies the render state of one level-<paramref name="level"/>
	/// region (called by <see cref="UpdateLod"/> for each annulus cell). Sets the
	/// region's visibility (and creates it when first needed) and, when a build
	/// is required, records it in <paramref name="buildCandidates"/>. The actual
	/// build is started later by <see cref="UpdateLod"/>'s nearest-first
	/// scheduling pass, so distance from the player — not level or ring-walk
	/// order — decides which regions build first.
	/// </summary>
	private void VisitLodCell(
		int level, int rx, int rz, int regionSizeChunks, float px, float pz,
		HashSet<Vector2I> seen, List<(LodChunk Lod, float DistSq)> buildCandidates)
	{
		var rc = new Vector2I(rx, rz);
		if (!seen.Add(rc))
		{
			return; // corner emitted by two edges of the same ring
		}

		// Region center in base-chunk units (drives the distance rule).
		float cx = rx * regionSizeChunks + regionSizeChunks / 2f;
		float cz = rz * regionSizeChunks + regionSizeChunks / 2f;
		float dxc = cx - px, dzc = cz - pz;
		float d2 = dxc * dxc + dzc * dzc;

		float lodDistSq = LodRenderDistanceChunks * (float)LodRenderDistanceChunks;
		float normalDistSq = RenderDistanceChunks * (float)RenderDistanceChunks;

		bool shouldRender;
		if (d2 >= lodDistSq || d2 < normalDistSq)
		{
			// Outside the LOD band: beyond the LOD render distance or inside
			// the normal render distance (full-res owns it). NOT a `continue`
			// — an existing region must still be hidden here (freed later by
			// the cleanup pass).
			shouldRender = false;
		}
		else
		{
			float d = Mathf.Sqrt(d2);
			int desired = DesiredLodLevel(d);
			if (desired == level)
			{
				shouldRender = true; // this is the level the distance wants
			}
			else if (desired < level)
			{
				// Finer than desired: gap-fill — this coarser region covers
				// the area until all 4 finer children are built.
				shouldRender = !AllLodChildrenBuilt(level, rc);
			}
			else
			{
				shouldRender = false; // a coarser region covers this area
			}
		}

		if (_lodChunks.TryGetValue((level, rc), out LodChunk lod))
		{
			// Only touch the node when the state actually changed. The annulus
			// walk re-visits every in-band region every frame (thousands of them
			// at a large LodRenderDistanceChunks), so re-assigning Visible — even
			// to its current value — would fire the engine's visibility-change
			// path (and, with multi-bounce occlusion, a shadow re-evaluation)
			// thousands of times per second for no visual change.
			if (lod.Visible != shouldRender)
			{
				lod.Visible = shouldRender;
			}
			if (shouldRender && !lod.Built && !lod.BuildInFlight)
			{
				buildCandidates.Add((lod, d2));
			}
		}
		else if (shouldRender)
		{
			lod = new LodChunk { Name = $"Lod_{level}_{rc.X}_{rc.Y}" };
			lod.Initialize(level, rc);
			lod.Visible = true;
			_lodRoot.AddChild(lod);
			_lodChunks[(level, rc)] = lod;
			if (!lod.Built && !lod.BuildInFlight)
			{
				buildCandidates.Add((lod, d2));
			}
		}
	}

	/// <summary>
	/// True when all 4 direct children (the finer level, <paramref name="level"/>-1)
	/// of a level-<paramref name="level"/> region exist and have built meshes —
	/// i.e. the finer level fully covers the parent's area and the parent can
	/// stop gap-filling. A level-k region contains a 2x2 of level-(k-1) regions
	/// at coords <c>rc*2 + offset</c>.
	/// </summary>
	private bool AllLodChildrenBuilt(int level, Vector2I rc)
	{
		if (level <= 1)
		{
			return false; // the finest LOD level has no finer children
		}

		int childLevel = level - 1;
		for (int off = 0; off < 4; off++)
		{
			int ox = off % 2;
			int oz = off / 2;
			var child = new Vector2I(rc.X * 2 + ox, rc.Y * 2 + oz);
			if (!_lodChunks.TryGetValue((childLevel, child), out LodChunk c) || !c.IsReadyToRender)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>Removes and frees one LOD region.</summary>
	private void FreeLod(int level, Vector2I rc)
	{
		if (_lodChunks.Remove((level, rc), out LodChunk lod))
		{
			lod.QueueFree();
		}
	}

	/// <summary>Frees every LOD region (e.g. when LOD is disabled at runtime).</summary>
	private void FreeAllLod()
	{
		if (_lodChunks.Count == 0)
		{
			return;
		}

		var all = _lodChunks.Values.ToList();
		_lodChunks.Clear();
		foreach (LodChunk lod in all)
		{
			lod.QueueFree();
		}
	}

	/// <summary>
	/// Flags every loaded orthogonal + diagonal neighbour of <paramref name="coord"/>
	/// for a mesh rebuild. A neighbour's +X/+Z boundary cells depend on whether
	/// <paramref name="coord"/> is loaded (see the ownership predicate in
	/// MeshBuilder and the solid queries below), so adding or removing a chunk
	/// changes which edge cells its neighbours draw — they must re-mesh to
	/// close or open the seam. Rebuilds are budget-managed (ChunksPerFrame).
	/// </summary>
	private void MarkNeighboursForRebuild(Vector2I coord)
	{
		for (int dx = -1; dx <= 1; dx++)
		{
			for (int dz = -1; dz <= 1; dz++)
			{
				if (dx == 0 && dz == 0)
				{
					continue;
				}

				if (_chunks.TryGetValue(coord + new Vector2I(dx, dz), out VoxelChunk neighbour))
				{
					neighbour.NeedsMesh = true;
				}
			}
		}
	}

	/// <summary>
	/// Returns the chunk's voxel data at the given chunk coordinate, or null
	/// if that chunk is not loaded OR its data is still generating. The
	/// Marching Cubes builder pads each chunk's field with its neighbours'
	/// boundary voxels; an in-flight neighbour's buffer is all air, so it
	/// must be reported as null — otherwise the MC boundary gate
	/// (neighbourQuery != null) treats the edge as closed and samples the
	/// empty buffer, producing a spurious isosurface wall on the seam.
	/// </summary>
	public ChunkData GetChunkData(Vector2I coord)
	{
		return TryGetGeneratedChunk(coord, out VoxelChunk chunk) ? chunk.Data : null;
	}

	/// <summary>
	/// Returns the fully-generated voxel data of the chunk at world voxel
	/// (wx0, wz0), or null when that chunk is unloaded or still generating.
	/// Static so generation workers (thread pool, no node access) can fetch
	/// neighbour data for seam-safe checks. Same rule as <see cref="GetChunkData"/>:
	/// an in-flight chunk's buffer is all air and must be reported as null.
	/// </summary>
	public static ChunkData GeneratedChunkData(
		int wx0,
		int wz0,
		Dictionary<Vector2I, VoxelChunk> chunks,
		int chunkSizeXZ)
	{
		Vector2I coord = new Vector2I(
			Mathf.FloorToInt(wx0 / (float)chunkSizeXZ),
			Mathf.FloorToInt(wz0 / (float)chunkSizeXZ));
		return chunks.TryGetValue(coord, out VoxelChunk chunk) && chunk.DataReady
			? chunk.Data
			: null;
	}

	private int DistanceSq(Vector2I a, Vector2I b)
	{
		int dx = a.X - b.X;
		int dy = a.Y - b.Y;
		return dx * dx + dy * dy;
	}

	private Vector2I ChunkCoordOf(Vector3 worldPos)
	{
		Vector3 v = worldPos / VoxelSize;
		return new Vector2I(
			FloorDiv(Mathf.FloorToInt(v.X), ChunkSizeXZ),
			FloorDiv(Mathf.FloorToInt(v.Z), ChunkSizeXZ));
	}

	private Vector2I ChunkCoordOf(int x, int z)
	{
		return new Vector2I(FloorDiv(x, ChunkSizeXZ), FloorDiv(z, ChunkSizeXZ));
	}

	private static int FloorDiv(int a, int b)
	{
		return Mathf.FloorToInt(a / (float)b);
	}

	private static float NextBoundary(float pos, float dir, int step)
	{
		if (dir == 0f)
		{
			return 1e30f;
		}

		float boundary = step > 0 ? Mathf.Ceil(pos) : Mathf.Floor(pos);
		return (boundary - pos) / dir;
	}

	/// <summary>
	/// Snaps degenerate export values back to their code defaults. A `= null` scene
	/// override loads as 0/false for value types; these are the ones that break chunking
	/// or spawning outright (the rest just change terrain shape). Fallbacks must match
	/// the property initializers above.
	/// </summary>
	private void SanitizeExports()
	{
		var fixes = new List<string>();

		if (ChunkSizeXZ <= 0) { ChunkSizeXZ = 16; fixes.Add("ChunkSizeXZ -> 16"); }
		if (ChunkHeight <= 0) { ChunkHeight = 48; fixes.Add("ChunkHeight -> 48"); }
		if (RenderDistanceChunks < 1) { RenderDistanceChunks = 4; fixes.Add("RenderDistanceChunks -> 4"); }
		if (ChunksPerFrame < 1) { ChunksPerFrame = 3; fixes.Add("ChunksPerFrame -> 3"); }
		if (VoxelSize <= 0f) { VoxelSize = 1.0f; fixes.Add("VoxelSize -> 1"); }
		if (LodLevels < 1) { LodLevels = 4; fixes.Add("LodLevels -> 4"); }
		if (LodRenderDistanceChunks < 1) { LodRenderDistanceChunks = 48; fixes.Add("LodRenderDistanceChunks -> 48"); }
		if (LodRenderDistanceChunks < RenderDistanceChunks) { LodRenderDistanceChunks = RenderDistanceChunks; fixes.Add($"LodRenderDistanceChunks -> {RenderDistanceChunks}"); }
		if (LodDistanceBase <= 1f) { LodDistanceBase = 2.0f; fixes.Add("LodDistanceBase -> 2"); }
		if (LodMeshResolution < 2) { LodMeshResolution = 8; fixes.Add("LodMeshResolution -> 8"); }

		if (fixes.Count > 0)
		{
			GD.PushWarning("[VoxelWorld] Degenerate export values restored to defaults: " + string.Join(", ", fixes));
		}
	}

	private GenConfig BuildConfig()
	{
		var heightNoise = new FastNoiseLite
		{
			Seed = Seed,
			NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
			Frequency = HeightFrequency,
			FractalOctaves = HeightOctaves,
			FractalGain = HeightPersistence,
			FractalLacunarity = HeightLacunarity,
		};

		var caveNoise = new FastNoiseLite
		{
			Seed = Seed + 101,
			NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
			Frequency = CaveFrequency,
			FractalOctaves = CaveOctaves,
			FractalGain = CavePersistence,
			FractalLacunarity = CaveLacunarity,
		};

		var forestNoise = new FastNoiseLite
		{
			Seed = Seed + 202,
			NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
			Frequency = ForestNoiseFrequency,
			FractalOctaves = ForestOctaves,
			FractalGain = ForestPersistence,
			FractalLacunarity = ForestLacunarity,
		};

		// Biome selection noise uses the WORLD SEED (same as the player asked
		// for) so the biome layout is deterministic per seed and changing the
		// world seed re-rolls the biome placement. The frequency/octaves below
		// still make it a distinct field from the height noise even though they
		// share the seed value.
		var biomeNoise = new FastNoiseLite
		{
			Seed = Seed,
			NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
			Frequency = BiomeNoiseFrequency,
			FractalOctaves = BiomeOctaves,
			FractalGain = BiomePersistence,
			FractalLacunarity = BiomeLacunarity,
		};

		var config = new GenConfig
		{
			Seed = Seed,
			ChunkHeight = ChunkHeight,
			BaseHeight = BaseHeight,
			HeightAmplitude = HeightAmplitude,
			DirtDepth = DirtDepth,
			CaveStoneDepth = CaveStoneDepth,
			HeightNoise = heightNoise,
			EnableCaves = EnableCaves,
			CaveThreshold = CaveThreshold,
			CaveCeiling = CaveCeiling,
			CaveFloorDepth = CaveFloorDepth,
			CaveNoise = caveNoise,
			ForestNoise = forestNoise,
			FoliageDensity = FoliageDensity,
			MaterialConfig = MaterialConfig,
			BiomeConfig = BiomeConfig,
			BiomeNoise = biomeNoise,
		};

		// Ores: one generator over the whole data-driven ore list (disabled
		// ores are skipped inside the generator).
		config.Structures.Add(new OreStructureGenerator(OreConfig));

		if (EnableRocks)
		{
			// To add more structure types, create another IStructureGenerator
			// implementation and append it here.
			config.Structures.Add(new RockStructureGenerator
			{
				Probability = RockProbability,
				CellSize = RockCellSize,
				MinRadius = RockMinRadius,
				MaxRadius = RockMaxRadius,
				Squash = RockSquash,
			});
		}

		return config;
	}
}
