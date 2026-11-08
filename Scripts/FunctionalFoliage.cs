using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim;

/// <summary>
/// Base class for "functional foliage": a piece of world-growing vegetation
/// that is more than just voxels — it has runtime behavior. Concrete subclasses
/// (e.g. <see cref="Hive"/>) override the lifecycle hooks to add their
/// functionality (spawning enemies, producing resources, ...).
///
/// A piece of functional foliage:
/// - is PLACED into the terrain by the world (its voxels are part of the
///   chunk data, so it meshes and collides like normal terrain),
/// - is a live SCENE NODE (a <see cref="Node3D"/>) the world spawns when the
///   chunk containing it loads, so its behavior runs while it is in the world,
/// - KNOWS its own voxel footprint: it is grown as a shape (a sphere for hives)
///   centered on <see cref="CenterVoxel"/> with a known radius, and it recomputes
///   which of those voxels are still solid by querying the world. This lets a
///   subclass track "how much of me has been broken" without storing a per-voxel
///   list — robust to the player breaking AND replacing voxels.
///
/// Subclass contract:
/// - <see cref="OnSpawned"/>: called once, after the node is in the tree and the
///   world reference is set. Subscribe to events, start timers, etc.
/// - <see cref="OnVoxelRemoved"/>: called for each of the foliage's own voxels
///   that a player edit removes (routed from the world's <c>VoxelBroken</c>
///   event). The base updates <see cref="RemainingVoxelCount"/>; subclasses
///   override to react (e.g. a hive checks its depletion threshold).
/// - <see cref="OnUpdate"/>: called each <c>_Process</c> while spawned. Subclasses
///   override to run their periodic behavior.
///
/// The base tracks the voxel footprint generically (shape + center + radius), so
/// a future foliage type with a different shape (a vine, a crystal, ...) only
/// overrides <see cref="ShapeVoxels"/> to describe its own voxel set.
/// </summary>
[GlobalClass]
public partial class FunctionalFoliage : Node3D
{
	[ExportGroup("Placement")]
	/// <summary>
	/// The center of the foliage in world VOXEL coordinates. The world sets this
	/// when it spawns the node (it is the voxel the foliage was grown at). The
	/// voxel footprint is derived from this + <see cref="Radius"/> +
	/// <see cref="ShapeVoxels"/>.
	/// </summary>
	public Vector3I CenterVoxel { get; set; }

	/// <summary>
	/// The radius (in voxels) of the foliage's bounding shape. Combined with
	/// <see cref="ShapeVoxels"/> this describes the full voxel footprint.
	/// </summary>
	public int Radius { get; set; } = 2;

	/// <summary>
	/// The material id the foliage is made of (e.g. <see cref="VoxelMaterialIds.Hive"/>).
	/// Used to identify which broken voxels belong to this instance.
	/// </summary>
	public int MaterialId { get; set; } = VoxelMaterialIds.Hive;

	/// <summary>
	/// True once the node has been spawned into the world and <see cref="OnSpawned"/>
	/// has run. False before that (the node exists but is dormant).
	/// </summary>
	public bool IsSpawned { get; private set; }

	private VoxelWorld _world;
	private bool _worldSubscribed;

	/// <summary>
	/// The total number of voxels the foliage occupies when intact (computed from
	/// <see cref="ShapeVoxels"/> + <see cref="Radius"/>). Cached after the first
	/// computation.
	/// </summary>
	private int _totalVoxels = -1;

	/// <summary>
	/// The number of the foliage's voxels currently still SOLID in the world.
	/// Starts at <see cref="TotalVoxels"/> when spawned and decreases as the
	/// player breaks them (recomputed live, not just decremented, so replacing
	/// a broken voxel counts it back in).
	/// </summary>
	public int RemainingVoxelCount { get; private set; }

	/// <summary>
	/// The intact voxel count (computed from the shape). Lazily computed and
	/// cached. This is the THEORETICAL full shape (e.g. a complete sphere),
	/// which may be larger than the voxels actually present in the world (a
	/// sphere overhanging an unloaded chunk, or sitting partly on terrain, has
	/// fewer written voxels). For depletion tracking, use
	/// <see cref="IntactBaseline"/> instead.
	/// </summary>
	public int TotalVoxels
	{
		get
		{
			if (_totalVoxels < 0)
			{
				_totalVoxels = 0;
				foreach (var _ in EnumerateShapeVoxels())
				{
					_totalVoxels++;
				}
			}
			return _totalVoxels;
		}
	}

	/// <summary>
	/// The number of the foliage's voxels that were ACTUALLY present (its own
	/// material) at spawn time — the baseline for depletion tracking. This is
	/// the correct denominator for "how much of me has been broken": it reflects
	/// the voxels that really exist in the world (not the theoretical full shape,
	/// which may include cells in unloaded chunks or on terrain). Set in
	/// <see cref="Spawn"/>; 0 before spawn. A subclass may update this when the
	/// voxels become available later (e.g. a restored hive whose edit overlay
	/// is applied after Spawn ran).
	/// </summary>
	public int IntactBaseline { get; protected set; }

	/// <summary>
	/// The VoxelWorld this foliage lives in, or null before <see cref="Spawn"/>.
	/// </summary>
	protected VoxelWorld World => _world;

