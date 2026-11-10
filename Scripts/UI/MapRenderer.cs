using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// A single player-placed map waypoint, as it lives in the running game (as
/// opposed to the on-disk <see cref="FactorySim.Voxel.WorldSaveFile.WaypointRecord"/>
/// form). Carries a short name, a world position (meters), and a display color,
/// plus a monotonic id for selection / remove. Rendered by both the minimap and
/// the map window; persisted to the world save (v9+).
/// </summary>
public readonly struct Waypoint
{
	/// <summary>Player-given waypoint name (short).</summary>
	public string Name { get; }

	/// <summary>World position the waypoint marks (meters).</summary>
	public Vector3 Position { get; }

	/// <summary>Display color (RGBA, 0..1).</summary>
	public Color Color { get; }

	/// <summary>Monotonic id (used for selection / remove).</summary>
	public long Id { get; }

	public Waypoint(long id, string name, Vector3 position, Color color)
	{
		Id = id;
		Name = name;
		Position = position;
		Color = color;
	}
}

/// <summary>
/// The 2D map data for ONE world chunk (ChunkSizeXZ × ChunkSizeXZ tiles): the
/// surface height (Y of the topmost solid voxel) and the topmost material id for
/// each column. -1 height = no solid voxel (void). This is the "2D height data"
/// the world PUSHES to the map the moment a chunk finishes generating (see
/// <see cref="VoxelWorld.OnGenerationComplete"/>) — the map never scans the world
/// itself, it only displays what the world hands it.
///
/// The two parallel arrays are indexed [z * Size + x] (row-major, x = fast axis).
/// </summary>
public sealed class ChunkMapData
{
	/// <summary>
	/// The chunk's tile count per axis (16×16). MUST match
	/// <see cref="VoxelWorld.ChunkSizeXZ"/> (an instance property defaulting to 16,
	/// which the degenerate-fix in <c>VoxelWorld</c> snaps back to 16, so it is
	/// effectively a constant). A compile-time const here lets the map data use
	/// fixed-size arrays without querying a live world instance.
	/// </summary>
	public const int Size = 16;

	/// <summary>Surface height per tile (Y of topmost solid voxel), or -1 for void. Indexed [z*Size+x].</summary>
	public readonly int[] Heights = new int[Size * Size];

	/// <summary>Topmost material id per tile (0 = air/void). Indexed [z*Size+x].</summary>
	public readonly byte[] Materials = new byte[Size * Size];

	public void Clear()
	{
		for (int i = 0; i < Heights.Length; i++)
		{
			Heights[i] = -1;
		}
	}

	/// <summary>
	/// Writes one tile. <paramref name="x"/>,<paramref name="z"/> are chunk-local
	/// (0..Size-1). Called by the world when a column's surface changes (generation
	/// or an edit).
	/// </summary>
	public void SetTile(int x, int z, int height, int material)
	{
		if (x < 0 || x >= Size || z < 0 || z >= Size)
		{
			return;
		}
		int i = z * Size + x;
		Heights[i] = height;
		Materials[i] = (byte)material;
	}
}

/// <summary>
/// The SHARED MAP DATA STORE that backs BOTH the minimap and the full map window.
/// It is the single source of truth for what the map shows, so the two views can
/// never disagree.
///
/// ARCHITECTURE (push-based — see the breakdown in the PR / commit notes):
///   1. The world computes each chunk's 2D height+material data ONCE, the moment
///      the chunk finishes generating (<see cref="VoxelWorld.OnGenerationComplete"/>)
///      and on every edit (<see cref="VoxelWorld.SetVoxel"/>), and PUSHES it here
///      via <see cref="SetChunk"/> / <see cref="SetTile"/>.
///   2. The minimap and map window NEVER scan the world. They simply read
///      <see cref="GetTileColor"/> for each visible tile (an O(1) dictionary +
///      array lookup) and blit the pixels. There is no per-frame
///      GetSurfaceHeight scan, no budget, no dirty-chunk tracking — the map is a
///      pure DISPLAY of the data the world handed it.
///
/// This is what fixes the 40fps: the old design scanned up to ChunkHeight (256)
/// voxels per visible tile, every frame, for BOTH the minimap and the full map.
/// Now the per-tile cost is a single dictionary lookup + two array reads + one
/// multiply-add for the height shade.
///
/// Also owns the player's WAYPOINT store (add / remove / restore / save), shared
/// by both views.
/// </summary>
public partial class MapRenderer : RefCounted
{
	// ------------------------------------------------------------------
	// Waypoints
	// ------------------------------------------------------------------

