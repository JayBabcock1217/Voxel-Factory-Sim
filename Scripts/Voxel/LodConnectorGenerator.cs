using Godot;
using System;
using System.Collections.Generic;

namespace FactorySim.Voxel;

/// <summary>
/// Deterministic data + mesh generation for one <see cref="LodConnector"/>
/// strip: the ring between the full-res chunk square's outer edge and the LOD
/// inner boundary, for ONE side of the square.
///
/// All methods are pure (no engine-node state beyond the supplied palette), so
/// they are safe to run on the thread pool. The terrain is sampled directly
/// from the SAME deterministic functions the full-res chunks and the LOD use
/// (TerrainGenerator.SurfaceHeight + the structure definitions' LodHeight), so
/// the connector meets the full-res top layer and the LOD heightfield without a
/// height step or material mismatch.
///
/// Winding follows <see cref="LodChunkGenerator"/> / <see cref="MeshBuilder"/>:
/// Godot front faces are CLOCKWISE (cross product (b-a)x(c-a) points INTO the
/// solid); stored normals point outward (toward the air).
/// </summary>
public sealed class ConnectorData
{
	public short[] Heights;
	public byte[] Materials;
	public int Segs;   // segments along the strip (nodes along = Segs + 1)
	public int Depth;  // segments across the strip (nodes across = Depth + 1)
	public int Edge;   // the full-res square's outer edge in world voxels (X for N/S, Z for E/W)
	public float InnerOffset; // world-voxel offset of the inner edge from the square edge (= R*chunkSize)
	public float OuterOffset; // world-voxel offset of the outer edge from the square edge (= R*sqrt(2)*chunkSize)
	public int Side;     // 0=N, 1=E, 2=S, 3=W
	public float VoxelSize;
}

public static class LodConnectorGenerator
{
	/// <summary>
	/// The base height the skirt drops to at the full-res outer edge. The
	/// full-res terrain sits around BaseHeight (a heightmap offset), so a skirt
	/// a few voxels below the lowest plausible surface fully closes the open
	/// rim. Exposed so the world can tune it.
	/// </summary>
	public const float SkirtDepth = 4f;

	/// <summary>
	/// Samples the connector strip's heightfield. The strip is a (Segs+1) x
	/// (Depth+1) grid: the "along" axis runs the length of the square's side
	/// (2*R*chunkSize world voxels), the "across" axis runs from the full-res
	/// edge (inner) out to the LOD inner boundary (outer).
	/// </summary>
	/// <param name="chunks">
	/// The world's loaded-chunk map (may be null). Used for the foliage
	/// solid-base check so connector canopies match what full-res chunks plant.
	/// Null (or a missing chunk) means the base is unknown and is treated as
	/// solid (a tree is never dropped just because the nearby chunk isn't
	/// loaded) — the same rule LodChunkGenerator uses.
	/// </param>
	public static ConnectorData GenerateData(
		int side, int edge, int chunkSizeXZ, int requestedRes, GenConfig cfg,
		int renderDistanceChunks, float lodInnerBoundary,
		Dictionary<Vector2I, VoxelChunk> chunks = null)
	{
		int R = renderDistanceChunks;
		int cs = chunkSizeXZ;
		// The square's outer edge is at world voxel R*cs on the positive axis
		// and -R*cs on the negative axis. The side's edge line:
		//   N: z = -R*cs, x in [-R*cs, R*cs]
		//   E: x = +R*cs, z in [-R*cs, R*cs]
		//   S: z = +R*cs, x in [-R*cs, R*cs]
		//   W: x = -R*cs, z in [-R*cs, R*cs]
		float edgePos = R * cs;          // the edge line's coordinate on its axis
		float alongHalf = R * cs;        // half the strip length (world voxels)

		// Inner edge = the square's edge (offset 0 along the across axis, on the
		// outer side). Outer edge = the LOD inner boundary (R*sqrt(2)*cs).
		float innerOffset = 0f;
		float outerOffset = lodInnerBoundary * cs; // = R*sqrt(2)*cs by construction
		float depth = outerOffset - innerOffset;   // = (sqrt(2)-1)*R*cs

		// Pick a resolution that divides the strip length evenly so the along
		// nodes tile the side exactly (boundary nodes land on world-voxel
		// positions shared with the neighbouring strip and the full-res edge).
		int segs = EffectiveResolution((int)(2 * alongHalf), requestedRes);
		int depthSegs = EffectiveResolution((int)depth, 8); // a few cells across the ring
		int nodesAlong = segs + 1;
		int nodesAcross = depthSegs + 1;

		var heights = new short[nodesAlong * nodesAcross];
		var materials = new byte[nodesAlong * nodesAcross];

		for (int j = 0; j < nodesAcross; j++)
		{
			// Across offset: j=0 -> innerOffset (the square edge),
			// j=nodesAcross-1 -> outerOffset (the LOD boundary).
			float acrossOffset = innerOffset + (outerOffset - innerOffset) * ((float)j / (nodesAcross - 1));

			for (int i = 0; i < nodesAlong; i++)
			{
				float along = -alongHalf + (2f * alongHalf) * ((float)i / (nodesAlong - 1));

				// World voxel (wx, wz) of this grid node, from the side + edge.
				int wx, wz;
				switch (side)
				{
					case 0: // N: z = -edge, x = along, across adds to z (outward = -z)
						wx = (int)Math.Round(along);
						wz = (int)Math.Round(-edgePos - acrossOffset);
						break;
					case 1: // E: x = +edge, z = along, across adds to x (outward = +x)
						wx = (int)Math.Round(edgePos + acrossOffset);
						wz = (int)Math.Round(along);
						break;
					case 2: // S: z = +edge, x = along, across adds to z (outward = +z)
						wx = (int)Math.Round(along);
						wz = (int)Math.Round(edgePos + acrossOffset);
						break;
					default: // W: x = -edge, z = along, across adds to x (outward = -x)
						wx = (int)Math.Round(-edgePos - acrossOffset);
						wz = (int)Math.Round(along);
						break;
				}

				int h = TerrainGenerator.SurfaceHeight(wx, wz, cfg);
				var biome = TerrainGenerator.DominantBiome(wx, wz, cfg);
				byte mat = (byte)(biome != null ? biome.SurfaceMaterial : VoxelMaterialIds.Grass);

				// Tree canopies: same rule as the full-res foliage stage + LOD,
				// so the connector's silhouette matches both.
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

				int idx = i + nodesAlong * j;
				heights[idx] = (short)h;
				materials[idx] = mat;
			}
		}

		return new ConnectorData
		{
			Heights = heights,
			Materials = materials,
			Segs = segs,
			Depth = depthSegs,
			Edge = (int)Math.Round(edgePos),
			InnerOffset = innerOffset,
			OuterOffset = outerOffset,
			Side = side,
			VoxelSize = 1f,
		};
	}

