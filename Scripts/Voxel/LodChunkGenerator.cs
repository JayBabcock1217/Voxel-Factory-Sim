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
				// reads "forest" from afar. Rocks are intentionally omitted —
				// a boulder is smaller than a coarse cell at this resolution,
				// so it would not appear in the distant silhouette anyway.
				// The base voxel must be solid (same rule as full-res foliage),
				// so a canopy never floats over a hollowed-out surface.
				if (TerrainGenerator.TreeAt(wx, wz, cfg) && biome != null
					&& TerrainGenerator.FoliageBaseSolid(wx, h, wz, chunks, chunkSizeXZ))
				{
					h += TerrainGenerator.TrunkHeight(wx, wz, cfg) + TerrainGenerator.LeafRadius(wx, wz, cfg);
					mat = (byte)(biome.LeafMaterial > 0 ? biome.LeafMaterial : mat);
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
		int h = data.Heights[i + nodes * j];
		return new Vector3(data.Wx0 + i * data.Cell, h, data.Wz0 + j * data.Cell) * voxelSize;
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
