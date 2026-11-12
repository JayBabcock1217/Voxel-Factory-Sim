using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FactorySim.Voxel;

/// <summary>
/// One "connector" strip that bridges the gap between the full-resolution
/// chunk square and the Distant-Horizons-style LOD annulus.
///
/// The full-res square's outer edge sits at distance R (RenderDistanceChunks)
/// from the player, but the LOD annulus starts at R*sqrt(2) (the square's
/// farthest corner). The ring between R and R*sqrt(2) is EMPTY (no full-res
/// chunks, no LOD), and the full-res outer chunks leave their outer edges OPEN
/// (no skirt) — so there is a visible see-through gap at the transition. This
/// strip fills that ring for ONE side of the square (north / east / south /
/// west):
///
///   * Its INNER edge runs along the full-res square's outer edge, sampling the
///     EXACT same deterministic terrain functions the full-res chunks use
///     (TerrainGenerator.SurfaceHeight + foliage), so it meets the full-res top
///     layer column-for-column (no height step, no material mismatch).
///   * Its OUTER edge runs along the LOD inner boundary (R*sqrt(2)), sampling
///     the same functions the LOD uses, so it meets the LOD heightfield.
///   * A SKIRT (vertical wall) drops from the full-res edge to a base height,
///     closing the open rim of the outer chunks so the transition is not
///     see-through from a low angle.
///
/// Like <see cref="LodChunk"/>, the heavy CPU work (heightfield sampling + mesh
/// build) runs on the thread pool and the prebuilt ArrayMesh is wired in on the
/// main thread via CallDeferred. The built mesh is cached on the node, so when
/// the player moves away and back the strip is not regenerated.
///
/// The connector is VISUAL ONLY — it never gets collision (it is at the edge of
/// the full-res square, far outside the collision radius).
/// </summary>
public partial class LodConnector : Node3D
{
	/// <summary>Which side of the full-res square this strip bridges (0=N, 1=E, 2=S, 3=W).</summary>
	public int Side { get; private set; }

	/// <summary>The full-res square's outer edge this strip runs along (in world voxels).</summary>
	public int Edge { get; private set; }

	/// <summary>True while the (re)build is pending or in flight.</summary>
	public bool NeedsMesh { get; set; } = true;

	/// <summary>True once the mesh has been built and applied.</summary>
	public bool Built { get; private set; } = false;

	private MeshInstance3D _meshInstance;
	private Task _meshBuild;
	private ArrayMesh _pendingMesh;
	private int _generation;
	private int _pendingGeneration;

	public void Initialize(int side, int edge)
	{
		Side = side;
		Edge = edge;

		_meshInstance = new MeshInstance3D { Name = "Mesh" };
		AddChild(_meshInstance);
	}

	public bool IsReadyToRender => Built;
	public bool BuildInFlight => _meshBuild != null;

	/// <summary>Invalidates the cached mesh so the strip rebuilds from current terrain.</summary>
	public void Invalidate()
	{
		Built = false;
		NeedsMesh = true;
		_generation++;
	}

	/// <summary>
	/// Starts rebuilding this strip's mesh on the thread pool. No-ops while a
	/// build is in flight or the mesh is up to date, so the world's per-frame
	/// loop can call it every frame without starting duplicates. The immutable
	/// inputs are snapshotted before the task so the worker reads stable values.
	/// </summary>
	public void Rebuild(
		int chunkSizeXZ, GenConfig cfg, VoxelMaterialPalette palette, float voxelSize,
		int lodResolution, int renderDistanceChunks, float lodInnerBoundary,
		Dictionary<Vector2I, VoxelChunk> chunks = null)
	{
		if (!NeedsMesh || _meshBuild != null)
		{
			return;
		}

		int side = Side;
		int edge = Edge;
		int generation = _generation;

		_meshBuild = Task.Run(() =>
		{
			if (!GodotObject.IsInstanceValid(this))
			{
				return; // connector freed before the worker started
			}

			try
			{
				var data = LodConnectorGenerator.GenerateData(
					side, edge, chunkSizeXZ, lodResolution, cfg, renderDistanceChunks,
					lodInnerBoundary, chunks);
				var mesh = LodConnectorGenerator.BuildMesh(data, voxelSize, palette);

				// Written on the worker, read only in ApplyBuildResult after the
				// task completes (happens-before via the deferred call on a later
				// frame). No lock needed.
				_pendingMesh = mesh;
				_pendingGeneration = generation;

				// Marshal the cheap engine wiring back to the main thread. The
				// connector can be freed between the check above and this call;
				// calling into a freed object would throw on the worker thread.
				CallDeferred(nameof(ApplyBuildResult));
			}
			catch (Exception)
			{
				// The connector was freed between the validity check and the call;
				// the mesh is discarded with the node.
			}
		});
	}

	/// <summary>
	/// Called on the main thread (via CallDeferred) when the async build
	/// finishes. Assigns the prebuilt mesh and marks the strip built.
	/// </summary>
	private void ApplyBuildResult()
	{
		if (!GodotObject.IsInstanceValid(this))
		{
			return;
		}

		_meshBuild = null;

		if (_pendingGeneration != _generation)
		{
			// Invalidated while the build was in flight (F-key regenerate): the
			// result is stale. Discard it and stay dirty so the next UpdateLod
			// rebuilds from the current parameters.
			_pendingMesh = null;
			_pendingGeneration = _generation;
			Built = false;
			NeedsMesh = true;
			return;
		}

		if (_pendingMesh != null)
		{
			_meshInstance.Mesh = _pendingMesh;
			_pendingMesh = null;
		}

		Built = true;
		NeedsMesh = false;
	}
}
