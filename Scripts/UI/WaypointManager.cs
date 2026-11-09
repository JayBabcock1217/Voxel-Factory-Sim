using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The in-memory owner of the player's map waypoints and the shared
/// <see cref="MapRenderer"/>. A single <see cref="Node"/> hosted in the gameplay
/// scene (<c>Scenes/Main.tscn</c>) — it is the one object that both the minimap
/// and the map window draw from, so the two views can never disagree.
///
/// Lifecycle:
///   - The world restores saved waypoints into the renderer once the player is
///     placed (see <see cref="VoxelWorld.SpawnOrRestorePlayer"/> →
///     <see cref="RestoreWaypoints"/>).
///   - The save system reads the renderer's waypoint list when collecting a
///     snapshot (see <see cref="WorldSave.CollectSnapshot"/>).
///   - The map window adds/removes waypoints through the renderer (delegated
///     here for the world / save to call).
///
/// The renderer is created in <see cref="_Ready"/> centered on the world origin;
/// the minimap (which runs first) calls <see cref="EnsureRenderer"/> to make it
/// exists and to (re)center it on the player's spawn area when the world is
/// ready.
/// </summary>
[GlobalClass]
public partial class WaypointManager : Node
{
	/// <summary>The shared terrain renderer + waypoint store (created in _Ready).</summary>
	public MapRenderer Renderer { get; private set; }

	/// <summary>Raised whenever the waypoint set changes (delegated from the renderer).</summary>
	public event System.Action WaypointsChanged;

	/// <summary>Raised whenever the renderer's terrain texture is re-sampled.</summary>
	public event System.Action TextureUpdated;

	/// <summary>
	/// Creates (or returns) the shared <see cref="MapRenderer"/>, centering it on
	/// <paramref name="centerX"/>,<paramref name="centerZ"/> and the given
	/// <paramref name="size"/> tiles. Re-creating when the center/size changes
	/// resets the image to "unknown" (the world's dirty-chunk marks then
	/// re-sample it). Idempotent when the params are unchanged.
	/// </summary>
	public MapRenderer EnsureRenderer(int size, int centerX, int centerZ)
	{
		// Idempotent: return the existing renderer when it already covers the
		// requested region (so the minimap's per-frame call is a no-op and the
		// image — and its sampled pixels — are preserved). Only (re)create when
		// the region is NOT covered, which resets the image to "unknown" and
		// triggers a re-sample (the deliberate "re-center" case).
		int needMinX = centerX - size / 2;
		int needMinZ = centerZ - size / 2;
		if (Renderer != null)
		{
			bool covers =
				needMinX >= Renderer.MinX &&
				needMinX + size <= Renderer.MinX + Renderer.Size &&
				needMinZ >= Renderer.MinZ &&
				needMinZ + size <= Renderer.MinZ + Renderer.Size;
			if (covers)
			{
				return Renderer;
			}
		}
		var r = new MapRenderer(size, centerX, centerZ);
		r.TextureUpdated += OnRendererTextureUpdated;
		r.WaypointsChanged += OnRendererWaypointsChanged;
		Renderer = r;
		return r;
	}

	private void OnRendererTextureUpdated() => TextureUpdated?.Invoke();
	private void OnRendererWaypointsChanged() => WaypointsChanged?.Invoke();

	/// <summary>
	/// Restores the saved waypoints into the renderer. Called by the world once
	/// the player is placed. A null list clears the set.
	/// </summary>
	public void RestoreWaypoints(List<WorldSaveFile.WaypointRecord> saved)
	{
		Renderer?.RestoreWaypoints(saved);
	}

	/// <summary>Builds the save-form list from the renderer (used by the save system).</summary>
	public List<WorldSaveFile.WaypointRecord> ToSaveRecords()
		=> Renderer?.ToSaveRecords() ?? new List<WorldSaveFile.WaypointRecord>();
}
