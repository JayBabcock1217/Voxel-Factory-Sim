using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// Scatters squashed rock boulders around the surface, placed on a hashed cell grid
/// so placement is stable across chunk borders.
/// </summary>
public sealed class RockStructureGenerator : IStructureGenerator
{
	public float Probability { get; set; } = 0.35f;
	public int CellSize { get; set; } = 8;
	public int MinRadius { get; set; } = 2;
	public int MaxRadius { get; set; } = 4;

	/// <summary>Vertical squash of the boulder (&lt; 1 flattens it into the ground, &gt; 1 stretches it up).</summary>
	public float Squash { get; set; } = 0.7f;

	/// <summary>Synchronously applies this structure generator to the chunk.</summary>
	public void Apply(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz)
	{
		int cellMinX = FloorDiv(wx0, CellSize);
		int cellMaxX = FloorDiv(wx0 + chunk.SizeX - 1, CellSize);
		int cellMinZ = FloorDiv(wz0, CellSize);
		int cellMaxZ = FloorDiv(wz0 + chunk.SizeZ - 1, CellSize);

		var tasks = new List<Task>();

		for (int cx = cellMinX; cx <= cellMaxX; cx++)
		{
			for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
			{
				var h = VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0x51A5u);
				if (VoxelHash.Hash01(h) > Probability) continue;

				int wx = cx * CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xA111u)) * (CellSize - 1));
				int wz = cz * CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cz, cx, (uint)cfg.Seed ^ 0xB222u)) * (CellSize - 1));

				int ground = (wx >= wx0 && wx < wx0 + sx && wz >= wz0 && wz < wz0 + sz)
					? heights[(wx - wx0) + sx * (wz - wz0)]
					: TerrainGenerator.SurfaceHeight(wx, wz, cfg);
				int r = MinRadius + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xC333u)) * (MaxRadius - MinRadius + 1));

				float invR2 = 1f / (r * r);
				float squash = Squash;
				float invRy2 = 1f / (r * r * squash * squash);
				int cy = ground + 1;
				var rockBiome = TerrainGenerator.DominantBiome(wx, wz, cfg);
				byte rockMat = (byte)(rockBiome != null && rockBiome.RockMaterial > 0 ? rockBiome.RockMaterial : VoxelMaterialIds.Rock);

				tasks.Add(Task.Run(() => {
					for (int dx = -r; dx <= r; dx++)
					{
						for (int dy = -r; dy <= r; dy++)
						{
							for (int dz = -r; dz <= r; dz++)
							{
								if (dx * dx * invR2 + dy * dy * invRy2 + dz * dz * invR2 > 1f) continue;

								int nx = wx + dx - wx0;
								int ny = cy + dy;
								int nz = wz + dz - wz0;
								if (nx < 0 || nx >= chunk.SizeX || ny < 0 || ny >= chunk.SizeY || nz < 0 || nz >= chunk.SizeZ) continue;

								chunk.Set(nx, ny, nz, rockMat);
							}
						}
					}
				}));
			}
		}

		Task.WaitAll(tasks.ToArray());
	}

	/// <summary>
	/// Asynchronously applies this structure generator (for multithreaded use).
	/// Collects every boulder for the chunk and processes them on a SINGLE worker
	/// (instead of the old one-Task.Run-per-boulder). Writes go straight to the
	/// data array. Output is identical.
	/// </summary>
	public async Task ApplyAsync(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz)
	{
		int sy = chunk.SizeY;
		var data = chunk.Data;

		int cellMinX = FloorDiv(wx0, CellSize);
		int cellMaxX = FloorDiv(wx0 + sx - 1, CellSize);
		int cellMinZ = FloorDiv(wz0, CellSize);
		int cellMaxZ = FloorDiv(wz0 + sz - 1, CellSize);

		// Gather all boulders for this chunk.
		var boulders = new List<(int wx, int wz, int cy, int r, float invR2, float invRy2, byte mat)>();
		for (int cx = cellMinX; cx <= cellMaxX; cx++)
		{
			for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
			{
				var h = VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0x51A5u);
				if (VoxelHash.Hash01(h) > Probability) continue;

				int wx = cx * CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xA111u)) * (CellSize - 1));
				int wz = cz * CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cz, cx, (uint)cfg.Seed ^ 0xB222u)) * (CellSize - 1));

				int ground = (wx >= wx0 && wx < wx0 + sx && wz >= wz0 && wz < wz0 + sz)
					? heights[(wx - wx0) + sx * (wz - wz0)]
					: TerrainGenerator.SurfaceHeight(wx, wz, cfg);
				int r = MinRadius + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xC333u)) * (MaxRadius - MinRadius + 1));

				float invR2 = 1f / (r * r);
				float squash = Squash;
				float invRy2 = 1f / (r * r * squash * squash);
				int cy = ground + 1;
				var rockBiome = TerrainGenerator.DominantBiome(wx, wz, cfg);
				byte rockMat = (byte)(rockBiome != null && rockBiome.RockMaterial > 0 ? rockBiome.RockMaterial : VoxelMaterialIds.Rock);

				boulders.Add((wx, wz, cy, r, invR2, invRy2, rockMat));
			}
		}

		if (boulders.Count == 0)
		{
			return;
		}

		await Task.Run(() =>
		{
			for (int b = 0; b < boulders.Count; b++)
			{
				var (wx, wz, cy, r, invR2, invRy2, rockMat) = boulders[b];
				for (int dx = -r; dx <= r; dx++)
				{
					for (int dy = -r; dy <= r; dy++)
					{
						for (int dz = -r; dz <= r; dz++)
						{
							if (dx * dx * invR2 + dy * dy * invRy2 + dz * dz * invR2 > 1f) continue;

							int nx = wx + dx - wx0;
							int ny = cy + dy;
							int nz = wz + dz - wz0;
							if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;

							data[nx + sx * (ny + sy * nz)] = rockMat;
						}
					}
				}
			}
		});
	}

	/// <summary>Floor division (C# integer division truncates toward zero).</summary>
	private static int FloorDiv(int a, int b)
	{
		return (a - (a % b + b) % b) / b;
	}
}
