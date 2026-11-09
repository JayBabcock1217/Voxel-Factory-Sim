using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The in-memory owner of the shared <see cref="MapRenderer"/> (the waypoint
/// store + terrain-coloring service). A single <see cref="Node"/> hosted in the
/// gameplay scene (<c>Scenes/Main.tscn</c>) — it is the one object that both the
/// minimap and the map window draw from, so the two views share the same
/// waypoints and the same terrain colors.
///
/// Lifecycle:
///   - The world restores saved waypoints into the renderer once the player is
///     placed (see <see cref="VoxelWorld.SpawnOrRestorePlayer"/> →
///     <see cref="RestoreWaypoints"/>).
///   - The save system reads the renderer's waypoint list when collecting a
///     snapshot (see <see cref="WorldSave.CollectSnapshot"/>).
///   - The minimap / map window sample terrain directly from the world (via
///     <see cref="MapRenderer.SampleRegion"/>) and draw their own display images.
///
/// The renderer is created in <see cref="_Ready"/> (it is stateless about the
/// terrain — only the waypoint list and a texture-color cache live in it).
/// </summary>
[GlobalClass]
public partial class WaypointManager : Node
{
	/// <summary>The shared waypoint store + terrain-coloring service.</summary>
	public MapRenderer Renderer { get; private set; }

	/// <summary>Raised whenever the waypoint set changes (delegated from the renderer).</summary>
	public event System.Action WaypointsChanged;

	public override void _Ready()
	{
		// Both the minimap and the map window find this node by group, so
		// register it (matching the project's code-side AddToGroup pattern used
		// for the "player" and "voxel_world" groups).
		AddToGroup("waypoint_manager");

		Renderer = new MapRenderer();
		Renderer.WaypointsChanged += OnRendererWaypointsChanged;
	}

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

	public override void _ExitTree()
	{
		if (Renderer != null)
		{
			Renderer.WaypointsChanged -= OnRendererWaypointsChanged;
		}
	}
}
