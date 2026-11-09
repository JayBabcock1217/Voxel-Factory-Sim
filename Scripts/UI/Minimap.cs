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
/// Performance / correctness: each frame the minimap samples a small square of
/// world tiles AROUND THE PLAYER directly from the live <see cref="VoxelWorld"/>
/// (via the shared <see cref="MapRenderer.SampleRegion"/>), spread over a
/// per-frame budget so it fills in smoothly and never hitches. Because the
/// sampled region is always re-centered on the player, the circle always has
/// fresh terrain around the center — there is no stale shared image to go out of
/// date. The result is rotated by the player's yaw into a circular display
/// texture (one draw call on a TextureRect).
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
	/// Each tile is (DiameterPx / RadiusTiles) px on screen.
	/// </summary>
	[Export] public int RadiusTiles { get; set; } = 64;

	/// <summary>
	/// How many terrain tiles to sample per frame (spread out so the fill-in is
	/// smooth). Higher = faster fill-in, more per-frame cost.
	/// </summary>
	[Export] public int SampleBudgetPerFrame { get; set; } = 512;

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

	// The terrain slice (RadiusTiles × RadiusTiles world tiles), sampled directly
	// from the world each frame (budget-limited).
	private Image _slice;

	// Tracks the slice's world origin; when the player moves, the origin moves
	// with them (a fresh sample of the new region).
	private int _sliceMinX = int.MinValue;
	private int _sliceMinZ = int.MinValue;
	private bool _worldReady;

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

		if (_world == null || _player == null || _manager?.Renderer == null)
		{
			return;
		}

		var px = (int)Mathf.Floor(_player.GlobalPosition.X);
		var pz = (int)Mathf.Floor(_player.GlobalPosition.Z);

		if (!_worldReady)
		{
			// First usable frame: position the slice origin on the player and
			// start sampling. (The budget spreads the fill-in over a few frames.)
			_sliceMinX = px - RadiusTiles / 2;
			_sliceMinZ = pz - RadiusTiles / 2;
			_slice.Fill(MapRenderer.UnknownColor);
			_worldReady = true;
		}
		else
		{
			// Keep the slice centered on the player. When the origin moves, reset
			// the slice to unknown so the new region samples fresh (no stale data
			// from the old location). A small threshold avoids resetting (and a
			// brief blank) on sub-tile jitter.
			int wantMinX = px - RadiusTiles / 2;
			int wantMinZ = pz - RadiusTiles / 2;
			if (wantMinX != _sliceMinX || wantMinZ != _sliceMinZ)
			{
				_sliceMinX = wantMinX;
				_sliceMinZ = wantMinZ;
				_slice.Fill(MapRenderer.UnknownColor);
			}
		}

		// Sample the slice's region directly from the world (budget-limited).
		_manager.Renderer.SampleRegion(_world, _slice, _sliceMinX, _sliceMinZ, SampleBudgetPerFrame);

		// Rotate the slice into the circular display and draw the overlays.
		UpdateDisplay(px, pz);
	}

	public override void _ExitTree()
	{
		if (_manager != null)
		{
			_manager.WaypointsChanged -= OnWaypointsChanged;
		}
	}

	// ------------------------------------------------------------------
	// Display (rotate + overlays)
	// ------------------------------------------------------------------

	/// <summary>
	/// Rotates the terrain slice by the player's yaw into the circular display
	/// image and draws the waypoint / player overlays.
	/// </summary>
	private void UpdateDisplay(int px, int pz)
	{
		float yaw = _player.Rotation.Y;
		float angle = -yaw; // rotate so the player's facing points up

		RotateBlit(_slice, _displayImage, angle, DiameterPx);

		OverlayWaypoints(px, pz, yaw);
		OverlayPlayerCenter();

		float northAngleScreen = -yaw; // "N" rotates with the map
		PlaceCompass(_compassN, northAngleScreen, DiameterPx);
		PlaceArrow(_arrow, northAngleScreen, DiameterPx);

		_displayTexture.Update(_displayImage);
	}

	/// <summary>
	/// Rotates the source tile image by <paramref name="angle"/> (radians) and
	/// scales it to <paramref name="destSize"/>×<paramref name="destSize"/>,
	/// writing into <paramref name="dest"/>. Bilinear sampling for a smooth
	/// (non-pixelated) rotation. The result is CIRCULAR: pixels outside the
	/// inscribed circle are transparent (with a ~2px soft edge).
	/// </summary>
	private static void RotateBlit(Image src, Image dest, float angle, int destSize)
	{
		float cosA = Mathf.Cos(angle);
		float sinA = Mathf.Sin(angle);
		int srcW = src.GetWidth();
		int srcH = src.GetHeight();
		float srcCx = srcW * 0.5f;
		float srcCy = srcH * 0.5f;
		float scale = destSize / (float)srcW;
		float destCx = destSize * 0.5f;
		float radius = destSize * 0.5f;
		float edge = 2f;

		for (int dy = 0; dy < destSize; dy++)
		{
			for (int dx = 0; dx < destSize; dx++)
			{
				float px = dx + 0.5f - destCx;
				float py = dy + 0.5f - destCx;
				float dist = Mathf.Sqrt(px * px + py * py);
				if (dist >= radius - edge)
				{
					float a = Mathf.Clamp((radius - edge - dist) / edge, 0f, 1f);
					dest.SetPixel(dx, dy, new Color(0, 0, 0, a));
					continue;
				}
				float dxs = (dx + 0.5f - destSize * 0.5f) / scale;
				float dys = (dy + 0.5f - destSize * 0.5f) / scale;
				float rx = cosA * dxs - sinA * dys;
				float ry = sinA * dxs + cosA * dys;
				float sx = rx + srcCx;
				float sy = ry + srcCy;
				if (sx < 0 || sy < 0 || sx >= srcW - 1 || sy >= srcH - 1)
				{
					dest.SetPixel(dx, dy, new Color(0.07f, 0.08f, 0.10f, 1f));
					continue;
				}
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
		var v0 = Rotate(new Vector2(0, -r), angle);
		var v1 = Rotate(new Vector2(r * 0.87f, r * 0.5f), angle);
		var v2 = Rotate(new Vector2(-r * 0.87f, r * 0.5f), angle);
		var c = new Vector2(cx, cy);
		float minX = Mathf.Min(c.X, Mathf.Min(v0.X, Mathf.Min(v1.X, v2.X))) - 1;
		float maxX = Mathf.Max(c.X, Mathf.Max(v0.X, Mathf.Max(v1.X, v2.X))) + 1;
		float minY = Mathf.Min(c.Y, Mathf.Min(v0.Y, Mathf.Min(v1.Y, v2.Y))) - 1;
		float maxY = Mathf.Max(c.Y, Mathf.Max(v0.Y, Mathf.Max(v1.Y, v2.Y))) + 1;
		int x0 = Mathf.Max(0, (int)minX);
		int x1 = Mathf.Min(img.GetWidth() - 1, (int)maxX);
		int y0 = Mathf.Max(0, (int)minY);
		int y1 = Mathf.Min(img.GetHeight() - 1, (int)maxY);
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				if (PointInTriangle(new Vector2(x, y), c, v0, v1))
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
