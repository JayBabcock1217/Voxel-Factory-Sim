using Godot;
using FactorySim.Items;
using FactorySim.Voxel;
using System;
using System.Collections.Generic;

namespace FactorySim;

/// <summary>
/// A basic zombie enemy that walks toward the player and stops when within
/// <see cref="StopDistance"/> meters. Uses a CharacterBody3D so it interacts
/// with terrain collision (walks on the ground, doesn't fall through).
///
/// Spawn: the zombie is invisible and frozen until the player has spawned
/// (which guarantees the surrounding chunks are loaded). It then queries the
/// terrain height at its own XZ via the VoxelWorld, snaps to the surface,
/// and becomes active.
///
/// Detection is range-based: the zombie only starts chasing when the player
/// is within <see cref="DetectionRange"/> meters. Once aggro'd it keeps
/// chasing until the player leaves that range again.
///
/// Pathfinding: the zombie uses a local grid-based A* pathfinder
/// (<see cref="EnemyPathfinder"/>) to walk across the terrain surface toward
/// the player. The path follows the ground, avoiding holes (void columns)
/// and steep cliffs (steps larger than <see cref="MaxStepUp"/> /
/// <see cref="MaxStepDown"/>). The zombie follows the path waypoints one at
/// a time, re-pathing every <see cref="RepathInterval"/> seconds or when the
/// current path is exhausted.
///
/// Tool: the zombie always carries a single tool (configurable in the
/// editor via <see cref="ToolType"/> and <see cref="ToolPower"/>). When the
/// pathfinder finds NO path to the player (a wall or impassable terrain
/// between them), the zombie falls back to breaking the nearest blocking
/// voxel in the player's direction using the same damage model as the
/// player's VoxelTool (ToolPower × affinity / BreakTime). Broken voxels
/// spawn item drops.
///
/// The zombie finds the player and the voxel world via their groups
/// ("player" and "voxel_world"), so it works without any manual references.
/// </summary>
[GlobalClass]
public partial class Zombie : CharacterBody3D
{
	[ExportGroup("Movement")]
	/// <summary>Speed while walking toward the player, in meters per second.</summary>
	[Export] public float WalkSpeed { get; set; } = 2.5f;

	/// <summary>Gravity applied while in the air (matches the player's value).</summary>
	[Export] public float Gravity { get; set; } = 20f;

	[ExportGroup("AI")]
	/// <summary>
	/// Distance in meters at which the zombie stops walking and faces the
	/// player. Defaults to 1 m.
	/// </summary>
	[Export(PropertyHint.Range, "0.5,10,0.1")]
	public float StopDistance { get; set; } = 1.0f;

	/// <summary>
	/// Maximum distance in meters at which the zombie can see the player and
	/// begin chasing. Set to a very large value to make it always aggro.
	/// </summary>
	[Export(PropertyHint.Range, "1,100,1")]
	public float DetectionRange { get; set; } = 15.0f;

	/// <summary>
	/// How often (in seconds) the zombie re-evaluates its target and path.
	/// Keeps the AI cheap without making it feel unresponsive.
	/// </summary>
	[Export(PropertyHint.Range, "0.05,1,0.05")]
	public float AiTickInterval { get; set; } = 0.1f;

	[ExportGroup("Pathfinding")]
	/// <summary>
	/// Maximum step-up (in voxels) the zombie will climb. A value of 2 means
	/// the zombie can walk up 1- or 2-voxel steps but not 3-voxel cliffs.
	/// </summary>
	[Export(PropertyHint.Range, "0,8,1")]
	public int MaxStepUp { get; set; } = 2;

	/// <summary>
	/// Maximum step-down (in voxels) the zombie will walk down. A value of 4
	/// means the zombie can walk down ledges up to 4 voxels but will not jump
	/// into deeper pits (it will path around them instead).
	/// </summary>
	[Export(PropertyHint.Range, "0,16,1")]
	public int MaxStepDown { get; set; } = 4;

	/// <summary>
	/// The search radius (in voxels) for the A* pathfinder. The search area is
	/// a square of (2R+1)² voxels centered on the zombie. Larger = better paths
	/// around obstacles, but more expensive. 20 covers a 41×41 area (1681 cells).
	/// </summary>
	[Export(PropertyHint.Range, "4,64,1")]
	public int SearchRadius { get; set; } = 20;

