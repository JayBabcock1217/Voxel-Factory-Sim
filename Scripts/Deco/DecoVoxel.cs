using Godot;

namespace FactorySim.Deco;

/// <summary>
/// Base class for all voxels that do NOT get built into the terrain marching-cubes
/// mesh. This is the unified "non-terrain voxel" system: grass, walls, doors,
/// windows, machines, and any future structural/decorative object the player
/// places or that spawns in the world.
///
/// HOW IT STAYS OUT OF THE MARCHING-CUBES MESH:
///   The cell in <c>ChunkData.Data</c> stays AIR (0). The mesh builder's
///   solid-vs-air decision is <c>materialId != 0</c>, so an air cell is never
///   meshed. The DecoVoxel is a SEPARATE scene-tree node with its own
///   <see cref="MeshInstance3D"/> (and optionally its own collision), parented
///   to the shared "DecoVoxels" container under the scene root. The DDA
///   raycast (VoxelWorld.RaycastVoxels) never hits it because its cell is air;
///   the tool does a separate slab-method raycast against DecoVoxel AABBs
///   (like the existing TryRaycastMachine).
///
/// LIGHTING:
///   The world's light flood fill (MeshBuilder.WorldSolidForLight) checks the
///   VoxelWorld's DecoVoxel occupancy: a cell with a non-transparent DecoVoxel
///   reads as SOLID (blocks light), a transparent one (grass) reads as AIR.
///   This is the "as the mesh is generating, consider these as air" rule —
///   the flood fill and the mesher both treat DecoVoxels as non-terrain.
///
/// INHERITANCE:
///   <c>Machine</c> (existing) and <c>GrassDeco</c> (new) both inherit from
///   DecoVoxel. Walls, doors, and windows (future) also inherit from it.
///   The machine system keeps its own definition/registry/spawner; the deco
///   system (grass, etc.) gets a parallel, lighter-weight definition/registry/
///   spawner. Both share the DecoVoxel base (positioning, mesh, collision,
///   occupancy, lifecycle).
///
/// PLACEMENT:
///   The player places a DecoVoxel the same way they place a machine: the
///   tool's TryPlaceHeldMaterial checks whether the held item is a deco item,
///   and if so calls DecoSpawner.SpawnDeco (instead of PlaceVoxel). The cell
///   stays air in ChunkData; the DecoVoxel node is the only thing that exists.
///
/// REMOVAL:
///   Breaking a DecoVoxel (with a tool or via damage) frees the node from the
///   "DecoVoxels" container. No chunk rebuild is needed (the cell was always
///   air). The VoxelBroken event is NOT raised (that's for terrain voxels);
///   the tool handles the drop directly.
/// </summary>
[GlobalClass]
public partial class DecoVoxel : Node3D
{
	/// <summary>The world voxel this DecoVoxel occupies (its logical anchor).</summary>
	public Vector3I VoxelPos { get; protected set; }

	/// <summary>
	/// The <see cref="FactorySim.Voxel.VoxelWorld"/> this DecoVoxel was placed
	/// into. Set during <see cref="Initialize"/>. Subclasses use it to query
	/// the world (neighbours, light, etc.).
	/// </summary>
	public FactorySim.Voxel.VoxelWorld World { get; protected set; }

	/// <summary>
	/// The world voxel size (the edge length of one voxel in world units).
	/// Set during <see cref="Initialize"/>. Used to position the node at the
	/// center of its voxel.
	/// </summary>
	public float VoxelSize { get; protected set; }

	/// <summary>
	/// True when this DecoVoxel is a transparent PREVIEW (the placement ghost
	/// the tool shows while aiming). A preview has no collision and is not
	/// counted for occupancy. Set by the spawner for the ghost; cleared for
	/// a real (placed) DecoVoxel.
	/// </summary>
	public bool IsPreview { get; set; }

	/// <summary>
	/// True when this DecoVoxel BLOCKS light (the light flood fill treats its
	/// cell as solid). Grass is false (light passes through); walls, doors,
	/// and machines are true. Defaults to true (most structural objects block
	/// light); decorative foliage (grass) overrides to false.
	/// </summary>
	public virtual bool BlocksLight => true;

	/// <summary>
	/// True when this DecoVoxel has a COLLISION shape (the player can't walk
	/// through it). Grass is false (walk-through); walls, doors, and machines
	/// are true. Defaults to true; decorative foliage overrides to false.
	/// </summary>
	public virtual bool HasCollision => true;

