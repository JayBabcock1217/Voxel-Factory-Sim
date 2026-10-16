namespace FactorySim.Voxel;

/// <summary>
/// Raw voxel data for one chunk. Index 0 is air; other values are
/// <see cref="VoxelMaterial"/> ids.
///
/// Also holds the per-voxel HEALTH (0..100): how much damage the player has
/// done to each voxel. Generation never writes it, so it is implicitly
/// 100 ("full") for every generated voxel; edits (adds / breaks) reset it
/// through <see cref="VoxelWorld.SetVoxel"/>.
/// </summary>
public sealed class ChunkData
{
	public int SizeX { get; }
	public int SizeY { get; }
	public int SizeZ { get; }
	public byte[] Data { get; }

	/// <summary>
	/// Per-voxel health in percentage points (0 = broken, 100 = undamaged).
	/// Implicitly full for generated voxels; mutated by the player's damage
	/// system and reset by edits.
	/// </summary>
	public float[] Health { get; }

	public ChunkData(int sizeX, int sizeY, int sizeZ)
	{
		SizeX = sizeX;
		SizeY = sizeY;
		SizeZ = sizeZ;
		Data = new byte[sizeX * sizeY * sizeZ];
		Health = new float[sizeX * sizeY * sizeZ];
	}

	public int Index(int x, int y, int z) => x + SizeX * (y + SizeY * z);

	public byte Get(int x, int y, int z) => Data[Index(x, y, z)];

	public void Set(int x, int y, int z, byte value) => Data[Index(x, y, z)] = value;

	// ------------------------------------------------------------------
	// Voxel health
	// ------------------------------------------------------------------

	/// <summary>
	/// Reads a voxel's health; a missing entry (or a voxel that isn't solid)
	/// reports <see cref="VoxelHealth.MaxHealth"/> — generation leaves every
	/// voxel at full health without ever writing the array.
	/// </summary>
	public float GetHealth(int x, int y, int z)
	{
		int i = Index(x, y, z);
		float h = Health[i];
		return h <= 0f ? VoxelHealth.MaxHealth : h;
	}

	/// <summary>
	/// Writes a voxel's health, clamped to [0, <see cref="VoxelHealth.MaxHealth"/>].
	/// Values at or above full health are cleared from storage (100 is the
	/// implicit default).
	/// </summary>
	public void SetHealth(int x, int y, int z, float value)
	{
		int i = Index(x, y, z);
		if (value >= VoxelHealth.MaxHealth)
		{
			Health[i] = 0f; // implicit full
			return;
		}
		Health[i] = System.Math.Max(0f, value);
	}

	/// <summary>Subtracts damage from a voxel's health (clamped at 0).</summary>
	public void Damage(int x, int y, int z, float amount)
	{
		SetHealth(x, y, z, GetHealth(x, y, z) - amount);
	}
}