	/// <summary>
	/// How often (in seconds) the zombie re-computes its path. Between
	/// re-paths it follows the existing waypoints. Should be a multiple of
	/// <see cref="AiTickInterval"/>.
	/// </summary>
	[Export(PropertyHint.Range, "0.5,10,0.1")]
	public float RepathInterval { get; set; } = 1.0f;

	[ExportGroup("Tool")]
	/// <summary>
	/// The type of tool the zombie carries. Determines the break affinity
	/// (e.g. a Pickaxe is fast on stone, a Shovel is fast on dirt).
	/// </summary>
	[Export] public ToolType Tool { get; set; } = ToolType.Pickaxe;

	/// <summary>
	/// The zombie's tool power (damage multiplier). The damage dealt to a
	/// voxel per second is <c>(ToolPower × affinity / BreakTime) × 100%</c>,
	/// matching the player's VoxelTool model. A power of 1.0 with a 1.0
	/// affinity breaks a material in exactly its BreakTime seconds.
	/// </summary>
	[Export(PropertyHint.Range, "0.1,32,0.05")]
	public float ToolPower { get; set; } = 1.0f;

	/// <summary>
	/// Maximum raycast distance (meters) for detecting a blocking voxel
	/// between the zombie and the player (used when no path is found).
	/// </summary>
	[Export(PropertyHint.Range, "1,32,0.5")]
	public float BreakRaycastDistance { get; set; } = 8f;

	/// <summary>
	/// Half the capsule height — the distance from the body origin to the
	/// feet. Used to place the zombie on the terrain surface.
	/// </summary>
	private const float HalfHeight = 0.9f;

	private Node3D _player;
	private VoxelWorld _world;
	private float _aiTimer;
	private float _repathTimer;
	private Vector3 _moveDirection;
	private bool _aggroed;
	private bool _spawned;
	private bool _isBreaking;
	private bool _warnedGate;

	// The current path (list of world-voxel waypoints) and the index of the
	// next waypoint to walk to. The zombie follows waypoints in order,
	// advancing to the next one when it is within WaypointArriveDistance.
	private List<Vector3I> _path = new();
	private int _pathIndex;

	// When no path is found, the world voxel that is blocking the route.
	// The zombie breaks this voxel (and subsequent ones in the same column
	// or adjacent columns) to open a path. Zero when a path exists or no
	// blocker was identified.
	private Vector3I _blockedVoxel;

	/// <summary>
	/// Distance (in meters) from a waypoint at which the zombie considers
	/// itself "arrived" and advances to the next waypoint.
	/// </summary>
	private const float WaypointArriveDistance = 0.8f;

	// Per-voxel mining damage (same model as VoxelTool._mineDamage): keyed by
	// world voxel, value is accumulated damage in percentage points (0..100).
	// The damage PERSISTS across AI ticks, so if the zombie re-aims at the
	// same voxel it resumes where it left off.
	private readonly Dictionary<Vector3I, float> _mineDamage = new();

	/// <summary>
	/// True once the zombie has snapped to the terrain and is active in the
	/// world. Used by the save system to know whether the zombie's state is
	/// worth persisting.
	/// </summary>
	public bool IsSpawned => _spawned;

	/// <summary>
	/// Fired once, when the zombie passes its spawn gate and becomes active
	/// (visible + physics on). The spawning hive uses this to add the zombie to
	/// its tracking list — a zombie that is still waiting on the gate (chunk
	/// not full-res, or collision not ready) does NOT count against the hive's
	/// active cap, so a blocked spawn doesn't waste a slot.
	/// </summary>
	public event Action<Zombie> Activated;

	/// <summary>
	/// Set by the spawning hive so it can be notified when the zombie
	/// activates. The hive adds the zombie to its tracking list only on
	/// activation, not on instantiation.
	/// </summary>
	public Action<Zombie> OnActivated;

