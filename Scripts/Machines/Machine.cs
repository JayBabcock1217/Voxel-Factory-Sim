using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Items;

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
[Tool]
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

	/// <summary>
	/// The small emissive arrow shown on a REAL (placed) machine that points
	/// along the machine's LOCAL +Z axis — its "front". Because the machine's
	/// Basis is the placement rotation, this child's local +Z IS the world-space
	/// front, so one shared visual works for every machine type with no
	/// per-type logic. Present only on real machines (never on preview ghosts),
	/// so this field is non-null only when the indicator is shown.
	/// </summary>
	private MeshInstance3D _directionArrow;

	/// <summary>
	/// The single shared, bright, unshaded emissive material for every direction
	/// arrow. Saturated cyan-green so it reads clearly against any machine mesh
	/// and in any lighting. Created once (lazily) and reused by all arrows —
	/// one material for the whole scene, like <see cref="PreviewMaterial"/>.
	/// </summary>
	private static StandardMaterial3D _directionArrowMaterial;

	private static StandardMaterial3D DirectionArrowMaterial
	{
		get
		{
			_directionArrowMaterial ??= new StandardMaterial3D
			{
				AlbedoColor = new Color(0.2f, 1.0f, 0.65f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, // reads in any light
				EmissionEnabled = true,
				Emission = new Color(0.2f, 1.0f, 0.65f),
				EmissionEnergyMultiplier = 1.0f,
			};
			return _directionArrowMaterial;
		}
	}

	// ------------------------------------------------------------------
	// Face-port indicators (the per-face input/output markers the DEV config)
	// ------------------------------------------------------------------

	/// <summary>
	/// The per-face INDICATOR nodes (one <see cref="MeshInstance3D"/> per face
	/// whose <see cref="MachineDefinition.Directions"/> entry is Input or
	/// Output). Indexed by face (0..5, the canonical <see cref="MachineFace"/>
	/// order); a null slot means that face has no indicator (disabled). These
	/// are children of the machine in LOCAL space, so they rotate with the
	/// placement rotation and always sit on the right world face. Built at
	/// <see cref="Initialize"/> from the definition's Directions, and shown/hidden
	/// together by <see cref="SetIndicatorsVisible"/> (the F2 toggle).
	/// </summary>
	private MeshInstance3D[] _faceIndicators = new MeshInstance3D[MachineFace.Count];

	/// <summary>
	/// Global visibility of all face-port indicators (the F2 toggle target).
	/// Default true (indicators shown). Flipped by <see cref="SetIndicatorsVisible"/>,
	/// which also shows/hides every placed machine's indicator nodes immediately.
	/// New machines built after a toggle inherit the current value.
	/// </summary>
	public static bool IndicatorsVisible { get; private set; } = true;

	/// <summary>The shared, bright, unshaded material for INPUT face indicators.</summary>
	private static StandardMaterial3D _inputIndicatorMaterial;

	private static StandardMaterial3D InputIndicatorMaterial
	{
		get
		{
			// A saturated GREEN that reads as "items go IN here." Distinct from
			// the direction arrow (cyan-green) by hue and by being a flat face
			// marker rather than a wedge.
			_inputIndicatorMaterial ??= new StandardMaterial3D
			{
				AlbedoColor = new Color(0.15f, 0.95f, 0.35f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				EmissionEnabled = true,
				Emission = new Color(0.15f, 0.95f, 0.35f),
				EmissionEnergyMultiplier = 1.0f,
			};
			return _inputIndicatorMaterial;
		}
	}

	/// <summary>The shared, bright, unshaded material for OUTPUT face indicators.</summary>
	private static StandardMaterial3D _outputIndicatorMaterial;

	private static StandardMaterial3D OutputIndicatorMaterial
	{
		get
		{
			// A saturated ORANGE/AMBER that reads as "items come OUT here."
			// Clearly distinct from the green input markers in any lighting.
			_outputIndicatorMaterial ??= new StandardMaterial3D
			{
				AlbedoColor = new Color(1.0f, 0.6f, 0.1f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				EmissionEnabled = true,
				Emission = new Color(1.0f, 0.6f, 0.1f),
				EmissionEnergyMultiplier = 1.0f,
			};
			return _outputIndicatorMaterial;
		}
	}

	/// <summary>
	/// Toggles the global visibility of every placed machine's face-port
	/// indicators (the F2 key calls this with the new value). Flips
	/// <see cref="IndicatorsVisible"/> and immediately shows/hides every
	/// indicator node under the shared "Machines" container, so existing
	/// machines update without being rebuilt. New machines built afterward
	/// inherit the current value. Safe to call with the same value (idempotent).
	/// </summary>
	public static void SetIndicatorsVisible(bool visible)
	{
		IndicatorsVisible = visible;
		var tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null)
		{
			return; // no scene tree (editor idle); the flag is still set for next build
		}
		var machinesRoot = tree.Root.GetNodeOrNull<Node3D>("Machines");
		if (machinesRoot == null)
		{
			return;
		}
		for (int i = 0; i < machinesRoot.GetChildCount(); i++)
		{
			if (machinesRoot.GetChild(i) is Machine m)
			{
				m.ApplyIndicatorVisibility();
			}
		}
	}

	/// <summary>
	/// Shows or hides THIS machine's face-port indicator nodes according to the
	/// current <see cref="IndicatorsVisible"/> flag. Called from
	/// <see cref="SetIndicatorsVisible"/> for every placed machine, and from
	/// <see cref="BuildFaceIndicators"/> so a freshly-built machine respects the
	/// current toggle.
	/// </summary>
	private void ApplyIndicatorVisibility()
	{
		for (int i = 0; i < _faceIndicators.Length; i++)
		{
			if (_faceIndicators[i] != null)
			{
				_faceIndicators[i].Visible = IndicatorsVisible;
			}
		}
	}

	/// <summary>The shared transparent material applied to a preview ghost's meshes.</summary>
	private static StandardMaterial3D _previewMaterial;

	private static StandardMaterial3D PreviewMaterial
	{
		get
		{
			// A single shared, VERY faint, unshaded material for every preview
			// ghost. It is created once (lazily) and reused — a preview is just
			// a visual cue, not a real machine, so it does not need its own
			// lighting. The opacity is deliberately LOW (the ghost should read as
			// a faint silhouette, with the bright green direction arrow as the
			// main cue); the albedo and emission alphas were cut from ~0.45/0.35
			// to ~0.15/0.10 so the ghost mesh no longer washes out the world.
			_previewMaterial ??= new StandardMaterial3D
			{
				AlbedoColor = new Color(0.55f, 0.85f, 1.0f, 0.15f),
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled, // double-sided
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				EmissionEnabled = true,
				Emission = new Color(0.4f, 0.7f, 1.0f, 0.10f),
				EmissionEnergyMultiplier = 0.4f,
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
		// The face-port indicators (the dev-set input/output markers) are shown
		// on BOTH real machines and preview ghosts: a ghost is exactly when the
		// player most wants to see which faces the machine type has ports on,
		// and they rotate with the ghost as it is turned (R key). They are built
		// for every machine here (the single place they are (re)created) and
		// their visibility follows the global F2 toggle.
		BuildFaceIndicators(def);
		// A preview ghost must NOT block the player or count as solid — only a
		// real (placed) machine gets a collision shape.
		if (!IsPreview)
		{
			BuildCollision(def);
			// Only real machines show the direction arrow: a ghost is just a
			// visual cue the player is actively rotating, so an arrow on it
			// would be redundant clutter. Real machines get the arrow (re)built
			// here at Initialize — there is no ghost→real conversion path (the
			// spawner always builds the machine as a real machine first), so
			// Initialize is the single place the indicator is (re)created.
			BuildDirectionIndicator();
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

		// A ghost SHOULD show the direction arrow: it is the visual cue that
		// tells the player which way the machine faces while they rotate the
		// ghost (R key). Build it now if the spawner's Initialize didn't already
		// (Initialize only builds it for real machines, and the spawner builds
		// the ghost as a real machine before flipping IsPreview — but if the
		// arrow is missing, build it so the preview always shows the direction).
		if (_directionArrow == null)
		{
			BuildDirectionIndicator();
		}
	}

	private static Mesh MakePlaceholderMesh()
	{
		var box = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) };
		return box;
	}

	/// <summary>
	/// Builds (or re-builds) the machine's direction indicator: one small
	/// prism (a wedge whose triangle cross-section reads as an arrowhead)
	/// parented to the machine, stretched along the machine's LOCAL +Z axis —
	/// the machine's "front" (the same convention
	/// <c>ConveyorMachine.ComputeFlowAxis</c> uses). Because the machine's
	/// Basis is already the placement rotation, the child's local +Z is the
	/// world-space front — no extra rotation is applied and no per-type logic
	/// is needed, so one shared visual works for smelters, crafters, power
	/// sources, and conveyors alike.
	///
	/// A <see cref="PrismMesh"/> is the fallback: the built-in <c>ArrowMesh</c>
	/// is NOT in this GodotSharp 4.8 binding. A PrismMesh needs no custom
	/// geometry, no texture, and no extra nodes. The indicator is deliberately
	/// SUPER CHEAP: one primitive mesh, one shared unshaded emissive material
	/// (reads in any light), no shadows, set up once at placement — no custom
	/// geometry, no light, no per-frame updates.
	///
	/// Preview ghosts DO get the indicator — it is the cue that shows which way
	/// the machine faces while the player rotates the ghost (R key). Real and
	/// preview machines both build it the same way.
	/// </summary>
	private void BuildDirectionIndicator()
	{
		// Re-init safety: free any stale arrow before building a new one.
		if (_directionArrow != null)
		{
			_directionArrow.QueueFree();
			_directionArrow = null;
		}

		// Scale everything to the world's voxel size so the marker is the same
		// relative size on the machine at any voxel scale (guard a missing
		// world: Initialize always sets one, but stay safe).
		float voxelSize = World != null ? World.VoxelSize : 1.0f;

		// This binding's PrismMesh is a wedge (a triangle cross-section, its
		// cross-section lying in the XZ plane), NOT a cone — and it has no
		// radius properties. That is still exactly right: its triangular
		// cross-section is a clear arrowhead shape, and with Size.Z it
		// stretches ALONG the machine's front axis (+Z), so no rotation is
		// needed at all. Sized in voxel units (scaled to the voxel size
		// below): short and narrow in X/Y (a cue, not the body), longer along
		// Z so it reads as an arrow pointing where the machine faces.
		var arrow = new PrismMesh
		{
			Size = new Vector3(0.24f, 0.12f, 0.4f),
		};

		_directionArrow = new MeshInstance3D
		{
			Name = "DirectionArrow",
			Mesh = arrow,
			MaterialOverride = DirectionArrowMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // no shadow cost
		};

		// Position it near the FRONT face (local +Z) and slightly above center
		// so it sits just in front of the machine body — visible, pointing at
		// the machine's front, without occluding the mesh. The prism is
		// centered on its origin, so placing the origin ~0.4 voxels forward of
		// center puts its tip ~0.6 voxels forward (a little past the front face
		// on purpose — that is what makes the direction read unambiguously).
		_directionArrow.Position = new Vector3(0f, 0.25f, 0.4f) * voxelSize;
		// Scale to the voxel size (the mesh itself is authored in ~1-voxel units).
		_directionArrow.Scale = new Vector3(voxelSize, voxelSize, voxelSize);

		AddChild(_directionArrow);
	}

	/// <summary>
	/// Builds (or re-builds) this machine's FACE-PORT indicators — one small flat
	/// marker on each face whose <see cref="MachineDefinition.Directions"/> entry
	/// is <see cref="MachinePort.Input"/> or <see cref="MachinePort.Output"/>.
	/// Input faces get an input-colored marker, output faces an output-colored
	/// one, and disabled faces get nothing.
	///
	/// Each indicator is a thin <see cref="BoxMesh"/> plate sitting just OUTSIDE
	/// the machine's face (offset by half a voxel along that face's axis), in the
	/// machine's LOCAL space — so the machine's placement-rotation Basis turns
	/// the whole set and every plate stays glued to the correct world face. The
	/// plate is small (a corner of the face, not the whole face) and unshaded
	/// emissive (reads in any light, no shadow cost), keeping it SUPER CHEAP: a
	/// primitive mesh per enabled face, shared materials, set up once at build.
	///
	/// The set of indicators is driven entirely by the DEV-set definition config
	/// (<see cref="MachineDefinition.Directions"/>), NOT per-instance state, so
	/// every machine of a type shows the same faces. Visibility follows the
	/// global F2 toggle (<see cref="IndicatorsVisible"/>). Idempotent: any stale
	/// indicators are freed first.
	/// </summary>
	private void BuildFaceIndicators(MachineDefinition def)
	{
		// Free any stale indicators (re-init safety).
		for (int i = 0; i < _faceIndicators.Length; i++)
		{
			if (_faceIndicators[i] != null)
			{
				_faceIndicators[i].QueueFree();
				_faceIndicators[i] = null;
			}
		}

		float voxelSize = World != null ? World.VoxelSize : 1.0f;

		// The six face ports from the definition (all-disabled when the
		// definition or its Directions is missing / short).
		var directions = def?.Directions;
		if (directions == null)
		{
			ApplyIndicatorVisibility();
			return;
		}

		for (int face = 0; face < MachineFace.Count; face++)
		{
			MachinePort port = face < directions.Length ? directions[face] : MachinePort.Disabled;
			if (port == MachinePort.Disabled)
			{
				continue; // no indicator on a disabled face
			}

			// The face's outward unit direction as a Vector3 (Vector3 has no
			// Vector3I constructor in this SDK, so build it from the components).
			Vector3I dirI = MachineFace.FaceDirections[face];
			Vector3 dir = new Vector3(dirI.X, dirI.Y, dirI.Z);
			// A thin plate (a flat box) sized to a corner of the face. The thin
			// dimension runs ALONG the face's outward axis so the plate sits just
			// proud of the surface; the two in-face dimensions are the plate's
			// width/height. Kept small so it reads as a marker, not a panel.
			var plateSize = new Vector3(0.22f, 0.22f, 0.06f);
			// Orient the plate so its thin axis is the face normal:
			//   +X/-X face → thin in X; +Y/-Y face → thin in Y; +Z/-Z face → thin in Z.
			switch (face)
			{
				case 0: // +X
				case 1: // -X
					plateSize = new Vector3(0.06f, 0.22f, 0.22f);
					break;
				case 2: // +Y
				case 3: // -Y
					plateSize = new Vector3(0.22f, 0.06f, 0.22f);
					break;
				// 4 (+Z) / 5 (-Z): default plateSize (thin in Z)
			}
			var plate = new BoxMesh { Size = plateSize };

			// Position the plate at the face, pushed slightly OUTSIDE the surface
			// (half a voxel along the face normal + a hair past the surface) so it
			// sits proud of the machine body without z-fighting the mesh.
			Vector3 pos = dir * (0.5f * voxelSize + 0.04f * voxelSize);

			var indicator = new MeshInstance3D
			{
				Name = $"FacePort_{MachineFace.Label(MachineFace.FaceDirections[face])}",
				Mesh = plate,
				MaterialOverride = port == MachinePort.Input
					? InputIndicatorMaterial
					: OutputIndicatorMaterial,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			};
			indicator.Position = pos;
			indicator.Scale = new Vector3(voxelSize, voxelSize, voxelSize);
			AddChild(indicator);
			_faceIndicators[face] = indicator;
		}

		// Apply the current global visibility so a freshly-built machine respects
		// the F2 toggle state.
		ApplyIndicatorVisibility();
	}

	/// <summary>
	/// Rebuilds this machine's collision shape from its CURRENT visual. Use
	/// this after the visual has changed (e.g. the definition's mesh or scale
	/// was re-imported, or the machine was re-initialized with a new definition).
	/// The old collision shape is freed and a new one is generated from the
	/// current visual's AABB. Preview machines (ghosts) have no collision and
	/// are left unchanged.
	/// </summary>
	public void RebuildCollision()
	{
		if (IsPreview)
		{
			return; // ghosts have no collision
		}
		if (_collision != null)
		{
			_collision.QueueFree();
			_collision = null;
		}
		BuildCollision(Definition);
	}

	/// <summary>
	/// Computes the machine's collision box (size + center offset) from a
	/// <see cref="MachineDefinition"/>'s mesh/scene — PURELY on the resource,
	/// with NO live machine node or scene tree required. This is what the
	/// editor's "Generate Collision Boxes" button calls to author the collision
	/// box outside of runtime.
	///
	/// It temporarily instantiates the definition's mesh/scene (respecting the
	/// definition's <see cref="MachineDefinition.Scale"/>) into a throwaway node
	/// in the tree just long enough to read the combined AABB, then frees it.
	/// The result is in the machine's LOCAL space (origin = voxel center),
	/// matching what <see cref="BuildCollision"/> expects.
	///
	/// Returns the box size and center (in local space) via <paramref name="size"/>
	/// and <paramref name="center"/>. Returns false when no usable mesh is found
	/// (the caller should then fall back to a full-voxel box).
	/// </summary>
	public static bool TryGetCollisionBoxFromDefinition(MachineDefinition def,
		out Vector3 size, out Vector3 center)
	{
		size = Vector3.Zero;
		center = Vector3.Zero;

		if (def == null)
		{
			return false;
		}

		// Case 1: a single Mesh. Measure it directly from the resource (no scene
		// tree needed) — Mesh.GetAabb() returns the mesh's AABB in local space,
		// which we scale by the definition's Scale. This is the common case (a
		// Blender export assigned to the definition's Mesh field) and works
		// entirely in the editor, outside of runtime.
		if (def.Mesh != null)
		{
			Aabb meshAabb = def.Mesh.GetAabb();
			if (meshAabb.Size.LengthSquared() <= 0f)
			{
				return false; // empty mesh
			}

			// Apply the definition's uniform scale to the AABB. Scaling about the
			// mesh origin (0,0,0) matches how BuildVisual scales the MeshInstance3D.
			Vector3 scaledMin = meshAabb.Position * def.Scale;
			Vector3 scaledMax = (meshAabb.Position + meshAabb.Size) * def.Scale;
			Aabb scaled = new Aabb(scaledMin, scaledMax - scaledMin);

			const float pad = 0.05f;
			scaled.Grow(pad);

			size = scaled.Size;
			center = scaled.Position + scaled.Size * 0.5f;
			return true;
		}

		// Case 2: a PackedScene. We can't measure a scene's AABB without
		// instantiating it, but calling MeshInstance3D.GetAabb() on a freshly
		// instantiated (not-yet-rendered) scene in the EDITOR returns a stale or
		// empty AABB. So instead we:
		//   1. Instantiate the scene into a throwaway probe (to read each
		//      MeshInstance3D's GlobalTransform, which bakes in the scene root's
		//      scale + any per-mesh transforms).
		//   2. Measure each mesh's TRUE AABB from the Mesh RESOURCE itself
		//      (Mesh.GetAabb() is accurate and doesn't require rendering).
		//   3. Transform each mesh AABB by the instance's GlobalTransform and
		//      union them.
		// This gives a correct box in the editor, outside of runtime.
		if (def.MeshScene != null)
		{
			var sceneTree = Engine.GetMainLoop() as SceneTree;
			if (sceneTree == null)
			{
				return false; // no scene tree (shouldn't happen in editor/play)
			}

			var probe = new Node3D { Name = "_CollisionProbe" };
			sceneTree.Root.AddChild(probe);
			try
			{
				var visual = def.MeshScene.Instantiate();
				visual.Name = "ProbeScene";
				if (visual is Node3D sceneNode)
				{
					// Apply the definition's scale to the scene root, matching
					// how BuildVisual scales the instantiated scene.
					sceneNode.Scale = def.Scale;
				}
				probe.AddChild(visual);

				Aabb combined = default;
				bool first = true;
				CollectMeshResourceAabb(visual, ref combined, ref first);

				if (first)
				{
					return false; // empty scene / no usable mesh
				}

				const float pad = 0.05f;
				combined.Grow(pad);

				size = combined.Size;
				center = combined.Position + combined.Size * 0.5f;
				return true;
			}
			finally
			{
				probe.QueueFree();
			}
		}

		return false; // no mesh/scene assigned
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
	/// When the definition has an AUTHORED collision box
	/// (<see cref="MachineDefinition.HasCollisionBox"/>), that box is used
	/// directly (it was computed in the editor and stored on the resource), so
	/// the runtime collider matches what the user authored. Otherwise the AABB
	/// is computed from the live visual.
	///
	/// The shape is built in the machine's LOCAL space (centered on the origin,
	/// the voxel center), so it rotates WITH the machine when the placement
	/// rotation (the R-key orientation) is applied to the body's Basis.
	/// </summary>
	private void BuildCollision(MachineDefinition def)
	{
		Vector3 boxSize;
		Vector3 boxCenter;

		if (def != null && def.HasCollisionBox)
		{
			// Use the authored box (computed in the editor, stored on the resource).
			boxSize = def.CollisionBoxSize;
			boxCenter = def.CollisionBoxOffset;
		}
		else
		{
			// No authored box: compute the combined AABB of every mesh in the
			// machine's visual, in local space (the machine's origin is the voxel
			// center). The visual was just built, so its meshes are in the tree
			// and their AABBs are valid.
			Aabb combined = default;
			bool first = true;
			CollectAabb(_meshInstance, ref combined, ref first);
			CollectAabb(_meshSceneRoot, ref combined, ref first);

			// Fall back to a full-voxel box if no mesh produced a usable AABB
			// (e.g. an empty scene), so the machine still collides with something
			// sensible.
			if (first)
			{
				combined = new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1f, 1f, 1f));
			}

			// Pad the box slightly so the collider overhangs the mesh a touch.
			const float pad = 0.05f;
			combined.Grow(pad);

			boxSize = combined.Size;
			boxCenter = combined.Position + combined.Size * 0.5f;
		}

		// A BoxShape3D is always centered on its own origin, so position it at
		// the box center to sit exactly over the mesh (the mesh may not be
		// centered on the machine's origin, e.g. a Blender export whose origin is
		// at a corner).
		var box = new BoxShape3D { Size = boxSize };

		_collision = new CollisionShape3D
		{
			Name = "MachineCollision",
			Shape = box,
			Transform = new Transform3D(Basis.Identity, boxCenter),
		};
		AddChild(_collision);
	}

	/// <summary>
	/// Folds the TRUE mesh-resource AABB(s) of a scene into <paramref
	/// name="combined"/> (union), in the scene root's local space. Unlike
	/// <see cref="CollectAabb"/> (which calls MeshInstance3D.GetAabb() and can
	/// be stale for a freshly-instantiated, unrendered scene in the editor),
	/// this reads each <see cref="Mesh"/> resource's own AABB (accurate, no
	/// rendering required) and transforms it by the MeshInstance3D's
	/// <see cref="Node3D.GlobalTransform"/> — which bakes in the scene root's
	/// scale, the definition's Scale, and any per-mesh transforms/offsets.
	/// Recurses into all descendant MeshInstance3Ds.
	/// </summary>
	private static void CollectMeshResourceAabb(Node node, ref Aabb combined, ref bool first)
	{
		if (node == null)
		{
			return;
		}

		if (node is MeshInstance3D mi && mi.Mesh != null)
		{
			Aabb meshAabb = mi.Mesh.GetAabb();
			if (meshAabb.Size.LengthSquared() > 0f)
			{
				// Compute the mesh instance's effective transform in the scene
				// root's space by walking the parent chain (synchronous — does
				// not rely on GlobalTransform being resolved, which isn't
				// reliable for a freshly-instantiated scene in the editor).
				Transform3D effective = mi.Transform;
				for (Node3D p = mi.GetParent() as Node3D; p != null; p = p.GetParent() as Node3D)
				{
					effective = p.Transform * effective;
				}

				// Transform the mesh's local AABB into the scene root's space by
				// transforming its 8 corners and re-deriving the AABB.
				Aabb transformed = TransformAabb(meshAabb, effective);
				combined = first ? transformed : combined.Merge(transformed);
				first = false;
			}
		}

		for (int i = 0; i < node.GetChildCount(); i++)
		{
			CollectMeshResourceAabb(node.GetChild(i), ref combined, ref first);
		}
	}

	/// <summary>
	/// Transforms an AABB by a <see cref="Transform3D"/> by transforming its 8
	/// corners and re-deriving the (possibly rotated) AABB. The SDK's
	/// <see cref="Aabb"/> has no Transform method, so we do it by hand. For a
	/// mesh whose transform includes rotation, the result is the axis-aligned
	/// bounding box of the transformed corners (a slight over-approximation,
	/// which is fine for a collision box).
	/// </summary>
	private static Aabb TransformAabb(Aabb input, Transform3D transform)
	{
		Vector3 min = input.Position;
		Vector3 max = input.Position + input.Size;

		Vector3 outMin = Vector3.Inf;
		Vector3 outMax = -Vector3.Inf;

		// Transform all 8 corners.
		for (int cx = 0; cx <= 1; cx++)
		for (int cy = 0; cy <= 1; cy++)
		for (int cz = 0; cz <= 1; cz++)
		{
			Vector3 corner = new Vector3(
				cx == 0 ? min.X : max.X,
				cy == 0 ? min.Y : max.Y,
				cz == 0 ? min.Z : max.Z);
			Vector3 transformed = transform * corner;
			outMin = new Vector3(
				System.Math.Min(outMin.X, transformed.X),
				System.Math.Min(outMin.Y, transformed.Y),
				System.Math.Min(outMin.Z, transformed.Z));
			outMax = new Vector3(
				System.Math.Max(outMax.X, transformed.X),
				System.Math.Max(outMax.Y, transformed.Y),
				System.Math.Max(outMax.Z, transformed.Z));
		}

		return new Aabb(outMin, outMax - outMin);
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
	/// Captures this machine's runtime state into a <see cref="MachineSnapshot"/>
	/// for saving. The base captures the input/output buffer contents (as flat
	/// (materialId, count) pairs) and leaves <see cref="MachineSnapshot.RecipeIndex"/>
	/// at -1. Processors (smelter/crafter) override to also record their selected
	/// recipe. Called by the save system (<c>WorldSave.CollectSnapshot</c>) when
	/// the world is being persisted.
	/// </summary>
	public virtual MachineSnapshot TakeSnapshot()
	{
		var snap = new MachineSnapshot();
		snap.InputSlots = CaptureBuffer(InputBuffer);
		snap.OutputSlots = CaptureBuffer(OutputBuffer);
		return snap;
	}

	/// <summary>
	/// Restores this machine's runtime state from a <see cref="MachineSnapshot"/>
	/// (the inverse of <see cref="TakeSnapshot"/>). The base restores the
	/// input/output buffer contents and leaves recipe selection to the processor
	/// subclass. Called by the save system (<c>WorldSave.ApplyMachines</c>) right
	/// after the machine is spawned.
	/// </summary>
	public virtual void RestoreSnapshot(MachineSnapshot snap)
	{
		if (snap == null)
		{
			return;
		}
		RestoreBuffer(InputBuffer, snap.InputSlots);
		RestoreBuffer(OutputBuffer, snap.OutputSlots);
	}

	/// <summary>
	/// Captures a buffer's stacks into a flat (materialId, count) list (one entry
	/// per NON-EMPTY slot, in slot order). Null/empty buffers produce an empty list.
	/// </summary>
	protected static List<(int MaterialId, int Count)> CaptureBuffer(MachineBuffer buffer)
	{
		var list = new List<(int, int)>();
		if (buffer == null)
		{
			return list;
		}
		for (int i = 0; i < buffer.Capacity; i++)
		{
			var s = buffer[i];
			if (!s.IsEmpty)
			{
				list.Add((s.MaterialId, s.Count));
			}
		}
		return list;
	}

	/// <summary>
	/// Restores a buffer's stacks from a flat (materialId, count) list. Clears the
	/// buffer first, then re-inserts each stack (in order) so the result matches
	/// the captured state. A null buffer is a no-op (the machine has no such buffer).
	/// </summary>
	protected static void RestoreBuffer(MachineBuffer buffer, List<(int MaterialId, int Count)> stacks)
	{
		if (buffer == null)
		{
			return;
		}
		buffer.Clear();
		if (stacks == null)
		{
			return;
		}
		foreach (var (materialId, count) in stacks)
		{
			if (materialId == 0 || count <= 0)
			{
				continue;
			}
			buffer.TryInsert(ItemStack.From(materialId, count));
		}
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
