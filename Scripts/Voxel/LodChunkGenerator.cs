using Godot;
using System;
using System.Collections.Generic;

namespace FactorySim.Voxel;

/// <summary>
/// One LOD region's coarse heightfield data: the (res+1) x (res+1) surface
/// heights and per-node materials sampled from the deterministic terrain
/// functions. Produced by <see cref="LodChunkGenerator.GenerateData"/> on the
/// thread pool and consumed by <see cref="LodChunkGenerator.BuildMesh"/>.
///
/// A region is a top-surface heightfield, NOT a full-resolution voxel block:
/// it only stores the topmost height per coarse grid node (plus a material
/// flag for tree canopies). That is the Distant-Horizons-style "simplified
/// mesh from downsampled data" — the same silhouette the full-resolution
/// terrain would show from far away, at a fraction of the cost, with no
/// full-res chunks needed to produce it.
/// </summary>
public sealed class LodData
{
	public short[] Heights;
	public byte[] Materials;
	public int Res; // coarse grid cells per side (nodes per side = Res + 1)
	public int Wx0; // world voxel X of the region's min corner
	public int Wz0; // world voxel Z of the region's min corner
	public int Cell; // voxel spacing between grid nodes (regionSize / Res)

	/// <summary>
	/// Bitmask of the region's four sides that get a flat vertical "skirt"
	/// (a wall from the surface edge down to <see cref="SkirtBaseHeight"/>):
	/// bit 0 = -X side, bit 1 = +X side, bit 2 = -Z side, bit 3 = +Z side.
	/// Used by the LOD-1 per-chunk placeholders: when a side of the chunk
	/// touches a full-resolution chunk that is already built, the placeholder
	/// closes the seam with a flat side facing that chunk (the heightfield's
	/// top surface ends at the boundary, so without the skirt there is an
	/// open gap down to the void). Zero (the default) means no skirts — the
	/// distant LOD regions share boundary nodes with their neighbours, so the
	/// top surface is continuous and they need none.
	/// </summary>
	public int SkirtSides;

	/// <summary>
	/// Height (in voxels) the skirts extend down to. Kept at 0 (world floor):
	/// the wall is only ever seen in the thin seam between the placeholder's
	/// edge and a full-res chunk, and extending to the floor guarantees no
	/// light leaks through even where the surface is high.
	/// </summary>
	public int SkirtBaseHeight;
}

/// <summary>
/// Deterministic LOD data + mesh generation for a single LOD region.
///
/// All methods are pure (no engine-node state beyond the supplied palette),
/// so they are safe to run on the thread pool. The terrain is sampled
/// directly from the same deterministic functions the full-resolution chunks
/// use (<c>TerrainGenerator.SurfaceHeight</c> + trees), so an LOD region is
/// stable across sessions and independent of which full-res chunks happen to
/// be loaded.
///
/// Winding follows <see cref="MeshBuilder"/>: Godot front faces are
/// CLOCKWISE, i.e. the cross product (b-a)x(c-a) points INTO the solid;
/// stored normals point outward (up, toward the air).
/// </summary>
public static class LodChunkGenerator
{
	/// <summary>
	/// Samples the coarse heightfield for a level-<paramref name="level"/>
	/// region at region coord <paramref name="rc"/>. See the class docs for
	/// what the data represents.
	/// </summary>
	/// <param name="chunks">
	/// The world's loaded-chunk map (may be null). Used for the foliage
	/// solid-base check so LOD canopies only appear where full-res chunks
	/// would actually plant a tree. Null (or a missing chunk) means the base
	/// is unknown and is treated as solid — a tree is never dropped just
	/// because the nearby chunk isn't loaded.
	/// </param>
	public static LodData GenerateData(
		int level, Vector2I rc, int chunkSizeXZ, int requestedRes, GenConfig cfg,
		Dictionary<Vector2I, VoxelChunk> chunks = null)
	{
		int regionSize = chunkSizeXZ * (1 << level); // base voxels per side
		int res = EffectiveResolution(regionSize, requestedRes);
		int cell = regionSize / res; // integer by construction (res divides regionSize)
		int wx0 = rc.X * regionSize;
		int wz0 = rc.Y * regionSize;

		int nodes = res + 1;
		var heights = new short[nodes * nodes];
		var materials = new byte[nodes * nodes];

		for (int j = 0; j < nodes; j++)
		{
			int wz = wz0 + j * cell;
			for (int i = 0; i < nodes; i++)
			{
				int wx = wx0 + i * cell;
				int h = TerrainGenerator.SurfaceHeight(wx, wz, cfg);
				var biome = TerrainGenerator.DominantBiome(wx, wz, cfg);
				// Surface material comes from the dominant biome at this column
				// (falls back to the canonical grass id when no biomes exist).
				byte mat = (byte)(biome != null ? biome.SurfaceMaterial : VoxelMaterialIds.Grass);

				// Tree canopies: raise the node to the top of the canopy and
				// flag it with the dominant biome's leaf material so the LOD
				// reads "forest" from afar. The silhouette comes from the tree
				// structure definition (LodHeight), which shares the exact gate
				// and sizing the full-res foliage stage uses, so distant trees
				// line up with near trees. Rocks and hives are intentionally
				// omitted — a boulder or a hive is smaller than a coarse cell at
				// this resolution, so it would not appear in the distant
				// silhouette anyway.
				if (cfg.Structures != null && biome != null && cfg.ForestNoise != null)
				{
					foreach (var def in cfg.Structures.ColumnStructures)
					{
						if (def.LodHeight(wx, wz, h, cfg, chunks, chunkSizeXZ, out int delta, out int lm))
						{
							h += delta;
							if (lm > 0)
							{
								mat = (byte)lm;
							}
						}
					}
				}

				int idx = i + nodes * j;
				heights[idx] = (short)h;
				materials[idx] = mat;
			}
		}

		return new LodData
		{
			Heights = heights,
			Materials = materials,
			Res = res,
			Wx0 = wx0,
			Wz0 = wz0,
			Cell = cell,
		};
	}