	/// <summary>
	/// The <see cref="DecoType"/> this DecoVoxel represents. Used to look up
	/// its <see cref="DecoDefinition"/> (the editor-assigned appearance) in
	/// <see cref="Initialize"/>. Subclasses each report their own type; the base
	/// returns <see cref="DecoType.None"/> (no definition lookup) so a bare
	/// DecoVoxel (e.g. one placed in the editor) still works.
	/// </summary>
	public virtual DecoType DecoType => DecoType.None;

	/// <summary>
	/// The item id this DecoVoxel was spawned from (for the drop pipeline:
	/// breaking it drops this item). 0 = no drop (e.g. auto-spawned grass
	/// that drops nothing, or a machine that has its own drop logic).
	/// </summary>
	public int DropItemId { get; protected set; }

	/// <summary>
	/// The editor-assigned <see cref="DecoDefinition"/> for this DecoVoxel's
	/// type, or null when no definition is registered (in which case the
	/// built-in default look is used). Set during <see cref="Initialize"/> from
	/// the <see cref="DecoRegistry"/>. Subclasses read it in
	/// <see cref="BuildDecoVisual"/> to apply the user's material / mesh.
	/// </summary>
	public DecoDefinition Definition { get; protected set; }

	protected MeshInstance3D _meshInstance;
	protected CollisionShape3D _collision;

	/// <summary>
	/// Initializes the DecoVoxel: positions it at its voxel (via the virtual
	/// <see cref="ApplyVoxelPosition"/> — the cell center by default), stores
	/// the world reference + voxel size, and calls the virtual
	/// <see cref="BuildDecoVisual"/> / <see cref="BuildDecoCollision"/> to
	/// create the mesh (and collision, when <see cref="HasCollision"/> is true
	/// and the DecoVoxel is not a preview).
	///
	/// Called by the spawner AFTER the node is added to the tree (GlobalPosition
	/// needs a tree).
	/// </summary>
	public void Initialize(Vector3I voxelPos, float voxelSize, FactorySim.Voxel.VoxelWorld world, int dropItemId = 0)
	{
		VoxelPos = voxelPos;
		VoxelSize = voxelSize;
		World = world;
		DropItemId = dropItemId;

		// Resolve the editor-assigned definition for this deco's type (null when
		// none is registered). Subclasses read it in BuildDecoVisual to apply
		// the user's material / mesh; a null definition means built-in defaults.
		Definition = DecoRegistry.GetByType(DecoType);

		// Position at the voxel (default: the cell center — the same convention
		// the machine uses). Subclasses override ApplyVoxelPosition for other
		// anchors (e.g. grass sits at the BOTTOM of its cell so its crossed
		// planes grow up from the surface).
		GlobalPosition = ApplyVoxelPosition(voxelPos, voxelSize);

		BuildDecoVisual();
		if (!IsPreview && HasCollision)
		{
			BuildDecoCollision();
		}
	}

	/// <summary>
	/// Computes this DecoVoxel's world position for its voxel. Default is the
	/// cell CENTER (the convention machines use: the center is where the player
	/// aimed). Subclasses override this when their mesh is anchored elsewhere
	/// (e.g. <c>GrassDeco</c> anchors at the cell BOTTOM so the crossed planes
	/// fill the cell above the surface).
	/// </summary>
	protected virtual Vector3 ApplyVoxelPosition(Vector3I pos, float size)
		=> new Vector3(pos.X, pos.Y, pos.Z) * size + new Vector3(0.5f, 0.5f, 0.5f) * size;

	/// <summary>
	/// Builds the DecoVoxel's visual (a MeshInstance3D child). Subclasses
	/// override this to create their specific mesh (grass's crossed planes,
	/// a wall's box, a door's sliding panel, etc.). The base does nothing —
	/// a DecoVoxel with no visual is invisible (useful for a pure-logic deco
	/// that only has collision or behavior).
	/// </summary>
	protected virtual void BuildDecoVisual() { }

	/// <summary>
	/// Builds the DecoVoxel's collision (a CollisionShape3D child). Subclasses
	/// override this to create their specific collision shape. The base
	/// creates a full-voxel box (the default for structural objects like walls
	/// and machines). Decorative foliage (grass) overrides to do nothing (no
	/// collision — walk-through).
	/// </summary>
	protected virtual void BuildDecoCollision()
	{
		var shape = new BoxShape3D { Size = new Vector3(VoxelSize, VoxelSize, VoxelSize) };
		_collision = new CollisionShape3D
		{
			Name = "DecoCollision",
			Shape = shape,
		};
		AddChild(_collision);
	}

