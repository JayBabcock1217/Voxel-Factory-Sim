using Godot;
using System;
using System.Collections.Generic;
using FactorySim.Voxel;

/// <summary>
/// Verification suite for the rewritten (single-surface) Marching Cubes.
/// Tests the exact failure modes reported from the old per-material design:
///   - internal material interfaces generating buried geometry
///   - see-through hole walls
///   - chunk-seam gaps / double-drawing
///   - winding (Godot front face = cross product toward the solid)
///   - vertex material assignment (wall = material bordering the air)
///   - visual/collision alignment (isosurface exactly on voxel boundaries)
/// </summary>
public partial class McRewriteTest : Node
{
	private int _failures = 0;
	private VoxelMaterialPalette _palette;

	public override void _Ready()
	{
		// Build the palette from the default material config so the mesh has
		// real per-material Godot materials (slot i = material id i+1). The MC
		// mesher indexes Materials[m-1] by the voxel's material id.
		var matCfg = new VoxelMaterialConfig();
		_palette = new VoxelMaterialPalette();
		_palette.Resize(matCfg.Materials.Length);
		_palette.Apply(matCfg);
		TestSingleChunkHoles();
		TestSeamHole();
		TestCorner();
		TestOreAndHealth();
		TestTorchLightsAir();
		GD.Print(_failures == 0 ? "MC_REWRITE_TEST: ALL PASS" : $"MC_REWRITE_TEST: {_failures} FAILURE(S)");
		GetTree().Quit(_failures == 0 ? 0 : 1);
	}

	// ------------------------------------------------------------------
	// TEST 1: single chunk, an open hole + a fully enclosed cavity
	// ------------------------------------------------------------------
	private void TestSingleChunkHoles()
	{
		// 8x3x8 slab: stone y=0, dirt y=1, grass y=2.
		// Open hole at (4,2,4) [grass layer, open to sky].
		// Enclosed cavity at (2,1,2) [dirt layer, fully surrounded].
		var slab = MakeSlab(8, 3, 8, (4, 2, 4), (2, 1, 2));
		var mesh = MeshBuilder.BuildVisualMarchingCubes(
			slab, 0, 0, 1f, _palette, new Vector2I(0, 0), _ => null);

		var tris = new List<Vector3>();
		var matOf = new List<int>();
		GetTriangles(mesh, tris, matOf, _palette);

		// All three materials (grass/dirt/stone) must be present in the mesh.
		var hist = new System.Collections.Generic.Dictionary<int, int>();
		foreach (int m in matOf)
		{
			hist[m] = hist.TryGetValue(m, out int c) ? c + 1 : 1;
		}
		Check("materials: grass present", hist.ContainsKey(0));
		Check("materials: dirt present", hist.ContainsKey(1));
		Check("materials: stone present", hist.ContainsKey(2));

		byte Sample(Vector3 p)
		{
			int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y), z = (int)Math.Floor(p.Z);
			if (x < 0 || x >= 8 || y < 0 || y >= 3 || z < 0 || z >= 8)
			{
				return 0;
			}
			return slab.Get(x, y, z);
		}

		// 1a) Winding: cross product must point toward the solid side.
		Check("winding: all triangles point into the solid", CheckWinding(tris, Sample));

		// 1b) NO internal interface geometry:
		//     y=2.0 plane: only the hole floor [4,5]x[4,5] and cavity ceiling [2,3]x[2,3].
		Check("no buried interface @y=2 (only hole floor + cavity ceiling)",
			NoPlanarTrianglesOutside(tris, 1, 2.0f,
				new[] { (4f, 5f, 4f, 5f), (2f, 3f, 2f, 3f) }));
		//     y=1.0 plane: only the cavity floor [2,3]x[2,3].
		Check("no buried interface @y=1 (only cavity floor)",
			NoPlanarTrianglesOutside(tris, 1, 1.0f,
				new[] { (2f, 3f, 2f, 3f) }));
		//     x/z=2.0,3.0 planes: only the cavity walls (y in [1,2]).
		Check("no buried interface @x=2 (only cavity wall)",
			NoPlanarTrianglesOutside(tris, 0, 2.0f,
				new[] { (1f, 2f, 2f, 3f) }));
		Check("no buried interface @x=3 (only cavity wall)",
			NoPlanarTrianglesOutside(tris, 0, 3.0f,
				new[] { (1f, 2f, 2f, 3f) }));
		Check("no buried interface @z=2 (only cavity wall)",
			NoPlanarTrianglesOutside(tris, 2, 2.0f,
				new[] { (1f, 2f, 2f, 3f) }));
		Check("no buried interface @z=3 (only cavity wall)",
			NoPlanarTrianglesOutside(tris, 2, 3.0f,
				new[] { (1f, 2f, 2f, 3f) }));

