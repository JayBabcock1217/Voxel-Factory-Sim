using Godot;

namespace FactorySim.Deco;

/// <summary>
/// A grass DecoVoxel: two VERTICAL planes that cross in an X shape (the
/// Minecraft grass look) — each plane spans the whole voxel corner-to-corner
/// on the XZ diagonals and stands the full voxel height. Walk-through (no
/// collision), light-passing (doesn't block sky light), and NOT built into
/// the terrain marching-cubes mesh (its cell stays air in ChunkData).
///
/// VISUAL:
///   Two vertical rectangles: plane A runs from the (-h,-h) bottom corner to
///   the (h,h) corner (the + diagonal), plane B the other diagonal (-h,h) to
///   (h,-h). Both fill the full cell height (y 0..voxelSize). The node is
///   positioned at the BOTTOM of its cell (see <see cref="ApplyVoxelPosition"/>),
///   so the planes occupy the air cell directly above the solid surface —
///   exactly the Minecraft crossed-grass silhouette. The material is
///   double-sided (visible from both faces) and green.
///
/// LIGHTING:
///   <see cref="BlocksLight"/> is false: the light flood fill treats the
///   grass's cell as AIR, so sky light passes through the grass to the
///   surface below.
///
/// COLLISION:
///   <see cref="HasCollision"/> is false: the player and projectiles pass
///   through the grass. No collision shape is built.
///
/// PLACEMENT:
///   Auto-spawns on the top surface of solid voxels when a chunk generates
///   (or when a voxel is exposed to sky). The player can also place/remove
///   it manually with a tool (like a torch).
/// </summary>
[GlobalClass]
public partial class GrassDeco : DecoVoxel
{
	/// <summary>
	/// Grass passes light through (the light flood fill treats its cell as
	/// air, so sky light reaches the surface below).
	/// </summary>
	public override bool BlocksLight => false;

	/// <summary>
	/// Grass is walk-through (no collision shape is built).
	/// </summary>
	public override bool HasCollision => false;

	/// <summary>
	/// This is the Grass deco type (used to look up its
	/// <see cref="DecoDefinition"/> in <see cref="DecoVoxel.Initialize"/>).
	/// </summary>
	public override DecoType DecoType => DecoType.Grass;

	/// <summary>
	/// The grass's crossed-planes mesh (shared across all GrassDeco instances —
	/// one mesh for all grass, no per-instance allocation). Built at unit size
	/// (1.0); the node's scale handles the actual voxel size.
	/// </summary>
	private static ArrayMesh _grassMesh;

	/// <summary>
	/// The grass's material (green, double-sided). Shared across all
	/// GrassDeco instances.
	/// </summary>
	private static StandardMaterial3D _grassMaterial;

	/// <inheritdoc/>
	protected override void BuildDecoVisual()
	{
		_grassMesh ??= BuildCrossedPlanesMesh(1.0f); // built at unit size
		_grassMaterial ??= BuildGrassMaterial();

		// Resolve the material: the editor-assigned definition (custom material
		// or albedo texture, set in the DecoDefinition resource) wins over the
		// built-in green. A null Definition (none registered) keeps the green.
		var material = ResolveDecoMaterial(_grassMaterial);

		_meshInstance = new MeshInstance3D
		{
			Name = "GrassMesh",
			Mesh = _grassMesh,
			// Uniform scale: the mesh is built at unit size (1.0) and spans the
			// FULL voxel (corner to corner, floor to top). The node sits at the
			// BOTTOM of its cell (see Initialize), so the planes fill the voxel
			// above the solid surface below — the Minecraft crossed-grass look.
			Scale = new Vector3(VoxelSize, VoxelSize, VoxelSize),
			MaterialOverride = material,
		};
		AddChild(_meshInstance);

		if (IsPreview)
		{
			_meshInstance.MaterialOverride = PreviewMaterial;
		}
	}

	/// <summary>
	/// Grass is walk-through: no collision shape is built (HasCollision is
	/// false, so the base's BuildDecoCollision is never called).
	/// </summary>
	protected override void BuildDecoCollision()
	{
		// No collision (walk-through).
	}

	/// <summary>
	/// Positions the grass at the BOTTOM of its cell (the surface of the solid
	/// voxel below) instead of the cell center. The crossed planes span the
	/// full cell height from the node origin, so anchoring at the bottom makes
	/// them grow UP from the surface — the Minecraft grass silhouette.
	/// </summary>
	protected override Vector3 ApplyVoxelPosition(Vector3I pos, float size)
		=> new Vector3(pos.X * size, pos.Y * size, pos.Z * size);

