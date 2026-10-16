using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FactorySim.Voxel;

/// <summary>
/// One distant-Horizons-style LOD region: a visual-only low-res heightfield
/// mesh rendered BEYOND the normal chunk render distance.
///
/// Mirrors <see cref="VoxelChunk"/> minus the collision: the heavy CPU work
/// (coarse heightfield sampling + mesh build) runs on the thread pool and the
/// prebuilt <see cref="ArrayMesh"/> is wired into the node on the main thread
/// via <c>CallDeferred</c>. The built mesh is cached on the node, so when the
/// player moves away and back the region is not regenerated (client-side
/// caching, per session).
///
/// LOD has NO collision — it is never close enough to be stood on.
/// </summary>
public partial class LodChunk : Node3D
{
	/// <summary>LOD level: 1 = finest (2x2 base chunks per side), higher = coarser/larger.</summary>
	public int Level { get; private set; }

	/// <summary>Region coord in level-<see cref="Level"/> region units.</summary>
	public Vector2I Coord { get; private set; }

	/// <summary>True while the (re)build is pending or in flight.</summary>
	public bool NeedsMesh { get; set; } = true;

	/// <summary>True once the mesh has been built and applied to the node.</summary>
	public bool Built { get; private set; } = false;

	private MeshInstance3D _meshInstance;
	private Task _meshBuild;
	private ArrayMesh _pendingMesh;

	/// <summary>
	/// Bumped by <see cref="Invalidate"/>. A build snapshots it on start and
	/// <see cref="ApplyBuildResult"/> discards its result if it no longer
	/// matches — i.e. the region was invalidated (F-key regenerate or a
	/// texture-tiling change) while the build was in flight, so the stale
	/// mesh must not overwrite the invalidation.
	/// </summary>
	private int _generation;
	private int _pendingGeneration;

	public void Initialize(int level, Vector2I coord)
	{
		Level = level;
		Coord = coord;

		_meshInstance = new MeshInstance3D { Name = "Mesh" };
		AddChild(_meshInstance);
	}

	/// <summary>True when the mesh is complete and can be shown (drives gap-fill).</summary>
	public bool IsReadyToRender => Built;

	/// <summary>True while a build is running on the thread pool (main-thread read).</summary>
	public bool BuildInFlight => _meshBuild != null;

	/// <summary>
	/// Invalidates the cached mesh (e.g. after an F-key regenerate) so the
	/// region rebuilds from the current terrain parameters.
	/// </summary>
	public void Invalidate()
	{
		Built = false;
		NeedsMesh = true;
		_generation++;
	}

	/// <summary>
	/// Starts rebuilding this region's mesh on the thread pool. No-ops while a
	/// build is in flight or the mesh is already up to date, so the world's
	/// per-frame loop can call it every frame without starting duplicates.
	/// The immutable inputs are snapshotted before the task so the worker reads
	/// stable values (a concurrent F-key regenerate swaps the world's config;
	/// the in-flight build keeps its snapshot and is invalidated anyway).
	/// </summary>
	/// <param name="chunks">
	/// The world's loaded-chunk map (may be null). Used for the foliage
	/// solid-base check so LOD canopies match what full-res chunks actually
	/// plant.
	/// </param>
	public void Rebuild(
		int chunkSizeXZ, GenConfig cfg, VoxelMaterialPalette palette, float voxelSize, int lodResolution,
		Dictionary<Vector2I, VoxelChunk> chunks = null)
	{
		if (!NeedsMesh || _meshBuild != null)
		{
			return;
		}

		int level = Level;
		Vector2I coord = Coord;
		int generation = _generation;

		_meshBuild = Task.Run(() =>
		{
			if (!GodotObject.IsInstanceValid(this))
			{
				return; // region freed before the worker even started
			}

			var data = LodChunkGenerator.GenerateData(level, coord, chunkSizeXZ, lodResolution, cfg, chunks);
			var mesh = LodChunkGenerator.BuildMesh(data, voxelSize, palette);

			// Written here, read only in ApplyBuildResult after the task
			// completes (happens-before via task completion + the deferred
			// call on a later frame). No lock needed.
			_pendingMesh = mesh;
			_pendingGeneration = generation;

			// Marshal the cheap engine wiring back to the main thread. The
			// region can be freed between the check above and this call;
			// calling into a freed object would throw on the worker thread,
			// so swallow it (the mesh is discarded with the node).
			try
			{
				CallDeferred(nameof(ApplyBuildResult));
			}
			catch (Exception)
			{
				// The region was freed between the validity check above and
				// this call; the mesh is discarded with the node.
			}
		});
	}

	/// <summary>
	/// Called on the main thread (via <c>CallDeferred</c>) when the async CPU
	/// build finishes. Assigns the prebuilt mesh and marks the region built.
	/// The mesh stays cached on the node for the rest of the session.
	/// </summary>
	private void ApplyBuildResult()
	{
		// The region may have been freed (player moved far / LOD disabled)
		// before this deferred call ran; never touch freed nodes.
		if (!GodotObject.IsInstanceValid(this))
		{
			return;
		}

		_meshBuild = null;

		if (_pendingGeneration != _generation)
		{
			// The region was invalidated while this build was in flight (F-key
			// regenerate or a texture-tiling change): the result is stale.
			// Discard it and stay dirty so the next UpdateLod rebuilds it from
			// the current parameters.
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