	/// <summary>
	/// Picks the largest grid resolution <= <paramref name="requestedRes"/>
	/// that evenly divides <paramref name="regionSize"/>, so the coarse grid
	/// tiles the region exactly (adjacent regions share boundary node world
	/// positions and the heightfields are seamless). Always returns >= 2.
	/// </summary>
	private static int EffectiveResolution(int regionSize, int requestedRes)
	{
		int max = Math.Min(requestedRes, regionSize / 2);
		for (int r = max; r >= 2; r--)
		{
			if (regionSize % r == 0)
			{
				return r;
			}
		}
		return 2;
	}

	/// <summary>
	/// Builds the visual <see cref="ArrayMesh"/> for an LOD region: a
	/// flat-shaded top-surface heightfield, one surface per material.
	/// VISUAL ONLY — no collision is produced (distant LOD is never stood on).
	///
	/// No skirt: adjacent regions sample the same boundary nodes, so the top
	/// surface is continuous across region borders (no walls at the seams).
	/// The outer edge of the LOD ring is left open, matching the normal
	/// chunks' open edges at the render-distance boundary.
	/// </summary>
	public static ArrayMesh BuildMesh(LodData data, float voxelSize, VoxelMaterialPalette palette)
	{
		int res = data.Res;
		int nodes = res + 1;
		int lastMaterial = palette.MaterialCount;

		// Guard against a structurally broken LodData (null/short Heights or
		// Materials, or Res < 1). The top-surface loop and EmitSkirts both index
		// these arrays by (Res+1)²; a mismatch would throw IndexOutOfRange and
		// crash the engine. Returning an empty mesh is safe: the chunk simply
		// shows no placeholder this frame (the next refresh rebuilds it).
		if (res < 1
			|| data.Heights == null || data.Heights.Length < nodes * nodes
			|| data.Materials == null || data.Materials.Length < nodes * nodes)
		{
			return new ArrayMesh();
		}

		var positions = new List<Vector3>[lastMaterial];
		var normals = new List<Vector3>[lastMaterial];
		var uvs = new List<Vector2>[lastMaterial];
		for (int i = 0; i < lastMaterial; i++)
		{
			positions[i] = new List<Vector3>();
			normals[i] = new List<Vector3>();
			uvs[i] = new List<Vector2>();
		}

		for (int j = 0; j < res; j++)
		{
			for (int i = 0; i < res; i++)
			{
				Vector3 v00 = NodePosition(data, voxelSize, i, j);
				Vector3 v10 = NodePosition(data, voxelSize, i + 1, j);
				Vector3 v11 = NodePosition(data, voxelSize, i + 1, j + 1);
				Vector3 v01 = NodePosition(data, voxelSize, i, j + 1);

				byte m00 = data.Materials[i + nodes * j];
				byte m10 = data.Materials[i + 1 + nodes * j];
				byte m11 = data.Materials[i + 1 + nodes * (j + 1)];
				byte m01 = data.Materials[i + nodes * (j + 1)];

				// Two triangles per cell. This corner order yields a cross
				// product pointing down (-Y, into the solid) for a roughly
				// flat top — Godot's front-face (clockwise) winding.
				EmitTriangle(positions, normals, uvs, v00, v10, v11, m00, m10, m11, palette);
				EmitTriangle(positions, normals, uvs, v00, v11, v01, m00, m11, m01, palette);
			}
		}

		// Flat vertical skirts on sides that face a full-resolution chunk
		// (see LodData.SkirtSides): a wall strip from the surface edge down to
		// SkirtBaseHeight, closing the seam between this coarse heightfield and
		// the neighbour's solid mesh. Distant LOD regions leave SkirtSides=0.
		EmitSkirts(data, voxelSize, palette, positions, normals, uvs);

		// One flat-shaded surface per material (mirrors MeshBuilder).
		var mesh = new ArrayMesh();
		for (int m = 1; m <= lastMaterial; m++)
		{
			if (positions[m - 1].Count == 0)
			{
				continue;
			}

			var tool = new SurfaceTool();
			tool.Begin(Mesh.PrimitiveType.Triangles);
			tool.SetMaterial(palette.Materials[m - 1]);
			for (int i = 0; i < positions[m - 1].Count; i++)
			{
				tool.SetNormal(normals[m - 1][i]);
				tool.SetUV(uvs[m - 1][i]);
				// UV2.x = per-vertex sky light. LOD is distant, open terrain
				// (a heightfield of surface voxels) — it is never a cave
				// interior, so it always gets full sky light (1.0). Without this
				// the light-aware material shader would read UV2.x = 0 and
				// render the LOD fully black.
				tool.SetUV2(new Vector2(1f, 0f));
				tool.AddVertex(positions[m - 1][i]);
			}
			tool.Commit(mesh);
		}

		return mesh;
	}

