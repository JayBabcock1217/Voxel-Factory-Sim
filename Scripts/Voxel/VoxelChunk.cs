using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FactorySim.Voxel;

/// <summary>
/// One chunk: voxel data + visual mesh + concave collision shape.
/// </summary>
public partial class VoxelChunk : Node3D
{
	public Vector2I Coord { get; private set; }
	public ChunkData Data { get; private set; }

	/// <summary>True when the mesh/collision need (re)building.</summary>
	public bool NeedsMesh { get; set; } = true;

	/// <summary>
	/// Rebuild priority for the world's per-frame mesh budget: higher means the
	/// chunk was edited more recently, so it gets rebuilt sooner. Set by
	/// <c>VoxelWorld.FillSphere</c>; 0 means "never edited" and such chunks
	/// sort after edited ones. Not an export, purely internal.
	/// </summary>
	public int EditPriority { get; set; } = 0;

	/// <summary>
	/// True once the chunk's voxel data has been generated and is safe to read.
	/// Chunks created by <c>VoxelWorld.StartGeneration</c> start with this
	/// <c>false</c> (data still in flight on the thread pool) and have it set to
	/// true by <c>VoxelWorld.OnGenerationComplete</c>. <see cref="Rebuild"/>
	/// no-ops while this is <c>false</c> so the mesh/collision are never built
	/// from uninitialised data.
	/// </summary>
	public bool DataReady { get; set; } = false;

	private MeshInstance3D _meshInstance;
	private CollisionShape3D _shape;
	private ConcavePolygonShape3D _builtShape;
	private bool _collisionReady;

	/// <summary>The chunk's visual mesh instance (exposed for rendering settings like wireframe).</summary>
	public MeshInstance3D MeshInstance => _meshInstance;

	/// <summary>The last collision-enabled state actually applied to the shape.</summary>
	public bool CollisionEnabled { get; private set; }

	/// <summary>
	/// True while this chunk's async mesh/collision build is in flight (from
	/// the moment <see cref="Rebuild"/> launches the worker until
	/// <see cref="ApplyBuildResult"/> clears it). A chunk is "updated" — safe
	/// for a drop to land on — only when <see cref="NeedsMesh"/> is false AND
	/// this is false.
	/// </summary>
	public bool MeshBuildInFlight => _meshBuild != null;

	/// <summary>
	/// The mesh/collision build currently in flight on the thread pool, or
	/// <c>null</c> when idle. Set and cleared only on the main thread; the
	/// worker task writes only to the pending-result fields below and marshals
	/// completion back via <c>CallDeferred</c>. While non-null the budget loop
	/// skips the chunk (see <see cref="Rebuild"/>).
	/// </summary>
	private Task _meshBuild;

	// Pending results produced by the in-flight CPU build. Written by the worker
	// task, read on the main thread only after the task has completed (the task
	// sets them before calling CallDeferred, which runs on a later frame), so
	// this is safe without a lock.
	private ArrayMesh _pendingMesh;
	private List<Vector3> _pendingCollision;

	public void Initialize(Vector2I coord, ChunkData data)
	{
		Coord = coord;
		Data = data;

		_meshInstance = new MeshInstance3D { Name = "Mesh" };
		// Terrain lives on RENDER LAYER 2 (see VoxelWorld.TerrainRenderLayer). The
		// DirectionalLight3D's shadow Caster Mask is layer 1 only, so the terrain
		// RECEIVES the sun's real-time shadow (the Caster Mask does not gate
		// receiving) but does NOT CAST one — only layer-1 entities (the player,
		// machines, drops) cast. Keeps the huge voxel terrain out of the shadow
		// map while still showing a crisp character shadow on the ground.
		// Render layer: GeometryInstance3D exposes the bitmask as `Layers` in C#
		// (maps to the `layer` scene property). Layer 2 = 1 << 1.
		_meshInstance.Layers = VoxelWorld.TerrainRenderLayer;
		AddChild(_meshInstance);

		var body = new StaticBody3D { Name = "Collision" };
		AddChild(body);

		_shape = new CollisionShape3D { Name = "Shape" };
		body.AddChild(_shape);
	}

