using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The always-on-top-left MINIMAP: a small, rotating circular map of the terrain
/// around the player, drawn over the gameplay HUD. Modeled on Xaero's Minimap —
/// the map rotates so the player's view direction is always up, with a
/// directional arrow on the rim, the player's position at the center, and
/// waypoint markers (with off-screen edge arrows for waypoints outside the
/// circle).
///
/// Performance / correctness: each frame the minimap reads a small square of
/// world tiles AROUND THE PLAYER from the shared <see cref="MapRenderer"/>. The
/// world PUSHES each chunk's 2D height+material data to the renderer the moment
/// the chunk generates / is edited, so reading a tile is an O(1) lookup — the
/// minimap does NO scanning of the world (no GetSurfaceHeight, no budget). It
/// simply displays the data the world handed it, re-centered on the player,
/// rotated by the player's yaw into a circular display texture (one draw call).
/// Unpushed chunks (not yet generated) read as "unknown".
///
/// The node builds its own <see cref="CanvasLayer"/> in <c>_Ready</c> (matching
/// the project's code-built UI pattern) and is added to the scene by
/// <c>Scenes/Main.tscn</c>. It finds the <c>WaypointManager</c>, the
/// <c>Player</c>, and the <c>VoxelWorld</c> via their groups.
/// </summary>
public partial class Minimap : Node
{
	// ------------------------------------------------------------------
	// Tunables
	// ------------------------------------------------------------------

	/// <summary>Diameter (px) of the circular minimap.</summary>
	[Export] public int DiameterPx { get; set; } = 320;

	/// <summary>Screen margin from the top-left corner.</summary>
	[Export] public int MarginPx { get; set; } = 12;

	/// <summary>
	/// The width (in world tiles) of the square terrain slice that the circle is
	/// cut out of. It must be LARGER than the circle's diameter (in tiles) so the
	/// circle always fits inside sampled terrain with a margin — otherwise the
	/// circle's diagonal edges read past the slice and show as dark "unknown"
	/// (which is what made the map look "covered"). The circle's radius in tiles
	/// is (DiameterPx/2) / (DiameterPx/RadiusTiles) = RadiusTiles/2, so a value
	/// ~50% larger than needed (RadiusTiles ≈ 1.5 × DiameterPx/tile) is safe.
	/// </summary>
	[Export] public int RadiusTiles { get; set; } = 96;

	// ------------------------------------------------------------------
	// State
	// ------------------------------------------------------------------

	private CanvasLayer _layer;
	private Panel _panel;
	private TextureRect _mapRect;
	private Control _arrow;
	private Label _compassN;

	private WaypointManager _manager;
	private Player _player;
	private VoxelWorld _world;

	// The circular display texture (rotated terrain slice + waypoint overlay).
	private Image _displayImage;
	private ImageTexture _displayTexture;

	// The terrain slice (RadiusTiles × RadiusTiles world tiles), read fresh each
	// frame from the shared map data (which the world pushes as chunks generate).
	private Image _slice;

	// The slice's world origin; re-centered on the player every frame.
	private int _sliceMinX;
	private int _sliceMinZ;

	// Reusable managed buffer for the slice's pixel data (filled in ReadSlice,
	// pushed into the Image via SetData). Avoids per-frame allocation.
	private byte[] _sliceBuffer;

	// Reusable managed buffer for the display (rotated) pixel data (filled in
	// RotateBlit, pushed into the Image via SetData). Avoids per-frame
	// allocation and eliminates the per-pixel SetPixel binding calls.
	private byte[] _displayBuf;

	// Dirty flag: set when the terrain data changes (a chunk generates, gets
	// edited, or is removed) OR the player moves to a new tile. The slice is
	// only re-read when this is true — the map data is persistent and only
	// changes on chunk events, so we don't re-read it every frame.
	private bool _sliceDirty = true;

	// The player tile the slice was last read for (to detect movement).
	private int _lastSlicePx;
	private int _lastSlicePz;

	// ------------------------------------------------------------------
	// Lifecycle
	// ------------------------------------------------------------------

