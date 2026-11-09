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
/// The shared terrain-map renderer + waypoint store that backs BOTH the
/// minimap and the full map window.
///
/// Terrain is sampled from the live <see cref="VoxelWorld"/> and baked into a
/// small <see cref="Image"/> (one pixel per world tile) that is shown as an
/// <see cref="ImageTexture"/>. This is the performance core of the feature:
///   - the map is an N×N image, NOT per-tile mesh / quads (no thousands of
///     draw calls);
///   - sampling a tile is one <see cref="VoxelWorld.GetVoxel"/> + one
///     <see cref="VoxelWorld.GetSurfaceHeight"/> — cheap;
///   - chunks are generated in a RING around the player, so the map fills in
///     outward naturally; and
///   - we do NOT re-sample every frame. We keep a dirty set of chunks and, when
///     it is non-empty, re-sample ONLY the affected tiles, spread over a small
///     per-frame budget (a few hundred tiles) so the fill-in is smooth and never
///     hitches.
///
/// The image is shared: the minimap and the map window both display the SAME
/// <see cref="ImageTexture"/> (the minimap crops/rotates it around the player,
/// the map window scrolls/zooms it). A single source of truth means the two
/// views can never disagree about the terrain.
///
/// Waypoints live here too (the store is the same object both UIs draw). The
/// <see cref="WaypointManager"/> node feeds the saved set in on load; the map
/// window adds/removes live ones. Every change raises <see cref="WaypointsChanged"/>.
/// </summary>
public partial class MapRenderer : RefCounted
{
	// ------------------------------------------------------------------
	// Image / world parameters
	// ------------------------------------------------------------------

	/// <summary>Image width/height in tiles (the map is square).</summary>
	public int Size { get; }

	/// <summary>World X of the tile the image's LEFT edge starts at.</summary>
	public int MinX { get; }

	/// <summary>World Z of the tile the image's TOP edge starts at.</summary>
	public int MinZ { get; }

	/// <summary>The shared terrain image (one RGBA8 pixel per world tile).</summary>
	public Image Image { get; }

	/// <summary>The live texture over the image (updated in place; swap on resize).</summary>
	public ImageTexture Texture { get; private set; }

	/// <summary>
	/// Raised after a terrain re-sample (the image pixels changed). The minimap
	/// / map window listen to push the new texture into their display nodes.
	/// </summary>
	public event System.Action TextureUpdated;

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
	// Dirty-chunk tracking
	// ------------------------------------------------------------------

	/// <summary>
	/// The world chunks whose tiles need (re)sampling. Keys are chunk coords
	/// (Vector2I = (x / ChunkSizeXZ, z / ChunkSizeXZ)). The world marks a chunk
	/// here when it generates / edits; the minimap drains it under a per-frame
	/// budget.
	/// </summary>
	private readonly HashSet<Vector2I> _dirtyChunks = new();

	/// <summary>
	/// The world-chunk coords the renderer has ALREADY sampled at least once.
	/// Used to know which chunks are "known" so an unloaded chunk that was once
	/// seen can be cleared back to "unknown" (so the map doesn't show stale
	/// terrain for a chunk the player has walked far from).
	/// </summary>
	private readonly HashSet<Vector2I> _sampledChunks = new();

	// The color of an "unknown" tile (terrain not yet sampled). A dark, slightly
	// blue-grey that reads as "off the map" and contrasts with terrain colors.
	private static readonly Color UnknownColor = new Color(0.07f, 0.08f, 0.10f, 1f);

	// ------------------------------------------------------------------
	// Construction
	// ------------------------------------------------------------------

	/// <summary>
	/// Builds the renderer for a world region centered on (centerX, centerZ):
	/// the image spans [centerX - Size/2, centerX + Size/2) in X and the same in
	/// Z. All tiles start as "unknown" (dark); the world's dirty-chunk marks
	/// drive the fill-in.
	/// </summary>
	public MapRenderer(int size, int centerX, int centerZ)
	{
		Size = Mathf.Max(8, size);
		MinX = centerX - Size / 2;
		MinZ = centerZ - Size / 2;
		Image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
		Image.Fill(UnknownColor);
		Texture = ImageTexture.CreateFromImage(Image);
	}

	// ------------------------------------------------------------------
	// Dirty-chunk API (called by the world / minimap)
	// ------------------------------------------------------------------

	/// <summary>
	/// Marks a world chunk as needing (re)sampling. Called by the world when a
	/// chunk generates or is edited, and by the minimap when it (re)discovers a
	/// loaded chunk in range. Idempotent (a chunk already marked stays marked).
	/// </summary>
	public void MarkDirty(Vector2I chunkCoord)
	{
		_dirtyChunks.Add(chunkCoord);
	}

	/// <summary>
	/// Marks EVERY world chunk in the [minC..maxC]×[minC..maxC] chunk range as
	/// dirty (a bulk re-sample, e.g. on world load or after a world switch).
	/// </summary>
	public void MarkRegionDirty(Vector2I minC, Vector2I maxC)
	{
		for (int cx = minC.X; cx <= maxC.X; cx++)
		{
			for (int cz = minC.Y; cz <= maxC.Y; cz++)
			{
				_dirtyChunks.Add(new Vector2I(cx, cz));
			}
		}
	}

	/// <summary>True when any chunk is waiting to be sampled.</summary>
	public bool HasDirtyChunks => _dirtyChunks.Count > 0;

