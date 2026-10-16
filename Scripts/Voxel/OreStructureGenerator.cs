using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// Scatters ore veins through the deep rock, placed on a hashed cell grid so
/// placement is stable across chunk borders (the same cell rolls the same vein
/// no matter which chunk generates it).
///
/// Each <see cref="OreDef"/> in the config gets its own cell grid, probability,
/// vein size, squash, and height range — all editor-modifiable. Veins are
/// squashed ellipsoids stamped in place of solid voxels (air and surface
/// voxels are never touched, so ores never poke through the terrain surface).
/// </summary>
public sealed class OreStructureGenerator : IStructureGenerator
{
	private sealed class OreParams
	{
		public int MaterialId;
		public float Probability;
		public int CellSize;
		public int MinRadius;
		public int MaxRadius;
		public float Squash;
		public int MinHeight;
		public int MaxHeight;
	}

	private readonly List<OreParams> _ores = new();

	/// <summary>
	/// Creates the generator from an ore config. Disabled ores and ores with no
	/// material (id 0) are skipped; an empty/null config yields a no-op
	/// generator so generation keeps working without ores.
	/// </summary>
	public OreStructureGenerator(OreConfig config)
	{
		if (config?.Ores == null)
		{
			return;
		}

		foreach (var def in config.Ores)
		{
			if (def == null || !def.Enabled || def.MaterialId <= 0)
			{
				continue;
			}

			int cell = Mathf.Max(2, def.CellSize);
			int minR = Mathf.Max(1, def.MinRadius);
			int maxR = Mathf.Max(minR, def.MaxRadius);
			_ores.Add(new OreParams
			{
				MaterialId = def.MaterialId,
				Probability = Mathf.Clamp(def.Probability, 0f, 1f),
				CellSize = cell,
				MinRadius = minR,
				MaxRadius = maxR,
				Squash = Mathf.Max(0.1f, def.Squash),
				MinHeight = Mathf.Min(def.MinHeight, def.MaxHeight),
				MaxHeight = Mathf.Max(def.MinHeight, def.MaxHeight),
			});
		}
	}