	public override void _Ready()
	{
		AddToGroup("minimap");
		_layer = new CanvasLayer { Layer = 90 };
		AddChild(_layer);

		_manager = GetTree().GetFirstNodeInGroup("waypoint_manager") as WaypointManager;
		_player = GetTree().GetFirstNodeInGroup("player") as Player;

		// Build the display nodes.
		_panel = new Panel
		{
			Name = "MinimapPanel",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_layer.AddChild(_panel);

		_mapRect = new TextureRect
		{
			Name = "Map",
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			CustomMinimumSize = new Vector2(DiameterPx, DiameterPx),
		};
		_panel.AddChild(_mapRect);

		// The player-direction arrow (a small triangle drawn at the top edge).
		_arrow = MakeArrow();
		_panel.AddChild(_arrow);

		// A subtle "N" label that rotates with the map (so it always marks north).
		_compassN = new Label
		{
			Name = "North",
			Text = "N",
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			CustomMinimumSize = new Vector2(20, 20),
		};
		_compassN.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.85f));
		_panel.AddChild(_compassN);

		// Layout: anchor the panel to the top-left.
		_panel.Position = new Vector2(MarginPx, MarginPx);
		_panel.Size = new Vector2(DiameterPx, DiameterPx);

		// Display image (square, same size as the circle).
		_displayImage = Image.CreateEmpty(DiameterPx, DiameterPx, false, Image.Format.Rgba8);
		_displayTexture = ImageTexture.CreateFromImage(_displayImage);
		_mapRect.Texture = _displayTexture;

		// The terrain slice (RadiusTiles × RadiusTiles world tiles).
		_slice = Image.CreateEmpty(RadiusTiles, RadiusTiles, false, Image.Format.Rgba8);