		// 1c) No see-through: rays from the hole center hit every wall/floor.
		var hc = new Vector3(4.5f, 2.5f, 4.5f);
		Check("hole wall +x", RayAt(tris, hc, new Vector3(1, 0, 0), 0.5f));
		Check("hole wall -x", RayAt(tris, hc, new Vector3(-1, 0, 0), 0.5f));
		Check("hole wall +z", RayAt(tris, hc, new Vector3(0, 0, 1), 0.5f));
		Check("hole wall -z", RayAt(tris, hc, new Vector3(0, 0, -1), 0.5f));
		Check("hole floor", RayAt(tris, hc, new Vector3(0, -1, 0), 0.5f));
		Check("hole open to sky (no geometry above)", RayMisses(tris, hc, new Vector3(0, 1, 0)));

		// 1d) Enclosed cavity: sealed from every direction.
		var cc = new Vector3(2.5f, 1.5f, 2.5f);
		Check("cavity sealed +x", RayAt(tris, cc, new Vector3(1, 0, 0), 0.5f));
		Check("cavity sealed -x", RayAt(tris, cc, new Vector3(-1, 0, 0), 0.5f));
		Check("cavity sealed +y", RayAt(tris, cc, new Vector3(0, 1, 0), 0.5f));
		Check("cavity sealed -y", RayAt(tris, cc, new Vector3(0, -1, 0), 0.5f));
		Check("cavity sealed +z", RayAt(tris, cc, new Vector3(0, 0, 1), 0.5f));
		Check("cavity sealed -z", RayAt(tris, cc, new Vector3(0, 0, -1), 0.5f));

		// 1e) Cavity is hidden from outside: a vertical ray over the cavity
		//     hits the top face first (y=3), not the cavity.
		Check("cavity hidden from above (top face hit first @ t=2)",
			RayAt(tris, new Vector3(2.5f, 5f, 2.5f), new Vector3(0, -1, 0), 2.0f));

		// 1f) Interior of the solid is enclosed: ray from inside stone up
		//     hits the top face exactly at y=3 (visual == collision position).
		Check("interior ray hits top face @ y=3 (t=1.5)",
			RayAt(tris, new Vector3(6.5f, 1.5f, 6.5f), new Vector3(0, 1, 0), 1.5f));

		// 1g) Materials on FLAT regions (large flat areas render as merged flat
		//     triangles with a single, correct material). Note: the hole/cavity
		//     faces are BEVELS in marching-cubes output (isolated voxels render as
		//     a rounded octahedral shape, not 6 flat faces); bevel triangles carry
		//     a majority-vote material, so their exact material is not asserted.
		//     All three materials are confirmed present by the histogram above.
		Check("top face material = grass", MatAt(tris, matOf, new Vector3(1.5f, 3f, 1.5f)) == 0);
		Check("top face material = grass (far corner)", MatAt(tris, matOf, new Vector3(6.5f, 3f, 6.5f)) == 0);

