namespace FactorySim.Voxel;

/// <summary>
/// Small deterministic integer hash used for feature placement (trees, rocks)
/// so that the same world position always produces the same result,
/// even when generated from different chunks.
/// </summary>
public static class VoxelHash
{
	public static uint Hash2(int x, int y, uint seed)
	{
		uint h = Mix(seed, x);
		h = Mix(h, y);
		return h;
	}

	/// <summary>Maps a hash to [0, 1).</summary>
	public static float Hash01(uint h)
	{
		return (h >> 8) * (1f / 16777216f);
	}

	private static uint Mix(uint h, int v)
	{
		h ^= (uint)v;
		h = (h ^ (h >> 16)) * 0x7FEB352Du;
		h = (h ^ (h >> 13)) * 0x846CA68Bu;
		return h ^ (h >> 16);
	}
}
