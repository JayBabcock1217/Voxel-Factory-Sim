using Godot;
using System;
using System.Collections.Generic;

namespace FactorySim.Voxel;

/// <summary>
/// Builds the visual ArrayMesh (one flat-shaded multi-material surface) and the
/// concave collision triangle soup for a chunk.
///
/// Both meshes are classic hidden-face voxel meshes: for every solid voxel, a
/// face is emitted for each of the 6 directions whose neighbour voxel is not
/// solid. The visual surface is therefore exactly the solid/air boundary —
/// the same shape as the collision mesh — so the player never hovers over or
/// clips through visible geometry, and there are no seams between chunks.
///
/// Winding: Godot uses CLOCKWISE winding for front faces (verified against
/// BoxMesh), i.e. the cross product (b-a)x(c-a) points INTO the solid. Both
/// meshers emit that winding, while stored normals point outward (toward the
/// air side) for correct lighting. No global orientation pass is needed.
/// </summary>
public static class MeshBuilder
{
	/// <summary>
	/// Builds the visible mesh for a chunk.
	/// </summary>
	/// <param name="chunk">Chunk voxel data.</param>
	/// <param name="wx0">World voxel X of local (0,0,0).</param>
	/// <param name="wz0">World voxel Z of local (0,0,0).</param>
	/// <param name="voxelSize">Voxel size in world units.</param>
	/// <param name="palette">Shared flat-shaded materials.</param>
	/// <param name="solidQuery">
	/// Returns whether the given world voxel is solid; faces against a solid
	/// neighbour are internal (e.g. grass on dirt) and are not drawn.
	/// </param>
	/// <param name="collisionOut">
	/// Optional. When supplied, the SAME face quads emitted into the visual
	/// mesh are also appended here as a triangle soup (2 tris per face), so the
	/// collision geometry is exactly the visual geometry (no seam gap in
	/// Faces mode).
	/// </param>
	public static ArrayMesh BuildVisual(
		ChunkData chunk,
		int wx0,
		int wz0,
		float voxelSize,
		VoxelMaterialPalette palette,
		System.Func<int, int, int, bool> solidQuery,
		System.Collections.Generic.List<Vector3> collisionOut = null)
	{
		int lastMaterial = palette.MaterialCount;

		var positions = new List<Vector3>[lastMaterial];
		var normals = new List<Vector3>[lastMaterial];
		var uvs = new List<Vector2>[lastMaterial];
		for (int i = 0; i < lastMaterial; i++)
		{
			positions[i] = new List<Vector3>();
			normals[i] = new List<Vector3>();
			uvs[i] = new List<Vector2>();
		}

		ForEachExposedFace(chunk, wx0, wz0, solidQuery, (wx, y, wz, m, face) =>
		{
			// Skip faces whose material id has no palette slot (out-of-range
			// ids would index the per-material vertex lists out of bounds).
			if (m < 1 || m > lastMaterial)
			{
				return;
			}
			Vector3 origin = new Vector3(wx, y, wz) * voxelSize;
			AddFace(positions[m - 1], normals[m - 1], uvs[m - 1], origin, face, voxelSize, palette.TextureScales[m - 1]);
			// Collision is the exact visual surface: emit the same quad.
			if (collisionOut != null)
			{
				AddCollisionFace(collisionOut, origin, face, voxelSize);
			}
		});

		var mesh = new ArrayMesh();
		for (int m = 1; m <= lastMaterial; m++)
		{
			if (positions[m - 1].Count == 0)
			{
				continue;
			}

			// Flat shading: every vertex owns its face normal (6 vertices per
			// quad), so no face ever blends normals with a neighbour.
			var tool = new SurfaceTool();
			tool.Begin(Mesh.PrimitiveType.Triangles);
			tool.SetMaterial(palette.Materials[m - 1]);

			for (int i = 0; i < positions[m - 1].Count; i++)
			{
				tool.SetNormal(normals[m - 1][i]);
				tool.SetUV(uvs[m - 1][i]);
				tool.AddVertex(positions[m - 1][i]);
			}

			tool.Commit(mesh);
		}

		return mesh;
	}

