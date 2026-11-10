using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The in-memory owner of the shared <see cref="MapRenderer"/> (the waypoint
/// store + the pushed 2D terrain-data store + terrain-coloring). A single
/// <see cref="Node"/> hosted in the gameplay scene (<c>Scenes/Main.tscn</c>) — it
/// is the one object that both the minimap and the map window draw from, so the
/// two views share the same waypoints, the same terrain data, and the same
/// terrain colors.
///
/// Lifecycle:
///   - The world restores saved waypoints into the renderer once the player is
///     placed (see <see cref="VoxelWorld.SpawnOrRestorePlayer"/> →
///     <see cref="RestoreWaypoints"/>).
///   - The save system reads the renderer's waypoint list when collecting a
///     snapshot (see <see cref="WorldSave.CollectSnapshot"/>).
///   - The world PUSHES each chunk's 2D height+material data to the renderer as
///     chunks generate / get edited (<see cref="MapRenderer.SetChunk"/> /
///     <see cref="MapRenderer.SetTile"/>); the minimap / map window only READ it
///     back (<see cref="MapRenderer.GetTileColor"/>) and never scan the world.
///
/// The renderer is created in <see cref="_Ready"/>. It holds the waypoint list,
/// the pushed per-chunk 2D terrain data, and a per-material color cache.
/// </summary>
[GlobalClass]
public partial class WaypointManager : Node
{
	/// <summary>The shared waypoint store + terrain-coloring service.</summary>
	public MapRenderer Renderer { get; private set; }

	/// <summary>Raised whenever the waypoint set changes (delegated from the renderer).</summary>
	public event System.Action WaypointsChanged;

	/// <summary>
	/// Whether the map system (minimap + map window) is enabled. When false, the
	/// minimap hides itself and the map window cannot be opened. Toggled with
	/// the Tab key (or <see cref="ToggleMapSystem"/>).
	/// </summary>
	public bool IsMapSystemEnabled { get; private set; } = true;

	/// <summary>Raised when the map system's enabled state changes.</summary>
	public event System.Action MapSystemToggled;

	/// <summary>
	/// The key that toggles the map system (minimap + map window) on/off.
	/// Default O (a free key; Tab is used by the inventory / machine UIs).
	/// </summary>
	[Export] public Key MapToggleKey { get; set; } = Key.O;

	public override void _Ready()
	{
		// Both the minimap and the map window find this node by group, so
		// register it (matching the project's code-side AddToGroup pattern used
		// for the "player" and "voxel_world" groups).
		AddToGroup("waypoint_manager");

		Renderer = new MapRenderer();
		Renderer.WaypointsChanged += OnRendererWaypointsChanged;
	}

	/// <summary>Toggles the map system on/off. Raises <see cref="MapSystemToggled"/>.</summary>
	public void ToggleMapSystem()
	{
		IsMapSystemEnabled = !IsMapSystemEnabled;
		MapSystemToggled?.Invoke();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == MapToggleKey)
		{
			ToggleMapSystem();
		}
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