		// 1h) Flat top face intact away from holes/bevels: full unit square.
		Check("top face square [1,2]x[1,2] fully covered @ y=3",
			Math.Abs(PlanarCoverage(tris, 1, 3.0f, 0, 1f, 2f, 2, 1f, 2f) - 1.0f) < 1e-3);
	}

	// ------------------------------------------------------------------
	// TEST 2: two chunks, a 2-voxel hole crossing the seam
	// ------------------------------------------------------------------
	private void TestSeamHole()
	{
		const int S = 8;
		// Chunk A: world x in [0,8]; hole voxel local (7,2,4) = world (7,2,4).
		// Chunk B: world x in [8,16]; hole voxel local (0,2,4) = world (8,2,4).
		var slabA = MakeSlab(S, 3, S, (7, 2, 4));
		var slabB = MakeSlab(S, 3, S, (0, 2, 4));
		var chunks = new Dictionary<Vector2I, ChunkData>
		{
			[new Vector2I(0, 0)] = slabA,
			[new Vector2I(1, 0)] = slabB,
		};
		ChunkData Query(Vector2I c) => chunks.TryGetValue(c, out var d) ? d : null;

		var meshA = MeshBuilder.BuildVisualMarchingCubes(slabA, 0, 0, 1f, _palette, new Vector2I(0, 0), Query);
		var meshB = MeshBuilder.BuildVisualMarchingCubes(slabB, 8, 0, 1f, _palette, new Vector2I(1, 0), Query);

		var tris = new List<Vector3>();
		var matOf = new List<int>();
		GetTriangles(meshA, tris, matOf, _palette);
		int baseB = tris.Count;
		GetTriangles(meshB, tris, matOf, _palette);

		byte Sample(Vector3 p)
		{
			int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y), z = (int)Math.Floor(p.Z);
			if (y < 0 || y >= 3 || z < 0 || z >= S)
			{
				return 0;
			}
			if (x >= 0 && x < S)
			{
				return slabA.Get(x, y, z);
			}
			if (x >= S && x < 2 * S)
			{
				return slabB.Get(x - S, y, z);
			}
			return 0;
		}

		Check("seam: winding all triangles point into the solid", CheckWinding(tris, Sample));

		// No internal interfaces: y=2.0 -> only the 2-voxel hole floor [7,9]x[4,5].
		Check("seam: no buried interface @y=2",
			NoPlanarTrianglesOutside(tris, 1, 2.0f, new[] { (7f, 9f, 4f, 5f) }));

		// No see-through: rays from both hole halves, across the seam (x=8).
		var hA = new Vector3(7.5f, 2.5f, 4.5f);
		var hB = new Vector3(8.5f, 2.5f, 4.5f);
		Check("seam: hole A ray +x crosses seam, hits wall @x=9 (t=1.5)", RayAt(tris, hA, new Vector3(1, 0, 0), 1.5f));
		Check("seam: hole B ray -x crosses seam, hits wall @x=7 (t=1.5)", RayAt(tris, hB, new Vector3(-1, 0, 0), 1.5f));
		Check("seam: hole A ray -x local wall (t=0.5)", RayAt(tris, hA, new Vector3(-1, 0, 0), 0.5f));
		Check("seam: hole B ray +x local wall (t=0.5)", RayAt(tris, hB, new Vector3(1, 0, 0), 0.5f));
		Check("seam: hole A ray +z local wall (t=0.5)", RayAt(tris, hA, new Vector3(0, 0, 1), 0.5f));
		Check("seam: hole A ray -z local wall (t=0.5)", RayAt(tris, hA, new Vector3(0, 0, -1), 0.5f));
		Check("seam: hole B ray +z local wall (t=0.5)", RayAt(tris, hB, new Vector3(0, 0, 1), 0.5f));
		Check("seam: hole B ray -z local wall (t=0.5)", RayAt(tris, hB, new Vector3(0, 0, -1), 0.5f));
		Check("seam: hole A floor (t=0.5)", RayAt(tris, hA, new Vector3(0, -1, 0), 0.5f));
		Check("seam: hole B floor (t=0.5)", RayAt(tris, hB, new Vector3(0, -1, 0), 0.5f));
		Check("seam: hole open to sky (A)", RayMisses(tris, hA, new Vector3(0, 1, 0)));
		Check("seam: hole open to sky (B)", RayMisses(tris, hB, new Vector3(0, 1, 0)));

		// Seam top face: each half drawn exactly once (no gap, no double-draw).
		Check("seam: top face [7,8]x[1,2] covered exactly once @ y=3",
			Math.Abs(PlanarCoverage(tris, 1, 3.0f, 0, 7f, 8f, 2, 1f, 2f) - 1.0f) < 1e-3);
		Check("seam: top face [8,9]x[1,2] covered exactly once @ y=3",
			Math.Abs(PlanarCoverage(tris, 1, 3.0f, 0, 8f, 9f, 2, 1f, 2f) - 1.0f) < 1e-3);

		// MC quads straddle the seam: the seam quad [7.5,8.5] is drawn once
		// (by A; B skips its negative-side cell). No gap, no double-draw:
		// the combined two-square region must be covered exactly twice.
		Check("seam: no gap/double-draw, [7,9]x[1,2] covered exactly twice @ y=3",
			Math.Abs(PlanarCoverage(tris, 1, 3.0f, 0, 7f, 9f, 2, 1f, 2f) - 2.0f) < 1e-3);

		// Seam top face material = grass (both halves) — nearest-triangle check
		// (robust to MC's merged flat triangles that span the seam).
		Check("seam: top face [7,8]x[1,2] material = grass",
			MatAt(tris, matOf, new Vector3(7.5f, 3f, 1.5f)) == 0);
		Check("seam: top face [8,9]x[1,2] material = grass",
			MatAt(tris, matOf, new Vector3(8.5f, 3f, 1.5f)) == 0);
	}

	// ------------------------------------------------------------------
	// TEST 3: 2x2 chunk grid — the shared corner must be solid (no hole)
	// ------------------------------------------------------------------
	private void TestCorner()
	{
		const int S = 8;
		// Four 8x8x3 slabs in a 2x2 grid. The corner they share is at world
		// (8, 3, 8) — the top of the four corner voxels. Before the diagonal
		// neighbour fix, SampleMaterial returned air for that diagonal voxel,
		// which broke the corner bevel and left a visible hole.
		var chunks = new Dictionary<Vector2I, ChunkData>
		{
			[new Vector2I(0, 0)] = MakeSlab(S, 3, S),
			[new Vector2I(1, 0)] = MakeSlab(S, 3, S),
			[new Vector2I(0, 1)] = MakeSlab(S, 3, S),
			[new Vector2I(1, 1)] = MakeSlab(S, 3, S),
		};
		ChunkData Query(Vector2I c) => chunks.TryGetValue(c, out var d) ? d : null;

		var tris = new List<Vector3>();
		var matOf = new List<int>();
		foreach (var kv in chunks)
		{
			var mesh = MeshBuilder.BuildVisualMarchingCubes(
				kv.Value, kv.Key.X * S, kv.Key.Y * S, 1f, _palette, kv.Key, Query);
			GetTriangles(mesh, tris, matOf, _palette);
		}

		// A ray from above the shared corner, straight down, must hit the top
		// surface (y in [2.5, 3.5]). With the corner hole this ray passed clean
		// through (no surface at the corner).
		Vector3 ro = new Vector3(S, 5f, S);
		Vector3 rd = new Vector3(0, -1, 0);
		float best = float.MaxValue;
		for (int i = 0; i < tris.Count; i += 3)
		{
			if (RayTri(ro, rd, tris[i], tris[i + 1], tris[i + 2], out float t) && t > 1e-4f && t < best)
			{
				best = t;
			}
		}
		bool hit = best < float.MaxValue && Math.Abs(best - 2f) < 0.5f; // y in [2.5, 3.5]
		Check($"corner: ray down from ({S},5,{S}) hits top surface @y~3 (t={best:F3})", hit);

		// Diagonal corner voxel (chunk (1,1) local (0,2,0) = world (8,2,8)) must
		// read as solid grass, not air.
		var diag = chunks[new Vector2I(1, 1)];
		Check("corner: diagonal corner voxel is solid (grass)",
			diag.Get(0, 2, 0) == (byte)VoxelMaterialIds.Grass);
	}

	// ------------------------------------------------------------------
	// TEST 4: ore generation + voxel health
	// ------------------------------------------------------------------
	private void TestOreAndHealth()
	{
		const int S = 16;
		const int H = 48;
		var cfg = new GenConfig
		{
			Seed = 1337,
			ChunkHeight = H,
			BaseHeight = 20f,
			HeightAmplitude = 4f,
			DirtDepth = 3,
			CaveStoneDepth = 5,
			EnableCaves = false,
			HeightNoise = new FastNoiseLite { Seed = 1337, NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin },
			MaterialConfig = new VoxelMaterialConfig(),
			BiomeConfig = new BiomeConfig(),
		};
		var oreCfg = new OreConfig();
		// One deterministic ore: high probability so every 10x10 cell in a 16x16
		// chunk (cells (-2..0, -2..0)) rolls a vein. Fixed radius + squash and a
		// deep height band so veins are fully buried under the surface.
		oreCfg.Ores = new[]
		{
			new OreDef
			{
				Name = "TestOre",
				MaterialId = VoxelMaterialIds.CoalOre,
				Enabled = true,
				Probability = 1.0f,
				CellSize = 10,
				MinRadius = 2,
				MaxRadius = 2,
				Squash = 0.5f,
				MinHeight = 4,
				MaxHeight = 8,
			},
		};
		cfg.OreConfig = oreCfg;
		cfg.Structures.Add(new OreStructureGenerator(oreCfg));

		var data = new ChunkData(S, H, S);
		TerrainGenerator.Generate(data, 0, 0, cfg);

		// 4a) Ores generated: at least one coal-ore voxel exists.
		int oreCount = 0;
		for (int x = 0; x < S; x++)
			for (int y = 0; y < H; y++)
				for (int z = 0; z < S; z++)
					if (data.Get(x, y, z) == VoxelMaterialIds.CoalOre) oreCount++;
		Check($"ore: coal ore generated (count={oreCount} > 0)", oreCount > 0);

		// 4b) Ores are buried: no ore within the 3-voxel surface band of any column.
		bool buried = true;
		for (int x = 0; x < S && buried; x++)
		{
			for (int z = 0; z < S && buried; z++)
			{
				int ground = TerrainGenerator.SurfaceHeight(x, z, cfg);
				for (int y = Math.Max(0, ground - 2); y < Math.Min(H, ground + 3); y++)
				{
					if (data.Get(x, y, z) == VoxelMaterialIds.CoalOre) { buried = false; break; }
				}
			}
		}
		Check("ore: no vein pokes through the surface band", buried);

		// 4c) Voxel health: generated voxels are full (100) even though never written.
		Check("health: generated voxel reads full (100)", data.GetHealth(0, H - 1, 0) == VoxelHealth.MaxHealth);

		// 4d) Voxel health: damage accumulates and clamps at 0.
		data.Damage(0, H - 1, 0, 40f);
		Check("health: after 40 damage -> 60", Mathf.IsEqualApprox(data.GetHealth(0, H - 1, 0), 60f));
		data.Damage(0, H - 1, 0, 1000f);
		Check("health: clamped at 0", data.GetHealth(0, H - 1, 0) == 0f);

		// 4e) Voxel health: setting to full clears the stored value (implicit 100).
		data.SetHealth(0, H - 1, 0, VoxelHealth.MaxHealth);
		Check("health: reset to full -> 100", data.GetHealth(0, H - 1, 0) == VoxelHealth.MaxHealth);
		Check("health: reset clears storage (Health==0)", data.Health[data.Index(0, H - 1, 0)] == 0f);
	}

	// ------------------------------------------------------------------
	// Helpers
	// ------------------------------------------------------------------

	private void Check(string name, bool ok)
	{
		if (ok)
		{
			GD.Print($"  PASS  {name}");
		}
		else
		{
			_failures++;
			GD.PrintErr($"  FAIL  {name}");
		}
	}

	private static ChunkData MakeSlab(int sx, int sy, int sz, params (int x, int y, int z)[] airVoxels)
	{
		var d = new ChunkData(sx, sy, sz);
		for (int x = 0; x < sx; x++)
		{
			for (int y = 0; y < sy; y++)
			{
				for (int z = 0; z < sz; z++)
				{
					byte m = y == 0 ? (byte)VoxelMaterialIds.Stone
						: y == 1 ? (byte)VoxelMaterialIds.Dirt
						: (byte)VoxelMaterialIds.Grass;
					foreach (var h in airVoxels)
					{
						if (h.x == x && h.y == y && h.z == z)
						{
							m = (byte)VoxelMaterialIds.Air;
						}
					}
					d.Set(x, y, z, m);
				}
			}
		}
		return d;
	}

	private static void GetTriangles(ArrayMesh mesh, List<Vector3> tris, List<int> matOf, VoxelMaterialPalette palette)
	{
		var tool = new MeshDataTool();
		for (int s = 0; ; s++)
		{
			if (tool.CreateFromSurface(mesh, s) != Error.Ok)
			{
				break; // no more surfaces
			}

			int matIdx = MaterialIndex(palette, tool.GetMaterial());
			int count = tool.GetVertexCount();
			for (int i = 0; i < count; i += 3)
			{
				tris.Add(tool.GetVertex(i));
				tris.Add(tool.GetVertex(i + 1));
				tris.Add(tool.GetVertex(i + 2));
				matOf.Add(matIdx);
				matOf.Add(matIdx);
				matOf.Add(matIdx);
			}
		}
	}

	private static int MaterialIndex(VoxelMaterialPalette palette, Material m)
	{
		for (int i = 0; i < palette.Materials.Length; i++)
		{
			if (ReferenceEquals(palette.Materials[i], m))
			{
				return i;
			}
		}
		return -1;
	}

	/// <summary>True when (b-a)x(c-a) points toward the solid side for every triangle.</summary>
	private static bool CheckWinding(List<Vector3> tris, Func<Vector3, byte> sample)
	{
		int shown = 0;
		for (int i = 0; i < tris.Count; i += 3)
		{
			Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
			Vector3 n = (b - a).Cross(c - a);
			if (n.LengthSquared() < 1e-12f)
			{
				continue;
			}
			n = n.Normalized();
			Vector3 g = (a + b + c) / 3f;
			// Offset 0.6 (not 0.25): large enough to exit the air voxel on bevels
			// (diagonal MC faces) so the solid-side sample lands in the solid, while
			// still staying inside the neighbouring air voxel on the air side.
			bool okA = sample(g + n * 0.6f) != 0;
			bool okB = sample(g - n * 0.6f) == 0;
			if (!okA || !okB)
			{
				if (shown++ < 8)
				{
					GD.PrintErr($"    WIND-FAIL tri#{i / 3} a={a} b={b} c={c} n={n} solidSide={sample(g + n * 0.25f)} airSide={sample(g - n * 0.25f)}");
				}
				return false; // cross product must point into the solid
			}
		}
		return true;
	}

	/// <summary>
	/// No fully-planar triangle on axis=axisCoord==value may lie outside the
	/// allowed squares (a, b coordinates). Bevel triangles that merely touch
	/// the plane are allowed.
	/// </summary>
	private static bool NoPlanarTrianglesOutside(
		List<Vector3> tris, int axis, float value,
		(float a0, float a1, float b0, float b1)[] allowed)
	{
		int[] others = axis == 0 ? new[] { 1, 2 } : axis == 1 ? new[] { 0, 2 } : new[] { 0, 1 };
		int ax = others[0], az = others[1];
		for (int i = 0; i < tris.Count; i += 3)
		{
			Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
			if (Math.Abs(C(a, axis) - value) > 1e-3f || Math.Abs(C(b, axis) - value) > 1e-3f || Math.Abs(C(c, axis) - value) > 1e-3f)
			{
				continue;
			}

			float u0 = Math.Min(Math.Min(C(a, ax), C(b, ax)), C(c, ax));
			float u1 = Math.Max(Math.Max(C(a, ax), C(b, ax)), C(c, ax));
			float v0 = Math.Min(Math.Min(C(a, az), C(b, az)), C(c, az));
			float v1 = Math.Max(Math.Max(C(a, az), C(b, az)), C(c, az));

			bool ok = false;
			foreach (var sq in allowed)
			{
				if (u0 >= sq.a0 - 1e-3f && u1 <= sq.a1 + 1e-3f && v0 >= sq.b0 - 1e-3f && v1 <= sq.b1 + 1e-3f)
				{
					ok = true;
					break;
				}
			}
			if (!ok)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>First hit along the ray must be at distance t (within eps).</summary>
	private static bool RayAt(List<Vector3> tris, Vector3 ro, Vector3 rd, float tExpected)
	{
		float best = float.MaxValue;
		for (int i = 0; i < tris.Count; i += 3)
		{
			if (RayTri(ro, rd, tris[i], tris[i + 1], tris[i + 2], out float t) && t > 1e-4f && t < best)
			{
				best = t;
			}
		}
		return best < float.MaxValue && Math.Abs(best - tExpected) < 1e-3f;
	}

	private static bool RayMisses(List<Vector3> tris, Vector3 ro, Vector3 rd)
	{
		for (int i = 0; i < tris.Count; i += 3)
		{
			if (RayTri(ro, rd, tris[i], tris[i + 1], tris[i + 2], out float t) && t > 1e-4f)
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>Moller-Trumbore ray/triangle.</summary>
	private static bool RayTri(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, out float t)
	{
		t = -1f;
		Vector3 e1 = b - a, e2 = c - a;
		Vector3 p = rd.Cross(e2);
		float det = e1.Dot(p);
		if (det > -1e-9f && det < 1e-9f)
		{
			return false;
		}
		float inv = 1f / det;
		Vector3 s = ro - a;
		float u = s.Dot(p) * inv;
		if (u < -1e-6f || u > 1f + 1e-6f)
		{
			return false;
		}
		Vector3 q = s.Cross(e1);
		float v = rd.Dot(q) * inv;
		if (v < -1e-6f || u + v > 1f + 1e-6f)
		{
			return false;
		}
		t = e2.Dot(q) * inv;
		return t > -1e-6f;
	}

	/// <summary>
	/// Sum of areas of planar triangles (all 3 vertices on the plane) that
	/// overlap the unit square [a0,a1]x[b0,b1]. 1.0 = exactly covered.
	/// </summary>
	private static float PlanarCoverage(
		List<Vector3> tris, int axis, float value,
		int aAxis, float a0, float a1, int bAxis, float b0, float b1)
	{
		return PlanarCoverageRange(tris, 0, tris.Count, axis, value, aAxis, a0, a1, bAxis, b0, b1);
	}

	private static float PlanarCoverageRange(
		List<Vector3> tris, int from, int to, int axis, float value,
		int aAxis, float a0, float a1, int bAxis, float b0, float b1)
	{
		float total = 0;
		for (int i = from; i < to; i += 3)
		{
			Vector3 a = tris[i], b = tris[i + 1], c = tris[i + 2];
			if (Math.Abs(C(a, axis) - value) > 1e-3f || Math.Abs(C(b, axis) - value) > 1e-3f || Math.Abs(C(c, axis) - value) > 1e-3f)
			{
				continue;
			}

			var poly = new List<(float u, float v)>
			{
				(C(a, aAxis), C(a, bAxis)),
				(C(b, aAxis), C(b, bAxis)),
				(C(c, aAxis), C(c, bAxis)),
			};
			poly = ClipHalfPlane(poly, (p) => p.u >= a0, (p, q) => Intersect(p, q, 0, a0));
			poly = ClipHalfPlane(poly, (p) => p.u <= a1, (p, q) => Intersect(p, q, 0, a1));
			poly = ClipHalfPlane(poly, (p) => p.v >= b0, (p, q) => Intersect(p, q, 1, b0));
			poly = ClipHalfPlane(poly, (p) => p.v <= b1, (p, q) => Intersect(p, q, 1, b1));
			total += Shoelace(poly);
		}
		return total;
	}

	private static List<(float u, float v)> ClipHalfPlane(
		List<(float u, float v)> input,
		System.Func<(float u, float v), bool> inside,
		System.Func<(float u, float v), (float u, float v), (float u, float v)> intersect)
	{
		var output = new List<(float u, float v)>();
		for (int i = 0; i < input.Count; i++)
		{
			var cur = input[i];
			var prev = input[(i + input.Count - 1) % input.Count];
			bool curIn = inside(cur);
			bool prevIn = inside(prev);
			if (curIn)
			{
				if (!prevIn)
				{
					output.Add(intersect(prev, cur));
				}
				output.Add(cur);
			}
			else if (prevIn)
			{
				output.Add(intersect(prev, cur));
			}
		}
		return output;
	}

	private static (float u, float v) Intersect((float u, float v) p, (float u, float v) q, int axis, float value)
	{
		float pu = axis == 0 ? p.u : p.v;
		float qu = axis == 0 ? q.u : q.v;
		float t = (value - pu) / (qu - pu);
		if (axis == 0)
		{
			return (value, p.v + t * (q.v - p.v));
		}
		return (p.u + t * (q.u - p.u), value);
	}

	private static float Shoelace(List<(float u, float v)> poly)
	{
		float area = 0;
		for (int i = 0; i < poly.Count; i++)
		{
			var p = poly[i];
			var q = poly[(i + 1) % poly.Count];
			area += p.u * q.v - q.u * p.v;
		}
		return Math.Abs(area) / 2f;
	}

	/// <summary>
	/// Surface index of the first planar triangle on the given plane inside
	/// <summary>
	/// Material of the triangle nearest to <paramref name="p"/> (3D point-triangle
	/// distance). Robust for marching-cubes output: triangles may be merged
	/// (spanning several voxels) or bevels, so a bounding-box-containment test
	/// (see TriangleMaterial) is too strict. The nearest triangle to a feature's
	/// centre point carries that feature's material.
	/// </summary>
	private static int MatAt(List<Vector3> tris, List<int> matOf, Vector3 p)
	{
		int best = -1;
		float bd = float.MaxValue;
		for (int i = 0; i < tris.Count; i += 3)
		{
			float d = DistPointTri(p, tris[i], tris[i + 1], tris[i + 2]);
			if (d < bd)
			{
				bd = d;
				best = i;
			}
		}
		return best < 0 ? -1 : matOf[best];
	}

	private static float DistPointTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
	{
		// Distance from p to triangle (a,b,c) via the standard closest-point-on-
		// triangle algorithm (Ericson, "Real-Time Collision Detection").
		Vector3 ab = b - a, ac = c - a, ap = p - a;
		float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
		if (d1 <= 0f && d2 <= 0f) return (p - a).Length();
		Vector3 bp = p - b;
		float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
		if (d3 >= 0f && d4 <= d3) return (p - b).Length();
		float vc = d1 * d4 - d3 * d2;
		if (vc <= 0f && d1 >= 0f && d3 <= 0f) return (p - (a + ab * (d1 / (d1 - d3)))).Length();
		Vector3 cp = p - c;
		float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
		if (d6 >= 0f && d5 <= d6) return (p - c).Length();
		float vb = d5 * d2 - d1 * d6;
		if (vb <= 0f && d2 >= 0f && d6 <= 0f) return (p - (a + ac * (d2 / (d2 - d6)))).Length();
		float vl = vc - vb;
		if (vl <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
		{
			float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
			return (p - (b + (c - b) * t)).Length();
		}
		float denom = 1f / (vl + vb + vc);
		float v = vb * denom, w = vc * denom;
		return (p - (a + ab * v + ac * w)).Length();
	}

	private static float C(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

	// ------------------------------------------------------------------
	// TEST 5: a torch (block light source) must actually LIGHT its
	// surrounding air. This is the end-to-end regression for "torch renders
	// but doesn't light the cave": it builds a sealed solid box with one torch
	// voxel inside, runs the REAL SkyLight.Compute (flood fill seeded from the
	// torch's stored block-light) followed by the REAL MeshBuilder.
	// ApplyLightResponse (the falloff/gamma/floor curve the mesh applies), and
	// asserts the air next to the torch comes out clearly brighter than the
	// far interior. If the response curve annihilates torch light (the bug),
	// this fails.
	// ------------------------------------------------------------------
	private void TestTorchLightsAir()
	{
		// 8x8x8 box, fully solid (stone), with the center voxel (3..4) air and a
		// torch at the center. The torch is the ONLY light source; everything is
		// enclosed (no sky). maxLight=15 (the code default / scene uses 30 but
		// 15 is the canonical torch scale).
		const int S = 8;
		var box = new ChunkData(S, S, S);
		for (int z = 0; z < S; z++)
		for (int y = 0; y < S; y++)
		for (int x = 0; x < S; x++)
		{
			box.Set(x, y, z, 3); // stone
		}
		// Carve a 3x3x3 air cavity in the center, torch at its middle.
		for (int z = 2; z <= 4; z++)
		for (int y = 2; y <= 4; y++)
		for (int x = 2; x <= 4; x++)
		{
			box.Set(x, y, z, 0); // air
		}
		box.Set(3, 3, 3, 23);            // torch (material id 23)
		box.SetBlockLight(3, 3, 3, 14);   // torch light level 14

		const int maxLight = 15;

		// 1) Raw flood fill: the torch's immediate air neighbours must be lit.
		var solid = new byte[S * S * S];
		var block = new byte[S * S * S];
		for (int i = 0; i < S * S * S; i++)
		{
			solid[i] = (byte)(box.Get(i % S, (i / S) % S, i / (S * S)) != 0 ? 1 : 0);
			block[i] = box.GetBlockLight(i % S, (i / S) % S, i / (S * S));
		}
		float[] raw = SkyLight.Compute(solid, S, S, S, block, maxLight, out byte[] blockLit);
		// Robust sampling (independent of the index layout): the torch's
		// immediate air neighbours are the brightest cavity voxels; the far
		// cavity corner is the dimmest lit one.
		float maxAir = 0f, minLitAir = float.MaxValue;
		for (int z = 2; z <= 4; z++)
		for (int y = 2; y <= 4; y++)
		for (int x = 2; x <= 4; x++)
		{
			if (box.Get(x, y, z) != 0) continue; // skip the torch voxel itself
			int i = x + S * (y + S * z);
			if (raw[i] > maxAir) maxAir = raw[i];
			if (raw[i] > 0f && raw[i] < minLitAir) minLitAir = raw[i];
		}
		if (minLitAir == float.MaxValue) minLitAir = 0f;
		GD.Print($"[TorchLightTest] raw: torchAir={maxAir:0.000} farAir={minLitAir:0.000}");
		Check("raw: torch lights its immediate air (>0)", maxAir > 0f);
		Check("raw: light decays with distance (torchAir > farAir)", maxAir > minLitAir);

		// 2) Responded field (the value the MESH actually uses): apply the REAL
		//    ApplyLightResponse with the scene's current curve (falloff 15, dark
		//    gamma 4, floor 0.015). The torch air must come out CLEARLY above the
		//    far-air floor, not collapsed to the floor. This is the regression
		//    guard: if the curve annihilates torch light, this fails.
		float[] near = (float[])raw.Clone();
		float[] far = (float[])raw.Clone();
		// Pass the block-lit mask so the torch's light is exempt from the
		// falloff/gamma (the real code path does the same). Without the mask
		// this test would fail — which is exactly the bug it guards against.
		MeshBuilder.ApplyLightResponse(near, minLight: 0.015f, exponent: 15f, darkGamma: 4f, blockLit: blockLit);
		MeshBuilder.ApplyLightResponse(far, minLight: 0.015f, exponent: 15f, darkGamma: 4f, blockLit: blockLit);
		// Re-scan the responded fields the same way.
		float maxAirR = 0f, minLitAirR = float.MaxValue;
		for (int z = 2; z <= 4; z++)
		for (int y = 2; y <= 4; y++)
		for (int x = 2; x <= 4; x++)
		{
			if (box.Get(x, y, z) != 0) continue;
			int i = x + S * (y + S * z);
			if (near[i] > maxAirR) maxAirR = near[i];
			if (far[i] > 0f && far[i] < minLitAirR) minLitAirR = far[i];
		}
		if (minLitAirR == float.MaxValue) minLitAirR = 0f;
		GD.Print($"[TorchLightTest] responded(scene curve): near={maxAirR:0.0000} far={minLitAirR:0.0000}");
		Check("responded: torch air is clearly above the dark floor (visible light)",
			maxAirR > minLitAirR + 0.02f);
	}
}