	/// <summary>
	/// Builds the visible mesh using classic table-driven Marching Cubes:
	/// ONE smooth isosurface at the AIR|SOLID boundary of the terrain
	/// (beveled edges and corners — the classic MC look), with every vertex
	/// assigned to the material of the solid voxel it borders.
	///
	/// The field is the material index per voxel (air = 0), so internal
	/// material interfaces (grass|dirt, dirt|stone) never cross the 0.5
	/// threshold and never generate geometry. Digging a hole reveals a
	/// single clean surface — no buried internal layers, no double-drawn
	/// walls.
	///
	/// Chunk seams: the per-chunk field is padded with a one-voxel shell
	/// sampled from the neighbouring chunks, so the surface is continuous
	/// across seams. Each chunk draws only its +X/+Z side boundary cells
	/// (ownership), so no seam surface is ever drawn twice. A +X/+Z boundary
	/// cell is drawn only when that side's neighbour is loaded; at the render
	/// boundary (neighbour unloaded) the edge is left OPEN — no solid wall —
	/// so a chunk generated later on that edge connects smoothly.
	///
	/// Triangles are grouped by material (majority vote of their vertex
	/// materials) and emitted as one flat-shaded surface per material,
	/// matching the face-based mesher's material layout.
	/// </summary>
	public static ArrayMesh BuildVisualMarchingCubes(
		ChunkData chunk,
		int wx0,
		int wz0,
		float voxelSize,
		VoxelMaterialPalette palette,
		Vector2I coord,
		System.Func<Vector2I, ChunkData> neighborQuery,
		System.Collections.Generic.List<Vector3> collisionOut = null)
	{
		int sx = chunk.SizeX, sy = chunk.SizeY, sz = chunk.SizeZ;
		int px = sx + 2, py = sy + 2, pz = sz + 2; // padded lattice dims
		int lastMaterial = palette.MaterialCount;
		Vector3 origin = new Vector3(wx0, 0, wz0);

		// 1) Build the padded field: material index per lattice point, air
		//    (0) everywhere outside the chunk data. A parallel binary
		//    solid|air field (1/0) drives marching cubes itself: feeding raw
		//    material indices as field values would shift the isosurface off
		//    the voxel boundary (crossing at t = 0.5/value instead of 0.5).
		var field = new float[px * py * pz];
		var solid = new float[px * py * pz];
		for (int iz = 0; iz < pz; iz++)
		{
			for (int iy = 0; iy < py; iy++)
			{
				for (int ix = 0; ix < px; ix++)
				{
					float m = SampleMaterial(
						chunk, sx, sy, sz, coord, neighborQuery, ix - 1, iy - 1, iz - 1);
					int idx = ix + px * (iy + py * iz);
					field[idx] = m;
					solid[idx] = m > 0f ? 1f : 0f;
				}
			}
		}

		// 2) Extract the single air|solid isosurface.
		//    Ownership: each chunk draws only its +X/+Z side boundary cells
		//    (the -X/-Z side is owned by the negative-side neighbour), so a
		//    seam between two LOADED chunks is drawn exactly once. The +X/+Z
		//    boundary cells are drawn ONLY when that side's neighbour is
		//    loaded: otherwise the edge is left OPEN (no wall), so a chunk
		//    generated later on that edge connects smoothly. The corner cell
		//    needs the east, south AND diagonal neighbours all loaded — all
		//    four of its corner voxels must be real data, not air shells.
		//    (Cell cx spans chunk-local voxels cx-1..cx, so the east boundary
		//    cell is cx==sx, the south cell cz==sz, the corner (sx, sz).)
		//
		//    Gate the boundary cells on neighbour LOAD STATE, not just on the
		//    side: with the old unconditional `cx > 0 && cz > 0` predicate the
		//    +X/+Z boundary cells were always drawn, and when that side's
		//    neighbour was UNLOADED the padding shell read as air, so the chunk
		//    emitted a full solid wall across its whole east/south face. The
		//    later-generated neighbour then drew its own -X/-Z surface on the
		//    same world plane -> z-fighting / visible seam gap. Drawing the
		//    boundary cells only when the neighbour is loaded leaves the edge
		//    OPEN until the neighbour exists, then closes it cleanly.
		bool eastLoaded = neighborQuery(new Vector2I(coord.X + 1, coord.Y)) != null;
		bool southLoaded = neighborQuery(new Vector2I(coord.X, coord.Y + 1)) != null;
		bool diagLoaded = neighborQuery(new Vector2I(coord.X + 1, coord.Y + 1)) != null;

		var tris = MarchingCubes.Generate(solid, px, py, pz,
			(cx, cy, cz) =>
			{
				// Interior cells (not on the +X or +Z boundary of this chunk):
				// always drawn — they don't depend on any neighbour's state.
				if (cx < sx && cz < sz)
				{
					return true;
				}

				// East boundary (cx == sx): draw only if the east neighbour is
				// loaded, so the seam closes instead of walling off.
				if (cx == sx && cz < sz)
				{
					return eastLoaded;
				}

				// South boundary (cz == sz): draw only if the south neighbour
				// is loaded.
				if (cx < sx && cz == sz)
				{
					return southLoaded;
				}

				// Corner cell (cx == sx && cz == sz): all four corner voxels
				// must be real data, so require east, south AND diagonal.
				return eastLoaded && southLoaded && diagLoaded;
			});

		// 3) Resolve every vertex (lattice position + material of the solid
		//    voxel the crossing came from) and group triangles by material.
		var perMaterial = new List<Vector3>[lastMaterial]; // [pos, normal] pairs
		var perUv = new List<Vector2>[lastMaterial];        // one UV per pos/normal pair
		for (int i = 0; i < lastMaterial; i++)
		{
			perMaterial[i] = new List<Vector3>();
			perUv[i] = new List<Vector2>();
		}

		foreach (var tri in tris)
		{
			Vector3 pa, pb, pc;
			int ma, mb, mc;
			Vertex(solid, field, px, py, pz, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeA, out pa, out ma);
			Vertex(solid, field, px, py, pz, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeB, out pb, out mb);
			Vertex(solid, field, px, py, pz, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeC, out pc, out mc);

			// Lattice -> world. The padding shifts the lattice by one, and the
			// +0.5 puts every field point at its voxel's CENTER, so the
			// isosurface lands exactly on the voxel boundaries — the same
			// positions as the face mesher and the collision mesh (no offset
			// between what you see and what you stand on).
			Vector3 half = new Vector3(0.5f, 0.5f, 0.5f) * voxelSize;
			pa = (pa - Vector3.One + origin) * voxelSize + half;
			pb = (pb - Vector3.One + origin) * voxelSize + half;
			pc = (pc - Vector3.One + origin) * voxelSize + half;

			// MarchingCubes emits Godot front faces: the cross product
			// points into the solid. The lighting normal is the opposite.
			Vector3 n = (pb - pa).Cross(pc - pa);
			if (n.LengthSquared() < 1e-12f)
			{
				continue;
			}

			n = -n.Normalized();

			// Triangle material: majority vote of the vertex materials
			// (mixed triangles occur where a face straddles a material
			// boundary — assign it to the dominant material).
			int m = ma == mb || ma == mc ? ma : (mb == mc ? mb : mc);
			if (m < 1 || m > lastMaterial)
			{
				// Air (impossible: every crossing has a solid endpoint) or a
				// material id with no palette slot — skip instead of indexing
				// the per-material lists out of bounds.
				continue;
			}

			var verts = perMaterial[m - 1];
			var uvs = perUv[m - 1];
			float uvScale = palette.TextureScales[m - 1];
			verts.Add(pa);
			verts.Add(n);
			uvs.Add(PlanarUv(pa, n, uvScale));
			verts.Add(pb);
			verts.Add(n);
			uvs.Add(PlanarUv(pb, n, uvScale));
			verts.Add(pc);
			verts.Add(n);
			uvs.Add(PlanarUv(pc, n, uvScale));

			if (collisionOut != null)
			{
				collisionOut.Add(pa);
				collisionOut.Add(pb);
				collisionOut.Add(pc);
			}
		}

		// 4) Emit one flat-shaded surface per material.
		var mesh = new ArrayMesh();
		for (int m = 1; m <= lastMaterial; m++)
		{
			var verts = perMaterial[m - 1];
			if (verts.Count == 0)
			{
				continue;
			}

			var uvs = perUv[m - 1];
			var tool = new SurfaceTool();
			tool.Begin(Mesh.PrimitiveType.Triangles);
			tool.SetMaterial(palette.Materials[m - 1]);
			for (int i = 0; i + 1 < verts.Count; i += 2)
			{
				tool.SetNormal(verts[i + 1]);
				tool.SetUV(uvs[i / 2]);
				tool.AddVertex(verts[i]);
			}
			tool.Commit(mesh);
		}

		return mesh;
	}