	public override void _Ready()
	{
		AddToGroup("zombie");

		// Invisible and frozen until we've spawned on the terrain.
		Visible = false;
		SetPhysicsProcess(false);

		// Find the player and world via their groups. Both may not be ready
		// yet (their _Ready can run after ours), so we retry each frame.
		_player = GetTree().GetFirstNodeInGroup("player") as Node3D;
		_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;

		// Subscribe to voxel-broken events so we can spawn drops for voxels
		// the zombie breaks.
		if (_world != null)
		{
			_world.VoxelBroken += OnVoxelBroken;
		}
	}

	public override void _ExitTree()
	{
		if (_world != null)
		{
			_world.VoxelBroken -= OnVoxelBroken;
		}
	}

	public override void _Process(double delta)
	{
		// Wait for the player to spawn. The player only appears after the
		// spawn gate opens (3x3 neighbourhood meshed + collision ready +
		// settle delay), which guarantees the chunks around the spawn point
		// are loaded. We use the player's presence as the "world is ready"
		// signal.
		if (!_spawned)
		{
			if (_player == null || !GodotObject.IsInstanceValid(_player))
			{
				_player = GetTree()?.GetFirstNodeInGroup("player") as Node3D;
			}
			if (_world == null)
			{
				_world = GetTree()?.GetFirstNodeInGroup("voxel_world") as VoxelWorld;
				if (_world != null)
				{
					_world.VoxelBroken += OnVoxelBroken;
				}
			}

			// The player's IsSpawned flag is set by VoxelWorld when the
			// spawn gate opens (chunks loaded, player placed, physics on).
			// This is our "world is ready" signal.
			if (_player is Player player && GodotObject.IsInstanceValid(player)
				&& player.IsSpawned && _world != null)
			{
				SpawnOnTerrain();
			}
			return;
		}

		// Spawned: run the AI tick (throttled).
		float dt = (float)delta;
		_aiTimer += dt;
		if (_aiTimer >= AiTickInterval)
		{
			_aiTimer = 0f;
			UpdateAi();
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (!_spawned || !IsInsideTree())
		{
			return;
		}

		float dt = (float)delta;

		// Gravity.
		if (IsOnFloor())
		{
			Velocity = new Vector3(Velocity.X, 0f, Velocity.Z);
		}
		else
		{
			Velocity = new Vector3(Velocity.X, Velocity.Y - Gravity * dt, Velocity.Z);
		}

		// Horizontal movement toward the current waypoint (or zero when
		// breaking / stopped / no path).
		Velocity = new Vector3(
			_moveDirection.X * WalkSpeed,
			Velocity.Y,
			_moveDirection.Z * WalkSpeed);

		MoveAndSlide();
	}

	/// <summary>
	/// Queries the terrain height at the zombie's XZ, places the zombie on
	/// the surface, makes it visible, and enables physics.
	/// </summary>
	private void SpawnOnTerrain()
	{
		int wx = (int)System.Math.Round(GlobalPosition.X / _world.VoxelSize);
		int wz = (int)System.Math.Round(GlobalPosition.Z / _world.VoxelSize);
		int surfaceY = FindTerrainHeight(wx, wz);

		if (surfaceY < 0)
		{
			return; // terrain not loaded yet; retry next frame
		}

		// Two gates before activating:
		// 1. The chunk must be FULL-RES (within RenderDistanceChunks of the
		//    player). Beyond that, the terrain is low-res LOD (heightfield, no
		//    per-voxel collision) — a zombie can't stand on or path across LOD.
		// 2. The sub-cell under the zombie must have a LIVE collision body with
		//    a shape attached (the same ground-truth check the player's spawn
		//    gate uses). This guarantees the ground actually collides on the
		//    zombie's very first physics step. The body is only attached within
		//    CollisionRadiusChunks of the player, so a zombie at a hive outside
		//    that radius waits here until the player gets close — which is the
		//    intended behaviour (hives are dormant outside the full-res area).
		bool fullRes = _world.IsFullResChunkAt(GlobalPosition);
		bool liveCollision = _world.HasLiveCollisionAt(GlobalPosition);
		if (!fullRes || !liveCollision)
		{
			// Diagnostic: log once which gate is blocking activation.
			if (!_warnedGate)
			{
				_warnedGate = true;
				GD.PushWarning($"[Zombie] Spawn gate blocked at {GlobalPosition}: fullRes={fullRes}, liveCollision={liveCollision}, surfaceY={surfaceY}");
			}
			return; // chunk not ready yet; retry next frame
		}

		float groundY = (surfaceY + 1) * _world.VoxelSize + HalfHeight;
		GlobalPosition = new Vector3(GlobalPosition.X, groundY, GlobalPosition.Z);

		_spawned = true;
		Visible = true;
		SetPhysicsProcess(true);

		// Notify the spawner that we are now active. This is the signal the
		// hive uses to count us against its cap — a zombie that never passes
		// the gate never fires this, so it never counts.
		OnActivated?.Invoke(this);
		Activated?.Invoke(this);
	}

	/// <summary>
	/// Marks the zombie as already-spawned (skipping the terrain-snapping
	/// wait). Used by the save system when restoring a zombie at its exact
	/// saved position.
	/// </summary>
	public void MarkAsSpawned()
	{
		_spawned = true;
		Visible = true;
		SetPhysicsProcess(true);
	}

	/// <summary>
	/// Scans downward from the top of the chunk to find the highest solid
	/// voxel at the given world XZ. Returns the voxel Y of the surface, or
	/// -1 if the chunk is not loaded.
	/// </summary>
	private int FindTerrainHeight(int wx, int wz)
	{
		if (_world == null)
		{
			return -1;
		}

		int maxH = _world.ChunkHeight;
		for (int y = maxH - 1; y >= 0; y--)
		{
			int id = _world.GetVoxel(wx, y, wz);
			if (id != 0)
			{
				return y;
			}
		}
		return -1;
	}

	/// <summary>
	/// The main AI tick. Decides what the zombie does this tick:
	///
	/// 1. If not aggro'd → stand still.
	/// 2. If within StopDistance → face the player, stop.
	/// 3. Always compute (or reuse) a path first. If a path is found, follow
	///    the next waypoint.
	/// 4. If no path is found (the route is blocked), the zombie breaks the
	///    blocking voxel along the path direction. After breaking enough
	///    voxels, the next re-path will find a route.
	/// </summary>
	private void UpdateAi()
	{
		// Guard: the zombie may have been freed (e.g. by ClearAllZombiesImmediate
		// on a save reload) but _Process still fires one last time before the
		// node is fully removed from the tree. GlobalPosition throws on a
		// freed node.
		if (!IsInsideTree())
		{
			return;
		}

		// Re-resolve the player if the cached reference is stale (the player
		// was freed, e.g. on a save switch or scene reload). IsInstanceValid
		// returns false for a disposed object without throwing.
		if (_player == null || !GodotObject.IsInstanceValid(_player))
		{
			_player = GetTree()?.GetFirstNodeInGroup("player") as Node3D;
			if (_player == null)
			{
				_moveDirection = Vector3.Zero;
				_isBreaking = false;
				_path.Clear();
				return;
			}
		}

		Vector3 toPlayer = _player.GlobalPosition - GlobalPosition;
		// Ignore vertical for distance checks.
		toPlayer.Y = 0;
		float dist = toPlayer.Length();

		// Aggro / de-aggro.
		if (!_aggroed && dist > DetectionRange)
		{
			_moveDirection = Vector3.Zero;
			_isBreaking = false;
			_path.Clear();
			return;
		}
		if (dist <= DetectionRange)
		{
			_aggroed = true;
		}

		// Stop when close enough.
		if (dist <= StopDistance)
		{
			_moveDirection = Vector3.Zero;
			_isBreaking = false;
			_path.Clear();
			FaceToward(toPlayer);
			return;
		}

		// Repath timer: re-compute the path periodically. We ALWAYS re-path
		// when the path is exhausted or expired, so the zombie always has the
		// best available route.
		_repathTimer += AiTickInterval;

		bool needRepath =
			_path.Count == 0
			|| _pathIndex >= _path.Count
			|| _repathTimer >= RepathInterval;

		if (needRepath)
		{
			_repathTimer = 0f;
			ComputePath();
		}

		// If we have a path, follow it.
		if (_path.Count > 0 && _pathIndex < _path.Count)
		{
			_isBreaking = false;
			FollowPath();
			return;
		}

		// No path found: the route is blocked. Break the blocking voxel
		// (along the path direction) to open a route.
		TryBreakBlockingVoxel(toPlayer, dist);
	}

	/// <summary>
	/// Computes a new path from the zombie to the player using the
	/// EnemyPathfinder. The path is stored in <see cref="_path"/> and the
	/// index is reset to 0. When no path is found (impassable terrain),
	/// <see cref="_path"/> is left empty and <see cref="_blockedVoxel"/> is
	/// set to the world voxel that is blocking the route (the zombie should
	/// break this voxel to open a path).
	/// </summary>
	private void ComputePath()
	{
		if (_world == null || !GodotObject.IsInstanceValid(_world))
		{
			_path.Clear();
			_blockedVoxel = Vector3I.Zero;
			return;
		}

		float vs = _world.VoxelSize;
		Vector3I startVoxel = new Vector3I(
			Mathf.FloorToInt(GlobalPosition.X / vs),
			0,
			Mathf.FloorToInt(GlobalPosition.Z / vs));
		Vector3I goalVoxel = new Vector3I(
			Mathf.FloorToInt(_player.GlobalPosition.X / vs),
			0,
			Mathf.FloorToInt(_player.GlobalPosition.Z / vs));

		// If the player is outside the search radius, clamp the goal to the
		// boundary (the pathfinder does this internally, but we also cap the
		// effective radius to avoid searching too far).
		float distToPlayer = (GlobalPosition - _player.GlobalPosition).Length();
		int effectiveRadius = System.Math.Min(SearchRadius,
			(int)System.Math.Ceiling(distToPlayer / vs) + 2);

		var result = EnemyPathfinder.FindPath(_world, startVoxel, goalVoxel,
			effectiveRadius, MaxStepUp, MaxStepDown);
		_path = result.Path;
		_pathIndex = 0;

		// When no path was found, record the blocking voxel (relative to the
		// start, converted to world coordinates). The zombie will break this
		// voxel (and subsequent ones along the same direction) to open a path.
		if (result.Path.Count == 0)
		{
			if (result.BlockedDirection != Vector3I.Zero)
			{
				_blockedVoxel = new Vector3I(
					startVoxel.X + result.BlockedDirection.X,
					0, // Y is filled in below from the surface
					startVoxel.Z + result.BlockedDirection.Z);
				// Find the topmost solid voxel in that column to target.
				int colX = _blockedVoxel.X;
				int colZ = _blockedVoxel.Z;
				int surfaceY = FindTerrainHeight(colX, colZ);
				if (surfaceY >= 0)
				{
					_blockedVoxel = new Vector3I(colX, surfaceY, colZ);
				}
				else
				{
					// No ground in that column — the "blocker" is actually a
					// void (hole). We can't break a hole; fall back to the
					// raycast-toward-player approach.
					_blockedVoxel = Vector3I.Zero;
				}
			}
			else
			{
				_blockedVoxel = Vector3I.Zero;
			}
		}
		else
		{
			_blockedVoxel = Vector3I.Zero;
		}
	}

	/// <summary>
	/// Follows the current path: walks toward the waypoint at
	/// <see cref="_pathIndex"/>. When within <see cref="WaypointArriveDistance"/>
	/// of it, advances to the next waypoint. When the path is exhausted,
	/// <see cref="_moveDirection"/> is zeroed (the next AI tick will re-path).
	/// </summary>
	private void FollowPath()
	{
		if (_world == null || _pathIndex >= _path.Count)
		{
			_moveDirection = Vector3.Zero;
			return;
		}

		Vector3I wp = _path[_pathIndex];
		float vs = _world.VoxelSize;

		// The waypoint's world position (center of the surface voxel).
		Vector3 wpPos = new Vector3(
			(wp.X + 0.5f) * vs,
			(wp.Y + 1f) * vs + HalfHeight, // feet on the surface
			(wp.Z + 0.5f) * vs);

		Vector3 toWaypoint = wpPos - GlobalPosition;
		toWaypoint.Y = 0; // horizontal only
		float wpDist = toWaypoint.Length();

		if (wpDist < WaypointArriveDistance)
		{
			// Arrived at this waypoint: advance to the next.
			_pathIndex++;
			if (_pathIndex >= _path.Count)
			{
				// Path exhausted: face the player; the next tick will re-path.
				_moveDirection = Vector3.Zero;
				Vector3 toPlayer = _player.GlobalPosition - GlobalPosition;
				toPlayer.Y = 0;
				FaceToward(toPlayer);
				return;
			}
			// Continue to the next waypoint this tick.
			wp = _path[_pathIndex];
			wpPos = new Vector3(
				(wp.X + 0.5f) * vs,
				(wp.Y + 1f) * vs + HalfHeight,
				(wp.Z + 0.5f) * vs);
			toWaypoint = wpPos - GlobalPosition;
			toWaypoint.Y = 0;
			wpDist = toWaypoint.Length();
		}

		if (wpDist < 0.01f)
		{
			_moveDirection = Vector3.Zero;
			return;
		}

		_moveDirection = toWaypoint.Normalized();
		FaceToward(toWaypoint);
	}

	/// <summary>
	/// Called when the pathfinder finds no route: the zombie breaks the
	/// blocking voxel to open a path. The target voxel comes from
	/// <see cref="_blockedVoxel"/> (the pathfinder's reported blocker, which
	/// is along the path direction). If that is not set (e.g. the blocker is
	/// a void/hole that can't be broken), it falls back to a raycast toward
	/// the player.
	/// </summary>
	private void TryBreakBlockingVoxel(Vector3 toPlayer, float distToPlayer)
	{
		if (_world == null || !GodotObject.IsInstanceValid(_world))
		{
			_moveDirection = Vector3.Zero;
			return;
		}

		// Primary: break the pathfinder-identified blocker (along the path).
		// The blocker must be within BreakRaycastDistance — the zombie can
		// only break what is in reach, even if the path is blocked further
		// away (in which case it walks toward the player to close the gap).
		if (_blockedVoxel != Vector3I.Zero)
		{
			float vs = _world.VoxelSize;
			Vector3 blockerPos = new Vector3(
				(_blockedVoxel.X + 0.5f) * vs,
				(_blockedVoxel.Y + 0.5f) * vs,
				(_blockedVoxel.Z + 0.5f) * vs);
			float blockerDist = (blockerPos - GlobalPosition).Length();

			if (blockerDist <= BreakRaycastDistance)
			{
				// Verify the voxel is still solid (it may have been broken by
				// another agent or a prior tick).
				int mat = _world.GetVoxel(_blockedVoxel.X, _blockedVoxel.Y, _blockedVoxel.Z);
				if (mat != VoxelMaterialIds.Air && !_world.IsUnbreakable(mat))
				{
					_moveDirection = Vector3.Zero;
					_isBreaking = true;
					FaceToward(toPlayer);
					ApplyMiningDamage(_blockedVoxel);
					return;
				}

				// The blocker is gone (already broken or became unbreakable):
				// clear it and re-path next tick to pick up the new route.
				_blockedVoxel = Vector3I.Zero;
				_repathTimer = RepathInterval; // force a re-path next tick
			}
			else
			{
				// Blocker is out of reach: don't break it. Walk toward the
				// player to close the gap (the next re-path will find the
				// blocker is closer, or a route may open up).
				_isBreaking = false;
				_moveDirection = toPlayer.Normalized();
				FaceToward(toPlayer);
				return;
			}
		}

		// Fallback: raycast toward the player and break the nearest solid
		// voxel in that direction.
		Vector3 rayFrom = GlobalPosition;
		Vector3 rayDir = (_player.GlobalPosition - GlobalPosition).Normalized();
		float maxDist = Mathf.Min(BreakRaycastDistance, distToPlayer);

		if (_world.RaycastVoxels(rayFrom, rayDir, maxDist, out Vector3I hitVoxel, out _))
		{
			float hitDist = (new Vector3(hitVoxel.X, hitVoxel.Y, hitVoxel.Z) * _world.VoxelSize
				+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize
				- GlobalPosition).Length();

			if (hitDist < distToPlayer - 0.5f)
			{
				_moveDirection = Vector3.Zero;
				_isBreaking = true;
				FaceToward(toPlayer);
				ApplyMiningDamage(hitVoxel);
				return;
			}
		}

		// No blocking voxel found (e.g. the path is clear but the pathfinder
		// couldn't find a route — the player might be across a wide gap).
		// Walk toward the player anyway as a last resort.
		_isBreaking = false;
		_moveDirection = toPlayer.Normalized();
		FaceToward(toPlayer);
	}

	/// <summary>
	/// Damages the target voxel over time, using the same model as the
	/// player's VoxelTool: <c>(ToolPower × affinity / BreakTime) × 100% per
	/// second</c>. Damage is accumulated per voxel in <see cref="_mineDamage"/>
	/// and persists across AI ticks. When the total reaches 100%, the voxel
	/// breaks and a drop is spawned (via the VoxelBroken event).
	/// </summary>
	private void ApplyMiningDamage(Vector3I targetVoxel)
	{
		if (_world == null)
		{
			return;
		}

		int material = _world.GetVoxel(targetVoxel.X, targetVoxel.Y, targetVoxel.Z);
		if (material == VoxelMaterialIds.Air)
		{
			_mineDamage.Remove(targetVoxel);
			_isBreaking = false;
			return;
		}

		if (_world.IsUnbreakable(material))
		{
			_mineDamage.Remove(targetVoxel);
			_isBreaking = false;
			return;
		}

		float damage = _mineDamage.TryGetValue(targetVoxel, out float stored) ? stored : 0f;

		float breakTime = 1.0f;
		if (_world.MaterialConfig != null && _world.MaterialConfig.GetDef(material) is { } def && def.Entry != null)
		{
			breakTime = Mathf.Max(0.05f, def.Entry.BreakTime);
		}

		float affinity = ToolAffinity.GetMultiplier(Tool, material);

		damage += ToolPower * affinity / breakTime * VoxelHealth.MaxHealth * AiTickInterval;

		if (damage >= VoxelHealth.MaxHealth)
		{
			_world.BreakVoxel(targetVoxel);
			_mineDamage.Remove(targetVoxel);
			_isBreaking = true;
			return;
		}

		_mineDamage[targetVoxel] = damage;
	}

	// ------------------------------------------------------------------
	// Drop spawning (VoxelBroken event handler)
	// ------------------------------------------------------------------

	private int _pendingEditStamp;

	private void OnVoxelBroken(Vector3I pos, int materialId)
	{
		if (materialId == VoxelMaterialIds.Air)
		{
			return;
		}

		if (_pendingEditStamp > 0)
		{
			if (_world != null)
			{
				_world.PendingDrops.Add(new VoxelWorld.PendingDrop(
					pos,
					materialId,
					_pendingEditStamp,
					_world.VoxelChunkCoord(pos),
					Time.GetTicksMsec() / 1000.0));
			}
			return;
		}

		SpawnDropForBrokenVoxel(pos, materialId);
	}

	private void SpawnDropForBrokenVoxel(Vector3I pos, int materialId)
	{
		if (_world == null)
		{
			return;
		}
		Vector3 worldPos = new Vector3(pos.X, pos.Y, pos.Z) * _world.VoxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * _world.VoxelSize;
		DropSpawner.SpawnDrop(GetTree().Root, worldPos, materialId, 1);
	}

	/// <summary>
	/// Rotates the body to face <paramref name="direction"/> (horizontal only)
	/// with a smooth turn so the zombie doesn't snap instantly.
	/// </summary>
	private void FaceToward(Vector3 direction)
	{
		if (direction.LengthSquared() < 0.001f)
		{
			return;
		}

		float targetYaw = Mathf.Atan2(direction.X, direction.Z);
		float currentYaw = Rotation.Y;

		float delta = targetYaw - currentYaw;
		while (delta > Mathf.Pi)  { delta -= Mathf.Tau; }
		while (delta < -Mathf.Pi) { delta += Mathf.Tau; }

		float turnSpeed = 8f;
		float maxStep = turnSpeed * 0.1f;
		Rotation = new Vector3(0, currentYaw + Mathf.Sign(delta) * Mathf.Min(Mathf.Abs(delta), maxStep), 0);
	}
}
