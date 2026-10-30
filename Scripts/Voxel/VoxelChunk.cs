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
	/// True once this chunk has a live concave collision shape (built by
	/// <see cref="Rebuild"/>/apply). The spawn gate requires this in addition to
	/// the mesh being built, so the player never gets physics (and can't fall
	/// through) before the ground under it can actually collide.
	/// </summary>
	public bool CollisionReady => _collisionReady;

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
	// Pending light field produced by the in-flight full build (written by the
	// worker, committed to _cachedLightField in ApplyBuildResult on the main
	// thread — same happens-before safety as _pendingMesh).
	private float[] _pendingLight;
	private byte[] _pendingBlockLit;
	// Option B lighting: the in-flight full build's wide SOLID|AIR + BLOCK-LIGHT
	// volumes, committed to _cachedWideSolid/_cachedWideBlock in ApplyBuildResult
	// (same happens-before safety as _pendingLight).
	private byte[] _pendingWideSolid;
	private byte[] _pendingWideBlock;
	// Option B: set by the async Rebuild when sub-cell mode is on, telling
	// ApplyBuildResult (main thread) to run RebuildAllSubCells (the node wiring
	// for the sub-cell meshes + collision must happen on the main thread). The
	// worker only computes the light field; the actual sub-cell MC builds run on
	// the main thread in ApplyBuildResult (they're small, ~64 × single-digit ms,
	// spread over the deferred apply — acceptable for a first-build).
	private bool _pendingSubCellBuild;
	// Option B: the prebuilt sub-cell results (cell origin + mesh + collision
	// triangles) computed by the worker, wired to nodes on the main thread by
	// ApplyBuildResult. Written by the worker, read on the main thread after the
	// task completes (happens-before via the deferred call).
	private List<(int x, int y, int z, ArrayMesh mesh, List<Vector3> collision)> _pendingSubCells;

	// Path 1 (light decoupling): the last COMPUTED wide light field for this
	// chunk, ALREADY response-curved (the curve is applied once when the field
	// is computed and cached, so the instant path can reuse it WITHOUT re-running
	// the ~560k Math.Pow curve calls), plus its blockLit mask. The full (async)
	// build computes + curves + stores it; the synchronous INSTANT edit path
	// reuses it to skip BOTH the flood fill and the curve, so a placed/broken
	// voxel appears immediately with its (possibly one-edit-stale) lighting, and
	// the fresh light catches up on the next full rebuild. Written on the main
	// thread only.
	private float[] _cachedLightField;
	private byte[] _cachedBlockLit;
	// The last local recompute's CHANGED voxels (wide-field lattice indices), so
	// the instant-edit path can decide which sub-cells' vertices actually
	// changed light — and skip the expensive MC re-mesh for sub-cells whose
	// sampled light is unchanged (the Minecraft-style "only re-mesh what
	// changed" optimisation). Wide indices, matching _cachedLightField.
	private int[] _lightChangedWide;
	// Option B lighting: the wide light field's SOLID|AIR + BLOCK-LIGHT volumes
	// (the inputs to SkyLight.Compute/ComputeLocal), cached so the instant-edit
	// local recompute does NOT rebuild the full 385k-voxel volume on every edit.
	// They are immutable for a given solid+source state; a geometry edit only
	// changes the edited voxels' values (PatchedVoxels), and a fresh async full
	// rebuild recomputes + replaces them. null until the first build.
	private byte[] _cachedWideSolid;
	private byte[] _cachedWideBlock;
	// Option B lighting: the most-recent edited voxel (chunk-local) + its
	// material + block-light level, recorded by the instant-edit path so
	// RecomputeLightLocal can patch the cached wide volumes at just that voxel
	// (instead of rebuilding the full 385k-voxel volume). Cleared after use.
	private Vector3I _patchVoxel;
	private int _patchMaterial;
	private byte _patchLight;
	private bool _hasPatch;

	// ------------------------------------------------------------------
	// Sub-cell (dirty-region) meshing — Option B (see VoxelWorld.SubCellMeshing).
	// When enabled, the chunk's mesh is kept as a grid of small sub-cells; an
	// edit rebuilds only the sub-cell(s) it touches (tiny MC build, single-digit
	// ms) instead of the whole chunk (~20 ms). Each sub-cell has its own
	// MeshInstance3D + concave CollisionShape3D (collision is ALWAYS rebuilt for
	// the touched sub-cell, so physics stays correct). The whole-chunk
	// _meshInstance/_shape are unused in this mode (kept for the fallback path).
	// ------------------------------------------------------------------
	private readonly Dictionary<int, SubCellNode> _subCells = new();

	/// <summary>
	/// One sub-cell's visual + collision nodes. Created lazily when a sub-cell
	/// first gets geometry. <c>cellIndex</c> is 0..CellsPerChunk-1 (see
	/// <see cref="VoxelWorld.CellIndex"/>); <c>cellX/Y/Z</c> are the cell's local
	/// voxel origin (0..ChunkSize-1 per axis).
	/// </summary>
	private sealed class SubCellNode
	{
		public int CellIndex;
		public int CellX, CellY, CellZ;
		public MeshInstance3D Mesh;
		// The physics body for this sub-cell. Created LAZILY and only while the
		// chunk is within the player's collision radius (see
		// SetSubCellCollisionEnabled): a 16-chunk render distance would otherwise
		// create ~76k StaticBody3Ds (64/chunk), far past Jolt's 10240 body cap.
		// The mesh node is always present (visual is always built).
		public StaticBody3D Body;
		public CollisionShape3D Shape;
		public ConcavePolygonShape3D BuiltShape;
		public bool HasGeometry;
	}

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
	/// Gets or lazily creates the sub-cell node for a sub-cell's local voxel
	/// origin (cellX, cellY, cellZ). The node always holds a MeshInstance3D
	/// (visual, always built). The physics body + collision shape are created
	/// LAZILY by <see cref="SetSubCellCollisionEnabled"/> and only while the
	/// chunk is within the player's collision radius — a 16-chunk render
	/// distance would otherwise create ~76k StaticBody3Ds (64 per chunk), far
	/// past Jolt's 10240 body cap.
	/// </summary>
	private SubCellNode GetOrCreateSubCell(int cellX, int cellY, int cellZ)
	{
		int idx = VoxelWorld.CellIndex(cellX, cellY, cellZ);
		if (_subCells.TryGetValue(idx, out SubCellNode existing))
		{
			return existing;
		}
		var node = new SubCellNode
		{
			CellIndex = idx,
			CellX = cellX,
			CellY = cellY,
			CellZ = cellZ,
		};
		node.Mesh = new MeshInstance3D { Name = $"Cell_{idx}" };
		node.Mesh.Layers = VoxelWorld.TerrainRenderLayer;
		AddChild(node.Mesh);

		_subCells[idx] = node;
		return node;
	}

	/// <summary>
	/// Attaches (or detaches) the collision shape of EVERY sub-cell of this
	/// chunk. Called every frame for chunks within the player's collision radius
	/// (enabled = true); the rest are skipped, which is what keeps their far
	/// sub-cells body-less and the total Jolt body count under the cap. When
	/// enabled, each sub-cell's body is created lazily (only if it has geometry)
	/// and its built shape is attached, so the player collides with the sub-cell
	/// terrain. This is the sub-cell analogue of <see cref="SetCollisionEnabled"/>.
	/// </summary>
	public void SetSubCellCollisionEnabled(bool enabled)
	{
		foreach (var node in _subCells.Values)
		{
			SetSubCellCollisionEnabled(node.CellX, node.CellY, node.CellZ, enabled);
		}
	}

	/// <summary>
	/// Ensures one sub-cell's physics body exists when <paramref name="enabled"/>
	/// is true (creating it lazily) and applies its built shape, or clears the
	/// shape when disabled. Only in-radius chunks ever call this with enabled =
	/// true, so only they pay for a Jolt body.
	/// </summary>
	private void SetSubCellCollisionEnabled(int cellX, int cellY, int cellZ, bool enabled)
	{
		if (!_subCells.TryGetValue(VoxelWorld.CellIndex(cellX, cellY, cellZ), out SubCellNode node))
		{
			return;
		}
		if (enabled)
		{
			if (node.Body == null && node.HasGeometry && node.BuiltShape != null)
			{
				// Create the body + shape lazily. Only in-radius chunks with
				// geometry pay this, so the total body count stays under Jolt's cap.
				node.Body = new StaticBody3D { Name = $"Collision_{node.CellIndex}" };
				AddChild(node.Body);
				node.Shape = new CollisionShape3D { Name = "Shape" };
				node.Body.AddChild(node.Shape);
			}
			if (node.Body != null)
			{
				node.Shape.Shape = node.HasGeometry ? node.BuiltShape : null;
			}
		}
		else if (node.Body != null)
		{
			// Far chunk: drop the shape (the body stays but holds no shape, so it
			// costs no narrow-phase work), so re-entering the radius is cheap.
			node.Shape.Shape = null;
		}
	}

	/// <summary>
	/// Builds ONE sub-cell's MC surface + collision SYNCHRONOUSLY (Option B fast
	/// path) and applies it to that sub-cell's nodes. This is the small, cheap
	/// rebuild: a sub-cell is ~1/64th of a chunk, so its MC build is
	/// single-digit ms — fast enough to run on the main thread in the edit's
	/// frame (instant). The per-vertex light is sampled from the chunk's CACHED
	/// wide light field (Path 1), so no flood fill runs here. The sub-cell's
	/// collision shape is rebuilt in the same frame (collision is ALWAYS updated
	/// on the edit path — the player can't fall through a freshly placed block).
	///
	/// If the sub-cell has no surface (empty region), its mesh + collision are
	/// cleared (the block was removed).
	/// </summary>
	/// <summary>
	/// The frozen neighbour snapshot for the instant-edit path: all dirty
	/// sub-cells of this chunk cull against the same neighbour state, so the
	/// caller computes it once per edit and passes it to every
	/// <see cref="RebuildSubCell"/> call.
	/// </summary>
	public System.Collections.Generic.Dictionary<Vector2I, ChunkData> SnapshotForEdit(VoxelWorld world)
		=> world.SnapshotNeighbours(Coord, includeCentre: true);

	/// <summary>
	/// Records the edited voxel (chunk-local) + its material + block-light level
	/// so the next <see cref="ReconsumeLightLocal"/> can patch the CACHED wide
	/// solid/block volumes at just that voxel — instead of rebuilding the full
	/// 385k-voxel volume (the cost that made the instant edit ~300 ms). Called
	/// by the instant-edit path before the local recompute.
	/// </summary>
	public void SetPatchVoxel(int lx, int ly, int lz, int material, byte light)
	{
		_patchVoxel = new Vector3I(lx, ly, lz);
		_patchMaterial = material;
		_patchLight = light;
		_hasPatch = true;
	}

	/// <summary>
	/// Whether the cached wide block-light volume exists (built by a full
	/// rebuild). The instant-edit path uses it to read the PRE-edit block-light
	/// level at a voxel, to decide if the edit's light source actually changed.
	/// </summary>
	public bool HasWideCache => _cachedWideBlock != null;

	/// <summary>
	/// The PRE-edit block-light level at a wide-field voxel (wx, wy, wz), or 0 if
	/// the cache is missing or the index is out of range. This is the value the
	/// last full rebuild computed (before the current edit's patch), so comparing
	/// it to the post-edit level tells the instant-edit path whether the light
	/// SOURCE changed (and thus whether a local light recompute is needed at all).
	/// <paramref name="lpx"/>/<paramref name="lpy"/>/<paramref name="lpz"/> are the
	/// wide field dims (the caller computes them, matching the cached volume).
	/// </summary>
	public int GetWideBlockAt(int lpx, int lpy, int lpz, int wx, int wy, int wz)
	{
		if (_cachedWideBlock == null)
		{
			return 0;
		}
		if (wx < 0 || wx >= lpx || wy < 0 || wy >= lpy || wz < 0 || wz >= lpz)
		{
			return 0;
		}
		return _cachedWideBlock[wx + lpx * (wy + lpy * wz)];
	}

	/// <summary>
	/// The PRE-edit solid (1) / air (0) state at a wide-field voxel, or 0 (air)
	/// if the cache is missing or the index is out of range. Used by the
	/// instant-edit path to detect a SOLID-STATE change (placing/breaking a
	/// block), which can change sky light out to the sky-line even when no light
	/// source changed — so a local light recompute is still required.
	/// </summary>
	public int GetWideSolidAt(int lpx, int lpy, int lpz, int wx, int wy, int wz)
	{
		if (_cachedWideSolid == null)
		{
			return 0;
		}
		if (wx < 0 || wx >= lpx || wy < 0 || wy >= lpy || wz < 0 || wz >= lpz)
		{
			return 0;
		}
		return _cachedWideSolid[wx + lpx * (wy + lpy * wz)];
	}

	/// <summary>
	/// LOCAL light recompute (Option B lighting): recompute the wide light field
	/// only over a box around the edit (instead of the full 3×3-chunk flood fill),
	/// re-applying the response curve, and return the sub-cells whose per-vertex
	/// light changed (so the caller re-meshes exactly those). This is what makes
	/// an edit's LIGHT update instant — the light no longer waits for the async
	/// full rebuild. The box is clamped to the wide field; the boundary values are
	/// reused as fixed conditions (see <see cref="SkyLight.ComputeLocal"/>).
	///
	/// Uses the CACHED wide solid/block volumes (built by the last full rebuild),
	/// patching only the edited voxel's values (see <see cref="SetPatchVoxel"/>)
	/// so it does NOT rebuild the full volume on every edit. Returns the set of
	/// sub-cells to re-mesh (may be empty if the light didn't actually change).
	/// </summary>
	public System.Collections.Generic.HashSet<VoxelWorld.VoxelCell> RecomputeLightLocal(
		VoxelWorld world,
		int lpx, int lpy, int lpz,
		int bx0, int by0, int bz0, int bx1, int by1, int bz1)
	{
		var changed = new System.Collections.Generic.HashSet<VoxelWorld.VoxelCell>();
		_lightChangedWide = null;
		if (_cachedLightField == null || _cachedWideSolid == null)
		{
			return changed; // no light field yet (first build) — the full rebuild handles it
		}

		// Patch the cached wide volumes at the edited voxel (its solid state and
		// block-light source changed; everything else is unchanged).
		if (_hasPatch)
		{
			int half = lpx / 2 - 1;
			int wx = _patchVoxel.X + half;
			int wy = _patchVoxel.Y + 1;
			int wz = _patchVoxel.Z + half;
			if (wx >= 0 && wx < lpx && wy >= 0 && wy < lpy && wz >= 0 && wz < lpz)
			{
				int idx = wx + lpx * (wy + lpy * wz);
				_cachedWideSolid[idx] = (byte)(_patchMaterial != 0 ? 1 : 0);
				_cachedWideBlock[idx] = _patchLight;
			}
			_hasPatch = false;
		}

		// Snapshot the box before the recompute so we can diff which voxels
		// actually changed (and thus which sub-cells need re-meshing).
		int bx = bx1 - bx0 + 1, by = by1 - by0 + 1, bz = bz1 - bz0 + 1;
		int total = bx * by * bz;
		var before = new float[total];
		for (int iz = 0; iz < bz; iz++)
			for (int iy = 0; iy < by; iy++)
				for (int ix = 0; ix < bx; ix++)
				{
					int g = (bx0 + ix) + lpx * ((by0 + iy) + lpy * (bz0 + iz));
					before[ix + bx * (iy + by * iz)] = _cachedLightField[g];
				}

		// The local max-BFS recompute (writes the normalized 0..1 field in place
		// over the box; boundary values are the fixed conditions).
		SkyLight.ComputeLocal(_cachedWideSolid, _cachedWideBlock, _cachedLightField, _cachedBlockLit,
			lpx, lpy, lpz, bx0, by0, bz0, bx1, by1, bz1, world.MaxLightLevel);

		// Re-apply the response curve ONLY over the box (it's a pure per-voxel
		// remap, so it's safe to apply to just the changed region).
		ApplyLightResponseBox(world, total, bx, by, bz, bx0, by0, bz0, lpx, lpy, lpz);

		// Diff: a voxel changed if its light differs by more than a tiny epsilon.
		// Record the changed wide indices (for the caller's precise sub-cell
		// re-mesh) and map each changed voxel to the sub-cell(s) whose vertices
		// sample it (the conservative bound, used when no precise check is made).
		var changedWideList = new System.Collections.Generic.List<int>();
		for (int iz = 0; iz < bz; iz++)
			for (int iy = 0; iy < by; iy++)
				for (int ix = 0; ix < bx; ix++)
				{
					int l = ix + bx * (iy + by * iz);
					int g = (bx0 + ix) + lpx * ((by0 + iy) + lpy * (bz0 + iz));
					if (System.Math.Abs(_cachedLightField[g] - before[l]) < 0.0005f)
					{
						continue; // unchanged
					}
					changedWideList.Add(g);
					// Wide voxel -> chunk-local voxel -> sub-cell. The wide field's
					// center chunk origin maps to wide index (lpx/2 - 1, 1, lpz/2 - 1);
					// a wide voxel (wx,wy,wz) is chunk-local (wx - (lpx/2-1), wy - 1,
					// wz - (lpz/2-1)). Air-voxel light is what a vertex samples, so
					// the sub-cell containing that chunk-local voxel (and any whose
					// 1-voxel padding includes it) re-meshes.
					int clx = (bx0 + ix) - (lpx / 2 - 1);
					int cly = (by0 + iy) - 1;
					int clz = (bz0 + iz) - (lpz / 2 - 1);
					changed.UnionWith(DirtyCellsForChunkLocal(world, clx, cly, clz));
				}

		_lightChangedWide = changedWideList.Count > 0 ? changedWideList.ToArray() : null;
		return changed;
	}

	/// <summary>
	/// LOCAL light recompute centred on an ARBITRARY wide-world position
	/// <paramref name="wWorld"/> (a world voxel, not necessarily inside this
	/// chunk) with radius <paramref name="range"/>. Used by the instant-edit
	/// path for a NEIGHBOUR chunk whose wide field is lit by a light source in
	/// the edited chunk: the source's light crosses the seam, and this makes the
	/// neighbour's light update instant too (instead of waiting for the async
	/// full rebuild — the "delay before the other chunk computes its light").
	/// No patch is applied (the neighbour's own voxels are unchanged); its wide
	/// volumes already carry the source (the wide field covers the neighbouring
	/// chunks, so the source voxel is inside this neighbour's field). Returns the
	/// sub-cells to re-mesh (may be empty if the box is entirely outside this
	/// chunk, or the light didn't change).
	/// </summary>
	public System.Collections.Generic.HashSet<VoxelWorld.VoxelCell> RecomputeLightLocalAt(
		VoxelWorld world,
		int lpx, int lpy, int lpz,
		int wWorldX, int wWorldY, int wWorldZ,
		int range)
	{
		var changed = new System.Collections.Generic.HashSet<VoxelWorld.VoxelCell>();
		_lightChangedWide = null;
		if (_cachedLightField == null || _cachedWideSolid == null)
		{
			return changed; // no light field yet (first build) — the full rebuild handles it
		}

		int bx0 = Mathf.Max(0, wWorldX - range);
		int bx1 = Mathf.Min(lpx - 1, wWorldX + range);
		int by0 = Mathf.Max(0, wWorldY - range);
		int by1 = Mathf.Min(lpy - 1, wWorldY + range);
		int bz0 = Mathf.Max(0, wWorldZ - range);
		int bz1 = Mathf.Min(lpz - 1, wWorldZ + range);
		if (bx0 > bx1 || by0 > by1 || bz0 > bz1)
		{
			return changed; // the box is entirely outside this chunk's field
		}

		// Snapshot the box (diff which voxels changed), run the local recompute
		// (no patch — the neighbour's own voxels are unchanged), re-curve, diff.
		int bx = bx1 - bx0 + 1, by = by1 - by0 + 1, bz = bz1 - bz0 + 1;
		int total = bx * by * bz;
		var before = new float[total];
		for (int iz = 0; iz < bz; iz++)
			for (int iy = 0; iy < by; iy++)
				for (int ix = 0; ix < bx; ix++)
				{
					int g = (bx0 + ix) + lpx * ((by0 + iy) + lpy * (bz0 + iz));
					before[ix + bx * (iy + by * iz)] = _cachedLightField[g];
				}

		SkyLight.ComputeLocal(_cachedWideSolid, _cachedWideBlock, _cachedLightField, _cachedBlockLit,
			lpx, lpy, lpz, bx0, by0, bz0, bx1, by1, bz1, world.MaxLightLevel);

		ApplyLightResponseBox(world, total, bx, by, bz, bx0, by0, bz0, lpx, lpy, lpz);

		var changedWideList = new System.Collections.Generic.List<int>();
		for (int iz = 0; iz < bz; iz++)
			for (int iy = 0; iy < by; iy++)
				for (int ix = 0; ix < bx; ix++)
				{
					int l = ix + bx * (iy + by * iz);
					int g = (bx0 + ix) + lpx * ((by0 + iy) + lpy * (bz0 + iz));
					if (System.Math.Abs(_cachedLightField[g] - before[l]) < 0.0005f)
					{
						continue;
					}
					changedWideList.Add(g);
					int clx = (bx0 + ix) - (lpx / 2 - 1);
					int cly = (by0 + iy) - 1;
					int clz = (bz0 + iz) - (lpz / 2 - 1);
					changed.UnionWith(DirtyCellsForChunkLocal(world, clx, cly, clz));
				}

		_lightChangedWide = changedWideList.Count > 0 ? changedWideList.ToArray() : null;
		return changed;
	}

	/// <summary>
	/// The wide-field lattice indices of the voxels whose light changed in the
	/// most recent <see cref="RecomputeLightLocal"/> / <see cref="RecomputeLightLocalAt"/>,
	/// or null if none (or no recompute has run). The instant-edit path uses this
	/// to re-mesh only the sub-cells whose vertices actually sample a changed
	/// light value (see <see cref="SubCellSamplesLightChanged"/>).
	/// </summary>
	public int[] GetLightChangedWide() => _lightChangedWide;

	/// <summary>
	/// True if any of the sub-cell's sampled light values (its region + the
	/// 1-voxel shell + the 1 MC-lattice padding point each side — exactly the
	/// voxels <c>BuildSubCell</c> samples) changed in the last local recompute.
	/// Used to skip the expensive MC re-mesh for sub-cells whose surface light
	/// is unchanged: if no sampled voxel changed, the produced mesh is
	/// byte-identical, so re-meshing is wasted work.
	/// </summary>
	public bool SubCellSamplesLightChanged(VoxelWorld world, int cellX, int cellY, int cellZ)
	{
		int[] changedWide = _lightChangedWide;
		if (changedWide == null)
		{
			return false; // no light recompute ran (or nothing changed)
		}
		if (_cachedLightField == null)
		{
			return false;
		}
		var (x0, x1, y0, y1, z0, z1) = world.CellBounds(cellX, cellY, cellZ);
		// Wide field dims: center chunk origin -> wide (lpx/2-1, 1, lpz/2-1).
		int cs = world.ChunkSizeXZ;
		int maxL = world.MaxLightLevel < 1 ? 1 : world.MaxLightLevel;
		int fpx = 2 * cs + 2 * maxL + 2;
		int half = fpx / 2 - 1;
		// The sub-cell's sampled wide region: region + 1 shell + 1 MC pad each
		// side, in wide coords (the same lattice BuildSubCell samples).
		int wx0 = (x0 - 2) + half, wx1 = (x1 + 2) + half;
		int wy0 = (y0 - 2) + 1, wy1 = (y1 + 2) + 1;
		int wz0 = (z0 - 2) + half, wz1 = (z1 + 2) + half;
		for (int i = 0; i < changedWide.Length; i++)
		{
			int g = changedWide[i];
			int wz = g / (fpx * (world.ChunkHeight + 2));
			int rem = g - wz * fpx * (world.ChunkHeight + 2);
			int wy = rem / fpx;
			int wx = rem - wy * fpx;
			if (wx >= wx0 && wx <= wx1 && wy >= wy0 && wy <= wy1 && wz >= wz0 && wz <= wz1)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Applies the response curve to a box of the light field in place.</summary>
	private void ApplyLightResponseBox(
		VoxelWorld world, int total, int bx, int by, int bz,
		int bx0, int by0, int bz0, int lpx, int lpy, int lpz)
	{
		float exp = world.LightFalloff >= 1f ? world.LightFalloff : 1f;
		float dg = world.DarknessGamma >= 1f ? world.DarknessGamma : 1f;
		float minL = world.MinLight;
		bool hasFloor = minL > 0f;
		for (int iz = 0; iz < bz; iz++)
			for (int iy = 0; iy < by; iy++)
				for (int ix = 0; ix < bx; ix++)
				{
					int l = ix + bx * (iy + by * iz);
					int g = (bx0 + ix) + lpx * ((by0 + iy) + lpy * (bz0 + iz));
					float norm = _cachedLightField[g];
					bool isBlock = _cachedBlockLit != null && _cachedBlockLit[g] == 1;
					if (!isBlock)
					{
						if (exp > 1.0001f) norm = (float)Math.Pow(norm, exp);
						if (dg > 1.0001f) norm = (float)Math.Pow(norm, dg);
					}
					if (hasFloor) norm = VoxelWorld.ApplyMinLight(norm, minL);
					_cachedLightField[g] = norm;
				}
	}

	/// <summary>
	/// The sub-cells whose geometry/light can change when chunk-local voxel
	/// (clx,cly,clz) changes: the cell containing it plus any cell whose 1-voxel
	/// padding includes it (a vertex in a neighbouring cell can sample it).
	/// </summary>
	private System.Collections.Generic.HashSet<VoxelWorld.VoxelCell> DirtyCellsForChunkLocal(
		VoxelWorld world, int clx, int cly, int clz)
	{
		var set = new System.Collections.Generic.HashSet<VoxelWorld.VoxelCell>();
		int cs = world.ChunkSizeXZ, ch = world.ChunkHeight;
		for (int ox = -1; ox <= 1; ox++)
			for (int oy = -1; oy <= 1; oy++)
				for (int oz = -1; oz <= 1; oz++)
				{
					int px = clx + ox, py = cly + oy, pz = clz + oz;
					if (px < 0 || py < 0 || pz < 0 || px >= cs || py >= ch || pz >= cs) continue;
					set.Add(VoxelWorld.CellOf(px, py, pz));
				}
		return set;
	}

	public void RebuildSubCell(VoxelWorld world, int cellX, int cellY, int cellZ)
		=> RebuildSubCell(world, cellX, cellY, cellZ, null);

	/// <inheritdoc/>
	/// <param name="neighbourSnapshot">
	/// A precomputed frozen neighbour snapshot to reuse (all sub-cells of one
	/// chunk share it — see the instant-edit path, which computes it once per
	/// edit and passes it to every dirty sub-cell). Null = compute it here.
	/// </param>
	public void RebuildSubCell(
		VoxelWorld world,
		int cellX, int cellY, int cellZ,
		System.Collections.Generic.Dictionary<Vector2I, ChunkData> neighbourSnapshot)
	{
		if (!DataReady)
		{
			return;
		}

		int cs = world.ChunkSizeXZ;
		int wx0 = Coord.X * cs;
		int wz0 = Coord.Y * cs;

		var data = Data;
		var palette = world.Palette;
		float voxelSize = world.VoxelSize;
		Vector2I coord = Coord;

		// The sub-cell's region (chunk-local voxel coords, inclusive) + clamped
		// to the chunk's real size (the last cell is short if the chunk isn't an
		// exact multiple of the cell size).
		var (x0, x1, y0, y1, z0, z1) = world.CellBounds(cellX, cellY, cellZ);

		// Frozen neighbour snapshot (same as the whole-chunk path) so the
		// sub-cell's border culling + light sample compute from the same
		// neighbour state — seams stay identical. Reused across the edit when
		// the caller supplies it (all sub-cells of the chunk share one).
		if (neighbourSnapshot == null)
		{
			neighbourSnapshot = world.SnapshotNeighbours(Coord, includeCentre: true);
		}
		System.Func<Vector2I, ChunkData> neighborQuery = c =>
			neighbourSnapshot.TryGetValue(c, out ChunkData d) ? d : null;
		System.Func<int, int, int, byte> materialQuery = world.Palette.BlendEnabled
			? world.VoxelMaterialForMesh
			: null;

		// The cached wide light field (Path 1). If none is cached yet (first
		// build), compute + curve it now (on the main thread) and cache it —
		// the sub-cell build then samples it. (First builds are budgeted/async
		// in practice, so this main-thread compute is rare.)
		if (_cachedLightField == null)
		{
			_cachedLightField = MeshBuilder.BuildLightFieldWide(
				data, wx0, wz0, data.SizeX, data.SizeY, neighborQuery, world.MaxLightLevel,
				out _cachedBlockLit, out _cachedWideSolid, out _cachedWideBlock);
			MeshBuilder.ApplyLightResponse(_cachedLightField, world.MinLight, world.LightFalloff, world.DarknessGamma, _cachedBlockLit);
		}
		int maxLight = world.MaxLightLevel;
		int lpx = 2 * data.SizeX + 2 * maxLight + 2;
		int lpy = data.SizeY + 2;
		int lpz = 2 * data.SizeZ + 2 * maxLight + 2;

		// Seam ownership: the +X/+Z boundary cells of a sub-cell on the chunk's
		// +X/+Z border are drawn only when that side's neighbour chunk is loaded
		// (same rule as the whole-chunk path).
		bool eastLoaded = neighborQuery(new Vector2I(coord.X + 1, coord.Y)) != null;
		bool southLoaded = neighborQuery(new Vector2I(coord.X, coord.Y + 1)) != null;
		bool diagLoaded = neighborQuery(new Vector2I(coord.X + 1, coord.Y + 1)) != null;

		// The small MC build (single-digit ms).
		var (visual, collision) = MeshBuilder.BuildSubCell(
			data, wx0, wz0, voxelSize, palette, coord, neighborQuery,
			x0, y0, z0, x1, y1, z1,
			_cachedLightField, _cachedBlockLit, lpx, lpy, lpz,
			eastLoaded, southLoaded, diagLoaded, materialQuery);

		// Apply to the sub-cell's nodes. The visual mesh is always set. The
		// collision shape is stashed (BuiltShape) and the body/shape wiring is
		// deferred to SetSubCellCollisionEnabled — which only attaches the shape
		// to a live body when the chunk is within the player's collision radius.
		// This keeps the instant edit from creating a Jolt body for a far sub-cell
		// (the per-frame radius gate handles the attach when the player is near).
		var node = GetOrCreateSubCell(cellX, cellY, cellZ);
		node.Mesh.Mesh = visual;
		if (visual != null && collision != null && collision.Count > 0)
		{
			var shape = new ConcavePolygonShape3D();
			shape.SetFaces(collision.ToArray());
			node.BuiltShape = shape;
			node.HasGeometry = true;
		}
		else
		{
			// Empty region: clear the sub-cell's geometry + collision.
			node.BuiltShape = null;
			node.HasGeometry = false;
			if (node.Body != null)
			{
				node.Shape.Shape = null;
			}
		}
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
		// The dark-side gamma: spreads the sparse integer light steps so the
		// MinLight floor is perceptually fine-grained instead of jumping.
		float darknessGamma = world.DarknessGamma;
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
		bool subCellMode = world.SubCellMeshing;
		_meshBuild = Task.Run(() =>
		{
			try
			{
				// Path 1 (light decoupling): compute the wide light field HERE
				// (the expensive flood fill, off the main thread), APPLY the
				// response curve to it (in place — it's a freshly-computed array
				// we own), and expose the ALREADY-CURVED field to the mesh build.
				// Both the whole-chunk and sub-cell paths use this cached curved
				// field, so the instant edit path skips both the flood fill AND
				// the ~560k Math.Pow curve calls. The curve is deterministic from
				// the field + the session-constant curve params, so caching the
				// curved result is correct; a curve-param change rebuilds all
				// chunks and re-caches with the new curve.
				float[] lightField = MeshBuilder.BuildLightFieldWide(data, wx0, wz0, data.SizeX, data.SizeY, neighborQuery, maxLight,
					out byte[] blockLit, out byte[] wideSolid, out byte[] wideBlock);
				MeshBuilder.ApplyLightResponse(lightField, minLight, lightFalloff, darknessGamma, blockLit);
				float[] pendingLight = lightField;
				byte[] pendingBlockLit = blockLit;
				// Cache the flood fill's input volumes for the instant-edit LOCAL
				// recompute (Option B): it patches only the edited voxels instead
				// of rebuilding this 385k-voxel volume on every edit.
				_pendingWideSolid = wideSolid;
				_pendingWideBlock = wideBlock;

				if (subCellMode)
				{
					// OPTION B (sub-cell remeshing): build every sub-cell of this
					// chunk (each a small MC build sampling the cached wide light
					// field) HERE, off the main thread. The node wiring (mesh +
					// collision assignments) must happen on the main thread, so
					// we stash the prebuilt results and marshal back via
					// ApplyBuildResult, which wires them up. We also stash the
					// light field for ApplyBuildResult to commit to the cache.
					int maxL = maxLight;
					int lpx = 2 * data.SizeX + 2 * maxL + 2;
					int lpy = data.SizeY + 2;
					int lpz = 2 * data.SizeZ + 2 * maxL + 2;
					bool east = neighborQuery(new Vector2I(coord.X + 1, coord.Y)) != null;
					bool south = neighborQuery(new Vector2I(coord.X, coord.Y + 1)) != null;
					bool diag = neighborQuery(new Vector2I(coord.X + 1, coord.Y + 1)) != null;
					var results = new List<(int, int, int, ArrayMesh, List<Vector3>)>();
					for (int cxx = 0; cxx < data.SizeX; cxx += VoxelWorld.CellSizeX)
					{
						for (int cyy = 0; cyy < data.SizeY; cyy += VoxelWorld.CellSizeY)
						{
							for (int czz = 0; czz < data.SizeZ; czz += VoxelWorld.CellSizeZ)
							{
								var (bx, bx1, by, by1, bz, bz1) = VoxelWorld.CellBoundsStatic(cxx, cyy, czz, data.SizeX, data.SizeY);
								var (mesh, col) = MeshBuilder.BuildSubCell(
									data, wx0, wz0, voxelSize, palette, coord, neighborQuery,
									bx, by, bz, bx1, by1, bz1,
									lightField, blockLit, lpx, lpy, lpz,
									east, south, diag, materialQuery);
								results.Add((cxx, cyy, czz, mesh, col));
							}
						}
					}
					_pendingLight = pendingLight;
					_pendingBlockLit = pendingBlockLit;
					_pendingSubCellBuild = true;
					_pendingSubCells = results;
					CallDeferred(nameof(ApplyBuildResult));
					return;
				}

				// Whole-chunk path (SubCellMeshing off): the heavy voxel math
				// happens here, off the main thread. Collision is the visual
				// surface exactly: MeshBuilder.Build emits the visual triangles
				// into the collision soup, so the player stands on exactly what
				// is drawn. The precomputed, already-curved lightField is passed
				// so the internal flood fill AND the response curve are both
				// skipped (Path 1 fast path).
				var (visual, collision) = MeshBuilder.Build(data, wx0, wz0, voxelSize, palette, mode, coord, solidQuery, neighborQuery, materialQuery, blockLightQuery, neighborChunkQuery, minLight, maxLight, lightFalloff, debugVertexLight, darknessGamma, pendingLight, pendingBlockLit);

				// Store the prebuilt results for the main thread. Written here,
				// read only in ApplyBuildResult after the task completes (happens-
				// before via the task completion + the deferred call on a later
				// frame). No lock needed. The light field is cached the same way
				// so the instant edit path can reuse it (Path 1).
				_pendingMesh = visual;
				_pendingCollision = collision;
				_pendingLight = pendingLight;
				_pendingBlockLit = pendingBlockLit;

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
	/// Rebuilds this chunk's mesh + collision SYNCHRONOUSLY on the main thread
	/// and applies the result immediately (no worker thread, no
	/// <c>CallDeferred</c> hop). This is the fast path for a freshly-edited
	/// chunk: a 16×256×16 hidden-face mesh is only ~1–3 ms of C# work, well
	/// within a frame, and applying it in the SAME frame the player clicked is
	/// what makes editing feel instant (the async <see cref="Rebuild"/> would
	/// cost a few extra frames: budget queue + worker build + deferred apply).
	///
	/// The mesh math is IDENTICAL to the async path (the same
	/// <c>MeshBuilder.Build</c> call over the same frozen neighbour snapshot),
	/// so the produced geometry is byte-for-byte the same as <see
	/// cref="Rebuild"/> — only the threading/apply timing differs. No-ops when
	/// the data isn't ready or an async build is already in flight (the caller
	/// checks, but this re-guards for safety).
	/// </summary>
	public void RebuildSynchronous(VoxelWorld world)
	{
		if (!DataReady || _meshBuild != null)
		{
			return;
		}

		int cs = world.ChunkSizeXZ;
		int wx0 = Coord.X * cs;
		int wz0 = Coord.Y * cs;

		var data = Data;
		var palette = world.Palette;
		float voxelSize = world.VoxelSize;
		VoxelMeshMode mode = world.MeshMode;
		Vector2I coord = Coord;
		System.Func<int, int, int, bool> solidQuery = world.VoxelSolidForMesh;

		// The same frozen neighbour snapshot the async path uses (centre
		// included), so the wide light field and border culling compute from
		// exactly the same neighbour state — identical geometry to Rebuild.
		var neighbourSnapshot = world.SnapshotNeighbours(Coord, includeCentre: true);
		System.Func<Vector2I, ChunkData> neighborQuery = c =>
			neighbourSnapshot.TryGetValue(c, out ChunkData d) ? d : null;
		System.Func<int, int, int, byte> materialQuery = world.Palette.BlendEnabled
			? world.VoxelMaterialForMesh
			: null;
		System.Func<int, int, int, byte> blockLightQuery = world.VoxelBlockLightForMesh;
		System.Func<Vector2I, ChunkData> neighborChunkQuery = neighborQuery;
		float minLight = world.MinLight;
		int maxLight = world.MaxLightLevel;
		float lightFalloff = world.LightFalloff;
		float darknessGamma = world.DarknessGamma;
		bool debugVertexLight = world.DebugVertexLight && VoxelWorld.VertexLightDumpPending;
		if (debugVertexLight)
		{
			VoxelWorld.VertexLightDumpPending = false;
		}

		// Path 1 (light decoupling): if this chunk already has a cached wide
		// light field (from a previous full build), reuse it so the expensive
		// flood fill is SKIPPED — the surface + collision build is the only work
		// done here, which is what makes the click instant. The cached field is
		// (possibly one-edit-stale) light: the voxel appears immediately with its
		// previous lighting, and a fresh full rebuild (which recomputes the light
		// and refreshes the cache) is scheduled right below. If there is NO
		// cached field yet (the very first build of this chunk), compute it now
		// (unavoidable) and cache it.
		bool hasCachedLight = _cachedLightField != null;
		float[] lightField = _cachedLightField;
		byte[] blockLit = _cachedBlockLit;
		if (!hasCachedLight)
		{
			// First build: compute the field on the main thread (the synchronous
			// path has no worker), apply the response curve (in place), and cache
			// the CURVED field for future instant edits (which reuse it without
			// re-curving). The curve is deterministic from the field + the
			// session-constant curve params, so caching the curved result is
			// correct; a curve-param change rebuilds all chunks and re-caches.
			lightField = MeshBuilder.BuildLightFieldWide(data, wx0, wz0, data.SizeX, data.SizeY, neighborQuery, maxLight,
				out blockLit, out byte[] wideSolid, out byte[] wideBlock);
			MeshBuilder.ApplyLightResponse(lightField, minLight, lightFalloff, darknessGamma, blockLit);
			_cachedLightField = lightField;
			_cachedBlockLit = blockLit;
			_cachedWideSolid = wideSolid;
			_cachedWideBlock = wideBlock;
		}

		// TEMP (Path 1 diagnosis): time the synchronous surface build so we can
		// see the per-edit cost now that the flood fill is skipped. Gated on a
		// static one-shot flag so it prints only a few times. Removed once we
		// know the number.
		bool profileInstant = VoxelWorld.InstantBuildProfile;
		var sw = profileInstant ? System.Diagnostics.Stopwatch.StartNew() : null;

		// The heavy voxel math, on the main thread (synchronous fast path). The
		// precomputed lightField is passed so the internal flood fill is skipped
		// (Path 1). Output is identical to the full build modulo the light value.
		var (visual, collision) = MeshBuilder.Build(
			data, wx0, wz0, voxelSize, palette, mode, coord, solidQuery,
			neighborQuery, materialQuery, blockLightQuery, neighborChunkQuery,
			minLight, maxLight, lightFalloff, debugVertexLight, darknessGamma,
			lightField, blockLit);

		if (profileInstant)
		{
			sw.Stop();
			GD.Print($"[InstantBuild] chunk ({Coord.X},{Coord.Y}) cachedLight={hasCachedLight}: " +
				$"build={sw.ElapsedMilliseconds} ms — {collision.Count / 3} tris");
			if (VoxelWorld.InstantBuildProfileCount < 12)
			{
				VoxelWorld.InstantBuildProfileCount++;
			}
			else
			{
				VoxelWorld.InstantBuildProfile = false;
			}
		}

		// Apply the result NOW (same frame). The same wiring ApplyBuildResult
		// does, but inline so there is no deferred hop.
		if (visual != null)
		{
			_meshInstance.Mesh = visual;
		}
		if (collision != null)
		{
			var shape = new ConcavePolygonShape3D();
			shape.SetFaces(collision.ToArray());
			_builtShape = shape;
			_collisionReady = true;
			_shape.Shape = _builtShape;
		}

		// Geometry + collision are applied (the instant part). Now decide
		// whether a fresh full rebuild (to recompute the LIGHT with the new
		// solid state) is needed. An edit changes the solid state, which changes
		// the light field, so when we reused a cached (stale) field we MUST
		// schedule a fresh full rebuild to refresh the lighting — that is the
		// "light lags behind" behaviour: the mesh is instant, the light catches
		// up on the next budgeted full build. When we just computed the field
		// (first build), the light is already fresh, so clear the flag.
		if (hasCachedLight)
		{
			// Keep NeedsMesh true so the budgeted async path runs a fresh full
			// build (recomputing light + refreshing the cache) shortly.
			NeedsMesh = true;
		}
		else
		{
			NeedsMesh = false;
		}
		_meshBuild = null;
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
		bool appliedMesh;

		// Option B (sub-cell mode): wire up the prebuilt sub-cell results. The
		// worker computed the per-sub-cell ArrayMesh + collision triangles; this
		// (main thread) assigns them to the sub-cell nodes. Also hide the
		// whole-chunk mesh (unused in sub-cell mode). A successful sub-cell
		// build (results present) counts as "applied" for the NeedsMesh gate.
		if (_pendingSubCellBuild)
		{
			_meshInstance.Mesh = null;
			appliedMesh = _pendingSubCells != null;
			if (appliedMesh)
			{
				foreach (var (cxx, cyy, czz, mesh, col) in _pendingSubCells)
				{
					var node = GetOrCreateSubCell(cxx, cyy, czz);
					node.Mesh.Mesh = mesh;
					// Collision is stashed, not attached: the per-frame
					// collision-radius gate (SetSubCellCollisionEnabled) only
					// creates the body + attaches the shape while this chunk is
					// near the player. This is what keeps the total Jolt body
					// count under the cap (far chunks have no bodies).
					if (mesh != null && col != null && col.Count > 0)
					{
						var shape = new ConcavePolygonShape3D();
						shape.SetFaces(col.ToArray());
						node.BuiltShape = shape;
						node.HasGeometry = true;
					}
					else
					{
						node.BuiltShape = null;
						node.HasGeometry = false;
					}
				}
				// The sub-cell shapes are live concave collision now. Flag the
				// chunk's collision as ready so the spawn gate (which reads
				// CollisionReady, not the whole-chunk _shape) unlocks — the
				// whole-chunk _shape stays null in sub-cell mode by design.
				_collisionReady = true;
				_pendingSubCells = null;
			}
			_pendingSubCellBuild = false;
		}
		else
		{
			appliedMesh = _pendingMesh != null;
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
		}

		// Commit the freshly-computed wide light field to the chunk's cache
		// (Path 1). This is what the synchronous INSTANT edit path reuses to
		// skip the flood fill. Only when a full build produced a field (a
		// failed build leaves the previous cache in place).
		if (_pendingLight != null)
		{
			_cachedLightField = _pendingLight;
			_cachedBlockLit = _pendingBlockLit;
			_cachedWideSolid = _pendingWideSolid;
			_cachedWideBlock = _pendingWideBlock;
			_pendingLight = null;
			_pendingBlockLit = null;
			_pendingWideSolid = null;
			_pendingWideBlock = null;
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
	///
	/// In sub-cell mode (Option B) this is a NO-OP: the whole-chunk <c>_shape</c>
	/// is never assigned (collision lives on the per-sub-cell bodies, managed by
	/// <see cref="SetSubCellCollisionEnabled"/>). The <c>_collisionReady</c> flag is
	/// still set (it gates spawn), so this runs but assigns null to the whole-chunk
	/// shape — harmless.
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
