using System.Threading.Tasks;

namespace FactorySim.Voxel;

/// <summary>
/// A procedural structure that is stamped into the terrain (rocks, ruins, ore deposits...).
///
/// Implementations must be deterministic for a given (cell, seed) so that structures
/// that cross chunk borders are generated consistently by every neighbouring chunk.
/// Only write voxels that fall inside the chunk you are given.
/// </summary>
public interface IStructureGenerator
{
	/// <param name="chunk">The chunk to stamp into (local coordinates).</param>
	/// <param name="wx0">World voxel X of the chunk's local (0,0,0).</param>
	/// <param name="wz0">World voxel Z of the chunk's local (0,0,0).</param>
	/// <param name="cfg">Global generation parameters.</param>
	/// <param name="heights">Per-column surface-height cache indexed <c>heights[x + sx * z]</c>, covering
	/// the range <c>[0, sx) × [0, sz)</c>.</param>
	/// <param name="sx">Chunk X dimension (columns along X).</param>
	/// <param name="sz">Chunk Z dimension (columns along Z).</param>
	/// <summary>
	/// Synchronously applies this structure generator to the chunk.
	/// Implementations should use <paramref name="heights"/> for in-bounds columns and may call
	/// <see cref="TerrainGenerator.SurfaceHeight"/> for out-of-bounds ones.
	/// </summary>
	void Apply(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz);

	/// <summary>
	/// Asynchronously applies this structure generator (for multithreaded use).<br/>
	/// Implementations should use Task.Run or similar for CPU-bound work.<br/>
	/// Implementations should use <paramref name="heights"/> for in-bounds columns and may call
	/// <see cref="TerrainGenerator.SurfaceHeight"/> for out-of-bounds ones.
	/// </summary>
	Task ApplyAsync(ChunkData chunk, int wx0, int wz0, GenConfig cfg, short[] heights, int sx, int sz);
}
