using Godot;

namespace FactorySim.Items;

/// <summary>
/// A single dropped item resting on the terrain: a small bobbing/spinning box
/// that falls under gravity (via <see cref="CharacterBody3D"/>), merges with
/// nearby drops of the same material, and is collected into the player's
/// inventory when the player walks close.
///
/// Pickup is handled by an <c>Area3D</c> child ("DropPickup", collision layer 4)
/// that the player's own pickup area (collision mask 4) detects. When the
/// player's sphere overlaps the drop's pickup area, <see cref="Collect"/> fires.
///
/// Drops are spawned by <see cref="DropSpawner"/> (wired to
/// <see cref="Voxel.VoxelWorld.VoxelBroken"/> from <c>VoxelTool</c>). All drops
/// live under one "Drops" parent node and are also found via the
/// "voxel_drops" group (merge checks).
/// </summary>
public partial class VoxelDrop : CharacterBody3D
{
	/// <summary>Max distance between two same-material drops that merge (~5 voxels).</summary>
	public const float MergeDistance = 5.0f;

	/// <summary>How often (seconds) merge checks run.</summary>
	private const float CheckInterval = 0.1f;

	/// <summary>Fall acceleration, matching the player's gravity feel.</summary>
	private const float Gravity = 20f;

	/// <summary>Bob amplitude / period (seconds) and spin speed (rad/s).</summary>
	private const float BobAmplitude = 0.05f;
	private const float BobPeriod = 2.0f;
	private const float SpinSpeed = 1.5f;

	private ItemStack _stack = ItemStack.From(0);
	private MeshInstance3D _mesh;
	private Vector3 _restPosition;
	private bool _settled;
	private float _mergeTimer;

	/// <summary>Monotonic per-instance id used to break merge symmetry.</summary>
	private static int _nextId;
	private int _id;

	/// <summary>The item stack this drop represents.</summary>
	public ItemStack Stack => _stack;

	/// <summary>Convenience accessors for the stack's contents.</summary>
	public int MaterialId => _stack.MaterialId;
	public int Count => _stack.Count;

	/// <summary>
	/// Sets the drop's material/count and (re)builds its mesh + collision.
	/// Safe to call in <c>_Ready</c> or from the spawner before the node
	/// enters the tree.
	/// </summary>
	public void Initialize(int materialId, int count)
	{
		_stack = ItemStack.From(materialId, count);
		_restPosition = GlobalPosition;
		BuildMesh();
	}

	public override void _Ready()
	{
		_id = _nextId++;
		AddToGroup("voxel_drops");
		_mesh = GetNodeOrNull<MeshInstance3D>("DropMesh");
		BuildMesh();
	}

	public override void _PhysicsProcess(double delta)
	{
		float dt = (float)delta;

		// Gravity: rest on the terrain like a normal body.
		var vel = Velocity;
		vel.Y -= Gravity * dt;
		Velocity = vel;
		MoveAndSlide();

		// Spin is always on (cosmetic, doesn't fight the physics body).
		var rotation = Rotation;
		rotation.Y += SpinSpeed * dt;
		Rotation = rotation;

		// Settle: once on the floor, anchor the resting position and apply
		// the cosmetic bob. If the floor disappears (terrain edited beneath
		// the drop), un-settle and let gravity pull it down again.
		if (!_settled)
		{
			if (IsOnFloor())
			{
				_settled = true;
				_restPosition = GlobalPosition;
			}
		}
		else if (IsOnFloor())
		{
			// Still on the floor — apply the cosmetic bob.
			float t = Time.GetTicksMsec() / 1000f;
			float bob = Mathf.Sin(t * Mathf.Tau / BobPeriod) * BobAmplitude;
			GlobalPosition = _restPosition + new Vector3(0f, bob, 0f);
		}
		else
		{
			// Floor is gone (terrain edited) — fall again.
			_settled = false;
		}

		// Periodic merge check (drops are few; an O(n) group scan every 0.5s
		// is fine). Pickup is now handled by the Area3D signal, not polling.
		_mergeTimer += dt;
		if (_mergeTimer >= CheckInterval)
		{
			_mergeTimer = 0f;
			TryMerge();
		}
	}

