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
	/// <param name="materialQuery">
	/// Optional. Returns the MATERIAL ID of a world voxel (0 = air) when
	/// blending is enabled. When non-null, a face whose outward neighbour is a
	/// DIFFERENT solid material is emitted as a blend-shader crossfade (a
	/// per-vertex vertical gradient) instead of snapping to a single material.
	/// Null = no blending (every face uses the single-material path).
	/// </param>
	public static ArrayMesh BuildVisual(
		ChunkData chunk,
		int wx0,
		int wz0,
		float voxelSize,
		VoxelMaterialPalette palette,
		System.Func<int, int, int, bool> solidQuery,
		System.Collections.Generic.List<Vector3> collisionOut = null,
		System.Func<int, int, int, byte> materialQuery = null,
		System.Func<int, int, int, byte> blockLightQuery = null,
		System.Func<Vector2I, ChunkData> neighborChunkQuery = null,
		float minLight = 0f,
		int maxLight = 15,
		float lightFalloff = 1f)
	{
		int lastMaterial = palette.MaterialCount;

		var positions = new List<Vector3>[lastMaterial];
		var normals = new List<Vector3>[lastMaterial];
		var uvs = new List<Vector2>[lastMaterial];
		var lights = new List<float>[lastMaterial];
		for (int i = 0; i < lastMaterial; i++)
		{
			positions[i] = new List<Vector3>();
			normals[i] = new List<Vector3>();
			uvs[i] = new List<Vector2>();
			lights[i] = new List<float>();
		}

		// Light: build a padded solid|air field (1 = solid) AND a padded
		// block-light field (0..15 per voxel's own emission) over the chunk plus
		// a 1-voxel neighbour shell (via solidQuery / blockLightQuery, which
		// already read neighbouring chunks). Flood-fill both, exposing a
		// lightQuery that maps a world voxel to its normalized light (0..1).
		// Sky light darkens caves; block light illuminates around placed
		// light-source voxels.
		int sx = chunk.SizeX, sy = chunk.SizeY, sz = chunk.SizeZ;
		// WIDE 3x3-chunk light field so sky light spans seams (see
		// BuildLightFieldWide). solidQuery / blockLightQuery no longer feed the
		// light; they still drive face culling further down.
		byte[] lightFieldF = BuildLightFieldWide(chunk, wx0, wz0, sx, sy, neighborChunkQuery, maxLight);
		// The wide field extends maxLight voxels beyond the chunk on every side
		// (see BuildLightFieldWide), so its X/Z extent is 2*cs+2*maxLight+2.
		int lpx = 2 * sx + 2 * maxLight + 2, lpy = sy + 2, lpz = 2 * sz + 2 * maxLight + 2;
		int wideHalf = lpx / 2 - 1; // = sx + maxLight: lattice offset of the center chunk origin
		// Apply the light response curve (falloff + dark-side floor) to the whole
		// field once (before any consumer normalizes it), so every per-vertex
		// light read gets it. Full-light voxels are unaffected.
		ApplyLightResponse(lightFieldF, minLight, lightFalloff, maxLight);
		float invMaxLight = 1f / maxLight;
		System.Func<int, int, int, float> lightQuery = (x, y, z) =>
		{
			int ix = x - wx0 + wideHalf;
			int iy = y + 1;
			int iz = z - wz0 + wideHalf;
			if (ix < 0 || ix >= lpx || iy < 0 || iy >= lpy || iz < 0 || iz >= lpz)
			{
				return 1f; // outside the wide field: treat as open sky (full light)
			}
			return lightFieldF[ix + lpx * (iy + lpy * iz)] * invMaxLight;
		};

		// Blended faces (a face whose outward neighbour is a DIFFERENT solid
		// material) are crossfaded per-pixel by the blend shader instead of
		// snapping to one material. Buckets are keyed by the blend
		// ShaderMaterial, so each distinct material pair gets one surface. Each
		// vertex carries its blend factor (UV2.x: 0 = pure A / lower id, 1 =
		// pure B / higher id), a vertical gradient across the face.
		var blended = new Dictionary<ShaderMaterial, (List<Vector3> pos, List<Vector3> norm, List<Vector2> uv, List<Vector2> uv2, List<float> lights)>();

		ForEachExposedFace(chunk, wx0, wz0, solidQuery, (wx, y, wz, m, face) =>
		{
			// Skip faces whose material id has no palette slot (out-of-range
			// ids would index the per-material vertex lists out of bounds).
			if (m < 1 || m > lastMaterial)
			{
				return;
			}
			Vector3 origin = new Vector3(wx, y, wz) * voxelSize;

			// The sky light this face should be darkened by is the light of the
			// AIR voxel on the outside of the face (the face's outward
			// neighbour). Open surfaces border light-15 air; cave walls border
			// dark air, so they darken to near-black.
			Vector3I o = FaceOffsets[face];
			float light = lightQuery(wx + o.X, y + o.Y, wz + o.Z);

			// Is this face at a MATERIAL interface? The outward neighbour (in
			// the face's direction) is a different, solid, configured material.
			// Only those faces crossfade; every other face keeps the cheap
			// single-material path.
			bool isBlended = false;
			if (materialQuery != null)
			{
				byte neighbor = materialQuery(wx + o.X, y + o.Y, wz + o.Z);
				if (neighbor != 0 && neighbor != m && neighbor >= 1 && neighbor <= lastMaterial)
				{
					isBlended = true;
					EmitBlendedFace(blended, palette, origin, face, voxelSize, m, neighbor, light);
				}
			}

			// A blended face is drawn by the blend shader, not the per-material
			// path — skip the single-material emission for it.
			if (!isBlended)
			{
				AddFace(positions[m - 1], normals[m - 1], uvs[m - 1], lights[m - 1], origin, face, voxelSize, palette.TextureScales[m - 1], light);
			}

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
				// UV2.x = per-vertex sky light (0..1); the material shader
				// multiplies ALBEDO by it to darken enclosed geometry. UV2 is
				// free on the single-material path (blending uses it too, but a
				// surface is either blended or single-material, never both).
				tool.SetUV2(new Vector2(lights[m - 1][i], 0f));
				tool.AddVertex(positions[m - 1][i]);
			}

			tool.Commit(mesh);
		}

		// Emit one surface per blended material pair — the faces that crossfade
		// between two materials. Each vertex carries its blend factor (UV2.x)
		// and sky light (UV3.x).
		foreach (var entry in blended)
		{
			var b = entry.Value;
			if (b.pos.Count == 0)
			{
				continue;
			}
			var tool = new SurfaceTool();
			tool.Begin(Mesh.PrimitiveType.Triangles);
			tool.SetMaterial(entry.Key);
			for (int i = 0; i < b.pos.Count; i++)
			{
				tool.SetNormal(b.norm[i]);
				tool.SetUV(b.uv[i]);
				// UV2.x = blend factor, UV2.y = per-vertex sky light (0..1).
				tool.SetUV2(new Vector2(b.uv2[i].X, b.lights[i]));
				tool.AddVertex(b.pos[i]);
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
	///
	/// When <paramref name="materialQuery"/> is non-null (blending enabled),
	/// MIXED triangles — whose three vertices span 2+ distinct materials, i.e.
	/// the bevels/edges where material regions meet on the isosurface — are
	/// emitted as a blend-shader crossfade instead of snapping to the majority
	/// material: each vertex carries a blend factor (UV2.x, 0 = pure A / lower
	/// id, 1 = pure B / higher id) and the shader mixes the two materials'
	/// textures per-pixel. The vertex materials themselves come from the padded
	/// material field (not from this query), which is used only as the on/off
	/// gate. Single-material triangles are unaffected. Null = no blending
	/// (every triangle uses the per-material path).
	/// </summary>
	public static ArrayMesh BuildVisualMarchingCubes(
		ChunkData chunk,
		int wx0,
		int wz0,
		float voxelSize,
		VoxelMaterialPalette palette,
		Vector2I coord,
		System.Func<Vector2I, ChunkData> neighborQuery,
		System.Collections.Generic.List<Vector3> collisionOut = null,
		System.Func<int, int, int, byte> materialQuery = null,
		float minLight = 0f,
		int maxLight = 15,
		float lightFalloff = 1f,
		bool debugVertexLight = false)
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
		var blockLight = new byte[px * py * pz];
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
					// The voxel's own block-light emission level (0 = not a
					// source). A torch placed in a neighbouring chunk still
					// lights the air across the seam via this shell.
					blockLight[idx] = SampleBlockLight(
						chunk, sx, sy, sz, coord, neighborQuery, ix - 1, iy - 1, iz - 1);
				}
			}
		}

		// 1b) Light: flood-fill a WIDE 3x3-chunk region (self + 8 neighbours)
		//     so sky light propagates a full 15-voxel radius across chunk seams
		//     (caves spanning a seam no longer stay dark on one side) and the
		//     loaded frontier isn't clipped to a 1-voxel shell. Each MC vertex
		//     samples the air voxel it borders from this wide field and stores
		//     it in UV2.x; the material shader multiplies ALBEDO by it. The
		//     1-voxel padded `solid`/`blockLight` fields above are still used
		//     for the MC isosurface and for the (now-legacy) debug dump below.
		byte[] wideLight = BuildLightFieldWide(chunk, wx0, wz0, sx, sy, neighborQuery, maxLight);
		int wx0w = wx0;
		int wz0w = wz0;
		// The wide field extends maxLight voxels beyond the chunk on every side
		// (see BuildLightFieldWide), so its X/Z extent is 2*cs+2*maxLight+2.
		int lpx = 2 * sx + 2 * maxLight + 2, lpy = sy + 2, lpz = 2 * sz + 2 * maxLight + 2;
		int wideHalf = lpx / 2 - 1; // = sx + maxLight: lattice offset of the center chunk origin
		float invMaxLight = 1f / maxLight;
		System.Func<int, int, int, float> lightQuery = (x, y, z) =>
		{
			int ix = x - wx0w + wideHalf;
			int iy = y + 1;
			int iz = z - wz0w + wideHalf;
			if (ix < 0 || ix >= lpx || iy < 0 || iy >= lpy || iz < 0 || iz >= lpz)
			{
				return 1f; // outside the wide field: treat as open sky (full light)
			}
			return wideLight[ix + lpx * (iy + lpy * iz)] * invMaxLight;
		};

		// Apply the light response curve (falloff + dark-side floor) to the wide
		// field once (before any consumer normalizes it), so every per-vertex
		// light read gets it. Full-light voxels are unaffected.
		ApplyLightResponse(wideLight, minLight, lightFalloff, maxLight);

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
		var perLight = new List<float>[lastMaterial];       // one sky-light (0..1) per vertex
		for (int i = 0; i < lastMaterial; i++)
		{
			perMaterial[i] = new List<Vector3>();
			perUv[i] = new List<Vector2>();
			perLight[i] = new List<float>();
		}

		// Mixed-material triangles (their vertices span 2+ distinct materials)
		// are emitted with a blend SHADER instead of snapping to the majority:
		// a per-pixel crossfade between the two materials' textures, with a
		// per-vertex blend factor (UV2.x). Buckets are keyed by the blend
		// ShaderMaterial, so each distinct material pair gets one surface.
		var blended = new Dictionary<ShaderMaterial, (List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector2> uv2s, List<float> lights)>();

		// DEBUG (one-shot): collect the per-vertex light the mesh actually
		// emits, so we can see whether the MC surface carries a light gradient
		// or a uniform value. Gated on debugVertexLight.
		var dbgLight = debugVertexLight ? new List<(Vector3 p, float l)>() : null;

		foreach (var tri in tris)
		{
			Vector3 pa, pb, pc;
			int ma, mb, mc;
			float la, lb, lc;
			Vertex(solid, field, px, py, pz, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeA, out pa, out ma);
			Vertex(solid, field, px, py, pz, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeB, out pb, out mb);
			Vertex(solid, field, px, py, pz, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeC, out pc, out mc);
			// Light comes from the WIDE 3x3-chunk field so it spans seams.
			VertexWideLight(solid, px, py, pz, wx0w, wz0w, wideLight, lpx, lpy, lpz, sx, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeA, invMaxLight, out _, out la);
			VertexWideLight(solid, px, py, pz, wx0w, wz0w, wideLight, lpx, lpy, lpz, sx, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeB, invMaxLight, out _, out lb);
			VertexWideLight(solid, px, py, pz, wx0w, wz0w, wideLight, lpx, lpy, lpz, sx, tri.CellX, tri.CellY, tri.CellZ, tri.EdgeC, invMaxLight, out _, out lc);
			if (dbgLight != null)
			{
				dbgLight.Add((pa, la));
				dbgLight.Add((pb, lb));
				dbgLight.Add((pc, lc));
			}

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

			// Is this triangle at a material interface (its 3 vertices span 2+
			// distinct materials)? When blending is on, those triangles are
			// emitted as a blend-shader crossfade instead of snapping to the
			// majority material — that's the whole point of the effect.
			bool isMixed = ma != mb || ma != mc;
			if (isMixed && materialQuery != null)
			{
				EmitBlendedTriangle(blended, palette, pa, pb, pc, n, ma, mb, mc, la, lb, lc);
			}
			else
			{
				// Single-material triangle (or blending off): majority vote of
				// the vertex materials.
				int m = ma == mb || ma == mc ? ma : (mb == mc ? mb : mc);
				if (m < 1 || m > lastMaterial)
				{
					// Air (impossible: every crossing has a solid endpoint) or
					// a material id with no palette slot — skip instead of
					// indexing the per-material lists out of bounds.
					continue;
				}

				var verts = perMaterial[m - 1];
				var uvs = perUv[m - 1];
				var lights = perLight[m - 1];
				float uvScale = palette.TextureScales[m - 1];
				verts.Add(pa);
				verts.Add(n);
				uvs.Add(PlanarUv(pa, n, uvScale));
				lights.Add(la);
				verts.Add(pb);
				verts.Add(n);
				uvs.Add(PlanarUv(pb, n, uvScale));
				lights.Add(lb);
				verts.Add(pc);
				verts.Add(n);
				uvs.Add(PlanarUv(pc, n, uvScale));
				lights.Add(lc);
			}

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
			var lights = perLight[m - 1];
			var tool = new SurfaceTool();
			tool.Begin(Mesh.PrimitiveType.Triangles);
			tool.SetMaterial(palette.Materials[m - 1]);
			for (int i = 0; i + 1 < verts.Count; i += 2)
			{
				tool.SetNormal(verts[i + 1]);
				tool.SetUV(uvs[i / 2]);
				// UV2.x = per-vertex sky light (0..1); the material shader
				// multiplies ALBEDO by it to darken enclosed geometry.
				tool.SetUV2(new Vector2(lights[i / 2], 0f));
				tool.AddVertex(verts[i]);
			}
			tool.Commit(mesh);
		}

		// Emit one surface per blended material pair — the mixed triangles
		// that crossfade between two materials. Each vertex carries its blend
		// factor (UV2.x: 0 = pure A / lower id, 1 = pure B / higher id) and its
		// sky light (UV3.x).
		foreach (var entry in blended)
		{
			var b = entry.Value;
			if (b.verts.Count == 0)
			{
				continue;
			}
			var tool = new SurfaceTool();
			tool.Begin(Mesh.PrimitiveType.Triangles);
			tool.SetMaterial(entry.Key);
			for (int i = 0; i < b.verts.Count; i++)
			{
				tool.SetNormal(b.norms[i]);
				tool.SetUV(b.uvs[i]);
				// UV2.x = blend factor, UV2.y = per-vertex sky light (0..1).
				tool.SetUV2(new Vector2(b.uv2s[i].X, b.lights[i]));
				tool.AddVertex(b.verts[i]);
			}
			tool.Commit(mesh);
		}

		// DEBUG (one-shot): report the per-vertex light the MC mesh emitted, so
		// we can see whether the surface carries a light gradient or a uniform
		// value. If the cave looks uniform, this tells us whether the vertices
		// actually vary.
		if (dbgLight != null)
		{
			float mn = float.MaxValue, mx = 0f;
			var byLight = new Dictionary<int, int>(); // rounded light -> count
			foreach (var (p, l) in dbgLight)
			{
				if (l < mn) mn = l;
				if (l > mx) mx = l;
				int k = (int)(l * 100f + 0.5f);
				byLight[k] = byLight.TryGetValue(k, out int c) ? c + 1 : 1;
			}
			string hist = string.Join(" ", System.Linq.Enumerable
				.Select(System.Linq.Enumerable.OrderBy(byLight, kv => kv.Key),
					kv => $"{(kv.Key / 100f):F2}:{kv.Value}"));
			GD.Print($"[VTEX-LIGHT] chunk {coord}: {dbgLight.Count} verts min={mn:F2} max={mx:F2} histogram(light:count)= [{hist}]");
		}

		return mesh;
	}

	/// <summary>
	/// Resolves one MC triangle vertex: its lattice-space position (from
	/// the binary solid field) and the material index of the solid voxel the
	/// crossing came from (from the material field). (Per-vertex light is
	/// sampled separately from the WIDE field by <see cref="VertexWideLight"/>.)
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
	/// Resolves one MC triangle vertex's light from the WIDE light field
	/// instead of the 1-voxel padded one. The air corner (ax,ay,az) is in the
	/// padded lattice (1..nx-1 etc.); converting to world voxels (ax-1, ay-1,
	/// az-1) and indexing the wide field lets the sample see a full
	/// maxLight-voxel radius of neighbour light across chunk seams. The solid
	/// check stays on the padded field (which is what the isosurface was built
	/// from). The wide field's X/Z offset is <c>lpx/2 - 1</c> (= cs + maxLight):
	/// it no longer coincides with the padded field's 1-voxel offset, so it is
	/// derived from the field dimensions instead of the chunk size.
	/// </summary>
	private static void VertexWideLight(
		float[] solidField,
		int nx,
		int ny,
		int nz,
		int wx0,
		int wz0,
		byte[] wideLight,
		int lpx,
		int lpy,
		int lpz,
		int cs,
		int cx,
		int cy,
		int cz,
		int edge,
		float invMaxLight,
		out Vector3 position,
		out float light)
	{
		MarchingCubes.ResolveEdge(solidField, nx, ny, nz, cx, cy, cz, edge,
			out position, out _, out _, out _);
		MarchingCubes.ResolveEdgeAirCorner(solidField, nx, ny, nz, cx, cy, cz, edge,
			out int ax, out int ay, out int az);
		// Air voxel in the padded field (defensive solid check on the field the
		// surface was built from), then its world-space light from the wide field.
		int slidx = ax + nx * (ay + ny * az);
		if (solidField[slidx] != 0f)
		{
			light = 0f;
			return;
		}
		int wx = wx0 + ax - 1;
		int wy = ay - 1;
		int wz = wz0 + az - 1;
		int half = lpx / 2 - 1; // wide field X offset = cs + maxLight
		int ix = wx - wx0 + half;
		int iy = wy + 1;
		int iz = wz - wz0 + half;
		if (ix < 0 || ix >= lpx || iy < 0 || iy >= lpy || iz < 0 || iz >= lpz)
		{
			light = 1f; // outside the wide field: open sky
			return;
		}
		light = wideLight[ix + lpx * (iy + lpy * iz)] * invMaxLight;
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
	/// Samples the per-voxel BLOCK LIGHT level (0 = not a light source) at
	/// chunk-relative lattice point (x, y, z), mirroring
	/// <see cref="SampleMaterial"/> but reading the <c>BlockLight</c> array
	/// instead of <c>Data</c>. Used to build the padded block-light field the
	/// sky-light flood fill seeds from, so a light source in a neighbouring
	/// chunk correctly lights the air across the chunk seam.
	/// </summary>
	private static byte SampleBlockLight(
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
			return chunk.BlockLight[x + sx * (y + sy * z)];
		}

		if (y < 0 || y >= sy)
		{
			return 0;
		}

		int dx = x < 0 ? -1 : (x >= sx ? 1 : 0);
		int dz = z < 0 ? -1 : (z >= sz ? 1 : 0);
		if (dx != 0 && dz != 0)
		{
			var diag = neighborQuery(new Vector2I(coord.X + dx, coord.Y + dz));
			if (diag != null)
			{
				int dlx = dx > 0 ? 0 : diag.SizeX - 1;
				int dlz = dz > 0 ? 0 : diag.SizeZ - 1;
				return diag.BlockLight[dlx + diag.SizeX * (y + diag.SizeY * dlz)];
			}
			return 0;
		}

		var neighbour = neighborQuery(new Vector2I(coord.X + dx, coord.Y + dz));
		if (neighbour == null)
		{
			return 0;
		}

		int lx = dx < 0 ? neighbour.SizeX - 1 : (dx > 0 ? 0 : x);
		int lz = dz < 0 ? neighbour.SizeZ - 1 : (dz > 0 ? 0 : z);
		return neighbour.BlockLight[lx + neighbour.SizeX * (y + neighbour.SizeY * lz)];
	}

	/// <summary>
	/// Samples the SOLID|air state for the LIGHT flood fill at chunk-relative
	/// lattice point (x, y, z), mirroring <see cref="SampleMaterial"/> but with
	/// one crucial difference: an UNLOADED (or still-generating) neighbour
	/// counts as SOLID, not air. This matches the mesh-culling rule
	/// (<c>VoxelSolidForMesh</c>) and, more importantly, stops the sky-light
	/// flood fill from falsely seeding the padded shell as open sky when a
	/// neighbour is not yet generated. Without this, a chunk built while its
	/// neighbour is unloaded gets a full-brightness (15) edge, then its
	/// neighbour (built with the data present) gets a dark edge — producing the
	/// "0 to 15 across one voxel" seam artifact. Treating unloaded as solid
	/// keeps the light field consistent with what is actually rendered.
	/// </summary>
	private static bool SampleSolidForLight(
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
			return chunk.Data[x + sx * (y + sy * z)] != 0;
		}

		// Below the world bottom is solid (the floor); above the chunk top is
		// open sky (air) — the sky seed needs the top to be air to light down.
		if (y < 0)
		{
			return true;
		}
		if (y >= sy)
		{
			return false;
		}

		int dx = x < 0 ? -1 : (x >= sx ? 1 : 0);
		int dz = z < 0 ? -1 : (z >= sz ? 1 : 0);
		if (dx != 0 && dz != 0)
		{
			// Diagonal corner voxel of the diagonal neighbour. Unloaded = solid
			// (opaque) for the light field so the edge doesn't leak sky.
			var diag = neighborQuery(new Vector2I(coord.X + dx, coord.Y + dz));
			if (diag == null)
			{
				return true;
			}
			int dlx = dx > 0 ? 0 : diag.SizeX - 1;
			int dlz = dz > 0 ? 0 : diag.SizeZ - 1;
			return diag.Data[dlx + diag.SizeX * (y + diag.SizeY * dlz)] != 0;
		}

		var neighbour = neighborQuery(new Vector2I(coord.X + dx, coord.Y + dz));
		if (neighbour == null)
		{
			return true; // UNLOADED neighbour: solid (opaque), matches culling
		}
		int lx = dx < 0 ? neighbour.SizeX - 1 : (dx > 0 ? 0 : x);
		int lz = dz < 0 ? neighbour.SizeZ - 1 : (dz > 0 ? 0 : z);
		return neighbour.Data[lx + neighbour.SizeX * (y + neighbour.SizeY * lz)] != 0;
	}

	/// <summary>
	/// Resolves the SOLID|air state of a WORLD voxel for the LIGHT field: the
	/// owning chunk's data when that chunk is loaded and generated, otherwise
	/// SOLID. Treating an unloaded / still-generating neighbour as solid is the
	/// conservative (dark) choice that prevents sky light from leaking in across
	/// an edge whose neighbour hasn't been generated yet (the "0 to 15 across one
	/// voxel" seam). A chunk whose <c>DataReady</c> is false holds an all-zero
	/// buffer, so we must check the flag — reading it as air would seed a false
	/// open-sky column. y below the world floor is solid; y above the chunk top
	/// is open sky (air) so the sky seed can light down from the top.
	/// </summary>
	private static bool WorldSolidForLight(
		ChunkData chunk,
		int wx0,
		int wz0,
		int cs,
		int sy,
		System.Func<Vector2I, ChunkData> neighborQuery,
		int wx,
		int wy,
		int wz)
	{
		if (wy < 0)
		{
			return true; // world floor: opaque
		}
		if (wy >= sy)
		{
			return false; // above the chunk top: open sky
		}
		// FloorDiv: the world can contain negative coordinates.
		int cxi = (wx >= 0) ? wx / cs : -((-wx + cs - 1) / cs);
		int czi = (wz >= 0) ? wz / cs : -((-wz + cs - 1) / cs);
		if (cxi * cs == wx0 && czi * cs == wz0)
		{
			return chunk.Data[(wx - cxi * cs) + cs * (wy + sy * (wz - czi * cs))] != 0;
		}
		var n = neighborQuery(new Vector2I(cxi, czi));
		if (n == null)
		{
			// GetChunkData returns null for unloaded / still-generating chunks,
			// so a null neighbour reads as solid (opaque, dark).
			return true;
		}
		return n.Data[(wx - cxi * cs) + cs * (wy + sy * (wz - czi * cs))] != 0;
	}

	/// <summary>
	/// Builds a WIDE light field over a region extending <paramref name="maxLight"/>
	/// voxels BEYOND the center chunk on all four X/Z sides (plus the 1-voxel
	/// padding above and below), so the flood fill in <see cref="SkyLight.Compute"/>
	/// can propagate a FULL light range without hitting the field boundary —
	/// the fix for caves that span a seam staying dark on one side, and for the
	/// dark line at chunk borders where a small field clipped the fill.
	///
	/// The returned field is (2cs + 2*maxLight + 2) wide in X/Z and (sy+2) tall
	/// in Y; the center chunk occupies lattice [cs+1+maxLight, 2cs+maxLight] in
	/// X/Z and [1, sy] in Y. The X/Z extent must cover the center chunk PLUS one
	/// full light range on each side: a voxel at the far edge of the chunk can
	/// receive light that traveled maxLevel voxels from a source up to a full
	/// chunk away across the seam, and the BFS must be able to see that source
	/// (or the solid wall that blocks it) before it stops. With the old
	/// 3x3-chunk (cs+1 shell) extent, light from sources beyond the shell was
	/// clipped at the field edge while the same distance was lit on the
	/// opposite side — a brightness step right at the chunk border.
	///
	/// Unloaded neighbours read as solid (see <see cref="WorldSolidForLight"/>),
	/// so a not-yet-generated region is dark rather than falsely bright, and it
	/// re-lights correctly the moment the neighbour appears (VoxelWorld flags
	/// its neighbours for rebuild).
	///
	/// NOTE: the field dimensions must stay in sync with the callers' <c>lpx</c>
	/// / <c>lpz</c> computations (2*cs + 2*maxLight + 2) and with
	/// <see cref="VertexWideLight"/>'s <c>lpx/2 - 1</c> offset.
	/// </summary>
	private static byte[] BuildLightFieldWide(
		ChunkData chunk,
		int wx0,
		int wz0,
		int cs,
		int sy,
		System.Func<Vector2I, ChunkData> neighborQuery,
		int maxLight = 15)
	{
		// Clamp the range exactly as SkyLight.Compute does (byte field, >= 1).
		int range = maxLight;
		if (range < 1)
		{
			range = 1;
		}
		else if (range > 255)
		{
			range = 255;
		}
		// Center chunk + one full light range per side + 1-voxel pad per side.
		int px = 2 * cs + 2 * range + 2, py = sy + 2, pz = 2 * cs + 2 * range + 2;
		int half = px / 2 - 1; // = cs + range: world offset of lattice ix 0
		var solid = new byte[px * py * pz];
		var block = new byte[px * py * pz];
		for (int iz = 0; iz < pz; iz++)
		{
			for (int iy = 0; iy < py; iy++)
			{
				for (int ix = 0; ix < px; ix++)
				{
					int wx = wx0 + ix - half;
					int wy = iy - 1;
					int wz = wz0 + iz - half;
					int idx = ix + px * (iy + py * iz);
					if (wy < 0 || wy >= sy)
					{
						solid[idx] = (byte)(wy < 0 ? 1 : 0); // floor solid / sky air
						continue;
					}
					int cxi = (wx >= 0) ? wx / cs : -((-wx + cs - 1) / cs);
					int czi = (wz >= 0) ? wz / cs : -((-wz + cs - 1) / cs);
					int lx = wx - cxi * cs;
					int lz = wz - czi * cs;
					ChunkData c = (cxi * cs == wx0 && czi * cs == wz0)
						? chunk
						: neighborQuery(new Vector2I(cxi, czi));
					if (c == null)
					{
						// Neighbor is NOT ready (unloaded or still generating):
						// read it as SOLID (opaque, dark) everywhere in the wide
						// field. This matches the mesh-culling rule
						// (VoxelSolidForMesh treats unloaded chunks as solid) and
						// is conservative for the deep fill, so the frontier
						// under-lights rather than falsely over-lighting a cave
						// mouth that opens toward the frontier. It is also what
						// stops sky light leaking in across an edge whose
						// neighbour hasn't been generated yet (the "0 to 15
						// across one voxel" seam). The old "read as air inside
						// the 1-voxel shell" special case is no longer needed:
						// every voxel a vertex can sample lies inside the center
						// chunk (whose own data is always ready), so vertex
						// light samples never hit the not-ready fallback.
						solid[idx] = 1;
						continue;
					}
					int li = lx + cs * (wy + sy * lz);
					solid[idx] = (byte)(c.Data[li] != 0 ? 1 : 0);
					block[idx] = c.BlockLight[li];
				}
			}
		}
		return SkyLight.Compute(solid, px, py, pz, block, maxLight);
	}

	/// <summary>
	/// Applies the light RESPONSE CURVE to a 0..maxLight field IN PLACE, in two
	/// stages (both pure per-voxel remaps, so the field stays a valid light
	/// field and the result is still seam-safe — both sides of a seam apply the
	/// same curve to the same values):
	/// <list type="number">
	///   <item>FALLOFF: <c>l' = l^exponent</c> on the normalized level. The BFS
	///   light decays 1/voxel, so a cave interior more than <c>maxLight</c>
	///   voxels from the nearest opening is level 0 (black) no matter what — a
	///   linear response can never make deep caves readable. Raising the
	///   exponent (&gt;1) lifts every sub-full level, stretching the *visible*
	///   falloff so the gradient spans much more distance and cave interiors
	///   sit at a dim-but-visible level instead of black. exponent 1.0 = linear
	///   (Minecraft-faithful, hard cutoff at the light range).</item>
	///   <item>FLOOR: <see cref="VoxelWorld.ApplyMinLight"/> lifts the remaining
	///   dark side to <paramref name="minLight"/> (0 = fully black).</item>
	/// </list>
	/// Full-light voxels (level maxLight) are unaffected by either stage.
	/// </summary>
	private static void ApplyLightResponse(byte[] light, float minLight, float exponent, int maxLight = 15)
	{
		float ml = maxLight;
		float exp = exponent >= 1f ? exponent : 1f;
		bool hasFloor = minLight > 0f;
		for (int i = 0; i < light.Length; i++)
		{
			float norm = light[i] / ml;
			// Stage 1 — falloff curve (no-op when exp == 1).
			if (exp > 1.0001f)
			{
				norm = (float)Math.Pow(norm, exp);
			}
			// Stage 2 — dark-side floor (no-op when minLight == 0).
			if (hasFloor)
			{
				norm = VoxelWorld.ApplyMinLight(norm, minLight);
			}
			light[i] = (byte)(norm * ml + 0.5f);
		}
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
		System.Func<Vector2I, ChunkData> neighborQuery,
		System.Func<int, int, int, byte> materialQuery = null,
		System.Func<int, int, int, byte> blockLightQuery = null,
		System.Func<Vector2I, ChunkData> neighborChunkQuery = null,
		float minLight = 0f,
		int maxLight = 15,
		float lightFalloff = 1f,
		bool debugVertexLight = false)
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
		// Blending: when a material query is supplied, material-interface faces
		// (Faces mode) and mixed triangles (MarchingCubes mode) crossfade via
		// the blend shader; null = no blending (every face/triangle snaps to a
		// single material).
		ArrayMesh visual = mode == VoxelMeshMode.MarchingCubes
			? BuildVisualMarchingCubes(chunk, wx0, wz0, voxelSize, palette, coord, neighborQuery, collision, materialQuery, minLight, maxLight, lightFalloff, debugVertexLight)
			: BuildVisual(chunk, wx0, wz0, voxelSize, palette, solidQuery, collision, materialQuery, blockLightQuery, neighborQuery, minLight, maxLight, lightFalloff);
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
	private static void AddFace(List<Vector3> positions, List<Vector3> normals, List<Vector2> uvs, List<float> lights, Vector3 origin, int face, float voxelSize, float uvScale, float light)
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
			lights.Add(light);
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
	/// Emits a face quad into the blend buckets as a per-vertex crossfade between
	/// the face voxel's material (<paramref name="m"/>) and the outward
	/// neighbour's material (<paramref name="neighbor"/>). The pair is ordered
	/// ascending (A = lower id, B = higher id, matching
	/// <see cref="VoxelMaterialPalette.GetBlendShaderMaterial"/>).
	///
	/// The blend factor is a VERTICAL gradient across the face: 0 at the top, 1
	/// at the bottom (UV2.x). For a horizontal interface (grass over dirt, the
	/// common case) the top corner belongs to the upper material and the bottom
	/// to the lower one, so the gradient fades the upper material out downward.
	/// The UV is tiled by the face voxel's own material scale (one full tile per
	/// face, same as <see cref="AddFace"/>).
	/// </summary>
	private static void EmitBlendedFace(
		Dictionary<ShaderMaterial, (List<Vector3> pos, List<Vector3> norm, List<Vector2> uv, List<Vector2> uv2, List<float> lights)> blended,
		VoxelMaterialPalette palette, Vector3 origin, int face, float voxelSize, byte m, byte neighbor, float light)
	{
		int idA = m < neighbor ? m : neighbor; // lower id
		int idB = m < neighbor ? neighbor : m; // higher id
		bool mIsA = m == idA;

		ShaderMaterial bm = palette.GetBlendShaderMaterial(idA, idB);
		if (!blended.TryGetValue(bm, out var bucket))
		{
			bucket = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>(), new List<Vector2>(), new List<float>());
			blended[bm] = bucket;
		}

		Vector3[] c = FaceCorners[face];
		Vector3 n = FaceNormals[face];

		Vector3 a = origin + c[0] * voxelSize;
		Vector3 b = origin + c[1] * voxelSize;
		Vector3 d = origin + c[2] * voxelSize;
		Vector3 e = origin + c[3] * voxelSize;

		// Vertical gradient: the two bottom corners (c[2], c[3]) get factor 1,
		// the two top corners (c[0], c[1]) get factor 0. Convert the face voxel's
		// material to the shader convention (1 = pure A / lower id, 0 = pure B /
		// higher id) at each corner: the corner that "belongs" to the face voxel's
		// material gets its side of the gradient, the other gets the opposite.
		float fa = mIsA ? 0f : 1f; // top-left  (c[0])
		float fb = mIsA ? 0f : 1f; // top-right (c[1])
		float fd = mIsA ? 1f : 0f; // bottom-left  (c[2])
		float fe = mIsA ? 1f : 0f; // bottom-right (c[3])

		float uvScale = palette.TextureScales[m - 1];

		// Same winding + UV table as AddFace (c[0],c[1],c[2] then c[0],c[2],c[3]).
		bucket.pos.Add(a); bucket.pos.Add(b); bucket.pos.Add(d);
		bucket.pos.Add(a); bucket.pos.Add(d); bucket.pos.Add(e);
		for (int i = 0; i < 6; i++) { bucket.norm.Add(n); bucket.lights.Add(light); }
		bucket.uv.Add(QuadUv[0] * uvScale); bucket.uv.Add(QuadUv[1] * uvScale); bucket.uv.Add(QuadUv[2] * uvScale);
		bucket.uv.Add(QuadUv[0] * uvScale); bucket.uv.Add(QuadUv[2] * uvScale); bucket.uv.Add(QuadUv[3] * uvScale);
		bucket.uv2.Add(new Vector2(fa, 0f)); bucket.uv2.Add(new Vector2(fb, 0f)); bucket.uv2.Add(new Vector2(fd, 0f));
		bucket.uv2.Add(new Vector2(fa, 0f)); bucket.uv2.Add(new Vector2(fd, 0f)); bucket.uv2.Add(new Vector2(fe, 0f));
	}

	/// <summary>
	/// Emits a Marching-Cubes triangle into the blend buckets as a per-vertex
	/// crossfade between two materials. Each vertex's material (from the MC
	/// crossing) determines its blend factor: a vertex whose material IS the
	/// lower id (A) gets 0 (pure A), a vertex whose material IS the higher id
	/// (B) gets 1 (pure B), and a vertex with a third material gets the
	/// majority's side. The pair is ordered ascending (A = lower id, B = higher
	/// id, matching <see cref="VoxelMaterialPalette.GetBlendShaderMaterial"/>).
	/// </summary>
	private static void EmitBlendedTriangle(
		Dictionary<ShaderMaterial, (List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<Vector2> uv2s, List<float> lights)> blended,
		VoxelMaterialPalette palette, Vector3 pa, Vector3 pb, Vector3 pc, Vector3 n, int ma, int mb, int mc,
		float la, float lb, float lc)
	{
		// The interface is the pair of the two most common vertex materials
		// (a triangle spans 2 or 3 distinct materials; the 3rd, if any, is a
		// minority vertex on a bevel and snaps to the dominant side below).
		// Candidate pair: (ma, the first of mb/mc that differs from ma).
		int a = ma, b = ma != mb ? mb : mc;
		// Count occurrences of each candidate among the three vertices:
		int ca = 1 + (mb == a ? 1 : 0) + (mc == a ? 1 : 0);
		int cb = 1 + (ma == b ? 1 : 0) + (mc == b ? 1 : 0);
		if (cb > ca)
		{
			// The second candidate is the more common one: swap so A is dominant.
			int t = a; a = b; b = t;
			int tc = ca; ca = cb; cb = tc;
		}
		int idA = a, idB = b;
		if (idA > idB) { int t = idA; idA = idB; idB = t; }

		ShaderMaterial bm = palette.GetBlendShaderMaterial(idA, idB);
		if (!blended.TryGetValue(bm, out var bucket))
		{
			bucket = (new List<Vector3>(), new List<Vector3>(), new List<Vector2>(), new List<Vector2>(), new List<float>());
			blended[bm] = bucket;
		}

		// Per-vertex blend factor: 0 = pure A (lower id), 1 = pure B (higher id).
		// A vertex whose material is the dominant one (a, = idA or idB with the
		// higher count) snaps to that side; a vertex with the other interface
		// material snaps to its side. (Every vertex is one of the pair: the pair
		// is built from the distinct vertex materials.)
		float Factor(int matId) => matId == idA ? 0f : 1f;

		float uvScale = palette.TextureScales[idA - 1];

		bucket.verts.Add(pa); bucket.norms.Add(n);
		bucket.uvs.Add(PlanarUv(pa, n, uvScale)); bucket.uv2s.Add(new Vector2(Factor(ma), 0f)); bucket.lights.Add(la);
		bucket.verts.Add(pb); bucket.norms.Add(n);
		bucket.uvs.Add(PlanarUv(pb, n, uvScale)); bucket.uv2s.Add(new Vector2(Factor(mb), 0f)); bucket.lights.Add(lb);
		bucket.verts.Add(pc); bucket.norms.Add(n);
		bucket.uvs.Add(PlanarUv(pc, n, uvScale)); bucket.uv2s.Add(new Vector2(Factor(mc), 0f)); bucket.lights.Add(lc);
	}

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
