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
/// The shared map SERVICES that back BOTH the minimap and the full map window:
///   - the player's WAYPOINT store (add / remove / restore / save), and
///   - the TERRAIN COLORING (a world tile → its topmost material's color, with a
///     height-relief shade for a 2.5D look).
///
/// It deliberately does NOT own a persistent terrain image. The minimap and the
/// map window each sample their OWN visible region directly from the live
/// <see cref="VoxelWorld"/> (via <see cref="SampleRegion"/>), spread over a
/// per-frame budget. This is simpler and more robust than a shared image window:
/// there is no "re-center" bookkeeping that can desync from the player and leave
/// the visible area unsampled (which is exactly what made the map render empty).
/// Sampling a tile is one <see cref="VoxelWorld.GetSurfaceHeight"/> + one
/// <see cref="VoxelWorld.GetVoxel"/> (both cheap, chunk-backed), and the budget
/// keeps the per-frame cost small and the fill-in smooth.
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
	// Terrain sampling
	// ------------------------------------------------------------------

	/// <summary>The color of an "unknown" tile (terrain not loaded / sampled yet).</summary>
	public static readonly Color UnknownColor = new Color(0.07f, 0.08f, 0.10f, 1f);

	/// <summary>
	/// Samples a world XZ region directly from the live world, writing up to
	/// <paramref name="budget"/> tiles this frame into <paramref name="dest"/>
	/// (an image whose (0,0) corresponds to world (<paramref name="minX"/>,
	/// <paramref name="minZ"/>)). Returns true when there are still unsampled
	/// tiles in the region (the caller should keep calling next frame).
	///
	/// A tile's color comes from its topmost solid voxel: the material's texture
	/// (averaged) or albedo tint, darkened by a height-relief shade. A column with
	/// no solid voxel (void / unloaded chunk) is written as "unknown".
	/// </summary>
	public bool SampleRegion(VoxelWorld world, Image dest, int minX, int minZ, int budget)
	{
		if (world == null || dest == null)
		{
			return false;
		}
		int destW = dest.GetWidth();
		int destH = dest.GetHeight();
		int paletteCount = world.Palette?.MaterialCount ?? 0;
		int sampled = 0;
		for (int dz = 0; dz < destH && sampled < budget; dz++)
		{
			for (int dx = 0; dx < destW && sampled < budget; dx++)
			{
				int wx = minX + dx;
				int wz = minZ + dz;
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
				dest.SetPixel(dx, dz, color);
				sampled++;
			}
		}
		return sampled < destW * destH;
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