	private readonly List<Waypoint> _waypoints = new();
	private long _nextWaypointId = 1;

	/// <summary>A read-only view of the live waypoints (for rendering).</summary>
	public IReadOnlyList<Waypoint> Waypoints => _waypoints;

	/// <summary>Raised whenever the waypoint set changes (add / remove / clear / restore).</summary>
	public event System.Action WaypointsChanged;

	// ------------------------------------------------------------------
	// Terrain data (pushed by the world)
	// ------------------------------------------------------------------

	/// <summary>The color of an "unknown" tile (chunk not yet generated / pushed).</summary>
	public static readonly Color UnknownColor = new Color(0.08f, 0.08f, 0.08f, 1f);

	/// <summary>
	/// Raised whenever the terrain data changes (a chunk is pushed, a tile is
	/// edited, a chunk is removed, or all terrain is cleared). The minimap and
	/// map window subscribe to this to know when they need to re-read the data
	/// (instead of re-reading every frame).
	/// </summary>
	public event System.Action TerrainChanged;

	/// <summary>Per-chunk 2D height+material data, keyed by chunk coord. Sparse (only pushed chunks).</summary>
	private readonly Dictionary<Vector2I, ChunkMapData> _chunks = new();

	/// <summary>
	/// Replaces (or inserts) the 2D map data for a chunk. Called by the world when
	/// a chunk finishes generating (a full 16×16 push) — this is the main data path.
	/// </summary>
	public void SetChunk(Vector2I coord, int[] heights, byte[] materials)
	{
		int size = ChunkMapData.Size;
		if (heights == null || materials == null || heights.Length != size * size || materials.Length != size * size)
		{
			return;
		}
		if (!_chunks.TryGetValue(coord, out ChunkMapData data))
		{
			data = new ChunkMapData();
			_chunks[coord] = data;
		}
		// Bulk-copy the pushed arrays into the stored tile data.
		System.Array.Copy(heights, data.Heights, size * size);
		System.Array.Copy(materials, data.Materials, size * size);
		TerrainChanged?.Invoke();
	}

	/// <summary>
	/// Updates a single tile of a chunk's map data. Called by the world when an
	/// EDIT changes a column's surface (a voxel placed/broken). If the chunk has
	/// no data yet (not generated), this is a no-op (it will be pushed whole when
	/// generation completes).
	/// </summary>
	public void SetTile(Vector2I coord, int localX, int localZ, int height, int material)
	{
		if (_chunks.TryGetValue(coord, out ChunkMapData data))
		{
			data.SetTile(localX, localZ, height, material);
			TerrainChanged?.Invoke();
		}
	}

	/// <summary>
	/// Removes a chunk's map data (called by the world when a chunk is freed so
	/// stale terrain doesn't linger on the map). A no-op when the chunk has no data.
	/// </summary>
	public void RemoveChunk(Vector2I coord)
	{
		if (_chunks.Remove(coord))
		{
			TerrainChanged?.Invoke();
		}
	}

	/// <summary>Drops ALL terrain data (world switch / new world). Waypoints are kept.</summary>
	public void ClearTerrain()
	{
		if (_chunks.Count > 0)
		{
			_chunks.Clear();
			TerrainChanged?.Invoke();
		}
	}

	/// <summary>
	/// The display color for a world tile (O(1)). Looks up the chunk's pushed 2D
	/// data and colors the tile by its topmost material + a height-relief shade.
	/// Returns <see cref="UnknownColor"/> for tiles whose chunk hasn't been pushed
	/// yet (not generated) or that are void.
	/// </summary>
	public Color GetTileColor(int wx, int wz, VoxelMaterialPalette palette)
	{
		int size = ChunkMapData.Size;
		int cx = FloorDiv(wx, size);
		int cz = FloorDiv(wz, size);
		if (!_chunks.TryGetValue(new Vector2I(cx, cz), out ChunkMapData data))
		{
			return UnknownColor;
		}
		int lx = wx - cx * size;
		int lz = wz - cz * size;
		int i = lz * size + lx;
		int h = data.Heights[i];
		if (h < 0)
		{
			return UnknownColor;
		}
		return TileColor(data.Materials[i], h, palette);
	}

