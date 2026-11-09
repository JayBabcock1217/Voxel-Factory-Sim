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
/// Architecture (the performance story):
///   - The terrain pixels come from the SHARED <see cref="MapRenderer"/> owned
///     by the <c>WaypointManager</c> node (the same source the map window uses),
///     so the minimap never re-samples terrain on its own.
///   - The minimap's own per-frame work is tiny: it drains a small budget of
///     dirty tiles into the shared image (so the map fills in smoothly as chunks
///     generate around the player) and then blits the relevant slice of that
///     image into a small circular display texture (rotated to match the player
///     yaw). A single <see cref="TextureRect"/> (with a circular clip) draws the
///     result — one draw call.
///   - The player is always at the center (the circle follows the player), so
///     there is no panning; the map just rotates and re-centers.
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
	[Export] public int DiameterPx { get; set; } = 160;

	/// <summary>Screen margin from the top-left corner.</summary>
	[Export] public int MarginPx { get; set; } = 12;

	/// <summary>
	/// How many world tiles the circle spans at its widest (the map's zoom).
	/// The display texture is this many tiles wide; each tile is (DiameterPx /
	/// RadiusTiles) px on screen.
	/// </summary>
	[Export] public int RadiusTiles { get; set; } = 64;

	/// <summary>
	/// How many dirty tiles the minimap re-samples per frame (spread out so the
	/// fill-in is smooth). Higher = faster fill-in, more per-frame cost.
	/// </summary>
	[Export] public int SampleBudgetPerFrame { get; set; } = 512;

	/// <summary>
	/// How often (seconds) the minimap re-checks which world chunks are loaded
	/// and marks new ones dirty. Chunks generate in a ring around the player,
	/// so a modest cadence keeps the map filling without spamming the world.
	/// Kept short so the map fills in quickly after a re-center.
	/// </summary>
	[Export] public float ChunkRescanIntervalSec { get; set; } = 0.25f;

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

	// The terrain slice source (a copy of the relevant part of the shared image,
	// rotated each frame into the display image).
	private Image _slice;

	private float _lastChunkRescanSec;
	private bool _worldReady;
	private int _lastRendererCenterX = int.MinValue;
	private int _lastRendererCenterZ = int.MinValue;

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

		// A circular clip: the minimap's display image is built as a CIRCLE (the
		// RotateBlit writes transparent pixels outside the circle), so the square
		// panel shows a round map. (Godot's Control has no canvas-Shader property
		// in this binding, so a per-pixel CPU mask is the reliable way.)

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
			}
		}
		if (_player == null)
		{
			_player = GetTree().GetFirstNodeInGroup("player") as Player;
		}
		if (_world == null)
		{
			_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;
		}

		if (_world == null || _player == null || _manager == null)
		{
			return;
		}

		// Make sure the shared renderer exists. It is centered on the PLAYER,
		// and — crucially — RE-CENTERED as the player walks (below), so the
		// image always covers the area around the player. Re-centering resets
		// the image to "unknown" and triggers a fast re-sample; the radius is
		// large enough that the visible circle is already sampled, so the map
		// never appears empty (it just re-fills the fresh outer ring).
		var px = (int)Mathf.Floor(_player.GlobalPosition.X);
		var pz = (int)Mathf.Floor(_player.GlobalPosition.Z);

		int desiredSize = Mathf.Max(RadiusTiles * 2 + 8, 64);
		if (!_worldReady)
		{
			_manager.EnsureRenderer(desiredSize, px, pz);
			_worldReady = true;
			_lastRendererCenterX = px;
			_lastRendererCenterZ = pz;
		}
		else
		{
			// Re-center the renderer when the player has moved far enough that
			// they are near the image edge (so the circle always has terrain
			// around the center). A ~half-image move threshold keeps re-centers
			// infrequent (a few per long walk), not every frame.
			int imgHalf = _manager.Renderer.Size / 2;
			if (Mathf.Abs(px - _lastRendererCenterX) > imgHalf * 2
				|| Mathf.Abs(pz - _lastRendererCenterZ) > imgHalf * 2)
			{
				_manager.EnsureRenderer(desiredSize, px, pz);
				_lastRendererCenterX = px;
				_lastRendererCenterZ = pz;
			}
		}

		MapRenderer renderer = _manager.Renderer;
		if (renderer == null)
		{
			return;
		}

		// Periodically scan the loaded chunks around the player and mark any new
		// ones dirty (so the map fills in as the player walks / chunks generate).
		float t = (float)Time.GetTicksMsec() * 1e-3f;
		if (t - _lastChunkRescanSec >= ChunkRescanIntervalSec)
		{
			_lastChunkRescanSec = t;
			ScanAndMarkDirtyChunks(renderer, px, pz);
		}

		// Drain a small budget of dirty tiles into the shared image.
		renderer.SampleBudget(_world, SampleBudgetPerFrame);

		// Blit the slice around the player into the display (rotated).
		UpdateDisplay(renderer, px, pz);
	}

	public override void _ExitTree()
	{
		if (_manager != null)
		{
			_manager.WaypointsChanged -= OnWaypointsChanged;
		}
	}

	// ------------------------------------------------------------------
	// Chunk scanning
	// ------------------------------------------------------------------

	/// <summary>
	/// Looks at the world chunks within the minimap's radius and marks any that
	/// are loaded (but not yet sampled by the renderer) as dirty, so the map
	/// fills in. The radius is the circle's tile radius in chunks, plus a small
	/// margin.
	/// </summary>
	private void ScanAndMarkDirtyChunks(MapRenderer renderer, int px, int pz)
	{
		if (_world == null)
		{
			return;
		}
		int chunkSize = _world.ChunkSizeXZ;
		int radiusChunks = RadiusTiles / chunkSize + 1;
		int pcx = ChunkCoordOf(px, chunkSize);
		int pcz = ChunkCoordOf(pz, chunkSize);
		for (int dx = -radiusChunks; dx <= radiusChunks; dx++)
		{
			for (int dz = -radiusChunks; dz <= radiusChunks; dz++)
			{
				var c = new Vector2I(pcx + dx, pcz + dz);
				// Only mark a chunk dirty when it is actually loaded now (so an
				// unloaded chunk isn't wasted on); the renderer skips unloaded
				// ones anyway, but this avoids queueing hundreds of not-ready
				// chunks.
				if (_world.GetChunkData(c) != null)
				{
					renderer.MarkDirty(c);
				}
			}
		}
	}

	private static int ChunkCoordOf(int world, int chunkSize)
	{
		// Floor division that matches the world's chunk-coord convention.
		int q = world / chunkSize;
		if (world < 0 && world % chunkSize != 0)
		{
			q--;
		}
		return q;
	}

	// ------------------------------------------------------------------
	// Display (the per-frame blit + rotate + overlay)
	// ------------------------------------------------------------------

	/// <summary>
	/// Copies the relevant slice of the shared terrain image (centered on the
	/// player), rotates it by the player's yaw, scales it to the display size,
	/// and draws the waypoint / player overlays. The result is written into
	/// <see cref="_displayImage"/> (the TextureRect's texture).
	/// </summary>
	private void UpdateDisplay(MapRenderer renderer, int px, int pz)
	{
		// The slice: RadiusTiles × RadiusTiles world tiles centered on the player.
		int half = RadiusTiles / 2;
		int sliceMinX = px - half;
		int sliceMinZ = pz - half;

		// Copy the slice out of the shared image (only the in-bounds part).
		var src = renderer.Image;
		int sw = src.GetWidth();
		int sh = src.GetHeight();
		for (int sy = 0; sy < RadiusTiles; sy++)
		{
			for (int sx = 0; sx < RadiusTiles; sx++)
			{
				int wx = sliceMinX + sx;
				int wz = sliceMinZ + sy;
				int ix = wx - renderer.MinX;
				int iz = wz - renderer.MinZ;
				Color c = (ix >= 0 && ix < sw && iz >= 0 && iz < sh)
					? src.GetPixel(ix, iz)
					: new Color(0.07f, 0.08f, 1.0f, 1f); // out of range = unknown
				_slice.SetPixel(sx, sy, c);
			}
		}

		// Rotation: the map rotates so the player's FACING is up. The player's
		// yaw (body Y rotation, radians) is the angle from +Z (forward) toward
		// +X. We rotate the terrain by -yaw so the forward direction points to
		// the top of the screen. (A positive yaw turns the body toward +X, so
		// the world must rotate the opposite way to keep "forward" up.)
		float yaw = _player.Rotation.Y;
		float angle = -yaw; // radians, clockwise positive in screen space

		// Rotate + scale the slice into the display image.
		RotateBlit(_slice, _displayImage, angle, DiameterPx);

		// Overlay: waypoints (and the player center marker).
		OverlayWaypoints(px, pz, yaw);
		OverlayPlayerCenter();

		// Rotate the "N" compass label and the direction arrow to match the map.
		// The north direction in world space is -Z (the world's "up" on the map
		// is -Z when facing north). After rotating the map by -yaw, the north
		// label sits at angle = -yaw from the top.
		float northAngleScreen = -yaw; // radians from top
		PlaceCompass(_compassN, northAngleScreen, DiameterPx);
		PlaceArrow(_arrow, northAngleScreen, DiameterPx);

		// Push the updated image to the texture.
		_displayTexture.Update(_displayImage);
	}

	/// <summary>
	/// Rotates the source tile image by <paramref name="angle"/> (radians) and
	/// scales it to <paramref name="destSize"/>×<paramref name="destSize"/>,
	/// writing into <paramref name="dest"/>. Uses bilinear sampling for a smooth
	/// (non-pixelated) rotation. The result is CIRCULAR: pixels outside the
	/// inscribed circle are written as fully transparent (with a ~2px soft edge),
	/// so the square panel shows a round map.
	/// </summary>
	private static void RotateBlit(Image src, Image dest, float angle, int destSize)
	{
		float cosA = Mathf.Cos(angle);
		float sinA = Mathf.Sin(angle);
		int srcW = src.GetWidth();
		int srcH = src.GetHeight();
		float srcCx = srcW * 0.5f;
		float srcCy = srcH * 0.5f;
		// Scale factor: map the source (RadiusTiles tiles) to destSize px.
		float scale = destSize / (float)srcW;
		float destCx = destSize * 0.5f;
		float radius = destSize * 0.5f;
		float edge = 2f; // soft-edge width in px

		for (int dy = 0; dy < destSize; dy++)
		{
			for (int dx = 0; dx < destSize; dx++)
			{
				// Circular mask: distance from the center.
				float px = dx + 0.5f - destCx;
				float py = dy + 0.5f - destCx;
				float dist = Mathf.Sqrt(px * px + py * py);
				if (dist >= radius - edge)
				{
					// Outside (or on the soft edge of) the circle: transparent.
					float a = Mathf.Clamp((radius - edge - dist) / edge, 0f, 1f);
					dest.SetPixel(dx, dy, new Color(0, 0, 0, a));
					continue;
				}
				// Destination pixel center in dest space, centered.
				float dxs = (dx + 0.5f - destSize * 0.5f) / scale;
				float dys = (dy + 0.5f - destSize * 0.5f) / scale;
				// Rotate back by -angle to find the source pixel.
				float rx = cosA * dxs - sinA * dys;
				float ry = sinA * dxs + cosA * dys;
				float sx = rx + srcCx;
				float sy = ry + srcCy;
				if (sx < 0 || sy < 0 || sx >= srcW - 1 || sy >= srcH - 1)
				{
					// Outside the sampled terrain (shouldn't happen inside the
					// circle, but guard): use the "unknown" color.
					dest.SetPixel(dx, dy, new Color(0.07f, 0.08f, 0.10f, 1f));
					continue;
				}
				// Bilinear sample.
				int x0 = (int)sx;
				int y0 = (int)sy;
				float fx = sx - x0;
				float fy = sy - y0;
				var c00 = src.GetPixel(x0, y0);
				var c10 = src.GetPixel(x0 + 1, y0);
				var c01 = src.GetPixel(x0, y0 + 1);
				var c11 = src.GetPixel(x0 + 1, y0 + 1);
				float top = c00.R + (c10.R - c00.R) * fx;
				float topG = c00.G + (c10.G - c00.G) * fx;
				float topB = c00.B + (c10.B - c00.B) * fx;
				float bot = c01.R + (c11.R - c01.R) * fx;
				float botG = c01.G + (c11.G - c01.G) * fx;
				float botB = c01.B + (c11.B - c01.B) * fx;
				dest.SetPixel(dx, dy, new Color(
					top + (bot - top) * fy,
					topG + (botG - topG) * fy,
					topB + (botB - topB) * fy,
					1f));
			}
		}
	}

	/// <summary>
	/// Draws the waypoint markers (and their off-screen edge arrows) into the
	/// display image. A waypoint within the circle is drawn as a colored
	/// diamond + label; one outside the circle becomes a small arrow on the
	/// rim pointing toward it.
	/// </summary>
	private void OverlayWaypoints(int px, int pz, float yaw)
	{
		if (_manager == null || _manager.Renderer == null)
		{
			return;
		}
		var waypoints = _manager.Renderer.Waypoints;
		float half = RadiusTiles / 2f;
		float scale = DiameterPx / (float)RadiusTiles; // px per tile
		float radiusPx = DiameterPx * 0.5f;

		foreach (var wp in waypoints)
		{
			// World delta from the player (the circle center).
			float dx = wp.Position.X - px;
			float dz = wp.Position.Z - pz;
			// Rotate into map space: the map is rotated by -yaw, so a world
			// vector (dx, dz) becomes (screenX, screenY) after the same
			// rotation applied to the terrain. The terrain's world +X maps to
			// the rotated frame; we apply the same rotation matrix.
			float cosA = Mathf.Cos(-yaw);
			float sinA = Mathf.Sin(-yaw);
			// World X axis -> screen; world Z axis -> screen down. We treat
			// screenX = worldX, screenY = worldZ in the pre-rotation tile space,
			// then rotate by -yaw.
			float mx = dx;
			float mz = dz;
			float rx = cosA * mx - sinA * mz;
			float ry = sinA * mx + cosA * mz;
			float screenX = DiameterPx * 0.5f + rx * scale;
			float screenY = DiameterPx * 0.5f + ry * scale;

			float distFromCenter = Mathf.Sqrt(
				(screenX - DiameterPx * 0.5f) * (screenX - DiameterPx * 0.5f) +
				(screenY - DiameterPx * 0.5f) * (screenY - DiameterPx * 0.5f));

			if (distFromCenter <= radiusPx - 8f)
			{
				// In view: draw a diamond + a short label.
				DrawDiamond(_displayImage, screenX, screenY, 5f, wp.Color);
				DrawLabel(_displayImage, screenX, screenY - 8f, wp.Name, wp.Color);
			}
			else
			{
				// Off-screen: draw a small arrow on the rim pointing toward it.
				float ang = Mathf.Atan2(screenX - DiameterPx * 0.5f, screenY - DiameterPx * 0.5f);
				float edgeX = DiameterPx * 0.5f + Mathf.Cos(ang) * (radiusPx - 12f);
				float edgeY = DiameterPx * 0.5f + Mathf.Sin(ang) * (radiusPx - 12f);
				DrawArrowAt(_displayImage, edgeX, edgeY, ang + Mathf.Pi * 0.5f, 8f, wp.Color);
			}
		}
	}

	/// <summary>Draws the player's center marker (a small white triangle pointing up).</summary>
	private void OverlayPlayerCenter()
	{
		float cx = DiameterPx * 0.5f;
		float cy = DiameterPx * 0.5f;
		// A small white triangle pointing up (the player, always at center).
		DrawTriangle(_displayImage, cx, cy, 7f, 0f, Colors.White);
	}

	// ------------------------------------------------------------------
	// Compass + arrow placement (in the panel, not the image)
	// ------------------------------------------------------------------

	/// <summary>Places the "N" label on the rim at the given screen angle (radians from top).</summary>
	private void PlaceCompass(Label label, float angleFromTop, float diameter)
	{
		float r = diameter * 0.5f - 14f;
		float x = diameter * 0.5f + Mathf.Sin(angleFromTop) * r;
		float y = diameter * 0.5f - Mathf.Cos(angleFromTop) * r;
		label.Position = new Vector2(x - 10, y - 10);
	}

	/// <summary>Places the direction arrow on the rim at the given screen angle.</summary>
	private void PlaceArrow(Control arrow, float angleFromTop, float diameter)
	{
		float r = diameter * 0.5f - 6f;
		float x = diameter * 0.5f + Mathf.Sin(angleFromTop) * r;
		float y = diameter * 0.5f - Mathf.Cos(angleFromTop) * r;
		arrow.Position = new Vector2(x - 10, y - 10);
		arrow.Rotation = angleFromTop;
	}

	private static Control MakeArrow()
	{
		// A small triangle (the player's facing) drawn with a ColorRect-backed
		// polygon. We use a simple Polygon2D-free approach: a Control that draws
		// a triangle in _Draw.
		return new ArrowTriangle();
	}

	// ------------------------------------------------------------------
	// Image drawing helpers (cheap, no nodes)
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
				float dx = x - cx;
				float dy = y - cy;
				// Diamond (Manhattan) distance.
				if (Mathf.Abs(dx) + Mathf.Abs(dy) <= r)
				{
					img.SetPixel(x, y, color);
				}
			}
		}
	}

	private static void DrawTriangle(Image img, float cx, float cy, float r, float angle, Color color)
	{
		// A filled triangle pointing in `angle` (radians, 0 = up/-Y).
		var v0 = new Vector2(0, -r);
		var v1 = new Vector2(r * 0.87f, r * 0.5f);
		var v2 = new Vector2(-r * 0.87f, r * 0.5f);
		DrawFilledTriangle(img, new Vector2(cx, cy), Rotate(v0, angle), Rotate(v1, angle), Rotate(v2, angle), color);
	}

	private static Vector2 Rotate(Vector2 v, float angle)
	{
		float c = Mathf.Cos(angle);
		float s = Mathf.Sin(angle);
		return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
	}

	private static void DrawFilledTriangle(Image img, Vector2 c, Vector2 a, Vector2 b, Vector2 d, Color color)
	{
		float minX = Mathf.Min(c.X, Mathf.Min(a.X, b.X)) - 1;
		float maxX = Mathf.Max(c.X, Mathf.Max(a.X, b.X)) + 1;
		float minY = Mathf.Min(c.Y, Mathf.Min(a.Y, b.Y)) - 1;
		float maxY = Mathf.Max(c.Y, Mathf.Max(a.Y, b.Y)) + 1;
		int x0 = Mathf.Max(0, (int)minX);
		int x1 = Mathf.Min(img.GetWidth() - 1, (int)maxX);
		int y0 = Mathf.Max(0, (int)minY);
		int y1 = Mathf.Min(img.GetHeight() - 1, (int)maxY);
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				if (PointInTriangle(new Vector2(x, y), c, a, b))
				{
					img.SetPixel(x, y, color);
				}
				// (d is unused; the triangle is c,a,b.)
			}
		}
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
	{
		return (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
	}

	private static void DrawArrowAt(Image img, float cx, float cy, float angle, float size, Color color)
	{
		DrawTriangle(img, cx, cy, size, angle, color);
	}

	private static void DrawLabel(Image img, float cx, float cy, string text, Color color)
	{
		// A tiny text label: draw the text using a 1px "glyph" is overkill;
		// instead draw a small colored dot above the diamond as a label marker
		// (the full name is shown in the map window's list / on hover).
		int x = Mathf.Clamp((int)cx, 0, img.GetWidth() - 1);
		int y = Mathf.Clamp((int)cy, 0, img.GetHeight() - 1);
		img.SetPixel(x, y, color);
	}

	private void OnWaypointsChanged()
	{
		// The display is rebuilt every frame in _Process, so a waypoint change is
		// picked up automatically; no extra work needed here. (Hook kept for
		// clarity / future immediate redraws.)
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
		var c = new Color(1, 1, 1, 0.95f);
		DrawColoredPolygon(new[]
		{
			new Vector2(10, 2),
			new Vector2(18, 16),
			new Vector2(2, 16),
		}, c);
	}
}