	/// <summary>
	/// Enumerates the OFFSETS (relative to <see cref="CenterVoxel"/>) of every
	/// voxel the foliage's shape occupies. The default is a solid sphere of
	/// <see cref="Radius"/>. A subclass with a different shape overrides this.
	/// </summary>
	protected virtual IEnumerable<Vector3I> EnumerateShapeVoxels()
	{
		int r2 = Radius * Radius;
		for (int dx = -Radius; dx <= Radius; dx++)
		{
			for (int dy = -Radius; dy <= Radius; dy++)
			{
				for (int dz = -Radius; dz <= Radius; dz++)
				{
					if (dx * dx + dy * dy + dz * dz > r2)
					{
						continue;
					}
					yield return new Vector3I(dx, dy, dz);
				}
			}
		}
	}

	/// <summary>
	/// True when the world voxel <paramref name="v"/> is part of this foliage's
	/// footprint (within the shape, centered on <see cref="CenterVoxel"/>).
	/// </summary>
	protected bool IsFootprintVoxel(Vector3I v)
	{
		var offset = v - CenterVoxel;
		// Quick bounding-box reject, then exact shape test.
		int ax = System.Math.Abs(offset.X);
		int ay = System.Math.Abs(offset.Y);
		int az = System.Math.Abs(offset.Z);
		if (ax > Radius || ay > Radius || az > Radius)
		{
			return false;
		}
		// Reuse the shape enumeration for an exact test: build the set once.
		// (Cheap: the shape is small and this is only called for candidate voxels.)
		foreach (var s in EnumerateShapeVoxels())
		{
			if (s == offset)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Spawns the foliage: resolves the world, recomputes its remaining voxel
	/// count, and fires <see cref="OnSpawned"/>. Called by the world once the
	/// chunk containing the foliage is loaded and the node is added to the tree.
	/// Safe to call once; a second call is a no-op.
	/// </summary>
	public void Spawn(VoxelWorld world)
	{
		if (IsSpawned)
		{
			return;
		}
		_world = world;
		if (_world != null)
		{
			_world.VoxelBroken += OnWorldVoxelBroken;
			_worldSubscribed = true;
		}
		RecomputeRemaining();
		// The baseline for depletion is the count of voxels that are ACTUALLY
		// present at spawn (not the theoretical full shape). For a fresh hive
		// this is the number of voxels the world wrote; for a restored hive it
		// reflects how many were left (a partially-broken hive comes back with a
		// smaller baseline, so it is already "partly spent").
		IntactBaseline = RemainingVoxelCount;
		IsSpawned = true;
		OnSpawned();
	}

	/// <summary>
	/// Recomputes <see cref="RemainingVoxelCount"/> by querying the world for
	/// each footprint voxel and counting the ones still solid. Called on spawn
	/// and whenever the base wants a fresh reading (e.g. after a batch of
	/// breaks).
	/// </summary>
	protected void RecomputeRemaining()
	{
		int count = 0;
		if (_world != null)
		{
			foreach (var offset in EnumerateShapeVoxels())
			{
				var v = CenterVoxel + offset;
				int mat = _world.GetVoxel(v.X, v.Y, v.Z);
				// Count a voxel as "remaining" when it is solid AND is the
				// foliage's own material. A replaced voxel (player placed dirt in
				// its place) does NOT count as intact foliage.
				if (mat == MaterialId)
				{
					count++;
				}
			}
		}
		RemainingVoxelCount = count;
	}

	/// <summary>
	/// Routes a world <c>VoxelBroken</c> event to <see cref="OnVoxelRemoved"/>
	/// when the broken voxel belongs to this foliage's footprint AND is the
	/// foliage's material.
	/// </summary>
	private void OnWorldVoxelBroken(Vector3I pos, int materialId)
	{
		if (!IsSpawned || _world == null)
		{
			return;
		}
		if (materialId != MaterialId)
		{
			return;
		}
		if (!IsFootprintVoxel(pos))
		{
			return;
		}
		RecomputeRemaining();
		OnVoxelRemoved(pos);
	}

	public override void _ExitTree()
	{
		if (_worldSubscribed && _world != null && GodotObject.IsInstanceValid(_world))
		{
			_world.VoxelBroken -= OnWorldVoxelBroken;
			_worldSubscribed = false;
		}
	}

	public override void _Process(double delta)
	{
		if (IsSpawned)
		{
			OnUpdate((float)delta);
		}
	}

	/// <summary>
	/// Called once after <see cref="Spawn"/> completes (the node is in the tree,
	/// the world is set, the voxel count is computed). Override to subscribe to
	/// extra events, start timers, or do one-time setup.
	/// </summary>
	protected virtual void OnSpawned()
	{
	}

	/// <summary>
	/// Called for each of the foliage's own voxels removed by a player edit (after
	/// <see cref="RemainingVoxelCount"/> has been refreshed). Override to react to
	/// the foliage being damaged (e.g. a hive checks its depletion threshold and
	/// stops spawning).
	/// </summary>
	protected virtual void OnVoxelRemoved(Vector3I pos)
	{
	}

	/// <summary>
	/// Called every <c>_Process</c> frame while spawned. Override to run the
	/// foliage's periodic behavior (e.g. a hive's spawn-interval timer).
	/// </summary>
	protected virtual void OnUpdate(float delta)
	{
	}
}
