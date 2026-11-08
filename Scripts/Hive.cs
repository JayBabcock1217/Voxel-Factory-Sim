using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim;

/// <summary>
/// A zombie HIVE: a purple/pink spherical blob of
/// <see cref="VoxelMaterialIds.Hive"/> voxels grown on the terrain that
/// periodically spawns ZOMBIES around it, on top of the terrain.
///
/// Behavior:
/// - <see cref="MaxActiveZombies"/> caps how many of its zombies can be alive
///   at once. The hive tracks the zombies it spawns (by weak reference) and
///   notices when one dies by finding its reference stale / the node invalid
///   (a freed zombie), which frees the slot for the next spawn.
/// - <see cref="SpawnIntervalSeconds"/> gates the spawn rate: the hive checks
///   its active count once per interval and spawns one zombie if below cap.
/// - It spawns each zombie at a random point within <see cref="SpawnRadius"/>
///   voxels around its center, snapped down to the terrain surface (so it
///   always lands on solid ground).
/// - Once <see cref="DepletionFraction"/> (default 0.25) of its voxels are
///   broken, it STOPS SPAWNING (it is "depleted"). The base class tracks the
///   voxel count and calls <see cref="OnVoxelRemoved"/> for each break.
///
/// Persistence: the world saves each hive's position + depleted state (see
/// <see cref="FactorySim.Voxel.WorldSaveFile.HiveRecord"/>). On load the world
/// restores the nodes; the voxel count is recomputed from the (edited) terrain
/// so a partially-broken hive comes back in the right state.
/// </summary>
[GlobalClass]
public partial class Hive : FunctionalFoliage
{
	[ExportGroup("Spawning")]
	[Export]
	/// <summary>The maximum number of zombies this hive can have alive at once.</summary>
	public int MaxActiveZombies { get; set; } = 4;

	[Export]
	/// <summary>
	/// How often (seconds) the hive checks its active count and spawns a zombie
	/// if below cap. The rate limit.
	/// </summary>
	public float SpawnIntervalSeconds { get; set; } = 6f;

	[Export]
	/// <summary>
	/// The radius (in world units) around the hive center in which zombies
	/// spawn. A spawn point is picked uniformly in this disc (on the XZ plane)
	/// and snapped to the terrain surface.
	/// </summary>
	public float SpawnRadius { get; set; } = 6f;

	[Export]
	/// <summary>The scene spawned for each zombie (the Zombie.tscn instance).</summary>
	public PackedScene ZombieScene { get; set; }

	[ExportGroup("Depletion")]
	[Export]
	/// <summary>
	/// The fraction (0..1) of the hive's voxels that must be broken before the
	/// hive stops spawning. Default 0.25 = stop when a quarter of it is gone.
	/// </summary>
	public float DepletionFraction { get; set; } = 0.25f;

	/// <summary>True once the hive is depleted and has stopped spawning.</summary>
	public bool IsDepleted { get; private set; }

	/// <summary>
	/// Force-marks the hive as depleted (stops spawning). Used when restoring a
	/// hive from a save that recorded it as depleted. Idempotent.
	/// </summary>
	public void MarkDepleted()
	{
		IsDepleted = true;
	}

	private float _spawnTimer;
	private readonly List<WeakReference<Zombie>> _spawnedZombies = new();
	private SceneTree _tree;
	private bool _warnedNoScene;
	private bool _warnedNoSurface;
	private bool _warnedAtCap;
	private bool _baselineResolved;

	protected override void OnSpawned()
	{
		_tree = GetTree();

		// Start the timer near the end of its first interval so the hive does
		// not dump all its cap worth of zombies the instant it loads.
		_spawnTimer = SpawnIntervalSeconds * 0.5f;

		// Resolve the voxel baseline. When the hive's voxels are already in the
		// terrain (fresh placement, or a restored hive whose chunk was
		// re-meshed before Spawn ran), RecomputeRemaining finds them and the
		// baseline is set immediately. When they are NOT yet in the terrain
		// (restored hive in a chunk that just became DataReady but whose edit
		// overlay hasn't been applied, or a chunk that generated after the
		// save was written), the count is 0 — we mark the baseline as
		// unresolved and retry each tick until the voxels appear.
		if (RemainingVoxelCount > 0)
		{
			_baselineResolved = true;
			UpdateDepletion();
		}

		// Adopt any zombies already near this hive (restored from a save). The
		// world re-spawns saved enemies (ApplyEnemies) as root-level zombies that
		// are NOT in this hive's tracking list, so without this a restored hive
		// would treat itself as having 0 active zombies and spawn a full cap's
		// worth on top of the ones that were already here. Adopting the nearby
		// ones keeps the per-hive cap meaningful across a save/load.
		AdoptNearbyZombies();
	}