	private static Vector3 NodePosition(LodData data, float voxelSize, int i, int j)
	{
		int nodes = data.Res + 1;
		int idx = i + nodes * j;
		// Defensive: a malformed LodData (e.g. Heights resized out from under a
		// concurrent build) must never crash the engine with an out-of-bounds
		// read. Clamp to the last valid node; the worst case is a slightly wrong
		// height at one corner, not an exception that kills the whole frame.
		if (data.Heights == null || idx < 0 || idx >= data.Heights.Length)
		{
			idx = System.Math.Max(0, data.Heights != null ? data.Heights.Length - 1 : 0);
		}
		int h = data.Heights[idx];
		return new Vector3(data.Wx0 + i * data.Cell, h, data.Wz0 + j * data.Cell) * voxelSize;
	}

	/// <summary>
	/// Safe per-node material read: returns the material at grid node (i, j),
	/// or 0 (air → the triangle is dropped) if the index is out of bounds. A
	/// malformed LodData must never crash the engine; worst case is a skirt
	/// wall rendered without its edge material.
	/// </summary>
	private static byte SafeMaterial(LodData data, int i, int j)
	{
		int nodes = data.Res + 1;
		int idx = i + nodes * j;
		if (data.Materials == null || idx < 0 || idx >= data.Materials.Length)
		{
			return 0;
		}
		return data.Materials[idx];
	}