	/// <summary>
	/// Resolves one MC triangle vertex: its lattice-space position (from
	/// the binary solid field) and the material index of the solid voxel
	/// the crossing came from (from the material field).
	/// </summary>
	private static void Vertex(
		float[] solidField,
		float[] materialField,
		int nx,
		int ny,
		int nz,
		int cx,
		int cy,
		int cz,
		int edge,
		out Vector3 position,
		out int material)
	{
		MarchingCubes.ResolveEdge(solidField, nx, ny, nz, cx, cy, cz, edge,
			out position, out int sx, out int sy, out int sz);
		material = (int)materialField[sx + nx * (sy + ny * sz)];
	}

	/// <summary>
	/// Samples the padded material field at chunk-relative lattice point
	/// (x, y, z): inside the chunk -> that voxel's material; on the padded
	/// shell -> the neighbouring chunk's boundary voxel (air when the
	/// neighbour is unloaded); diagonal or out-of-range -> air.
	/// </summary>
	private static float SampleMaterial(
		ChunkData chunk,
		int sx,
		int sy,
		int sz,
		Vector2I coord,
		System.Func<Vector2I, ChunkData> neighborQuery,
		int x,
		int y,
		int z)
	{
		if (x >= 0 && x < sx && y >= 0 && y < sy && z >= 0 && z < sz)
		{
			return chunk.Data[x + sx * (y + sy * z)];
		}

		// Below the world bottom or above the chunk top there is no data.
		if (y < 0 || y >= sy)
		{
			return 0;
		}

		int dx = x < 0 ? -1 : (x >= sx ? 1 : 0);
		int dz = z < 0 ? -1 : (z >= sz ? 1 : 0);
		if (dx != 0 && dz != 0)
		{
			// Diagonal corner: the lattice point sits on the corner voxel of the
			// diagonal neighbour (one chunk away in both X and Z). Query that
			// voxel directly — returning air here used to punch a hole in the
			// marching-cubes corner bevel at every chunk corner.
			var diag = neighborQuery(new Vector2I(coord.X + dx, coord.Y + dz));
			if (diag != null)
			{
				int dlx = dx > 0 ? 0 : diag.SizeX - 1;
				int dlz = dz > 0 ? 0 : diag.SizeZ - 1;
				return diag.Data[dlx + diag.SizeX * (y + diag.SizeY * dlz)];
			}
			return 0; // diagonal neighbour unloaded: air (rare edge case)
		}

		var neighbour = neighborQuery(new Vector2I(coord.X + dx, coord.Y + dz));
		if (neighbour == null)
		{
			return 0; // unloaded neighbour: air (clean cliff face)
		}

		int lx = dx < 0 ? neighbour.SizeX - 1 : (dx > 0 ? 0 : x);
		int lz = dz < 0 ? neighbour.SizeZ - 1 : (dz > 0 ? 0 : z);
		return neighbour.Data[lx + neighbour.SizeX * (y + neighbour.SizeY * lz)];
	}