	/// <summary>
	/// Scans the "zombie" group for live zombies within <see cref="SpawnRadius"/>
	/// of the hive center and adopts them into <see cref="_spawnedZombies"/>
	/// (up to <see cref="MaxActiveZombies"/>), so the hive's cap accounts for the
	/// zombies that were already present (e.g. restored from a save). Only
	/// zombies this hive did not already track are adopted, so a double-spawn
	/// (the hive spawning a zombie that is then re-adopted) is impossible.
	/// </summary>
	private void AdoptNearbyZombies()
	{
		if (_tree == null)
		{
			return;
		}

		// Build a set of already-tracked (valid) zombies so we don't re-adopt.
		var tracked = new HashSet<Zombie>();
		foreach (var wr in _spawnedZombies)
		{
			if (wr.TryGetTarget(out Zombie z) && GodotObject.IsInstanceValid(z))
			{
				tracked.Add(z);
			}
		}

		float r2 = SpawnRadius * SpawnRadius;
		foreach (Node node in _tree.GetNodesInGroup("zombie"))
		{
			if (_spawnedZombies.Count >= MaxActiveZombies)
			{
				break;
			}
			if (node is not Zombie z || !GodotObject.IsInstanceValid(z))
			{
				continue;
			}
			if (tracked.Contains(z))
			{
				continue;
			}
			// Distance in XZ (the hive spawns in a disc on the ground plane).
			Vector3 to = z.GlobalPosition - GlobalPosition;
			float d2 = to.X * to.X + to.Z * to.Z;
			if (d2 <= r2)
			{
				tracked.Add(z);
				_spawnedZombies.Add(new WeakReference<Zombie>(z));
			}
		}
	}

	protected override void OnVoxelRemoved(Vector3I pos)
	{
		UpdateDepletion();
	}

	protected override void OnUpdate(float delta)
	{
		if (IsDepleted)
		{
			return;
		}

		// The hive only activates (spawns zombies) when its chunk is FULL-RES
		// (within RenderDistanceChunks of the player). Beyond that, the terrain
		// is low-res LOD (heightfield, no per-voxel collision) — zombies can't
		// stand on or path across LOD terrain. The hive keeps running (baseline
		// resolution, depletion tracking) but skips spawning until the player
		// is close enough. The spawn timer does NOT advance while dormant, so
		// a dormant hive doesn't burn its interval on a no-op tick.
		if (World != null && !World.IsFullResChunkAt(GlobalPosition))
		{
			return; // dormant: not close enough to the player
		}

		// If the baseline hasn't been resolved yet (the hive's voxels weren't
		// in the terrain when Spawn ran — e.g. a restored hive in a chunk that
		// just became DataReady), retry now. The voxels should be present once
		// the edit overlay is applied (ApplyLoadedChunk) or the chunk is
		// regenerated. Once we find them, the baseline locks and depletion
		// tracking becomes meaningful.
		if (!_baselineResolved)
		{
			RecomputeRemaining();
			if (RemainingVoxelCount > 0)
			{
				IntactBaseline = RemainingVoxelCount;
				_baselineResolved = true;
				UpdateDepletion();
			}
			return; // don't spawn until we know our own size
		}

		_spawnTimer += delta;
		if (_spawnTimer < SpawnIntervalSeconds)
		{
			return;
		}
		_spawnTimer = 0f;

		int active = CountActiveZombies();
		if (active >= MaxActiveZombies)
		{
			if (!_warnedAtCap)
			{
				_warnedAtCap = true;
				GD.Print($"[Hive] Hive at {CenterVoxel} at zombie cap ({active}/{MaxActiveZombies}) — no new spawns.");
			}
			return;
		}

		if (ZombieScene == null)
		{
			if (!_warnedNoScene)
			{
				_warnedNoScene = true;
				GD.PushWarning("[Hive] ZombieScene is null — hive at " + CenterVoxel + " cannot spawn zombies. (Check Hive.tscn's ZombieScene reference.)");
			}
			return;
		}

		Vector3I spawnVoxel = PickSpawnVoxel();
		if (spawnVoxel == Vector3I.Zero && World == null)
		{
			return;
		}
		if (spawnVoxel == Vector3I.Zero)
		{
			// No surface found in range (e.g. the chunk under the hive is
			// unloaded, or it is over a void/cave). Retry on the next interval.
			if (!_warnedNoSurface)
			{
				_warnedNoSurface = true;
				GD.PushWarning("[Hive] No terrain surface found near hive at " + CenterVoxel + " — waiting for the ground to load before spawning.");
			}
			return;
		}

		SpawnZombieAt(spawnVoxel);
	}

