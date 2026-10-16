using Godot;

namespace FactorySim.Items;

/// <summary>
/// Spawns <see cref="VoxelDrop"/> nodes into the scene. The drop scene
/// (<c>VoxelDrop.tscn</c>) is loaded once and instanced per drop. All drops
/// are parented to the "Drops" node in the main scene (created on demand if
/// missing), so they can all be cleared/found from one place.
/// </summary>
public static class DropSpawner
{
	private static PackedScene _dropScene;

	private static PackedScene DropScene
	{
		get
		{
			if (_dropScene == null)
			{
				_dropScene = GD.Load<PackedScene>("res://Scenes/VoxelDrop.tscn");
			}
			return _dropScene;
		}
	}

	/// <summary>
	/// Creates a drop of <paramref name="materialId"/> x <paramref name="count"/>
	/// at <paramref name="worldPos"/>. <paramref name="parent"/> is any node in
	/// the tree; the drop is actually added under the shared "Drops" container
	/// (root of the tree) so drops are all siblings.
	/// </summary>
	public static void SpawnDrop(Node parent, Vector3 worldPos, int materialId, int count)
	{
		if (parent == null || !parent.IsInsideTree() || count <= 0 || materialId == 0)
		{
			return;
		}

		var scene = DropScene;
		if (scene == null)
		{
			GD.PushError("[DropSpawner] Failed to load VoxelDrop.tscn");
			return;
		}

		var root = parent.GetTree().Root;

		// Find (or create) the shared "Drops" container under the root.
		Node3D dropsRoot = root.GetNodeOrNull<Node3D>("Drops");
		if (dropsRoot == null)
		{
			dropsRoot = new Node3D { Name = "Drops" };
			root.AddChild(dropsRoot);
		}

		var drop = scene.Instantiate<VoxelDrop>();
		drop.Name = $"Drop_{materialId}_{count}";
		dropsRoot.AddChild(drop);
		drop.Initialize(materialId, count);
		drop.GlobalPosition = worldPos;
		// _PhysicsProcess lets the body fall and settle before starting the
		// cosmetic bob, so the drop rests on the terrain like a real object.
	}
}