	/// <summary>
	/// Non-allocating tile color lookup for the hot path (the minimap / map
	/// window call this per-tile, per-frame — a <see cref="Vector2I"/> allocation
	/// per call is pure GC churn). The caller computes the chunk coord and
	/// passes it in; this method only does the dictionary lookup + array reads.
	/// Returns the packed RGBA as a single <see cref="uint"/> (0xAABBGGRR) to
	/// avoid a <see cref="Color"/> allocation, plus an "is known" flag.
	/// </summary>
	public bool TryGetTileColorPacked(int cx, int cz, int lx, int lz, VoxelMaterialPalette palette, out uint packed)
	{
		packed = 0;
		if (!_chunks.TryGetValue(new Vector2I(cx, cz), out ChunkMapData data))
		{
			return false;
		}
		int i = lz * ChunkMapData.Size + lx;
		int h = data.Heights[i];
		if (h < 0)
		{
			return false;
		}
		Color c = TileColor(data.Materials[i], h, palette);
		packed = ((uint)(c.A * 255f) << 24) | ((uint)(c.B * 255f) << 16) | ((uint)(c.G * 255f) << 8) | (uint)(c.R * 255f);
		return true;
	}

	// Floor division (matches the world's chunk-coord convention for negatives).
	// Public so the minimap / map window can compute chunk coords incrementally
	// in their hot paths without a per-tile FloorDiv call.
	public static int FloorDiv(int a, int b)
	{
		int q = a / b;
		if (a < 0 && a % b != 0)
		{
			q--;
		}
		return q;
	}

	// ------------------------------------------------------------------
	// Coloring
	// ------------------------------------------------------------------

	// Per-material base color, cached (a material's texture/tint is static).
	// Keyed by material id. Computed once per material.
	private readonly Dictionary<int, Color> _baseColorCache = new();

	// Per-texture cached average color (a texture is static; sampling its pixels
	// once and reusing is far cheaper than re-reading every tile).
	private readonly Dictionary<Texture2D, Color> _avgColorCache = new();

	/// <summary>
	/// The color for a terrain tile: the topmost material's albedo (texture-aware
	/// via the palette's tint), darkened by a simple height relief so the map has
	/// a 2.5D look (higher = brighter, lower = darker). A texture's average
	/// color is used when the material has one (so the map matches the terrain's
	/// actual look, not just its tint).
	/// </summary>
	private Color TileColor(int materialId, int surfaceY, VoxelMaterialPalette palette)
	{
		if (palette == null || materialId <= 0)
		{
			return UnknownColor;
		}

		Color baseColor;
		if (_baseColorCache.TryGetValue(materialId, out baseColor))
		{
			// Cache hit (fast path).
		}
		else
		{
			int slot = materialId - 1;
			if (slot >= palette.Materials.Length)
			{
				return UnknownColor;
			}
			// Base color: the texture's average when present (the palette's tint is
			// white for textured materials, so the tint alone wouldn't carry the
			// texture's color); otherwise the material's albedo tint.
			Texture2D tex = slot < palette.Textures.Length ? palette.Textures[slot] : null;
			if (tex != null)
			{
				baseColor = AverageTextureColor(tex);
			}
			else
			{
				baseColor = slot < palette.Tints.Length ? palette.Tints[slot] : Colors.White;
			}
			_baseColorCache[materialId] = baseColor;
		}

		// Height relief: normalize the surface height into a 0..1 shade and mix
		// it over the base color. A flat 0.55..1.0 range keeps the map readable
		// (not blown out, not pitch dark).
		float h = surfaceY / 256f; // chunk height is 256 in the shipped world
		float shade = 0.55f + 0.45f * Mathf.Clamp(h, 0f, 1f);
		return new Color(
			baseColor.R * shade,
			baseColor.G * shade,
			baseColor.B * shade,
			1f);
	}