	/// <summary>Merges with the nearest other same-material drop within range.</summary>
	private void TryMerge()
	{
		// Only the "lower" drop initiates the merge to avoid both drops
		// merging into each other in the same tick (which would corrupt
		// counts). InstanceId is unique and stable for the node's lifetime.
		foreach (Node otherNode in GetTree().GetNodesInGroup("voxel_drops"))
		{
			if (!(otherNode is VoxelDrop other) || other == this)
			{
				continue;
			}
			// Skip drops already queued for freeing this frame.
			if (other.IsQueuedForDeletion())
			{
				continue;
			}
			if (other.MaterialId != _stack.MaterialId)
			{
				continue;
			}
			if (GlobalPosition.DistanceTo(other.GlobalPosition) > MergeDistance)
			{
				continue;
			}
			// Only the drop with the lower id merges into the higher
			// one. The higher drop will skip this one (and vice versa), so
			// exactly one merge happens per pair per tick.
			if (_id > other._id)
			{
				continue;
			}

			// Merge as much as fits; the other keeps any remainder.
			int space = ItemStack.StackLimit - _stack.Count;
			if (space > 0)
			{
				int moved = Mathf.Min(space, other.Count);
				_stack = ItemStack.From(_stack.MaterialId, _stack.Count + moved);
				other._stack = ItemStack.From(other.MaterialId, other.Count - moved);
			}

			// The other is empty now: free it.
			if (other.Count <= 0)
			{
				other.QueueFree();
			}
			// A remainder is left on the other; keep scanning for more.
		}
	}

	/// <summary>
	/// Called by the player's pickup area when it overlaps this drop.
	/// Collects the drop into the inventory.
	/// </summary>
	public void Pickup()
	{
		Collect();
	}

	/// <summary>
	/// Collects as many items as the inventory can hold. If the whole stack
	/// fits the drop is freed; if only part fits the drop keeps the remainder
	/// and stays in the world.
	/// </summary>
	public void Collect()
	{
		if (GetTree().GetFirstNodeInGroup("inventory") is not Inventory inventory)
		{
			return;
		}

		int collected = inventory.CollectItem(_stack);
		if (collected <= 0)
		{
			// Inventory has no room for this material: stay put.
			return;
		}

		if (collected < _stack.Count)
		{
			// Partial pickup: keep the remainder.
			_stack = ItemStack.From(_stack.MaterialId, _stack.Count - collected);
		}
		else
		{
			QueueFree();
		}
	}

	/// <summary>
	/// Tints the scene's DropMesh with the material's albedo color from the
	/// world palette (grey fallback for unconfigured ids). The mesh and
	/// collision shape are defined in <c>VoxelDrop.tscn</c>.
	/// </summary>
	private void BuildMesh()
	{
		if (_mesh == null)
		{
			return;
		}

		_mesh.MaterialOverride = new StandardMaterial3D
		{
			AlbedoColor = LookUpColor(MaterialId),
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};

		_mesh.Visible = !_stack.IsEmpty;
	}

	/// <summary>
	/// The material's albedo from the world's palette (grey fallback for
	/// unconfigured ids — the palette is index <c>materialId - 1</c>).
	/// </summary>
	private Color LookUpColor(int materialId)
	{
		var world = GetTree() != null ? GetTree().GetFirstNodeInGroup("voxel_world") as Voxel.VoxelWorld : null;
		if (world != null && world.Palette != null
			&& world.Palette.Materials.Length > 0
			&& materialId > 0 && materialId - 1 < world.Palette.Materials.Length)
		{
			var mat = world.Palette.Materials[materialId - 1];
			if (mat != null)
			{
				return mat.AlbedoColor;
			}
		}
		return new Color(0.5f, 0.5f, 0.5f);
	}
}