	/// <summary>The number of chunks currently waiting to be sampled.</summary>
	public int DirtyChunkCount => _dirtyChunks.Count;

	// ------------------------------------------------------------------
	// Sampling (the per-frame work)
	// ------------------------------------------------------------------

	/// <summary>
	/// Re-samples up to <paramref name="budget"/> tiles this frame, working
	/// through the dirty chunks. Returns true when the dirty set is still
	/// non-empty (the caller should keep calling next frame).
	///
	/// A tile is sampled by reading the topmost solid voxel in its column and
	/// coloring it by that material's albedo (with a height-relief shade), or
	/// "unknown" when the chunk isn't loaded yet. Only tiles whose chunk is
	/// currently loaded are written; unloaded chunks in the dirty set are left
	/// alone (they'll be re-marked once they generate).
	/// </summary>
	public bool SampleBudget(VoxelWorld world, int budget)
	{
		if (world == null || _dirtyChunks.Count == 0)
		{
			return false;
		}

		int chunkSize = world.ChunkSizeXZ;
		int paletteCount = world.Palette?.MaterialCount ?? 0;
		int sampled = 0;

		// Take a snapshot of the dirty set so we can clear it as we go.
		var toProcess = new List<Vector2I>(_dirtyChunks);
		foreach (var c in toProcess)
		{
			if (sampled >= budget)
			{
				break;
			}

			int chunkMinX = c.X * chunkSize;
			int chunkMinZ = c.Y * chunkSize;

			// Is this chunk actually loaded right now? If not, skip it (leave it
			// dirty — it will be re-marked when it generates).
			if (world.GetChunkData(c) == null)
			{
				// Not loaded: don't clear the mark, don't sample. Keep it dirty.
				continue;
			}

			_dirtyChunks.Remove(c);
			_sampledChunks.Add(c);

			for (int lx = 0; lx < chunkSize && sampled < budget; lx++)
			{
				for (int lz = 0; lz < chunkSize && sampled < budget; lz++)
				{
					int wx = chunkMinX + lx;
					int wz = chunkMinZ + lz;

					// Image tile coords: image (0,0) = world (MinX, MinZ).
					int ix = wx - MinX;
					int iz = wz - MinZ;
					if (ix < 0 || ix >= Size || iz < 0 || iz >= Size)
					{
						continue; // outside the image
					}

					int surfaceY = world.GetSurfaceHeight(wx, wz);
					Color color;
					if (surfaceY < 0)
					{
						color = UnknownColor;
					}
					else
					{
						int mat = world.GetVoxel(wx, surfaceY, wz);
						color = TerrainColor(mat, surfaceY, world, paletteCount);
					}
					Image.SetPixel(ix, iz, color);
					sampled++;
				}
			}
		}

		if (sampled > 0)
		{
			TextureUpdated?.Invoke();
		}
		return _dirtyChunks.Count > 0;
	}

	/// <summary>
	/// Forces an immediate full re-sample of every loaded chunk in the image
	/// region (ignoring the dirty set). Used on world load / switch so the map
	/// starts correct. Samples everything loaded; unloaded tiles stay unknown.
	/// </summary>
	public void ForceResampleAll(VoxelWorld world)
	{
		if (world == null)
		{
			return;
		}
		int chunkSize = world.ChunkSizeXZ;
		int paletteCount = world.Palette?.MaterialCount ?? 0;
		for (int wx = MinX; wx < MinX + Size; wx++)
		{
			for (int wz = MinZ; wz < MinZ + Size; wz++)
			{
				int ix = wx - MinX;
				int iz = wz - MinZ;
				int surfaceY = world.GetSurfaceHeight(wx, wz);
				Color color = surfaceY < 0
					? UnknownColor
					: TerrainColor(world.GetVoxel(wx, surfaceY, wz), surfaceY, world, paletteCount);
				Image.SetPixel(ix, iz, color);
			}
		}
		_dirtyChunks.Clear();
		TextureUpdated?.Invoke();
	}

	// ------------------------------------------------------------------
	// Coloring
	// ------------------------------------------------------------------

	/// <summary>
	/// The color for a terrain tile: the topmost material's albedo (texture-aware
	/// via the palette's tint), darkened by a simple height relief so the map has
	/// a 2.5D look (higher = brighter, lower = darker). A texture's average
	/// color is used when the material has one (so the map matches the terrain's
	/// actual look, not just its tint).
	/// </summary>
	private Color TerrainColor(int materialId, int surfaceY, VoxelWorld world, int paletteCount)
	{
		var palette = world.Palette;
		if (palette == null || materialId <= 0)
		{
			return UnknownColor;
		}

		int slot = materialId - 1;
		if (slot >= palette.Materials.Length)
		{
			return UnknownColor;
		}

		// Base color: the texture's average when present (the palette's tint is
		// white for textured materials, so the tint alone wouldn't carry the
		// texture's color); otherwise the material's albedo tint.
		Color baseColor;
		Texture2D tex = slot < palette.Textures.Length ? palette.Textures[slot] : null;
		if (tex != null)
		{
			baseColor = AverageTextureColor(tex);
		}
		else
		{
			baseColor = slot < palette.Tints.Length ? palette.Tints[slot] : Colors.White;
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

	// Per-texture cached average color (a texture is static; sampling its pixels
	// once and reusing is far cheaper than re-reading every re-sample).
	private readonly System.Collections.Generic.Dictionary<Texture2D, Color> _avgColorCache = new();

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
