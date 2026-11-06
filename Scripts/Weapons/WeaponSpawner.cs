using Godot;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// Creates the player's <see cref="WeaponController"/> and parents it to the
/// player (a sibling of the <see cref="VoxelTool"/>). Mirrors how the other
/// player systems are wired: the controller resolves its own camera, world, and
/// inventory references in <c>_Ready</c>, so this spawner only has to find the
/// player and add the node. It is idempotent — if a controller already exists
/// (e.g. a scene reload), it returns the existing one instead of duplicating.
///
/// Called once at startup (from <see cref="VoxelWorld"/> or the main scene)
/// after the player is in the tree.
/// </summary>
public static class WeaponSpawner
{
	/// <summary>
	/// Returns the player's <see cref="WeaponController"/> (creating it as a
	/// child of the player if it does not already exist). Returns null when no
	/// player is found in the tree.
	/// </summary>
	public static WeaponController EnsureController(Node player)
	{
		if (player == null)
		{
			return null;
		}
		// Idempotent: reuse an existing controller.
		var existing = player.GetNodeOrNull<WeaponController>("WeaponController");
		if (existing != null)
		{
			return existing;
		}

		var controller = new WeaponController { Name = "WeaponController" };
		// Point the controller at the camera (a sibling of the controller under
		// the player) and the world (the standard path the VoxelTool uses).
		controller.CameraPath = new NodePath("../Camera3D");
		player.AddChild(controller);
		return controller;
	}

	/// <summary>
	/// Finds the player's <see cref="WeaponController"/> (creating it if needed).
	/// Looks the player up by its group. Pass any node in the tree (e.g. the
	/// VoxelWorld); it is only used to reach the scene tree. Returns null when no
	/// player is found.
	/// </summary>
	public static WeaponController EnsureControllerForPlayer(Node anyNode)
	{
		if (anyNode == null || !anyNode.IsInsideTree())
		{
			return null;
		}
		var tree = anyNode.GetTree();
		if (tree == null)
		{
			return null;
		}
		var player = tree.GetFirstNodeInGroup("player");
		return EnsureController(player);
	}
}