	/// <summary>
	/// Emits one skirt-wall triangle with self-correcting winding. The corner
	/// order (a, b, c) is given; if (b-a)x(c-a) points AWAY from the region
	/// interior (inX,inZ), b and c are swapped so the cross points INTO the
	/// region. EmitTriangle then negates the cross to store the OUTWARD normal
	/// (toward the full-res neighbour / finer LOD), making the wall front-facing
	/// to anyone looking at the seam from outside the region.
	/// </summary>
	private static void EmitSkirtTriangle(
		List<Vector3>[] positions, List<Vector3>[] normals, List<Vector2>[] uvs,
		Vector3 a, Vector3 b, Vector3 c,
		byte ma, byte mb, byte mc,
		int inX, int inZ,
		VoxelMaterialPalette palette)
	{
		Vector3 cross = (b - a).Cross(c - a);
		bool flip = (cross.X * inX + cross.Z * inZ) < 0;
		if (flip)
		{
			// Swap b and c (and their materials) so the cross points into the
			// region.
			Vector3 tmpV = b;
			byte tmpM = mb;
			b = c;
			mb = mc;
			c = tmpV;
			mc = tmpM;
		}
		EmitTriangle(positions, normals, uvs, a, b, c, ma, mb, mc, palette);
	}

	/// <summary>
	/// Emits the flat vertical skirt walls for every side set in
	/// <see cref="LodData.SkirtSides"/>. Each side's wall is a quad strip along
	/// that boundary edge: for every pair of adjacent edge nodes it drops two
	/// triangles from the surface heights down to <see cref="LodData.SkirtBaseHeight"/>.
	/// The wall's normal points OUTWARD from the region (toward the
	/// full-resolution neighbour), so it reads as a flat cliff face, and its
	/// material is the surface material of the edge it hangs from.
	///
	/// Winding: Godot front faces are clockwise, i.e. (b-a)x(c-a) points INTO
	/// the solid — the same convention the top-surface triangles use — so the
	/// wall is visible from outside (the side facing the neighbour) and
	/// back-face-culled from inside the region.
	/// </summary>
	private static void EmitSkirts(
		LodData data, float voxelSize, VoxelMaterialPalette palette,
		List<Vector3>[] positions, List<Vector3>[] normals, List<Vector2>[] uvs)
	{
		int sides = data.SkirtSides;
		if (sides == 0)
		{
			return;
		}
		int nodes = data.Res + 1;
		int baseY = data.SkirtBaseHeight;

		for (int side = 0; side < 4; side++)
		{
			if ((sides & (1 << side)) == 0)
			{
				continue;
			}

			// (fx, fz) fixes the boundary edge; (sx, sz) walks along it (always
			// in the +index direction so i0/j0 start at 0 and i1/j1 stay in
			// bounds), and (inX, inZ) is the unit direction pointing INTO the
			// region (the solid) from this edge — the cross product of each wall
			// triangle must point this way so EmitTriangle stores the OUTWARD
			// normal (toward the full-res neighbour).
			//   side 0: -X edge (fx=0,    walk +Z)  into region = +X
			//   side 1: +X edge (fx=res,  walk +Z)  into region = -X
			//   side 2: -Z edge (fz=0,    walk +X)  into region = +Z
			//   side 3: +Z edge (fz=res,  walk +X)  into region = -Z
			int fx, fz, sx, sz, inX, inZ;
			switch (side)
			{
				case 0: fx = 0; fz = 0; sx = 0; sz = 1; inX = 1; inZ = 0; break;
				case 1: fx = data.Res; fz = 0; sx = 0; sz = 1; inX = -1; inZ = 0; break;
				case 2: fx = 0; fz = 0; sx = 1; sz = 0; inX = 0; inZ = 1; break;
				default: fx = 0; fz = data.Res; sx = 1; sz = 0; inX = 0; inZ = -1; break;
			}

			for (int k = 0; k < data.Res; k++)
			{
				int i0 = fx + k * sx;
				int j0 = fz + k * sz;
				int i1 = fx + (k + 1) * sx;
				int j1 = fz + (k + 1) * sz;

				// Defensive: a malformed LodData (Res inconsistent with the
				// Heights/Materials arrays) must never index out of bounds and
				// crash the engine. If the edge node is out of the grid, skip
				// this skirt quad (the top surface already validated the data,
				// so this only fires if the data is genuinely broken).
				if (i0 < 0 || j0 < 0 || i0 >= nodes || j0 >= nodes
					|| i1 < 0 || j1 < 0 || i1 >= nodes || j1 >= nodes)
				{
					continue;
				}

				Vector3 top0 = NodePosition(data, voxelSize, i0, j0);
				Vector3 top1 = NodePosition(data, voxelSize, i1, j1);
				Vector3 bot0 = new Vector3(top0.X, baseY, top0.Z) * voxelSize;
				Vector3 bot1 = new Vector3(top1.X, baseY, top1.Z) * voxelSize;

				// The wall's material is the surface material of the edge it
				// hangs from (majority vote of the two edge nodes). Safe access
				// — out-of-bounds returns 0 (air) so the triangle is dropped.
				byte ma = SafeMaterial(data, i0, j0);
				byte mb = SafeMaterial(data, i1, j1);

				// Emit the quad as two triangles sharing the top0→bot1 diagonal.
				// The quad's corners are top0, top1 (top edge) and bot0, bot1
				// (bottom edge), where bot0 is below top0 and bot1 is below top1.
				// Splitting along top0→bot1 gives:
				//   tri A: top0, top1, bot1
				//   tri B: top0, bot1, bot0
				// which share the top0→bot1 edge and cover the full quad.
				//
				// Winding: Godot front faces are CLOCKWISE, i.e. (b-a)x(c-a)
				// points INTO the solid. For a skirt wall, the "solid" is the
				// region interior (direction inX,inZ). So we need
				// (b-a)x(c-a) to point toward (inX,inZ). We compute the cross
				// for each triangle and flip the corner order if it points the
				// wrong way. EmitTriangle negates the cross to store the outward
				// normal.
				EmitSkirtTriangle(positions, normals, uvs,
					top0, top1, bot1, ma, mb, mb, inX, inZ, palette);
				EmitSkirtTriangle(positions, normals, uvs,
					top0, bot1, bot0, ma, mb, ma, inX, inZ, palette);
			}
		}
	}