	/// <summary>
	/// Starts rebuilding this chunk's visual mesh and per-chunk concave
	/// collision shape. The heavy CPU work (voxel math for the visual mesh +
	/// the collision triangles) runs on the thread pool; the main thread only
	/// wires the prebuilt <see cref="ArrayMesh"/> and shape into the node when
	/// the build completes (cheap reference assignments, marshaled back via
	/// <c>CallDeferred</c>). While a build is in flight this method no-ops, so
	/// the budget loop can call it every frame without starting duplicate
	/// builds. The collision surface is the visual surface exactly (both mesh
	/// modes emit their visual triangles into the collision soup).
	/// </summary>
	public void Rebuild(VoxelWorld world)
	{
		// Guard: mesh + collision can't be built until voxel data has been
		// generated, and we can't start a second build while one is in flight.
		// DataReady goes true when generation finishes (OnGenerationComplete);
		// _meshBuild is non-null from launch until ApplyBuildResult clears it.
		if (!DataReady || _meshBuild != null)
		{
			return;
		}

		int cs = world.ChunkSizeXZ;
		int wx0 = Coord.X * cs;
		int wz0 = Coord.Y * cs;

		// Snapshot the immutable inputs the worker needs. The voxel data (Data)
		// is shared; it is only mutated by edits, which set NeedsMesh + a higher
		// EditPriority and never run a concurrent build on this chunk (Rebuild
		// no-ops while _meshBuild is in flight). So the worker reads a stable
		// dataset: generation is done (DataReady) and no edit can start a
		// competing build on this chunk.
		var data = Data;
		var palette = world.Palette;
		float voxelSize = world.VoxelSize;
		VoxelMeshMode mode = world.MeshMode;
		Vector2I coord = Coord;
		System.Func<int, int, int, bool> solidQuery = world.VoxelSolidForMesh;
		// SNAPSHOT the 8 neighbours' voxel data on the MAIN thread (here) into a
		// plain dictionary, and give the worker a closure over THAT snapshot —
		// not the live world.GetChunkData. The build runs on the worker pool while
		// the main thread keeps generating/editing chunks; a live read lets the
		// worker see _chunks/ChunkData mid-mutation, so one build's light field
		// can disagree with its neighbour's at their shared seam (a persistent
		// brightness step along the chunk grid). A frozen snapshot makes both
		// sides of every seam compute from the same neighbour state. The centre
		// is included because the wide light field also reads the chunk's own
		// data through the same query (it short-circuits to `chunk`, but the
		// snapshot keeps the code path uniform and race-free).
		var neighbourSnapshot = world.SnapshotNeighbours(Coord, includeCentre: true);
		System.Func<Vector2I, ChunkData> neighborQuery = c =>
			neighbourSnapshot.TryGetValue(c, out ChunkData d) ? d : null;
		// The material-blend query (Faces mode only): null when blending is off.
		System.Func<int, int, int, byte> materialQuery = world.Palette.BlendEnabled
			? world.VoxelMaterialForMesh
			: null;
		// The block-light query: each voxel's own light-source level (0 = none).
		// Seeds the sky-light flood fill so placed torches/glowstones light
		// their neighbourhood, including across chunk seams.
		System.Func<int, int, int, byte> blockLightQuery = world.VoxelBlockLightForMesh;
		// The wide light field (Faces mode) reads neighbouring chunks' ChunkData
		// directly to gather a 3x3-chunk region; use the SAME frozen snapshot the
		// MC path uses so the two never see different neighbour states.
		System.Func<Vector2I, ChunkData> neighborChunkQuery = neighborQuery;
		// The darkness floor: lifts the darkest spots above black. Snapshot it
		// here so the worker uses a stable value for the whole build.
		float minLight = world.MinLight;
		// The max light level (open sky seeds at this; light decays 1/voxel to 0).
		int maxLight = world.MaxLightLevel;
		// The falloff curve: stretches the visible brightness gradient so cave
		// interiors stay readable instead of hard-black beyond the light range.
		float lightFalloff = world.LightFalloff;
		// Debug: one-shot vertex-light histogram (auto-clears after first dump).
		bool debugVertexLight = world.DebugVertexLight && VoxelWorld.VertexLightDumpPending;
		if (debugVertexLight)
		{
			VoxelWorld.VertexLightDumpPending = false;
		}

		// A launched build is a chunk update in progress: treat the chunk as
		// dirty until ApplyBuildResult clears the flag, so a drop queued for
		// this chunk waits the whole rebuild, not just its budgeted slot.
		NeedsMesh = true;
		_meshBuild = Task.Run(() =>
		{
			try
			{
				// All the heavy voxel math happens here, off the main thread.
				// Collision is the visual surface exactly: MeshBuilder.Build
				// emits the visual triangles (face quads in Faces mode, the MC
				// isosurface in MarchingCubes mode) into the collision soup, so
				// the player stands on exactly what is drawn.
				var (visual, collision) = MeshBuilder.Build(data, wx0, wz0, voxelSize, palette, mode, coord, solidQuery, neighborQuery, materialQuery, blockLightQuery, neighborChunkQuery, minLight, maxLight, lightFalloff, debugVertexLight);

				// Store the prebuilt results for the main thread. Written here,
				// read only in ApplyBuildResult after the task completes (happens-
				// before via the task completion + the deferred call on a later
				// frame). No lock needed.
				_pendingMesh = visual;
				_pendingCollision = collision;

				// Marshal the cheap engine wiring back to the main thread.
				// CallDeferred resolves the method on THIS object (the chunk,
				// where ApplyBuildResult lives) and runs it on the main thread
				// next frame. Same pattern VoxelWorld.StartGeneration uses to
				// marshal generation completion back.
				CallDeferred(nameof(ApplyBuildResult));
			}
			catch (Exception ex)
			{
				// A build failure must not silently leave the chunk without a
				// mesh (it would just never appear). Log it and still marshal a
				// no-op result back so _meshBuild clears and the chunk can be
				// retried.
				GD.PushError($"[VoxelChunk] Mesh build failed for {Coord}: {ex}");
				CallDeferred(nameof(ApplyBuildResult));
			}
		});
	}