	/// <summary>
	/// Re-evaluates the depletion threshold from the (already-updated) remaining
	/// voxel count. Idempotent: once depleted it stays depleted (a hive does
	/// not come back to life if the player rebuilds it).
	/// </summary>
	private void UpdateDepletion()
	{
		if (IsDepleted)
		{
			return;
		}
		// Depletion is measured against the voxels that were ACTUALLY present at
		// spawn (the baseline), not the theoretical full shape — so a hive that
		// only partially made it into the world (overhang on an unloaded chunk,
		// or restored from a save already partly broken) is judged fairly.
		int baseline = IntactBaseline;
		if (baseline <= 0)
		{
			return;
		}
		// Depleted when fewer than (1 - fraction) of the baseline voxels remain.
		if (RemainingVoxelCount < baseline * (1f - DepletionFraction))
		{
			IsDepleted = true;
		}
	}

	/// <summary>
	/// Prunes dead/invalid zombies from the tracking list and returns how many
	/// are still alive. Called before each spawn decision.
	/// </summary>
	private int CountActiveZombies()
	{
		for (int i = _spawnedZombies.Count - 1; i >= 0; i--)
		{
			if (!_spawnedZombies[i].TryGetTarget(out Zombie z) || !GodotObject.IsInstanceValid(z))
			{
				_spawnedZombies.RemoveAt(i);
			}
		}
		return _spawnedZombies.Count;
	}

	/// <summary>
	/// Picks a random voxel to spawn a zombie at: a uniform point within
	/// <see cref="SpawnRadius"/> (world units) of the hive center on the XZ
	/// plane, snapped to the terrain surface height at that XZ. Returns the
	/// voxel just ABOVE the surface (the zombie's feet) so it lands on ground.
	/// Falls back to the hive center when no surface is found in range.
	/// </summary>
	private Vector3I PickSpawnVoxel()
	{
		if (World == null)
		{
			return Vector3I.Zero;
		}

		float radius = Mathf.Max(1f, SpawnRadius);
		// The inner exclusion ring: points within this XZ distance of the hive
		// center are INSIDE (or on) the hive sphere. Zombies must spawn in the
		// ring AROUND the hive, not on top of it. The hive has Radius voxels,
		// so we exclude that distance (plus 1 for the ground-level overhang).
		float innerR = Radius + 1f;

		for (int attempt = 0; attempt < 16; attempt++)
		{
			// Uniform in an ANNULUS (ring between innerR and radius): random
			// angle + radius drawn uniformly from [innerR, radius].
			float angle = (float)GD.RandRange(0.0, System.Math.PI * 2);
			float t = (float)GD.RandRange(0.0, 1.0);
			float dist = innerR + (radius - innerR) * t;
			float dx = (float)System.Math.Cos(angle) * dist;
			float dz = (float)System.Math.Sin(angle) * dist;

			int wx = CenterVoxel.X + Mathf.RoundToInt(dx);
			int wz = CenterVoxel.Z + Mathf.RoundToInt(dz);
			int surface = World.GetSurfaceHeight(wx, wz);
			if (surface < 0)
			{
				continue; // void / cave / unloaded — try another point.
			}

			// If the "surface" is a hive voxel (the sphere overhangs this column),
			// scan further down to find the actual ground beneath the hive.
			if (World.GetVoxel(wx, surface, wz) == VoxelMaterialIds.Hive)
			{
				for (int y = surface - 1; y >= 0; y--)
				{
					int mat = World.GetVoxel(wx, y, wz);
					if (mat != VoxelMaterialIds.Air && mat != VoxelMaterialIds.Hive)
					{
						surface = y; // found the real ground under the hive
						break;
					}
					if (mat != VoxelMaterialIds.Hive)
					{
						surface = y; // any non-hive solid is the ground
						break;
					}
				}
			}

			return new Vector3I(wx, surface + 1, wz);
		}

		// No clear surface found in the ring: fall back to just outside the
		// hive on the +X side (guaranteed to be in the annulus).
		int fx = CenterVoxel.X + Mathf.CeilToInt(innerR);
		int fz = CenterVoxel.Z;
		int fSurface = World.GetSurfaceHeight(fx, fz);
		if (fSurface < 0)
		{
			return Vector3I.Zero;
		}
		// Skip over any hive overhang at the fallback point.
		if (World.GetVoxel(fx, fSurface, fz) == VoxelMaterialIds.Hive)
		{
			for (int y = fSurface - 1; y >= 0; y--)
			{
				int mat = World.GetVoxel(fx, y, fz);
				if (mat != VoxelMaterialIds.Air && mat != VoxelMaterialIds.Hive)
				{
					fSurface = y;
					break;
				}
			}
		}
		return new Vector3I(fx, fSurface + 1, fz);
	}