	/// <summary>
	/// Builds the visual mesh and the collision triangle list for a chunk in a
	/// single CPU pass. Pure voxel math over <paramref name="chunk"/> plus the
	/// supplied queries — no engine node access — so it is safe to run on the
	/// thread pool. Returns the visual <see cref="ArrayMesh"/> and the
	/// collision triangles (world-space, ready for
	/// <c>ConcavePolygonShape3D.SetFaces</c>). The collision list is the visual
	/// surface itself (see <see cref="Build"/>).
	/// </summary>
	public static (ArrayMesh visual, List<Vector3> collision) Build(
		ChunkData chunk,
		int wx0,
		int wz0,
		float voxelSize,
		VoxelMaterialPalette palette,
		VoxelMeshMode mode,
		Vector2I coord,
		System.Func<int, int, int, bool> solidQuery,
		System.Func<Vector2I, ChunkData> neighborQuery)
	{
		// The collision surface is ALWAYS the visual surface, exactly: in both
		// mesh modes the triangles emitted into the visual mesh are also
		// collected (via the collisionOut sink) into the collision soup. The
		// player therefore always stands on what they see — no seam gap in
		// Faces mode, and in MarchingCubes mode the collision follows the
		// beveled isosurface (including its open chunk edges) rather than the
		// separate blocky face soup that used to leave a gap under the inset
		// visual surface.
		var collision = new List<Vector3>();
		ArrayMesh visual = mode == VoxelMeshMode.MarchingCubes
			? BuildVisualMarchingCubes(chunk, wx0, wz0, voxelSize, palette, coord, neighborQuery, collision)
			: BuildVisual(chunk, wx0, wz0, voxelSize, palette, solidQuery, collision);
		return (visual, collision);
	}

