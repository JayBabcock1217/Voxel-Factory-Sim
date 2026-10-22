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
///
/// The machine is a <see cref="StaticBody3D"/> (not a plain Node3D) so its
/// <see cref="CollisionShape3D"/> actually participates in physics — in Godot 4 a
/// collision shape only collides when its nearest ancestor is a physics body. A
/// StaticBody3D is the right choice for a static structure: the player's
/// CharacterBody3D MoveAndSlide() is blocked by it, and it never needs to move.
/// </summary>
public partial class Machine : StaticBody3D
{
	/// <summary>The definition this machine was built from (its type, mesh, and stats).</summary>
	public MachineDefinition Definition { get; private set; }

	/// <summary>
	/// The <see cref="VoxelWorld"/> this machine was placed into. Set during
	/// <see cref="Initialize"/>. Behavior subclasses use it to find neighbor
	/// machines (conveyor pull/push, crafter ingredient gathering) and to query
	/// the power network.
	/// </summary>
	public FactorySim.Voxel.VoxelWorld World { get; private set; }

	/// <summary>The machine's kind (a shortcut for <see cref="Definition"/>.Type).</summary>
	public MachineType Type => Definition?.Type ?? MachineType.Smelter;

	/// <summary>The world voxel this machine occupies (its logical anchor).</summary>
	public Vector3I VoxelPos { get; private set; }

	/// <summary>
	/// The machine's placement orientation (the 4×90° rotation the player chose
	/// with the R key before placing). Applied to the visual when built. Defaults
	/// to <see cref="MachineRotation.Identity"/>. Named PlacementRotation (not
	/// Rotation) to avoid hiding Node3D.Rotation (a Vector3 euler).
	/// </summary>
	public MachineRotation PlacementRotation { get; set; } = MachineRotation.Identity;

	/// <summary>Current health (starts at the definition's <see cref="MachineDefinition.Health"/>).</summary>
	public int Health { get; protected set; }

	/// <summary>True while the machine is powered and running.</summary>
	public bool IsRunning { get; private set; }

	private MeshInstance3D _meshInstance;
	private Node _meshSceneRoot;
	private CollisionShape3D _collision;

	/// <summary>The shared transparent material applied to a preview ghost's meshes.</summary>
	private static StandardMaterial3D _previewMaterial;

	private static StandardMaterial3D PreviewMaterial
	{
		get
		{
			// A single shared, semi-transparent, unshaded material for every
			// preview ghost. It is created once (lazily) and reused — a preview
			// is just a visual cue, not a real machine, so it does not need its
			// own lighting.
			_previewMaterial ??= new StandardMaterial3D
			{
				AlbedoColor = new Color(0.55f, 0.85f, 1.0f, 0.45f),
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled, // double-sided
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				EmissionEnabled = true,
				Emission = new Color(0.4f, 0.7f, 1.0f, 0.35f),
				EmissionEnergyMultiplier = 0.6f,
			};
			return _previewMaterial;
		}
	}

	/// <summary>
	/// True while this machine is acting as a transparent placement PREVIEW
	/// (a ghost shown at the target cell while the player holds a machine item).
	/// A preview is never "real": it does not run, does not block the player,
	/// and is not counted for occupancy. It is removed by the tool, not by the
	/// normal removal path.
	/// </summary>
	public bool IsPreview { get; set; }

	/// <summary>The placeholder cube shown when the definition has no mesh/scene.</summary>
	private static Mesh _placeholderMesh;