	/// <summary>
	/// Instantiates a zombie from <see cref="ZombieScene"/> at the given world
	/// VOXEL position (converted to world units) and tracks it. The zombie's
	/// "spawned" gate is left for the zombie itself (it waits for the player,
	/// just like the main-scene zombie).
	/// </summary>
	private void SpawnZombieAt(Vector3I voxel)
	{
		if (_tree == null || ZombieScene == null)
		{
			return;
		}

		var zombie = ZombieScene.Instantiate<Zombie>();
		if (zombie == null)
		{
			GD.PushError("[Hive] ZombieScene.Instantiate returned null — scene is corrupt or wrong type.");
			return;
		}

		// Add to the tree FIRST: GlobalPosition is only valid for a node that is
		// inside the tree (setting it before AddChild logs "Condition
		// !is_inside_tree()" and silently returns, leaving the zombie at the
		// origin). The world is at the origin, so local == global once in the
		// tree, but we assign GlobalPosition AFTER AddChild to be correct under
		// any parent transform.
		if (World != null)
		{
			World.AddChild(zombie);
		}
		else
		{
			AddChild(zombie);
		}

		float voxelSize = World != null ? World.VoxelSize : 1f;
		zombie.GlobalPosition = new Vector3(
			(voxel.X + 0.5f) * voxelSize,
			(voxel.Y + 0.5f) * voxelSize,
			(voxel.Z + 0.5f) * voxelSize);

		// Do NOT add the zombie to the tracking list here. The zombie is still
		// waiting on its own spawn gate (chunk full-res + live collision). A
		// zombie that is blocked there must not count against this hive's
		// active cap — otherwise a single blocked spawn wastes a slot and the
		// hive stops spawning. Instead we wait for the zombie's OnActivated
		// callback, which fires the moment it passes the gate and becomes
		// visible + active. That is the only moment a zombie truly counts.
		zombie.OnActivated += OnZombieActivated;
	}

	/// <summary>
	/// Called when a zombie this hive spawned passes its spawn gate and becomes
	/// active. This is the point at which the zombie starts counting against
	/// the hive's cap. The zombie is removed from the callback so a freed
	/// zombie (e.g. by ClearAllZombiesImmediate on a save reload) can't fire it
	/// again, and the tracking entry is added exactly once.
	/// </summary>
	private void OnZombieActivated(Zombie zombie)
	{
		if (zombie == null || !GodotObject.IsInstanceValid(zombie))
		{
			return;
		}
		zombie.OnActivated -= OnZombieActivated;

		// Avoid double-tracking if the zombie was already adopted (e.g. by
		// AdoptNearbyZombies running between instantiation and activation).
		foreach (var wr in _spawnedZombies)
		{
			if (wr.TryGetTarget(out Zombie existing)
				&& GodotObject.IsInstanceValid(existing)
				&& ReferenceEquals(existing, zombie))
			{
				return; // already tracked
			}
		}

		_spawnedZombies.Add(new WeakReference<Zombie>(zombie));
	}
}