	/// <summary>
	/// Frees this DecoVoxel: removes it from the "DecoVoxels" container and
	/// queues it for deletion. Called when the player breaks the DecoVoxel or
	/// when the world slot is cleared. No chunk rebuild is needed (the cell
	/// in ChunkData was always air).
	/// </summary>
	public void Despawn()
	{
		if (GetParent() != null)
		{
			GetParent().RemoveChild(this);
		}
		QueueFree();
	}

	/// <summary>
	/// Applies the transparent preview material to the visual (for the
	/// placement ghost). Subclasses with a scene-based visual should override
	/// this to apply the override to every MeshInstance3D in the scene.
	/// </summary>
	public void ApplyPreviewVisual()
	{
		if (_meshInstance != null)
		{
			_meshInstance.MaterialOverride = PreviewMaterial;
		}
	}

	/// <summary>
	/// The shared transparent material for preview ghosts (one instance shared
	/// by all DecoVoxel previews — no per-ghost allocation).
	/// </summary>
	private static StandardMaterial3D _previewMaterial;
	protected static StandardMaterial3D PreviewMaterial
	{
		get
		{
			if (_previewMaterial == null)
			{
				_previewMaterial = new StandardMaterial3D
				{
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					AlbedoColor = new Color(0.3f, 0.8f, 1.0f, 0.4f),
				};
			}
			return _previewMaterial;
		}
	}

	/// <summary>
	/// Resolves the material to apply to this DecoVoxel's visual, from the
	/// editor-assigned <see cref="Definition"/> (the <c>DecoDefinition</c>
	/// fields), with the type's <paramref name="builtin"/> as the fallback.
	/// Precedence (highest to lowest):
	///   1. <c>Definition.CustomMaterial</c> — a full material used as-is.
	///   2. <c>Definition.AlbedoTexture</c> (+ <c>AlbedoColor</c> tint) — a
	///      <see cref="StandardMaterial3D"/> built with the texture + tint,
	///      with the same cull/shading as the built-in.
	///   3. <paramref name="builtin"/> — the type's built-in material (e.g.
	///      grass's green crossed-planes material).
	///
	/// Subclasses call this at the end of <see cref="BuildDecoVisual"/> so the
	/// user's inspector edits (assigning a texture or a custom material in the
	/// <see cref="DecoDefinition"/> resource) override the built-in look.
	/// </summary>
	protected Material ResolveDecoMaterial(Material builtin)
	{
		var def = Definition;
		if (def == null)
		{
			return builtin;
		}
		if (def.CustomMaterial != null)
		{
			return def.CustomMaterial;
		}
		if (def.AlbedoTexture != null)
		{
			var m = new StandardMaterial3D
			{
				AlbedoTexture = def.AlbedoTexture,
				AlbedoColor = def.AlbedoColor,
			};
			// Match the built-in's cull / shading / TRANSPARENCY so a texture
			// swap doesn't change how the mesh culls or how its alpha channel is
			// handled. In particular, Transparency is copied from the built-in
			// (grass uses Alpha + alpha-scissor) so the texture's transparent
			// areas render as see-through instead of being ignored by the opaque
			// pipeline (which would make the whole quad a solid rectangle).
			if (builtin is StandardMaterial3D std)
			{
				m.CullMode = std.CullMode;
				m.ShadingMode = std.ShadingMode;
				// Copy the transparency mode so the texture's alpha channel is
				// handled the same way as the built-in (grass uses Alpha, so the
				// texture's transparent areas render see-through).
				m.Transparency = std.Transparency;
				// Copy the texture filtering so the texture's anti-aliasing is
				// handled the same way as the built-in (grass uses Nearest, so
				// the texture renders crisp/un-blurred instead of soft).
				m.TextureFilter = std.TextureFilter;
			}
			else
			{
				// No built-in to copy from: enable alpha transparency by default
				// so a texture's alpha channel is respected, and use Nearest
				// filtering so the texture renders crisp (no anti-aliasing).
				m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
				m.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
			}
			return m;
		}
		return builtin;
	}
}