		// Subscribe to waypoint changes (redraw the overlay immediately).
		if (_manager != null)
		{
			_manager.WaypointsChanged += OnWaypointsChanged;
			_manager.MapSystemToggled += OnMapSystemToggled;
			_manager.Renderer.TerrainChanged += OnTerrainChanged;
		}
	}

	private void OnTerrainChanged() => _sliceDirty = true;

	private void OnMapSystemToggled()
	{
		if (_panel != null)
		{
			_panel.Visible = _manager?.IsMapSystemEnabled ?? true;
		}
	}

	public override void _Process(double delta)
	{
		// Re-fetch the world / manager / player lazily (the scene tree may not be
		// fully up when _Ready ran, e.g. the player joins its group later).
		if (_manager == null)
		{
			_manager = GetTree().GetFirstNodeInGroup("waypoint_manager") as WaypointManager;
			if (_manager != null)
			{
				_manager.WaypointsChanged += OnWaypointsChanged;
				_manager.MapSystemToggled += OnMapSystemToggled;
				_manager.Renderer.TerrainChanged += OnTerrainChanged;
			}
		}

		// Skip all work when the map system is toggled off.
		if (_manager != null && !_manager.IsMapSystemEnabled)
		{
			return;
		}
		if (_player == null)
		{
			_player = GetTree().GetFirstNodeInGroup("player") as Player;
		}
		if (_world == null)
		{
			_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;
		}

		if (_world == null || _player == null || _manager?.Renderer == null)
		{
			return;
		}

		var px = (int)Mathf.Floor(_player.GlobalPosition.X);
		var pz = (int)Mathf.Floor(_player.GlobalPosition.Z);

		// Detect player movement to a new tile (the slice window shifts).
		if (px != _lastSlicePx || pz != _lastSlicePz)
		{
			_sliceDirty = true;
			_lastSlicePx = px;
			_lastSlicePz = pz;
		}

		// Re-read the slice ONLY when the terrain data changed (a chunk
		// generated / edited / removed) or the player moved to a new tile.
		// The map data is persistent — it only changes on chunk events, so we
		// don't re-read it every frame.
		if (_sliceDirty)
		{
			_sliceMinX = px - RadiusTiles / 2;
			_sliceMinZ = pz - RadiusTiles / 2;
			ReadSlice(_manager.Renderer);
			_sliceDirty = false;
		}

		// Rotate the slice into the circular display and draw the overlays.
		// This runs every frame (the yaw changes continuously), but it's cheap
		// now (raw buffer ops, no Godot bindings).
		UpdateDisplay(px, pz);
	}

	public override void _ExitTree()
	{
		if (_manager != null)
		{
			_manager.WaypointsChanged -= OnWaypointsChanged;
			_manager.MapSystemToggled -= OnMapSystemToggled;
			if (_manager.Renderer != null)
			{
				_manager.Renderer.TerrainChanged -= OnTerrainChanged;
			}
		}
	}

	// ------------------------------------------------------------------
	// Reading the slice from the shared map data (pushed by the world)
	// ------------------------------------------------------------------

	/// <summary>
	/// Reads the slice's tiles (RadiusTiles × RadiusTiles, centered on the player)
	/// from the shared <see cref="MapRenderer"/> into <see cref="_slice"/>. Each
	/// tile is an O(1) lookup (the world pushed the chunk's 2D height+material
	/// data), so reading the whole slice every frame is cheap — no scanning, no
	/// budget. This is the "map just displays the world" data path. Writes go
	/// through the slice's raw buffer (4 bytes/pixel) rather than per-pixel
	/// SetPixel, to avoid the per-pixel binding marshaling cost.
	/// </summary>
	private void ReadSlice(MapRenderer renderer)
	{
		var palette = _world.Palette;
		int size = RadiusTiles;
		const int CS = UI.ChunkMapData.Size; // 16
		// Image.GetData() returns a COPY of the pixel data — writing to it does
		// NOT modify the image. We must fill a managed buffer and push it into the
		// Image via SetData (one bulk copy per frame, no per-pixel marshaling).
		if (_sliceBuffer == null || _sliceBuffer.Length != size * size * 4)
		{
			_sliceBuffer = new byte[size * size * 4];
		}
		byte[] buf = _sliceBuffer;
		int row = size * 4;

		// Walk the slice tile-by-tile using the non-allocating packed color path
		// (no Vector2I allocation per tile, no Color allocation). The chunk coord
		// is computed per-tile via FloorDiv (a cheap int div + branch).
		for (int dz = 0; dz < size; dz++)
		{
			int wz = _sliceMinZ + dz;
			int cz = MapRenderer.FloorDiv(wz, CS);
			int lz = wz - cz * CS;
			int ro = dz * row;
			for (int dx = 0; dx < size; dx++)
			{
				int wx = _sliceMinX + dx;
				int cx = MapRenderer.FloorDiv(wx, CS);
				int lx = wx - cx * CS;

				uint packed;
				bool known = renderer.TryGetTileColorPacked(cx, cz, lx, lz, palette, out packed);
				int o = ro + dx * 4;
				if (known)
				{
					buf[o] = (byte)(packed & 0xFF);         // R
					buf[o + 1] = (byte)((packed >> 8) & 0xFF);   // G
					buf[o + 2] = (byte)((packed >> 16) & 0xFF);  // B
					buf[o + 3] = (byte)((packed >> 24) & 0xFF);  // A
				}
				else
				{
					buf[o] = 18;   // UnknownColor: (0.07, 0.08, 0.10, 1)
					buf[o + 1] = 20;
					buf[o + 2] = 26;
					buf[o + 3] = 255;
				}
			}
		}
		_slice.SetData(size, size, false, Image.Format.Rgba8, buf);
	}

	// ------------------------------------------------------------------
	// Display (rotate + overlays)
	// ------------------------------------------------------------------

	/// <summary>
	/// Rotates the terrain slice by the player's yaw into the circular display
	/// buffer, pushes it into the display Image, and draws the waypoint / player
	/// overlays.
	/// </summary>
	private void UpdateDisplay(int px, int pz)
	{
		float yaw = _player.Rotation.Y;
		int d = DiameterPx;

		// Rotate the slice into the display buffer (raw byte[] — no SetPixel).
		if (_displayBuf == null || _displayBuf.Length != d * d * 4)
		{
			_displayBuf = new byte[d * d * 4];
		}
		// The terrain is rotated by +yaw so that the player's facing points up AND
		// the map rotates the correct way: looking right (yaw decreases) turns the
		// map counter-clockwise, so world-north sweeps to the LEFT — matching the
		// compass and waypoint overlays (which are derived from the same yaw).
		RotateBlit(_sliceBuffer, RadiusTiles, RadiusTiles, _displayBuf, yaw, d);
		_displayImage.SetData(d, d, false, Image.Format.Rgba8, _displayBuf);

		// Overlays (drawn with SetPixel on the Image — only a few dozen calls).
		OverlayWaypoints(px, pz, yaw);
		OverlayPlayerCenter();

		float northAngleScreen = -yaw; // "N" rotates with the map
		PlaceCompass(_compassN, northAngleScreen, d);
		PlaceArrow(_arrow, northAngleScreen, d);

		_displayTexture.Update(_displayImage);
	}

	/// <summary>
	/// Rotates the source tile buffer by <paramref name="angle"/> (radians) and
	/// scales it to <paramref name="destSize"/>×<paramref name="destSize"/>,
	/// writing into <paramref name="destBuf"/> (a managed byte[] of size
	/// destSize²×4, Rgba8). Bilinear sampling for a smooth (non-pixelated)
	/// rotation. The result is CIRCULAR: pixels outside the inscribed circle
	/// are transparent (with a ~2px soft edge).
	///
	/// PERFORMANCE: operates entirely on managed byte[] buffers (no Godot
	/// SetPixel/GetPixel binding calls). The caller pushes destBuf into the
	/// Image via SetData once after this returns. ~25,600 pixels at 160px
	/// diameter, each doing 4 raw byte reads + 4 raw byte writes (no
	/// marshaling), so the whole rotation is well under 1ms.
	/// </summary>
	private static void RotateBlit(byte[] srcBuf, int srcW, int srcH, byte[] destBuf, float angle, int destSize)
	{
		float cosA = Mathf.Cos(angle);
		float sinA = Mathf.Sin(angle);
		float srcCx = srcW * 0.5f;
		float srcCy = srcH * 0.5f;
		float scale = destSize / (float)srcW;
		float destCx = destSize * 0.5f;
		float radius = destSize * 0.5f;
		float edge = 2f;
		int srcRow = srcW * 4;

		for (int dy = 0; dy < destSize; dy++)
		{
			float py = dy + 0.5f - destCx;
			float dys = (dy + 0.5f - destSize * 0.5f) / scale;
			float cosTerm = -sinA * dys;
			float sinTerm = cosA * dys;
			int destRow = dy * destSize * 4;

			for (int dx = 0; dx < destSize; dx++)
			{
				int po = destRow + dx * 4;
				float px = dx + 0.5f - destCx;
				float dist = Mathf.Sqrt(px * px + py * py);
				if (dist >= radius - edge)
				{
					float a = Mathf.Clamp((radius - edge - dist) / edge, 0f, 1f);
					destBuf[po] = 0;
					destBuf[po + 1] = 0;
					destBuf[po + 2] = 0;
					destBuf[po + 3] = (byte)(a * 255f);
					continue;
				}
				float dxs = (dx + 0.5f - destSize * 0.5f) / scale;
				float rx = cosA * dxs + cosTerm;
				float ry = sinA * dxs + sinTerm;
				float sx = rx + srcCx;
				float sy = ry + srcCy;
				if (sx < 0 || sy < 0 || sx >= srcW - 1 || sy >= srcH - 1)
				{
					destBuf[po] = 18;
					destBuf[po + 1] = 20;
					destBuf[po + 2] = 26;
					destBuf[po + 3] = 255;
					continue;
				}
				int x0 = (int)sx;
				int y0 = (int)sy;
				float fx = sx - x0;
				float fy = sy - y0;

				// Bilinear sample from the raw src buffer (4 bytes per pixel).
				int i00 = (y0 * srcW + x0) * 4;
				int i10 = (y0 * srcW + x0 + 1) * 4;
				int i01 = ((y0 + 1) * srcW + x0) * 4;
				int i11 = ((y0 + 1) * srcW + x0 + 1) * 4;

				float topR = (srcBuf[i00] + (srcBuf[i10] - srcBuf[i00]) * fx);
				float topG = (srcBuf[i00 + 1] + (srcBuf[i10 + 1] - srcBuf[i00 + 1]) * fx);
				float topB = (srcBuf[i00 + 2] + (srcBuf[i10 + 2] - srcBuf[i00 + 2]) * fx);
				float botR = (srcBuf[i01] + (srcBuf[i11] - srcBuf[i01]) * fx);
				float botG = (srcBuf[i01 + 1] + (srcBuf[i11 + 1] - srcBuf[i01 + 1]) * fx);
				float botB = (srcBuf[i01 + 2] + (srcBuf[i11 + 2] - srcBuf[i01 + 2]) * fx);

				destBuf[po] = (byte)(topR + (botR - topR) * fy);
				destBuf[po + 1] = (byte)(topG + (botG - topG) * fy);
				destBuf[po + 2] = (byte)(topB + (botB - topB) * fy);
				destBuf[po + 3] = 255;
			}
		}
	}

	/// <summary>
	/// Draws the waypoint markers (and their off-screen edge arrows) into the
	/// display image. A waypoint within the circle is a colored diamond; one
	/// outside the circle becomes a small arrow on the rim pointing toward it.
	/// </summary>
	private void OverlayWaypoints(int px, int pz, float yaw)
	{
		if (_manager?.Renderer == null)
		{
			return;
		}
		var waypoints = _manager.Renderer.Waypoints;
		float scale = DiameterPx / (float)RadiusTiles; // px per tile
		float radiusPx = DiameterPx * 0.5f;

		foreach (var wp in waypoints)
		{
			float dx = wp.Position.X - px;
			float dz = wp.Position.Z - pz;
			float cosA = Mathf.Cos(-yaw);
			float sinA = Mathf.Sin(-yaw);
			float rx = cosA * dx - sinA * dz;
			float ry = sinA * dx + cosA * dz;
			float screenX = DiameterPx * 0.5f + rx * scale;
			float screenY = DiameterPx * 0.5f + ry * scale;

			float offX = screenX - DiameterPx * 0.5f;
			float offY = screenY - DiameterPx * 0.5f;
			float distFromCenter = Mathf.Sqrt(offX * offX + offY * offY);

			if (distFromCenter <= radiusPx - 8f)
			{
				DrawDiamond(_displayImage, screenX, screenY, 5f, wp.Color);
			}
			else
			{
				float ang = Mathf.Atan2(offX, offY);
				float edgeX = DiameterPx * 0.5f + Mathf.Cos(ang) * (radiusPx - 12f);
				float edgeY = DiameterPx * 0.5f + Mathf.Sin(ang) * (radiusPx - 12f);
				DrawArrowAt(_displayImage, edgeX, edgeY, ang + Mathf.Pi * 0.5f, 8f, wp.Color);
			}
		}
	}

	/// <summary>Draws the player's center marker (a small white triangle pointing up).</summary>
	private void OverlayPlayerCenter()
	{
		DrawTriangle(_displayImage, DiameterPx * 0.5f, DiameterPx * 0.5f, 7f, 0f, Colors.White);
	}

	// ------------------------------------------------------------------
	// Compass + arrow placement
	// ------------------------------------------------------------------

	private void PlaceCompass(Label label, float angleFromTop, float diameter)
	{
		float r = diameter * 0.5f - 14f;
		float x = diameter * 0.5f + Mathf.Sin(angleFromTop) * r;
		float y = diameter * 0.5f - Mathf.Cos(angleFromTop) * r;
		label.Position = new Vector2(x - 10, y - 10);
	}

	private void PlaceArrow(Control arrow, float angleFromTop, float diameter)
	{
		float r = diameter * 0.5f - 6f;
		float x = diameter * 0.5f + Mathf.Sin(angleFromTop) * r;
		float y = diameter * 0.5f - Mathf.Cos(angleFromTop) * r;
		arrow.Position = new Vector2(x - 10, y - 10);
		arrow.Rotation = angleFromTop;
	}

	private static Control MakeArrow() => new ArrowTriangle();

	// ------------------------------------------------------------------
	// Image drawing helpers
	// ------------------------------------------------------------------

	private static void DrawDiamond(Image img, float cx, float cy, float r, Color color)
	{
		int x0 = Mathf.Max(0, (int)(cx - r));
		int x1 = Mathf.Min(img.GetWidth() - 1, (int)(cx + r));
		int y0 = Mathf.Max(0, (int)(cy - r));
		int y1 = Mathf.Min(img.GetHeight() - 1, (int)(cy + r));
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				if (Mathf.Abs(x - cx) + Mathf.Abs(y - cy) <= r)
				{
					img.SetPixel(x, y, color);
				}
			}
		}
	}

	private static void DrawTriangle(Image img, float cx, float cy, float r, float angle, Color color)
	{
		// The three corners of the triangle, offset from the center and rotated
		// by the given angle. (v0 is the "tip", v1 and v2 are the base.)
		var v0 = new Vector2(cx + Rotate(new Vector2(0, -r), angle).X, cy + Rotate(new Vector2(0, -r), angle).Y);
		var v1 = new Vector2(cx + Rotate(new Vector2(r * 0.87f, r * 0.5f), angle).X, cy + Rotate(new Vector2(r * 0.87f, r * 0.5f), angle).Y);
		var v2 = new Vector2(cx + Rotate(new Vector2(-r * 0.87f, r * 0.5f), angle).X, cy + Rotate(new Vector2(-r * 0.87f, r * 0.5f), angle).Y);
		float minX = Mathf.Min(v0.X, Mathf.Min(v1.X, v2.X)) - 1;
		float maxX = Mathf.Max(v0.X, Mathf.Max(v1.X, v2.X)) + 1;
		float minY = Mathf.Min(v0.Y, Mathf.Min(v1.Y, v2.Y)) - 1;
		float maxY = Mathf.Max(v0.Y, Mathf.Max(v1.Y, v2.Y)) + 1;
		int x0 = Mathf.Max(0, (int)minX);
		int x1 = Mathf.Min(img.GetWidth() - 1, (int)maxX);
		int y0 = Mathf.Max(0, (int)minY);
		int y1 = Mathf.Min(img.GetHeight() - 1, (int)maxY);
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				if (PointInTriangle(new Vector2(x, y), v0, v1, v2))
				{
					img.SetPixel(x, y, color);
				}
			}
		}
	}

	private static Vector2 Rotate(Vector2 v, float angle)
	{
		float c = Mathf.Cos(angle);
		float s = Mathf.Sin(angle);
		return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
	}

	private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
	{
		float d1 = Sign(p, a, b);
		float d2 = Sign(p, b, c);
		float d3 = Sign(p, c, a);
		bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
		bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
		return !(hasNeg && hasPos);
	}

	private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
		=> (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);

	private static void DrawArrowAt(Image img, float cx, float cy, float angle, float size, Color color)
	{
		DrawTriangle(img, cx, cy, size, angle, color);
	}

	private void OnWaypointsChanged()
	{
		// The display is rebuilt every frame in _Process, so a waypoint change is
		// picked up automatically.
	}
}

/// <summary>
/// A Control that draws a small filled triangle (the minimap's player-direction
/// arrow). Rotated by the minimap to point along the player's facing.
/// </summary>
public partial class ArrowTriangle : Control
{
	public override void _Ready()
	{
		CustomMinimumSize = new Vector2(20, 20);
		MouseFilter = Control.MouseFilterEnum.Ignore;
	}

	public override void _Draw()
	{
		DrawColoredPolygon(new[]
		{
			new Vector2(10, 2),
			new Vector2(18, 16),
			new Vector2(2, 16),
		}, new Color(1, 1, 1, 0.95f));
	}
}