	/// <summary>
	/// Builds the Minecraft-style crossed-planes mesh: two VERTICAL rectangles
	/// that span the whole voxel corner-to-corner, crossing in an X when viewed
	/// from above.
	///
	/// Plane A: a vertical quad from the (-h,-h) corner of the bottom face to
	/// the (h,h) corner — the + diagonal. Plane B: the other diagonal, (-h,h)
	/// to (h,-h). Both stand the full voxel height (y 0..size), so the grass
	/// fills the air cell above the surface it sits on (the node is positioned
	/// at the BOTTOM of its cell — see <see cref="DecoVoxel.Initialize"/>).
	///
	/// The mesh is built at unit size (1.0); the node's uniform scale handles
	/// the actual voxel size. 12 vertices (6 per quad, unindexed — each
	/// triangle carries its own vertex+normal+UV). Each plane's normal is its
	/// horizontal facing (the planes are double-sided via the material's
	/// CullMode.Disabled, so one normal per plane is enough).
	///
	/// NOTE: unindexed (no AddIndex) — Godot's SurfaceTool rejects a per-vertex
	/// SetNormal when the surface is indexed (the ARRAY_FORMAT_NORMAL flag is
	/// only set on the non-indexed path), which would otherwise throw a runtime
	/// error on every grass spawn.
	/// </summary>
	private static ArrayMesh BuildCrossedPlanesMesh(float size)
	{
		float h = size * 0.5f;  // half the voxel (corner offset)
		float y0 = 0f;          // bottom of the cell
		float y1 = size;        // top of the cell

		// Plane A: the + diagonal (bottom (-h,-h) → top (h,h)).
		Vector3 a0 = new Vector3(-h, y0, -h);
		Vector3 a1 = new Vector3(-h, y1, -h);
		Vector3 a2 = new Vector3( h, y1,  h);
		Vector3 a3 = new Vector3( h, y0,  h);
		// Plane B: the - diagonal (bottom (-h,h) → top (h,-h)).
		Vector3 b0 = new Vector3(-h, y0,  h);
		Vector3 b1 = new Vector3(-h, y1,  h);
		Vector3 b2 = new Vector3( h, y1, -h);
		Vector3 b3 = new Vector3( h, y0, -h);

		// Per-plane normals (horizontal facing, double-sided material).
		Vector3 na = new Vector3(h, 0f, -h);
		Vector3 nb = new Vector3(h, 0f,  h);

		// UV layout: U follows the diagonal (corner-to-corner width of the
		// plane), V follows the vertical (height). The bottom edge is V=1 and
		// the top edge is V=0, so the texture stands UPRIGHT on each plane
		// (not rotated/skewed along the diagonal). Each plane's four corners get
		// a consistent UV so the two triangles tile the texture seamlessly.
		//
		//   a0 (bottom-left)  = (0, 1)   a3 (bottom-right) = (1, 1)
		//   a1 (top-left)     = (0, 0)   a2 (top-right)    = (1, 0)
		//   b0 (bottom-left)  = (0, 1)   b3 (bottom-right) = (1, 1)
		//   b1 (top-left)     = (0, 0)   b2 (top-right)    = (1, 0)
		Vector2 A0 = new Vector2(0f, 1f);  // bottom-left
		Vector2 A1 = new Vector2(0f, 0f);  // top-left
		Vector2 A2 = new Vector2(1f, 0f);  // top-right
		Vector2 A3 = new Vector2(1f, 1f);  // bottom-right
		Vector2 B0 = new Vector2(0f, 1f);  // bottom-left
		Vector2 B1 = new Vector2(0f, 0f);  // top-left
		Vector2 B2 = new Vector2(1f, 0f);  // top-right
		Vector2 B3 = new Vector2(1f, 1f);  // bottom-right

		var mesh = new ArrayMesh();
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Triangles);

		// Plane A = two triangles: (a0,a1,a2) and (a0,a2,a3).
		// Plane B = two triangles: (b0,b1,b2) and (b0,b2,b3).
		// Unindexed (no AddIndex): each vertex is emitted per-triangle with its
		// own normal + UV. This is the path Godot's SurfaceTool supports for
		// per-vertex normals (the indexed path rejects SetNormal).
		// Plane A, tri 1: a0, a1, a2
		st.SetNormal(na); st.SetUV(A0); st.AddVertex(a0);
		st.SetNormal(na); st.SetUV(A1); st.AddVertex(a1);
		st.SetNormal(na); st.SetUV(A2); st.AddVertex(a2);
		// Plane A, tri 2: a0, a2, a3
		st.SetNormal(na); st.SetUV(A0); st.AddVertex(a0);
		st.SetNormal(na); st.SetUV(A2); st.AddVertex(a2);
		st.SetNormal(na); st.SetUV(A3); st.AddVertex(a3);
		// Plane B, tri 1: b0, b1, b2
		st.SetNormal(nb); st.SetUV(B0); st.AddVertex(b0);
		st.SetNormal(nb); st.SetUV(B1); st.AddVertex(b1);
		st.SetNormal(nb); st.SetUV(B2); st.AddVertex(b2);
		// Plane B, tri 2: b0, b2, b3
		st.SetNormal(nb); st.SetUV(B0); st.AddVertex(b0);
		st.SetNormal(nb); st.SetUV(B2); st.AddVertex(b2);
		st.SetNormal(nb); st.SetUV(B3); st.AddVertex(b3);

		st.Commit(mesh);
		return mesh;
	}

	/// <summary>
	/// Builds the grass material: a green, double-sided material with ALPHA
	/// TRANSPARENCY enabled, so a texture's transparent (alpha) areas render as
	/// see-through (the grass-blade cutout). <see cref="Transparency"/> is set
	/// to <c>Alpha</c> (the texture's alpha channel drives opacity) and
	/// <see cref="AlphaScissorMode"/> to <c>On</c> with a low threshold so the
	/// cutout edges are crisp (the classic "cutout" transparency used for
	/// foliage). CullMode is Disabled (double-sided) so both faces of the
	/// crossed planes render. Shared across all GrassDeco instances.
	/// </summary>
	private static StandardMaterial3D BuildGrassMaterial()
	{
		return new StandardMaterial3D
		{
			AlbedoColor = new Color(0.2f, 0.6f, 0.15f, 1f),
			// Alpha transparency: the texture's ALPHA channel drives opacity, so
			// transparent areas (the grass-blade cutout) render as see-through
			// instead of being ignored by the opaque pipeline. (The C# binding
			// in this Godot version does not expose AlphaCut/AlphaScissor as
			// properties, so plain Alpha is the supported path.)
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			// Nearest filtering: disables the texture's anti-aliasing (linear
			// sampling + mipmaps), so the grass texture renders with crisp,
			// un-blurred edges (the pixelated look) instead of a soft fade at
			// the cutout boundary.
			TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled, // double-sided
		};
	}
}
