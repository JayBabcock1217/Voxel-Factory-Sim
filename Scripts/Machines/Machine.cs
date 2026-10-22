using Godot;
using System;

namespace FactorySim.Machines;

/// <summary>
/// A single placed machine in the world — the runtime node that a
/// <see cref="MachineDefinition"/> produces when the player places a machine
/// item. This is the base of the machine design system.
///
/// A <see cref="Machine"/> is a <see cref="Node3D"/> that:
///  - renders the mesh its <see cref="Definition"/> specifies (the user's
///    imported Blender mesh, or a small placeholder cube when none is set),
///  - optionally adds a collision shape (when the definition has one), and
///  - exposes a uniform set of VIRTUAL behavior hooks (<see cref="StartMachine"/>,
///    <see cref="ProcessMachine"/>, <see cref="ShutdownMachine"/>,
///    <see cref="OnBroken"/>) that concrete subclasses override to implement
///    machine-specific logic (a Smelter melts, a Turret shoots, etc.).
///
/// EXTENSIBILITY:
///  - A machine with NO custom logic uses this base class directly — its
///    behavior params (power, process time, output, ...) come from the
///    <see cref="Definition"/> and are available via the <see cref="Def"/>
///    property.
///  - A machine WITH custom logic subclasses <see cref="Machine"/> and
///    overrides the hooks. The <see cref="MachineDefinition.MachineSceneOverride"/>
///    field (or a <see cref="MachineRegistry"/> registration) selects the
///    subclass scene so the spawner instantiates the right node.
///
/// Machines are parented to a shared "Machines" container in the scene (created
/// on demand, like the "Drops" container for item drops), so all machines are
/// siblings and can be found/cleared from one place.
/// </summary>
public partial class Machine : Node3D
{
	/// <summary>The definition this machine was built from (its type, mesh, and stats).</summary>
	public MachineDefinition Definition { get; private set; }

	/// <summary>The machine's kind (a shortcut for <see cref="Definition"/>.Type).</summary>
	public MachineType Type => Definition?.Type ?? MachineType.Smelter;

	/// <summary>The world voxel this machine occupies (its logical anchor).</summary>
	public Vector3I VoxelPos { get; private set; }

	/// <summary>Current health (starts at the definition's <see cref="MachineDefinition.Health"/>).</summary>
	public int Health { get; protected set; }

	/// <summary>True while the machine is powered and running.</summary>
	public bool IsRunning { get; private set; }

	private MeshInstance3D _meshInstance;
	private Node _meshSceneRoot;
	private CollisionShape3D _collision;

	/// <summary>The placeholder cube shown when the definition has no mesh/scene.</summary>
	private static Mesh _placeholderMesh;

	/// <summary>
	/// Initializes this machine from a definition and places it at a world voxel.
	/// Called by the spawner after the node is added to the tree. Builds the
	/// visual (the definition's mesh/scene or a placeholder) and the collision
	/// (if any), then calls <see cref="StartMachine"/>.
	/// </summary>
	public void Initialize(MachineDefinition def, Vector3I voxelPos, float voxelSize)
	{
		Definition = def;
		VoxelPos = voxelPos;
		Health = def.Health;

		// Position the machine at the center of its voxel (the voxel's center is
		// where the player aimed, the same convention the voxel highlight uses).
		GlobalPosition = new Vector3(voxelPos.X, voxelPos.Y, voxelPos.Z) * voxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * voxelSize;

		BuildVisual(def);
		BuildCollision(def);

		// Let the concrete machine start its behavior (the base does nothing).
		StartMachine();
	}