	// ------------------------------------------------------------------
	// Face tables
	// ------------------------------------------------------------------

	// Neighbour voxel offset per face index (also the outward normal axis).
	private static readonly Vector3I[] FaceOffsets =
	{
		new Vector3I(-1, 0, 0), // 0 -X
		new Vector3I(1, 0, 0),  // 1 +X
		new Vector3I(0, -1, 0), // 2 -Y
		new Vector3I(0, 1, 0),  // 3 +Y
		new Vector3I(0, 0, -1), // 4 -Z
		new Vector3I(0, 0, 1),  // 5 +Z
	};

	// 4 corners of the unit cube [0,1]^3 per face, ordered so that
	// (c1-c0) x (c2-c0) points INTO the cube — Godot's front-face rule is
	// CLOCKWISE as seen from outside (verified against BoxMesh), so this
	// winding is front-facing and survives CullMode.Back. Stored normals
	// (FaceNormals) still point outward, for correct lighting.
	private static readonly Vector3[][] FaceCorners =
	{
		new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 1, 1), new Vector3(0, 0, 1) }, // -X
		new[] { new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(1, 1, 1), new Vector3(1, 1, 0) }, // +X
		new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 1), new Vector3(1, 0, 0) }, // -Y
		new[] { new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 1, 1), new Vector3(0, 1, 1) }, // +Y
		new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 1, 0), new Vector3(0, 1, 0) }, // -Z
		new[] { new Vector3(0, 0, 1), new Vector3(0, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 0, 1) }, // +Z
	};

	// Outward normal per face index.
	private static readonly Vector3[] FaceNormals =
	{
		new Vector3(-1, 0, 0),
		new Vector3(1, 0, 0),
		new Vector3(0, -1, 0),
		new Vector3(0, 1, 0),
		new Vector3(0, 0, -1),
		new Vector3(0, 0, 1),
	};

	// ------------------------------------------------------------------
	// Internals
	// ------------------------------------------------------------------

	/// <summary>
	/// Visits every solid voxel face whose neighbour (in that direction) is not
	/// solid. Callback receives world-voxel coords of the solid voxel, its
	/// material, and the face index.
	/// </summary>
	private static void ForEachExposedFace(
		ChunkData chunk,
		int wx0,
		int wz0,
		System.Func<int, int, int, bool> solidQuery,
		System.Action<int, int, int, byte, int> onFace)
	{
		for (int x = 0; x < chunk.SizeX; x++)
		{
			for (int y = 0; y < chunk.SizeY; y++)
			{
				for (int z = 0; z < chunk.SizeZ; z++)
				{
					byte m = chunk.Get(x, y, z);
					if (m == 0)
					{
						continue;
					}

					int wx = wx0 + x;
					int wz = wz0 + z;

					for (int f = 0; f < FaceOffsets.Length; f++)
					{
						Vector3I o = FaceOffsets[f];
						if (solidQuery(wx + o.X, y + o.Y, wz + o.Z))
						{
							continue; // internal face, hidden by the solid neighbour
						}

						onFace(wx, y, wz, m, f);
					}
				}
			}
		}
	}

	// Emits one flat-shaded quad (6 vertices, fan of 2 triangles) for the face
	// of the unit cube at <origin>, with the face's outward normal.
	private static void AddFace(List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, Vector3 origin, int face, float voxelSize, float uvScale)
	{
		Vector3[] c = FaceCorners[face];
		Vector3 n = FaceNormals[face];

		Vector3 a = origin + c[0] * voxelSize;
		Vector3 b = origin + c[1] * voxelSize;
		Vector3 d = origin + c[2] * voxelSize;
		Vector3 e = origin + c[3] * voxelSize;

		positions.Add(a);
		positions.Add(b);
		positions.Add(d);
		positions.Add(a);
		positions.Add(d);
		positions.Add(e);

		for (int i = 0; i < 6; i++)
		{
			normals.Add(n);
		}

		// FaceCorners lists each face's 4 corners in loop order, so the same
		// UV table works for every face: one full texture tile per voxel face,
		// tiled uvScale times (set per material in VoxelMaterialConfig).
		uvs.Add(QuadUv[0] * uvScale);
		uvs.Add(QuadUv[1] * uvScale);
		uvs.Add(QuadUv[2] * uvScale);
		uvs.Add(QuadUv[0] * uvScale);
		uvs.Add(QuadUv[2] * uvScale);
		uvs.Add(QuadUv[3] * uvScale);
	}

	// UV corners shared by every face (see AddFace).
	private static readonly Vector2[] QuadUv =
	{
		new Vector2(0f, 0f),
		new Vector2(1f, 0f),
		new Vector2(1f, 1f),
		new Vector2(0f, 1f),
	};

	/// <summary>
	/// World-aligned planar UV for the Marching-Cubes mesh: project the point
	/// onto the plane whose axis is dominant in the face normal. Flat regions
	/// get continuous, seamless tiling; bevels blend between projections.
	/// </summary>
	private static Vector2 PlanarUv(Vector3 p, Vector3 n, float scale)
	{
		float ax = Math.Abs(n.X);
		float ay = Math.Abs(n.Y);
		float az = Math.Abs(n.Z);

		Vector2 uv;
		if (ax >= ay && ax >= az)
		{
			uv = new Vector2(p.Z, p.Y); // normal mostly along X -> project ZY
		}
		else if (ay >= az)
		{
			uv = new Vector2(p.X, p.Z); // normal mostly along Y -> project XZ
		}
		else
		{
			uv = new Vector2(p.X, p.Y); // normal mostly along Z -> project XY
		}

		return uv * scale;
	}

	// Emits the same quad as a bare triangle soup for the collision shape
	// (winding is irrelevant for ConcavePolygonShape3D, same table reused).
	private static void AddCollisionFace(List<Vector3> triangles, Vector3 origin, int face, float voxelSize)
	{
		Vector3[] c = FaceCorners[face];

		Vector3 a = origin + c[0] * voxelSize;
		Vector3 b = origin + c[1] * voxelSize;
		Vector3 d = origin + c[2] * voxelSize;
		Vector3 e = origin + c[3] * voxelSize;

		triangles.Add(a);
		triangles.Add(b);
		triangles.Add(d);
		triangles.Add(a);
		triangles.Add(d);
		triangles.Add(e);
	}
}