	/// <summary>
	/// Emits one flat-shaded triangle into the per-material vertex lists:
	/// computes the (outward) face normal, resolves the triangle's material
	/// by majority vote of its corner materials, and appends the 3 vertices.
	/// Degenerate (zero-area) triangles are dropped.
	/// </summary>
	private static void EmitTriangle(
		List<Vector3>[] positions,
		List<Vector3>[] normals,
		List<Vector2>[] uvs,
		Vector3 a, Vector3 b, Vector3 c,
		byte ma, byte mb, byte mc,
		VoxelMaterialPalette palette)
	{
		// (b-a)x(c-a) points into the solid (down, -Y for a top surface);
		// negate for the stored outward lighting normal (up).
		Vector3 n = (b - a).Cross(c - a);
		if (n.LengthSquared() < 1e-12f)
		{
			return; // degenerate (collinear) triangle
		}
		n = -n.Normalized();

		// Majority vote of the corner materials (mixed triangles occur where a
		// cell straddles a grass/leaves boundary — assign to the dominant one).
		int m = ma == mb || ma == mc ? ma : (mb == mc ? mb : mc);
		if (m < 1 || m > palette.MaterialCount)
		{
			return; // air, or a material id with no palette slot
		}

		float uvScale = palette.TextureScales[m - 1];
		positions[m - 1].Add(a);
		normals[m - 1].Add(n);
		uvs[m - 1].Add(PlanarUv(a, n, uvScale));
		positions[m - 1].Add(b);
		normals[m - 1].Add(n);
		uvs[m - 1].Add(PlanarUv(b, n, uvScale));
		positions[m - 1].Add(c);
		normals[m - 1].Add(n);
		uvs[m - 1].Add(PlanarUv(c, n, uvScale));
	}

	/// <summary>
	/// World-aligned planar UV (same projection rule as
	/// <see cref="MeshBuilder"/>): project onto the axis dominant in the face
	/// normal so flat regions tile seamlessly.
	/// </summary>
	private static Vector2 PlanarUv(Vector3 p, Vector3 n, float scale)
	{
		float ax = Math.Abs(n.X);
		float ay = Math.Abs(n.Y);
		float az = Math.Abs(n.Z);

		Vector2 uv;
		if (ax >= ay && ax >= az)
		{
			uv = new Vector2(p.Z, p.Y);
		}
		else if (ay >= az)
		{
			uv = new Vector2(p.X, p.Z);
		}
		else
		{
			uv = new Vector2(p.X, p.Y);
		}

		return uv * scale;
	}
}