	/// <summary>
	/// Called on the main thread (via <c>CallDeferred</c>) when this chunk's
	/// async CPU build finishes. Assigns the prebuilt mesh + collision shape
	/// into the node and marks the chunk solid. Cheap: a couple of reference
	/// assignments plus one <c>ConcavePolygonShape3D.SetFaces</c> over the
	/// already-computed triangle list.
	/// </summary>
	private void ApplyBuildResult()
	{
		_meshBuild = null;

		bool appliedMesh = _pendingMesh != null;
		if (appliedMesh)
		{
			_meshInstance.Mesh = _pendingMesh;
			_pendingMesh = null;
		}

		if (_pendingCollision != null)
		{
			var shape = new ConcavePolygonShape3D();
			shape.SetFaces(_pendingCollision.ToArray());
			_pendingCollision = null;
			_builtShape = shape;
			_collisionReady = true;
			_shape.Shape = _builtShape;
		}

		// Only clear the dirty flag when a mesh was actually applied. If the
		// build threw (see Rebuild's catch), keep NeedsMesh true so the world's
		// per-frame budget retries the chunk instead of leaving it empty.
		if (appliedMesh)
		{
			NeedsMesh = false;
		}
	}

	/// <summary>
	/// Assigns the per-chunk concave collision shape when <paramref name="enabled"/> is
	/// true, or removes it (nulls the shape) when false. The shape is (re)built by
	/// <see cref="Rebuild"/> and cached; toggling is O(1).
	/// </summary>
	public void SetCollisionEnabled(bool enabled)
	{
		if (!_collisionReady)
		{
			return;
		}
		// Only reassign when the state actually changed. The world calls this for
		// EVERY loaded chunk every frame; reassigning shape.Shape — even to the
		// same reference — re-runs Jolt's broad-phase add/remove for the body,
		// which is the dominant physics cost at a 16-chunk render distance.
		if (CollisionEnabled == enabled)
		{
			return;
		}
		CollisionEnabled = enabled;
		_shape.Shape = enabled ? _builtShape : null;
	}
}
