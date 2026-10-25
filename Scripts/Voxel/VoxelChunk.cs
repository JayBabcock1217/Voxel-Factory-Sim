using Godot;
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
		System.Func<Vector2I, ChunkData> neighborQuery = world.GetChunkData;
		// The material-blend query (Faces mode only): null when blending is off.
		System.Func<int, int, int, byte> materialQuery = world.Palette.BlendEnabled
			? world.VoxelMaterialForMesh
			: null;

		// A launched build is a chunk update in progress: treat the chunk as
		// dirty until ApplyBuildResult clears the flag, so a drop queued for
		// this chunk waits the whole rebuild, not just its budgeted slot.
		NeedsMesh = true;
		_meshBuild = Task.Run(() =>
		{
			// All the heavy voxel math happens here, off the main thread.
			// Collision is the visual surface exactly: MeshBuilder.Build emits
			// the visual triangles (face quads in Faces mode, the MC isosurface
			// in MarchingCubes mode) into the collision soup, so the player
			// stands on exactly what is drawn.
			var (visual, collision) = MeshBuilder.Build(data, wx0, wz0, voxelSize, palette, mode, coord, solidQuery, neighborQuery, materialQuery);

			// Store the prebuilt results for the main thread. Written here,
			// read only in ApplyBuildResult after the task completes (happens-
			// before via the task completion + the deferred call on a later
			// frame). No lock needed.
			_pendingMesh = visual;
			_pendingCollision = collision;

			// Marshal the cheap engine wiring back to the main thread. CallDeferred
			// resolves the method on THIS object (the chunk, where ApplyBuildResult
			// lives) and runs it on the main thread next frame. Same pattern
			// VoxelWorld.StartGeneration uses to marshal generation completion back.
			CallDeferred(nameof(ApplyBuildResult));
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

		if (_pendingMesh != null)
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

		NeedsMesh = false;
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