	/// <summary>Average (downsampled) color of a texture, cached per texture.</summary>
	private Color AverageTextureColor(Texture2D tex)
	{
		if (_avgColorCache.TryGetValue(tex, out Color cached))
		{
			return cached;
		}
		Color avg = Colors.White;
		try
		{
			// GetImage() extracts the texture's pixels (it works for every
			// Texture2D type, including imported CompressedTexture2D). Sample a
			// coarse grid (max ~16×16) so the average is cheap and stable.
			if (tex.GetImage() is Image img && img != null)
			{
				int w = img.GetWidth();
				int h = img.GetHeight();
				if (w > 0 && h > 0)
				{
					int grid = 16;
					float r = 0, g = 0, b = 0;
					int n = 0;
					for (int gy = 0; gy < grid; gy++)
					{
						int py = Mathf.Clamp((int)((gy + 0.5f) * h / grid), 0, h - 1);
						for (int gx = 0; gx < grid; gx++)
						{
							int px = Mathf.Clamp((int)((gx + 0.5f) * w / grid), 0, w - 1);
							var c = img.GetPixel(px, py);
							r += c.R; g += c.G; b += c.B;
							n++;
						}
					}
					if (n > 0)
					{
						avg = new Color(r / n, g / n, b / n, 1f);
					}
				}
			}
		}
		catch (System.Exception)
		{
			avg = Colors.White;
		}
		_avgColorCache[tex] = avg;
		return avg;
	}

	// ------------------------------------------------------------------
	// Waypoint store (shared by both UIs)
	// ------------------------------------------------------------------

	/// <summary>
	/// Adds a waypoint. The name is trimmed and clamped; an empty name becomes
	/// "Waypoint N". Returns the new waypoint (with its assigned id).
	/// </summary>
	public Waypoint AddWaypoint(string name, Vector3 position, Color color)
	{
		name = (name ?? "").Trim();
		if (name.Length > 32)
		{
			name = name.Substring(0, 32);
		}
		if (name.Length == 0)
		{
			name = "Waypoint " + (_waypoints.Count + 1);
		}
		var wp = new Waypoint(_nextWaypointId++, name, position, color);
		_waypoints.Add(wp);
		WaypointsChanged?.Invoke();
		return wp;
	}

	/// <summary>Removes the waypoint with the given id. Returns true when found.</summary>
	public bool RemoveWaypointById(long id)
	{
		for (int i = 0; i < _waypoints.Count; i++)
		{
			if (_waypoints[i].Id == id)
			{
				_waypoints.RemoveAt(i);
				WaypointsChanged?.Invoke();
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Replaces the whole waypoint set (used on save restore / world switch).
	/// Raises the change event once.
	/// </summary>
	public void SetWaypoints(IEnumerable<Waypoint> waypoints)
	{
		_waypoints.Clear();
		_nextWaypointId = 1;
		foreach (var wp in waypoints)
		{
			_waypoints.Add(new Waypoint(_nextWaypointId++, wp.Name, wp.Position, wp.Color));
		}
		WaypointsChanged?.Invoke();
	}

	/// <summary>Removes every waypoint.</summary>
	public void ClearWaypoints()
	{
		if (_waypoints.Count == 0)
		{
			return;
		}
		_waypoints.Clear();
		WaypointsChanged?.Invoke();
	}

	/// <summary>
	/// Restores the saved waypoints into this store. Called by the world once the
	/// player is placed. A null list clears the set (a fresh world has none).
	/// </summary>
	public void RestoreWaypoints(List<WorldSaveFile.WaypointRecord> saved)
	{
		_waypoints.Clear();
		_nextWaypointId = 1;
		if (saved != null)
		{
			foreach (var r in saved)
			{
				_waypoints.Add(new Waypoint(_nextWaypointId++, r.Name, r.Position, r.Color));
			}
		}
		WaypointsChanged?.Invoke();
	}

	/// <summary>
	/// Builds the save-form list from the live set (used by the save system).
	/// </summary>
	public List<WorldSaveFile.WaypointRecord> ToSaveRecords()
	{
		var list = new List<WorldSaveFile.WaypointRecord>(_waypoints.Count);
		foreach (var wp in _waypoints)
		{
			list.Add(new WorldSaveFile.WaypointRecord(wp.Name, wp.Position, wp.Color));
		}
		return list;
	}
}