	/// <summary>
	/// Picks the largest grid resolution <= requestedRes that evenly divides the
	/// strip's world-voxel length, so the along nodes tile the side exactly
	/// (adjacent strips and the full-res edge share boundary node positions).
	/// Always returns >= 2.
	/// </summary>
	private static int EffectiveResolution(int lengthVoxels, int requestedRes)
	{
		int max = Math.Min(requestedRes, Math.Max(2, lengthVoxels / 2));
		for (int r = max; r >= 2; r--)
		{
			if (lengthVoxels % r == 0)
			{
				return r;
			}
		}
		return 2;
	}

	/// <summary>
	/// Resolves a connector grid node to a world position (in world VOXEL units,
	/// NOT yet scaled by voxelSize). The across axis runs from the square edge
	/// (offset 0) outward to the LOD boundary (offset depth).
	/// </summary>
	private static Vector3 NodePosition(ConnectorData data, int i, int j)
	{
		int nodesAlong = data.Segs + 1;
		int nodesAcross = data.Depth + 1;
		float edgePos = data.Edge;
		float alongHalf = edgePos; // the strip length is 2*edgePos (from -edge to +edge)
		float along = -alongHalf + (2f * alongHalf) * ((float)i / (nodesAlong - 1));
		float acrossOffset = data.InnerOffset + (data.OuterOffset - data.InnerOffset) * ((float)j / (nodesAcross - 1));
		int h = data.Heights[i + nodesAlong * j];

		switch (data.Side)
		{
			case 0: // N
				return new Vector3(along, h, -edgePos - acrossOffset);
			case 1: // E
				return new Vector3(edgePos + acrossOffset, h, along);
			case 2: // S
				return new Vector3(along, h, edgePos + acrossOffset);
			default: // W
				return new Vector3(-edgePos - acrossOffset, h, along);
		}
	}