	/// <summary>
	/// Initializes this machine from a definition and places it at a world voxel.
	/// Called by the spawner after the node is added to the tree. Builds the
	/// visual (the definition's mesh/scene or a placeholder) and the collision
	/// (if any), then calls <see cref="StartMachine"/>. <paramref name="world"/>
	/// is the voxel world the machine was placed into (kept so behaviors can find
	/// neighbors and query power).
	/// </summary>
	public void Initialize(MachineDefinition def, Vector3I voxelPos, float voxelSize,
		FactorySim.Voxel.VoxelWorld world)
	{
		Definition = def;
		VoxelPos = voxelPos;
		Health = def.Health;
		World = world;

		// Position the machine at the center of its voxel (the voxel's center is
		// where the player aimed, the same convention the voxel highlight uses).
		GlobalPosition = new Vector3(voxelPos.X, voxelPos.Y, voxelPos.Z) * voxelSize
			+ new Vector3(0.5f, 0.5f, 0.5f) * voxelSize;

		// Apply the placement rotation (the R-key orientation) to the visual:
		// rotate the whole machine about its origin (the voxel center).
		Basis = PlacementRotation.BasisFor();

		BuildVisual(def);
		// A preview ghost must NOT block the player or count as solid — only a
		// real (placed) machine gets a collision shape.
		if (!IsPreview)
		{
			BuildCollision(def);
		}

		// Let the concrete machine start its behavior (the base does nothing).
		// A preview does not "run" — it is just a visual ghost.
		if (!IsPreview)
		{
			IsRunning = true;
			StartMachine();
		}
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
			if (IsPreview)
			{
				_meshInstance.MaterialOverride = PreviewMaterial;
			}
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
			// A preview ghost of a scene: apply the transparent override to every
			// mesh instance in the instantiated scene so the whole thing reads as
			// a translucent ghost.
			if (IsPreview)
			{
				ApplyPreviewToScene(_meshSceneRoot);
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
		if (IsPreview)
		{
			_meshInstance.MaterialOverride = PreviewMaterial;
		}
		AddChild(_meshInstance);
	}

	/// <summary>
	/// Applies the shared transparent preview material to every <see
	/// cref="MeshInstance3D"/> in an instantiated scene (recursively). Used for
	/// preview ghosts of <c>MeshScene</c>-based machines so the whole imported
	/// scene renders as a translucent ghost.
	/// </summary>
	private static void ApplyPreviewToScene(Node root)
	{
		if (root is MeshInstance3D mi)
		{
			mi.MaterialOverride = PreviewMaterial;
		}
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			ApplyPreviewToScene(root.GetChild(i));
		}
	}

	/// <summary>
	/// Converts an already-built machine into a transparent PREVIEW ghost. The
	/// <see cref="MachineSpawner"/> builds a machine as a real (opaque) machine;
	/// the tool then flips <see cref="IsPreview"/> on and calls this to swap in
	/// the transparent material override on every mesh and remove any collision
	/// the spawner added (a ghost must not block the player). Idempotent: safe
	/// to call when the machine is already a preview.
	/// </summary>
	public void ApplyPreviewVisual()
	{
		IsPreview = true;

		// Swap in the transparent material on the primary mesh instance.
		if (_meshInstance != null)
		{
			_meshInstance.MaterialOverride = PreviewMaterial;
		}
		// For a MeshScene-based machine, override every mesh in the scene.
		if (_meshSceneRoot != null)
		{
			ApplyPreviewToScene(_meshSceneRoot);
		}

		// A ghost must not block the player or count as solid: remove any
		// collision the spawner added for a real machine. A StaticBody3D with no
		// collision shape is inert (it registers no physics geometry), so
		// removing the shape is sufficient to make the ghost non-blocking.
		if (_collision != null)
		{
			_collision.QueueFree();
			_collision = null;
		}
	}