	/// <summary>Synchronously applies this structure generator to the chunk.</summary>
	public void Apply(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz)
	{
		int sy = chunk.SizeY;

		foreach (var ore in _ores)
		{
			int cellMinX = FloorDiv(wx0, ore.CellSize);
			int cellMaxX = FloorDiv(wx0 + sx - 1, ore.CellSize);
			int cellMinZ = FloorDiv(wz0, ore.CellSize);
			int cellMaxZ = FloorDiv(wz0 + sz - 1, ore.CellSize);

			for (int cx = cellMinX; cx <= cellMaxX; cx++)
			{
				for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
				{
					uint h = VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0x0BEEu);
					if (VoxelHash.Hash01(h) > ore.Probability) continue;

					int wx = cx * ore.CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xD001u)) * (ore.CellSize - 1));
					int wz = cz * ore.CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cz, cx, (uint)cfg.Seed ^ 0xD002u)) * (ore.CellSize - 1));

					// Height range: a vein center may sit anywhere in [MinHeight, MaxHeight].
					int range = ore.MaxHeight - ore.MinHeight;
					int wy = range > 0
						? ore.MinHeight + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xD003u)) * range)
						: ore.MinHeight;

					int r = ore.MinRadius + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xD004u)) * (ore.MaxRadius - ore.MinRadius + 1));

					float invR2 = 1f / (r * r);
					float invRy2 = 1f / (r * r * ore.Squash * ore.Squash);
					byte oreMat = (byte)ore.MaterialId;

					for (int dx = -r; dx <= r; dx++)
					{
						for (int dy = -r; dy <= r; dy++)
						{
							for (int dz = -r; dz <= r; dz++)
							{
								if (dx * dx * invR2 + dy * dy * invRy2 + dz * dz * invR2 > 1f) continue;

								int wxv = wx + dx;
								int ny = wy + dy;
								int wzv = wz + dz;
								int nx = wxv - wx0;
								int nz = wzv - wz0;
								if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;

								// Only replace real rock: never fill air (caves).
								byte existing = chunk.Get(nx, ny, nz);
								if (existing == VoxelMaterialIds.Air) continue;

								// Per-voxel surface check (see ApplyAsync).
								int colGround = (wxv >= wx0 && wxv < wx0 + sx && wzv >= wz0 && wzv < wz0 + sz)
									? heights[nx + sx * nz]
									: TerrainGenerator.SurfaceHeight(wxv, wzv, cfg);
								if (ny >= colGround - 2) continue;

								chunk.Set(nx, ny, nz, oreMat);
							}
						}
					}
				}
			}
		}
	}

	/// <summary>Asynchronously applies this structure generator (for multithreaded use).</summary>
	public async Task ApplyAsync(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz)
	{
		int sy = chunk.SizeY;

		var veinTasks = new List<Task>();
		foreach (var ore in _ores)
		{
			int cellMinX = FloorDiv(wx0, ore.CellSize);
			int cellMaxX = FloorDiv(wx0 + sx - 1, ore.CellSize);
			int cellMinZ = FloorDiv(wz0, ore.CellSize);
			int cellMaxZ = FloorDiv(wz0 + sz - 1, ore.CellSize);

			for (int cx = cellMinX; cx <= cellMaxX; cx++)
			{
				for (int cz = cellMinZ; cz <= cellMaxZ; cz++)
				{
					uint h = VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0x0BEEu);
					if (VoxelHash.Hash01(h) > ore.Probability) continue;

					int wx = cx * ore.CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xD001u)) * (ore.CellSize - 1));
					int wz = cz * ore.CellSize + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cz, cx, (uint)cfg.Seed ^ 0xD002u)) * (ore.CellSize - 1));

					// Height range: a vein center may sit anywhere in [MinHeight, MaxHeight].
					int range = ore.MaxHeight - ore.MinHeight;
					int wy = range > 0
						? ore.MinHeight + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xD003u)) * range)
						: ore.MinHeight;

					int r = ore.MinRadius + (int)(VoxelHash.Hash01(VoxelHash.Hash2(cx, cz, (uint)cfg.Seed ^ 0xD004u)) * (ore.MaxRadius - ore.MinRadius + 1));

					float invR2 = 1f / (r * r);
					float invRy2 = 1f / (r * r * ore.Squash * ore.Squash);
					byte oreMat = (byte)ore.MaterialId;

					veinTasks.Add(Task.Run(() => {
						for (int dx = -r; dx <= r; dx++)
						{
							for (int dy = -r; dy <= r; dy++)
							{
								for (int dz = -r; dz <= r; dz++)
								{
									if (dx * dx * invR2 + dy * dy * invRy2 + dz * dz * invR2 > 1f) continue;

									int wxv = wx + dx;
									int ny = wy + dy;
									int wzv = wz + dz;
									int nx = wxv - wx0;
									int nz = wzv - wz0;
									if (nx < 0 || nx >= sx || ny < 0 || ny >= sy || nz < 0 || nz >= sz) continue;

									// Only replace real rock: never fill air (caves).
									byte existing = chunk.Get(nx, ny, nz);
									if (existing == VoxelMaterialIds.Air) continue;

									// Per-voxel surface check: the ore must be at least
									// 2 voxels below THIS column's surface, not just
									// the vein center's. This prevents veins from
									// poking through on slopes/hills.
									int colGround = (wxv >= wx0 && wxv < wx0 + sx && wzv >= wz0 && wzv < wz0 + sz)
										? heights[nx + sx * nz]
										: TerrainGenerator.SurfaceHeight(wxv, wzv, cfg);
									if (ny >= colGround - 2) continue;

									chunk.Set(nx, ny, nz, oreMat);
								}
							}
						}
					}));
				}
			}
		}

		await Task.WhenAll(veinTasks);
	}

	/// <summary>Floor division (C# integer division truncates toward zero).</summary>
	private static int FloorDiv(int a, int b)
	{
		return (a - (a % b + b) % b) / b;
	}
}