	/// <summary>
	/// Builds the visual ArrayMesh for the connector: a two-sided strip
	/// (top surface + a skirt dropping to the base height at the full-res edge).
	/// One flat-shaded surface per material. VISUAL ONLY — no collision.
	/// </summary>
	public static ArrayMesh BuildMesh(ConnectorData data, float voxelSize, VoxelMaterialPalette palette)
	{
		int segs = data.Segs;
		int depth = data.Depth;
		int nodesAlong = segs + 1;
		int nodesAcross = depth + 1;
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

		// --- TOP SURFACE: the strip's heightfield, (segs) x (depth) quads. ---
		// The across axis runs inner (j=0, the square edge) -> outer (j=depth,
		// the LOD boundary). Winding: we want the top surface's normal to point
		// UP (+Y, toward the air). For a node at (i,j), the "next along" is
		// (i+1,j) and the "next across" (outward) is (i,j+1). The cross product
		// (v_nextAlong - v00) x (v_nextAcross - v00) for a flat top points in a
		// consistent direction; we negate to get the outward (up) normal,
		// matching LodChunkGenerator's convention.
		for (int j = 0; j < depth; j++)
		{
			for (int i = 0; i < segs; i++)
			{
				Vector3 v00 = NodePosition(data, i, j) * voxelSize;
				Vector3 v10 = NodePosition(data, i + 1, j) * voxelSize;
				Vector3 v11 = NodePosition(data, i + 1, j + 1) * voxelSize;
				Vector3 v01 = NodePosition(data, i, j + 1) * voxelSize;

				byte m00 = data.Materials[i + nodesAlong * j];
				byte m10 = data.Materials[i + 1 + nodesAlong * j];
				byte m11 = data.Materials[i + 1 + nodesAlong * (j + 1)];
				byte m01 = data.Materials[i + nodesAlong * (j + 1)];

				EmitTriangle(positions, normals, uvs, v00, v10, v11, m00, m10, m11, palette);
				EmitTriangle(positions, normals, uvs, v00, v11, v01, m00, m11, m01, palette);
			}
		}

		// --- SKIRT: a vertical wall along the INNER edge (the full-res square's
		// outer edge, j=0), dropping from the surface height to the base height.
		// This closes the open rim of the full-res outer chunks so the transition
		// is not see-through from a low angle. The skirt's top edge shares its
		// vertices with the strip's inner row (j=0), so it is watertight.
		float baseHeight = -SkirtDepth; // a few voxels below the lowest surface
		for (int i = 0; i < segs; i++)
		{
			// Top edge: the strip's inner row (j=0) at the surface height.
			Vector3 t0 = NodePosition(data, i, 0) * voxelSize;
			Vector3 t1 = NodePosition(data, i + 1, 0) * voxelSize;
			// Bottom edge: the same XZ, dropped to the base height.
			Vector3 b0 = new Vector3(t0.X, baseHeight * voxelSize, t0.Z);
			Vector3 b1 = new Vector3(t1.X, baseHeight * voxelSize, t1.Z);

			byte m0 = data.Materials[i + nodesAlong * 0];
			byte m1 = data.Materials[i + 1 + nodesAlong * 0];

			// Two triangles forming the vertical quad. The skirt faces OUTWARD
			// (away from the square), so its normal points along the side's
			// outward direction. EmitTriangle computes the normal from the
			// vertices; we pass the vertex order that yields the outward normal.
			EmitSkirtTriangle(positions, normals, uvs, t0, t1, b1, m1, palette);
			EmitSkirtTriangle(positions, normals, uvs, t0, b1, b0, m0, palette);
		}

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
				// UV2.x = per-vertex sky light. The connector is open, distant
				// terrain (a heightfield + a thin skirt) — always full light.
				tool.SetUV2(new Vector2(1f, 0f));
				tool.AddVertex(positions[m - 1][i]);
			}
			tool.Commit(mesh);
		}

		return mesh;
	}

	/// <summary>
	/// Emits one flat-shaded triangle into the per-material vertex lists,
	/// computing the (outward) face normal by negating the cross product
	/// (matching LodChunkGenerator). Degenerate triangles are dropped.
	/// </summary>
	private static void EmitTriangle(
		List<Vector3>[] positions,
		List<Vector3>[] normals,
		List<Vector2>[] uvs,
		Vector3 a, Vector3 b, Vector3 c,
		byte ma, byte mb, byte mc,
		VoxelMaterialPalette palette)
	{
		Vector3 n = (b - a).Cross(c - a);
		if (n.LengthSquared() < 1e-12f)
		{
			return;
		}
		n = -n.Normalized();

		int m = ma == mb || ma == mc ? ma : (mb == mc ? mb : mc);
		if (m < 1 || m > palette.MaterialCount)
		{
			return;
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
	/// Emits a skirt triangle, forcing the normal to the side's OUTWARD
	/// horizontal direction (the skirt is a vertical wall facing away from the
	/// square). The vertex order is chosen so the triangle's own normal already
	/// points outward; this just uses it.
	/// </summary>
	private static void EmitSkirtTriangle(
		List<Vector3>[] positions,
		List<Vector3>[] normals,
		List<Vector2>[] uvs,
		Vector3 a, Vector3 b, Vector3 c,
		byte m,
		VoxelMaterialPalette palette)
	{
		Vector3 n = (b - a).Cross(c - a);
		if (n.LengthSquared() < 1e-12f)
		{
			return;
		}
		n = -n.Normalized();

		if (m < 1 || m > palette.MaterialCount)
		{
			return;
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
	/// World-aligned planar UV (same projection rule as LodChunkGenerator):
	/// project onto the axis dominant in the face normal so flat regions tile
	/// seamlessly.
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