	private static Mesh MakePlaceholderMesh()
	{
		var box = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) };
		return box;
	}

	/// <summary>
	/// Builds the machine's collision shape so it blocks the player. The shape is
	/// an AUTO-GENERATED <see cref="BoxShape3D"/> sized to the visual mesh's
	/// bounding box (AABB) — this works for any imported mesh (single mesh,
	/// multi-mesh scene, or the placeholder cube) and always produces a
	/// non-degenerate box, so a machine always collides. The box is sized with a
	/// small padding margin so the collider slightly overhangs the mesh (prevents
	/// the player from clipping through thin parts).
	///
	/// The shape is built in the machine's LOCAL space (centered on the origin,
	/// the voxel center), so it rotates WITH the machine when the placement
	/// rotation (the R-key orientation) is applied to the body's Basis.
	/// </summary>
	private void BuildCollision(MachineDefinition def)
	{
		// Collect the combined AABB of every mesh in the machine's visual, in
		// local space (the machine's origin is the voxel center). The visual was
		// just built, so its meshes are in the tree and their AABBs are valid.
		Aabb combined = default;
		bool first = true;
		CollectAabb(_meshInstance, ref combined, ref first);
		CollectAabb(_meshSceneRoot, ref combined, ref first);

		// Fall back to a full-voxel box if no mesh produced a usable AABB (e.g.
		// an empty scene), so the machine still collides with something sensible.
		if (first)
		{
			combined = new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1f, 1f, 1f));
		}

		// Pad the box slightly so the collider overhangs the mesh a touch.
		const float pad = 0.05f;
		combined.Grow(pad);

		// A BoxShape3D is always centered on its own origin, so position it at
		// the (padded) AABB's center to sit exactly over the mesh (the mesh may
		// not be centered on the machine's origin, e.g. a Blender export whose
		// origin is at a corner).
		var box = new BoxShape3D { Size = combined.Size };
		Vector3 center = combined.Position + combined.Size * 0.5f;

		_collision = new CollisionShape3D
		{
			Name = "MachineCollision",
			Shape = box,
			Transform = new Transform3D(Basis.Identity, center),
		};
		AddChild(_collision);
	}

	/// <summary>
	/// Folds a node's mesh AABB(s) into <paramref name="combined"/> (union).
	/// Handles a single <see cref="MeshInstance3D"/> or a scene root (recursing
	/// into all descendants that are MeshInstance3Ds). <paramref name="first"/>
	/// starts true and is set false once a non-empty AABB is folded in.
	/// </summary>
	private static void CollectAabb(Node node, ref Aabb combined, ref bool first)
	{
		if (node == null)
		{
			return;
		}
		if (node is MeshInstance3D mi)
		{
			Aabb a = mi.GetAabb(); // local-space AABB of the mesh (incl. node scale)
			// A zero-size AABB means no mesh (or an empty one) — skip it. Aabb
			// has no IsEmpty property in this SDK, so test the size directly.
			if (a.Size.LengthSquared() > 0f)
			{
				combined = first ? a : combined.Merge(a);
				first = false;
			}
		}
		for (int i = 0; i < node.GetChildCount(); i++)
		{
			CollectAabb(node.GetChild(i), ref combined, ref first);
		}
	}

	// ------------------------------------------------------------------
	// Neighbor / item-transfer helpers (shared by behavior subclasses)
	// ------------------------------------------------------------------

	/// <summary>
	/// The machine occupying a world voxel (any of the six face-adjacent "any
	/// side" neighbors), or null when that cell has no (non-preview) machine.
	/// Used by conveyors and processors to find what is next to them.
	/// </summary>
	protected Machine NeighborMachine(Vector3I voxelPos)
	{
		if (World == null)
		{
			return null;
		}
		return PowerNetwork.MachineAt(World, voxelPos);
	}

	/// <summary>
	/// True when this machine currently has enough power to run (see
	/// <see cref="PowerNetwork.IsPowered"/>). Passive machines (no
	/// <see cref="MachineDefinition.PowerConsumption"/> and no output) always
	/// return true. Behavior subclasses call this in their per-tick hook and
	/// skip work when false (idle, keep buffers).
	/// </summary>
	protected bool HasPower()
	{
		if (World == null)
		{
			return true; // no world → don't gate
		}
		return PowerNetwork.IsPowered(World, VoxelPos);
	}

	/// <summary>
	/// The six face-adjacent neighbor positions of this machine's voxel (the
	/// "any side" set). Convenience wrapper around <see cref="PowerNetwork.Neighbors"/>.
	/// </summary>
	protected Vector3I[] NeighborPositions() => PowerNetwork.Neighbors(VoxelPos);

	/// <summary>
	/// The machine's INPUT buffer — where items enter (conveyors push into it,
	/// the player inserts into it via the UI). The base machine has none (null);
	/// a processor (crafter/smelter) overrides this to expose its input buffer.
	/// A conveyor has no input buffer (it has a single slot, not an input).
	/// </summary>
	public virtual MachineBuffer InputBuffer => null;

	/// <summary>
	/// The machine's OUTPUT buffer — where items leave (conveyors pull from it).
	/// The base machine has none (null); a processor overrides this to expose its
	/// output buffer. A conveyor exposes its single slot here so an adjacent
	/// conveyor can pull its item (see <see cref="ConveyorMachine"/>).
	/// </summary>
	public virtual MachineBuffer OutputBuffer => null;

	/// <summary>
	/// True when this machine can RECEIVE an item into its input from an adjacent
	/// conveyor (i.e. it has a non-full input buffer). Conveyors use this to
	/// decide whether to push. Virtual: a conveyor overrides it (it can receive
	/// only when its single slot is empty, not when the buffer is "not full").
	/// </summary>
	public virtual bool CanReceive()
	{
		var input = InputBuffer;
		return input != null && !input.IsFull;
	}

	/// <summary>
	/// True when this machine has an item available to be PULLED from its output
	/// by an adjacent conveyor. Conveyors use this to decide whether to pull.
	/// Virtual: a conveyor overrides it (its output is its single slot).
	/// </summary>
	public virtual bool HasOutput()
	{
		var output = OutputBuffer;
		return output != null && !output.IsEmpty;
	}

	// ------------------------------------------------------------------
	// Behavior hooks (override in subclasses for machine-specific logic)
	// ------------------------------------------------------------------

	/// <summary>
	/// Called once after the machine is placed and its visual built, once it is
	/// marked running. Override to start the machine's behavior (e.g. begin a
	/// processing timer, aim a turret, start a conveyor belt). The base does
	/// nothing. (The machine is already running — the base marks it running in
	/// <see cref="Initialize"/>.)
	/// </summary>
	protected virtual void StartMachine()
	{
		// Base machines have no one-time start-up behavior; subclasses override.
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