	/// <summary>
	/// Builds the machine's visual from its definition:
	///   1. If <see cref="MachineDefinition.Mesh"/> is set, a MeshInstance3D with
	///      that mesh (scaled by the definition's Scale).
	///   2. Else if <see cref="MachineDefinition.MeshScene"/> is set, the
	///      instantiated scene.
	///   3. Else a small placeholder cube (so an un-meshed machine is still
	///      visible and its position is obvious).
	/// </summary>
	private void BuildVisual(MachineDefinition def)
	{
		if (def.Mesh != null)
		{
			_meshInstance = new MeshInstance3D
			{
				Name = "MachineMesh",
				Mesh = def.Mesh,
			};
			// Apply the definition's scale so the imported mesh fits the world.
			// Scaling about the node origin (the voxel center) keeps the mesh
			// centered on the voxel.
			_meshInstance.Scale = def.Scale;
			AddChild(_meshInstance);
			return;
		}

		if (def.MeshScene != null)
		{
			_meshSceneRoot = def.MeshScene.Instantiate();
			_meshSceneRoot.Name = "MachineScene";
			if (_meshSceneRoot is Node3D sceneNode)
			{
				sceneNode.Scale = def.Scale;
			}
			AddChild(_meshSceneRoot);
			return;
		}

		// No mesh assigned: show a small, clearly-identifiable placeholder cube
		// (a neutral grey, slightly smaller than a full voxel so it reads as a
		// "machine slot" the user hasn't textured yet).
		_placeholderMesh ??= MakePlaceholderMesh();
		_meshInstance = new MeshInstance3D
		{
			Name = "MachinePlaceholder",
			Mesh = _placeholderMesh,
		};
		AddChild(_meshInstance);
	}

	private static Mesh MakePlaceholderMesh()
	{
		var box = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) };
		return box;
	}

	/// <summary>
	/// Adds a collision shape to the machine when its definition has one, so the
	/// machine blocks the player and can be targeted. No-op when the definition
	/// has no collision shape (the machine is then visual-only).
	/// </summary>
	private void BuildCollision(MachineDefinition def)
	{
		if (def.CollisionShape == null)
		{
			return;
		}
		_collision = new CollisionShape3D
		{
			Name = "MachineCollision",
			Shape = def.CollisionShape,
		};
		AddChild(_collision);
	}

	// ------------------------------------------------------------------
	// Behavior hooks (override in subclasses for machine-specific logic)
	// ------------------------------------------------------------------

	/// <summary>
	/// Called once after the machine is placed and its visual built. Override to
	/// start the machine's behavior (e.g. begin a processing timer, aim a
	/// turret, start a conveyor belt). The base implementation just marks the
	/// machine running.
	/// </summary>
	protected virtual void StartMachine()
	{
		IsRunning = true;
	}

	/// <summary>
	/// Called each physics tick while the machine is running. Override to
	/// advance the machine's logic (process a recipe, fire, move items). The
	/// base does nothing. <paramref name="delta"/> is the physics frame delta
	/// in seconds.
	/// </summary>
	protected virtual void ProcessMachine(float delta)
	{
		// Base machines have no per-tick behavior; subclasses override.
	}

	/// <summary>
	/// Called when the machine stops running (powered off, broken, or removed).
	/// Override to clean up (stop timers, drop contents, un-aim). The base just
	/// clears the running flag.
	/// </summary>
	protected virtual void ShutdownMachine()
	{
		IsRunning = false;
	}

	/// <summary>
	/// Called when the machine is broken/removed from the world. Override to
	/// spawn drops, persist state, etc. The base does nothing.
	/// </summary>
	protected virtual void OnBroken()
	{
	}

	/// <summary>
	/// Damages the machine by <paramref name="amount"/>. Returns true when the
	/// machine broke (health reached 0), false when it survived. A machine with
	/// 0 max health (the definition's Health) is unbreakable.
	/// </summary>
	public bool TakeDamage(int amount)
	{
		if (Definition == null || Definition.Health <= 0 || amount <= 0)
		{
			return false; // unbreakable or no damage
		}
		Health -= amount;
		if (Health <= 0)
		{
			Health = 0;
			OnBroken();
			return true;
		}
		return false;
	}

	/// <summary>Stops the machine (idempotent). Called by the spawner on removal.</summary>
	public void Stop()
	{
		if (IsRunning)
		{
			ShutdownMachine();
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (IsRunning)
		{
			ProcessMachine((float)delta);
		}
	}

	public override void _ExitTree()
	{
		Stop();
	}
}
