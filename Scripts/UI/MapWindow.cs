using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The full interactive MAP WINDOW, toggled with the M key. A large panel that
/// shows the same shared terrain image the minimap uses, but with full
/// interaction:
///   - ZOOM: scroll wheel (zooms around the mouse).
///   - PAN: left-drag to pan the view.
///   - WAYPOINTS: with "place mode" on, click to drop a named waypoint (the name
///     is entered in a small input box that appears under the map); select a
///     waypoint (click it) and press Delete to remove it.
///   - NAVIGATE: clicking a waypoint in the side list (or double-clicking it on
///     the map) centers the view on it.
///   - ENTITIES: the player (arrow) and nearby zombies / hives are drawn as dots.
///   - LIST PANEL: a side panel lists every waypoint (name + distance from the
///     player) with click-to-focus and a remove button.
///
/// The terrain pixels come from the SHARED <see cref="MapRenderer"/> (owned by
/// the <c>WaypointManager</c>) — the SAME data the minimap displays. The world
/// PUSHES each chunk's 2D height+material data to the renderer as chunks
/// generate / get edited, so the map window (like the minimap) never scans the
/// world: it just reads <see cref="MapRenderer.GetTileColor"/> per visible tile
/// (an O(1) lookup) and blits it at the current zoom. It differs from the
/// minimap only in that the player can PAN (drag) and ZOOM (wheel) the view.
/// When closed it does nothing.
///
/// The node builds its own <see cref="CanvasLayer"/> in <c>_Ready</c> (matching
/// the project's code-built UI pattern) and is added to the scene by
/// <c>Scenes/Main.tscn</c>. While open it releases the mouse (so the user can
/// click / drag) and freezes player input; closing re-captures the mouse.
/// </summary>
public partial class MapWindow : Node
{
	// ------------------------------------------------------------------
	// Tunables
	// ------------------------------------------------------------------

	/// <summary>The key that toggles the map window. Default M.</summary>
	[Export] public Key ToggleKey { get; set; } = Key.M;

	/// <summary>Panel size as a fraction of the viewport (0..1).</summary>
	[Export] public float PanelWidthFrac { get; set; } = 0.72f;

	/// <summary>Panel height as a fraction of the viewport (0..1).</summary>
	[Export] public float PanelHeightFrac { get; set; } = 0.8f;

	/// <summary>Minimum zoom (pixels per world tile). 1 = one tile = one pixel.</summary>
	[Export] public float MinZoom { get; set; } = 1f;

	/// <summary>Maximum zoom (pixels per world tile).</summary>
	[Export] public float MaxZoom { get; set; } = 32f;

	/// <summary>Initial zoom when the map opens.</summary>
	[Export] public float InitialZoom { get; set; } = 3f;

	// ------------------------------------------------------------------
	// State
	// ------------------------------------------------------------------

	private CanvasLayer _layer;
	private Panel _panel;
	private TextureRect _mapRect;
	private Label _title;
	private Label _coordsLabel;
	private Button _placeToggle;
	private LineEdit _nameInput;
	private Label _nameInputHint;
	private Godot.HBoxContainer _nameRow;
	private VBoxContainer _listPanel;
	private Label _listHeader;
	private Button _closeHint;

	private WaypointManager _manager;
	private Player _player;
	private VoxelWorld _world;

	private bool _open;
	private bool _placeMode;
	private long _selectedWaypointId = -1;

	/// <summary>
	/// True while the map window is open. Exposed so other systems (notably the
	/// <see cref="FactorySim.Voxel.VoxelTool"/> hotbar / world-input handler) can
	/// know to SUSPEND their world input while the map has the mouse and the
	/// player's attention — e.g. hotbar scrolling must not cycle the hotbar
	/// while the map is open.
	/// </summary>
	public bool IsOpen => _open;

	// View: the world tile at the CENTER of the panel, and pixels-per-tile zoom.
	private Vector2 _viewCenter = new Vector2(0, 0);
	private float _zoom = 3f;

	// Panning drag state.
	private bool _panning;
	private Vector2 _panStartScreen;
	private Vector2 _panStartCenter;

	// The display image (the panel's texture; rebuilt each frame while open).
	private Image _displayImage;
	private ImageTexture _displayTexture;

	// The list rows (rebuilt on waypoint change / open).
	private readonly List<WaypointRow> _rows = new();

	private int _displayW;
	private int _displayH;
	private Vector2 _lastVp;
	private bool _mouseNotOurs;

	// Reusable managed buffer for the display image's pixel data (filled in
	// RebuildDisplay, pushed into the Image via SetData). Avoids per-frame
	// allocation. Image.GetData() returns a copy, so we can't write to it.
	private byte[] _displayBuf;

	// Dirty flag: set when the terrain data changes (a chunk generates, gets
	// edited, or is removed), the view is panned, or the zoom changes. The
	// display is only rebuilt when this is true — the map data is persistent
	// and only changes on chunk events, so we don't rebuild every frame.
	private bool _displayDirty = true;

	// The view state the display was last built for (to detect pan/zoom changes).
	private Vector2 _lastViewCenter;
	private float _lastZoom;

	// A row in the waypoint list panel: a name label + distance label + focus/remove.
	private sealed class WaypointRow
	{
		public long WaypointId;
		public HBoxContainer Box;
		public Label NameLabel;
		public Label DistLabel;
		public Button FocusButton;
		public Button RemoveButton;
	}

	// ------------------------------------------------------------------
	// Lifecycle
	// ------------------------------------------------------------------

	public override void _Ready()
	{
		AddToGroup("map_window");
		_layer = new CanvasLayer { Layer = 110, Visible = false };
		AddChild(_layer);

		_manager = GetTree().GetFirstNodeInGroup("waypoint_manager") as WaypointManager;
		_player = GetTree().GetFirstNodeInGroup("player") as Player;
		_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;

		if (_manager != null)
		{
			_manager.MapSystemToggled += OnMapSystemToggled;
		}

		BuildUI();
		SetOpen(false);
	}

	private void OnMapSystemToggled()
	{
		// If the map system was just disabled while the map is open, close it.
		if (_open && _manager != null && !_manager.IsMapSystemEnabled)
		{
			SetOpen(false);
		}
	}

	public override void _Process(double delta)
	{
		// Lazy re-fetch (the tree may not be fully up at _Ready).
		if (_manager == null)
		{
			_manager = GetTree().GetFirstNodeInGroup("waypoint_manager") as WaypointManager;
			if (_manager != null)
			{
				_manager.MapSystemToggled += OnMapSystemToggled;
				if (_manager.Renderer != null)
				{
					_manager.Renderer.TerrainChanged += OnTerrainChanged;
				}
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

		if (!_open || _world == null || _player == null || _manager == null)
		{
			return;
		}

		// Re-layout if the viewport was resized while the map is open.
		Vector2 vp = GetViewport().GetVisibleRect().Size;
		if (Mathf.Abs(vp.X - _lastVp.X) > 1f || Mathf.Abs(vp.Y - _lastVp.Y) > 1f)
		{
			_lastVp = vp;
			LayoutPanel();
			_displayDirty = true;
		}

		// Detect pan/zoom changes (the view state the display was built for).
		if (_viewCenter != _lastViewCenter || _zoom != _lastZoom)
		{
			_lastViewCenter = _viewCenter;
			_lastZoom = _zoom;
			_displayDirty = true;
		}

		var renderer = _manager.Renderer;
		if (renderer == null)
		{
			return;
		}

		// Rebuild the terrain tiles ONLY when the terrain data changed, the view
		// was panned, or the zoom changed. The map data is persistent — it only
		// changes on chunk events, so we don't re-blit the tiles every frame.
		if (_displayDirty)
		{
			RebuildDisplay(renderer);
			_displayDirty = false;
		}

		// Draw the dynamic overlays (player arrow, waypoint markers) every frame
		// so the player arrow tracks the player smoothly. This is cheap (a few
		// dozen SetPixel calls, no terrain tile blit).
		DrawOverlays();
	}

	private void OnTerrainChanged() => _displayDirty = true;

	public override void _ExitTree()
	{
		// If the map is open when the scene exits, re-capture the mouse so the
		// game doesn't leave the cursor visible.
		if (_open && _player != null)
		{
			_player.SetInputEnabled(true);
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
		if (_manager != null)
		{
			_manager.MapSystemToggled -= OnMapSystemToggled;
			if (_manager.Renderer != null)
			{
				_manager.Renderer.TerrainChanged -= OnTerrainChanged;
			}
		}
	}

	// ------------------------------------------------------------------
	// UI construction
	// ------------------------------------------------------------------

	private void BuildUI()
	{
		// A dim backdrop so the frozen world reads as "behind" the map.
		var dim = new ColorRect
		{
			Name = "Dim",
			Color = new Color(0, 0, 0, 0.55f),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		dim.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		_layer.AddChild(dim);

		// The main map panel (a bordered box), centered.
		_panel = new Panel
		{
			Name = "MapPanel",
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		_layer.AddChild(_panel);

		// A header row: title + coordinate readout + place-mode toggle + close hint.
		var header = new HBoxContainer
		{
			Name = "Header",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		header.AddThemeConstantOverride("separation", 16);
		_panel.AddChild(header);

		_title = new Label { Text = "World Map", MouseFilter = Control.MouseFilterEnum.Ignore };
		_title.AddThemeFontSizeOverride("font_size", 22);
		header.AddChild(_title);

		_coordsLabel = new Label { Text = "—", MouseFilter = Control.MouseFilterEnum.Ignore };
		header.AddChild(_coordsLabel);

		// Spacer.
		var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
		header.AddChild(spacer);

		_placeToggle = new Button { Text = "Place Waypoint", ToggleMode = true, MouseFilter = Control.MouseFilterEnum.Stop };
		_placeToggle.Pressed += OnPlaceToggle;
		header.AddChild(_placeToggle);

		_closeHint = new Button { Text = "Close (M)", MouseFilter = Control.MouseFilterEnum.Stop };
		_closeHint.Pressed += () => SetOpen(false);
		header.AddChild(_closeHint);

		// The map itself (fills the panel below the header).
		_mapRect = new TextureRect
		{
			Name = "Map",
			StretchMode = TextureRect.StretchModeEnum.Scale,
			MouseFilter = Control.MouseFilterEnum.Stop,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		};
		_mapRect.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
		_mapRect.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_mapRect.MouseEntered += () => { };
		_mapRect.GuiInput += OnMapGuiInput;
		_panel.AddChild(_mapRect);

		// A name-input row that appears under the map when placing a waypoint.
		_nameRow = new HBoxContainer
		{
			Name = "NameRow",
			MouseFilter = Control.MouseFilterEnum.Stop,
			Visible = false,
		};
		_nameRow.AddThemeConstantOverride("separation", 8);
		_panel.AddChild(_nameRow);

		_nameInputHint = new Label { Text = "Name:", MouseFilter = Control.MouseFilterEnum.Ignore };
		_nameRow.AddChild(_nameInputHint);

		_nameInput = new LineEdit
		{
			PlaceholderText = "Waypoint",
			MaxLength = 32,
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		_nameInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		_nameInput.TextSubmitted += OnNameSubmitted;
		_nameRow.AddChild(_nameInput);

		var confirmBtn = new Button { Text = "Place", MouseFilter = Control.MouseFilterEnum.Stop };
		confirmBtn.Pressed += OnConfirmPlace;
		_nameRow.AddChild(confirmBtn);

		var cancelBtn = new Button { Text = "Cancel", MouseFilter = Control.MouseFilterEnum.Stop };
		cancelBtn.Pressed += OnCancelPlace;
		_nameRow.AddChild(cancelBtn);

		// The waypoint list panel (right side, overlaying the map's right edge).
		_listPanel = new VBoxContainer
		{
			Name = "ListPanel",
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		_listPanel.AddThemeConstantOverride("separation", 4);
		_panel.AddChild(_listPanel);

		_listHeader = new Label
		{
			Text = "Waypoints",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_listHeader.AddThemeFontSizeOverride("font_size", 16);
		_listPanel.AddChild(_listHeader);

		// The map display image (sized to the panel in LayoutPanel).
		_displayImage = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8);
		_displayTexture = ImageTexture.CreateFromImage(_displayImage);
		_mapRect.Texture = _displayTexture;

		if (_manager != null)
		{
			_manager.WaypointsChanged += OnWaypointsChanged;
		}
	}

	/// <summary>Positions the panel centered in the viewport and sizes it.</summary>
	private void LayoutPanel()
	{
		Vector2 vp = GetViewport().GetVisibleRect().Size;
		float w = vp.X * PanelWidthFrac;
		float h = vp.Y * PanelHeightFrac;
		_panel.Size = new Vector2(w, h);
		_panel.Position = new Vector2((vp.X - w) * 0.5f, (vp.Y - h) * 0.5f);

		// The map rect is a plain child of the Panel (a Control, NOT a container),
		// so SizeFlags.ExpandFill has no effect — it keeps its default (tiny) size
		// and the map renders blank. Position and size it explicitly: below the
		// header, filling the panel.
		float headerH = 48f;
		_mapRect.Position = new Vector2(0, headerH);
		_mapRect.Size = new Vector2(_panel.Size.X, _panel.Size.Y - headerH);

		// The name-input row sits under the map (hidden until placing).
		_nameRow.Position = new Vector2(10, _panel.Size.Y - 56);
		_nameRow.Size = new Vector2(_panel.Size.X - 20, 40);

		// The list panel: a fixed-width column on the right edge of the map.
		float listW = 260f;
		_listPanel.Position = new Vector2(_panel.Size.X - listW - 10, headerH + 8);
		_listPanel.Size = new Vector2(listW, _panel.Size.Y - headerH - 16);

		// Resize the display image to the map rect's actual size.
		_displayW = Mathf.Max(8, (int)_mapRect.Size.X);
		_displayH = Mathf.Max(8, (int)_mapRect.Size.Y);
		if (_displayImage == null || _displayImage.GetWidth() != _displayW || _displayImage.GetHeight() != _displayH)
		{
			_displayImage = Image.CreateEmpty(_displayW, _displayH, false, Image.Format.Rgba8);
			_displayTexture = ImageTexture.CreateFromImage(_displayImage);
			_mapRect.Texture = _displayTexture;
		}
	}

	// ------------------------------------------------------------------
	// Open / close
	// ------------------------------------------------------------------

	/// <summary>Toggles the map window.</summary>
	public void Toggle() => SetOpen(!_open);

	private void SetOpen(bool open)
	{
		if (_open == open)
		{
			return;
		}
		_open = open;
		_layer.Visible = open;
		// Re-fetch the player in case it wasn't found at _Ready.
		if (_player == null)
		{
			_player = GetTree().GetFirstNodeInGroup("player") as Player;
		}
		if (_manager == null)
		{
			_manager = GetTree().GetFirstNodeInGroup("waypoint_manager") as WaypointManager;
		}
		if (_world == null)
		{
			_world = GetTree().GetFirstNodeInGroup("voxel_world") as VoxelWorld;
		}

		if (open)
		{
			// Center the view on the player, reset zoom, exit place mode.
			if (_player != null)
			{
				_viewCenter = new Vector2(_player.GlobalPosition.X, _player.GlobalPosition.Z);
			}
			_zoom = InitialZoom;
			// Force a rebuild on the next frame (the view just changed).
			_lastViewCenter = Vector2.Zero;
			_lastZoom = -1f;
			_displayDirty = true;
			SetPlaceMode(false);
			_selectedWaypointId = -1;
			// Release the mouse so the user can click / drag the map.
			if (_player != null)
			{
				_player.SetInputEnabled(false);
			}
			Input.MouseMode = Input.MouseModeEnum.Visible;
			LayoutPanel();
			RebuildList();
		}
		else
		{
			if (_player != null)
			{
				_player.SetInputEnabled(true);
			}
			Input.MouseMode = Input.MouseModeEnum.Captured;
		}
	}

	// ------------------------------------------------------------------
	// Key handling (M to toggle; Delete to remove selected waypoint)
	// ------------------------------------------------------------------

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (!(@event is InputEventKey key && key.Pressed && !key.Echo))
		{
			return;
		}
		// The map window only OWNS the keys while it is open and has the mouse
		// (mouse visible = the map took over input). When closed, the player
		// owns the mouse and the M key, so we do NOT touch the key here — doing
		// so (with SetInputAsHandled) would race the player's capture handler and
		// make M unreliable. Opening is handled by _Input (below), which runs
		// BEFORE the player's handler and marks the event handled, so the player
		// never recaptures the mouse on the open-press.
		if (!_open || _mouseNotOurs)
		{
			return;
		}
		if (key.Keycode == ToggleKey)
		{
			SetOpen(false);
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode == Key.Escape)
		{
			SetOpen(false);
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode == Key.Delete && _selectedWaypointId >= 0)
		{
			RemoveSelectedWaypoint();
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode == Key.F && _selectedWaypointId >= 0)
		{
			FocusSelectedWaypoint();
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>
	/// Opens the map with the M key. This runs in <c>_Input</c>, which fires
	/// BEFORE the Player's <c>_Input</c> (the map window is earlier in the scene
	/// tree), so we can consume the key here and stop the player from recapturing
	/// the mouse on the same press. Only acts when the map is closed; when open,
	/// the map window's <c>_UnhandledKeyInput</c> owns the keys instead.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (_open)
		{
			return;
		}
		// Don't open if the map system is toggled off.
		if (_manager != null && !_manager.IsMapSystemEnabled)
		{
			return;
		}
		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == ToggleKey)
		{
			SetOpen(true);
			_mouseNotOurs = false;
			GetViewport().SetInputAsHandled();
		}
	}

	private bool IsKeyToggle(InputEvent @event)
		=> @event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == ToggleKey;

	// ------------------------------------------------------------------
	// Mouse / pan / zoom (on the map rect)
	// ------------------------------------------------------------------

	private void OnMapGuiInput(InputEvent @event)
	{
		if (!_open)
		{
			return;
		}
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
			{
				ZoomAt(mb.Position, 1.25f);
				GetViewport().SetInputAsHandled();
			}
			else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
			{
				ZoomAt(mb.Position, 0.8f);
				GetViewport().SetInputAsHandled();
			}
			else if (mb.Pressed && mb.ButtonIndex == MouseButton.Left)
			{
				if (_placeMode)
				{
					BeginPlaceAt(mb.Position);
				}
				else
				{
					// Start a pan, or (if the press is very short) select a waypoint.
					_panning = true;
					_panStartScreen = mb.Position;
					_panStartCenter = _viewCenter;
					_pendingClickPos = mb.Position;
					_pendingClickTime = Time.GetTicksMsec();
				}
				GetViewport().SetInputAsHandled();
			}
			else if (!mb.Pressed && mb.ButtonIndex == MouseButton.Left)
			{
				if (_panning)
				{
					_panning = false;
					// If the drag was tiny, treat it as a click: select / place.
					if (_pendingClickPos != Vector2.Zero && _player != null)
					{
						float dist = (_panStartCenter - _viewCenter).Length();
						ulong now = Time.GetTicksMsec();
						if (dist < 0.5f && now - _pendingClickTime < 400)
						{
							HandleClick(_pendingClickPos);
						}
					}
				}
				GetViewport().SetInputAsHandled();
			}
		}
		else if (@event is InputEventMouseMotion mm && _panning)
		{
			// Pan: move the view center opposite the mouse delta.
			float dx = (mm.Position.X - _panStartScreen.X) / _zoom;
			float dy = (mm.Position.Y - _panStartScreen.Y) / _zoom;
			_viewCenter = new Vector2(_panStartCenter.X - dx, _panStartCenter.Y - dy);
			GetViewport().SetInputAsHandled();
		}
	}

	private Vector2 _pendingClickPos = Vector2.Zero;
	private ulong _pendingClickTime;

	/// <summary>Handles a (non-drag) click on the map: select a waypoint or begin placing.</summary>
	private void HandleClick(Vector2 screenPos)
	{
		if (_placeMode)
		{
			BeginPlaceAt(screenPos);
			return;
		}
		// Hit-test the waypoints (topmost / nearest first).
		long hit = HitTestWaypoint(screenPos);
		if (hit >= 0)
		{
			_selectedWaypointId = hit;
			RebuildList();
		}
		else
		{
			_selectedWaypointId = -1;
			RebuildList();
		}
	}

	/// <summary>Finds the waypoint under the given map-screen position, or -1.</summary>
	private long HitTestWaypoint(Vector2 screenPos)
	{
		if (_manager == null || _manager.Renderer == null)
		{
			return -1;
		}
		long best = -1;
		float bestDist = float.MaxValue;
		foreach (var wp in _manager.Renderer.Waypoints)
		{
			Vector2 wpScreen = WorldToScreen(wp.Position);
			float d = wpScreen.DistanceTo(screenPos);
			if (d < 14f && d < bestDist)
			{
				bestDist = d;
				best = wp.Id;
			}
		}
		return best;
	}

	/// <summary>Zooms around the given screen position (keeps that world point fixed).</summary>
	private void ZoomAt(Vector2 screenPos, float factor)
	{
		float newZoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
		if (Mathf.IsEqualApprox(newZoom, _zoom))
		{
			return;
		}
		// The world point under the mouse before the zoom:
		Vector2 worldBefore = ScreenToWorld(screenPos);
		_zoom = newZoom;
		// After the zoom, that same world point should still be under the mouse.
		Vector2 worldAfter = ScreenToWorld(screenPos);
		_viewCenter += worldBefore - worldAfter;
	}

	// ------------------------------------------------------------------
	// Waypoint placement
	// ------------------------------------------------------------------

	private void OnPlaceToggle()
	{
		SetPlaceMode(!_placeMode);
	}

	private void OnCancelPlace()
	{
		SetPlaceMode(false);
	}

	private void SetPlaceMode(bool on)
	{
		_placeMode = on;
		if (_placeToggle != null)
		{
			_placeToggle.SetPressed(on);
		}
		if (_nameRow != null)
		{
			_nameRow.Visible = on;
		}
	}

	/// <summary>Begins placing a waypoint at the given map-screen position.</summary>
	private void BeginPlaceAt(Vector2 screenPos)
	{
		if (_manager == null)
		{
			return;
		}
		Vector3 worldPos = ScreenToWorld3(screenPos);
		// Show the name input and remember where to place.
		_pendingPlacePos = worldPos;
		_pendingPlaceScreen = screenPos;
		_placeMode = true;
		if (_placeToggle != null)
		{
			_placeToggle.SetPressed(true);
		}
		if (_nameRow != null)
		{
			_nameRow.Visible = true;
		}
		if (_nameInput != null)
		{
			_nameInput.Text = "";
			_nameInput.GrabFocus();
		}
	}

	private Vector3 _pendingPlacePos;
	private Vector2 _pendingPlaceScreen;

	private void OnNameSubmitted(string text)
	{
		CommitPlace(text);
	}

	private void OnConfirmPlace()
	{
		CommitPlace(_nameInput?.Text ?? "");
	}

	private void CommitPlace(string name)
	{
		if (_manager?.Renderer == null)
		{
			return;
		}
		// Default color cycles through a small palette based on the count.
		Color color = WaypointColorFor(_manager.Renderer.Waypoints.Count);
		var wp = _manager.Renderer.AddWaypoint(name, _pendingPlacePos, color);
		_selectedWaypointId = wp.Id;
		SetPlaceMode(false);
		RebuildList();
	}

	private static Color WaypointColorFor(int index)
	{
		Color[] palette =
		{
			new Color(1.0f, 0.2f, 0.2f),   // red
			new Color(0.2f, 0.8f, 1.0f),   // cyan
			new Color(0.9f, 0.9f, 0.2f),   // yellow
			new Color(0.4f, 1.0f, 0.4f),   // green
			new Color(0.9f, 0.5f, 1.0f),   // purple
			new Color(1.0f, 0.6f, 0.2f),   // orange
		};
		return palette[index % palette.Length];
	}

	private void RemoveSelectedWaypoint()
	{
		if (_manager?.Renderer != null && _selectedWaypointId >= 0)
		{
			_manager.Renderer.RemoveWaypointById(_selectedWaypointId);
			_selectedWaypointId = -1;
			RebuildList();
		}
	}

	private void FocusSelectedWaypoint()
	{
		if (_manager?.Renderer == null)
		{
			return;
		}
		foreach (var wp in _manager.Renderer.Waypoints)
		{
			if (wp.Id == _selectedWaypointId)
			{
				_viewCenter = new Vector2(wp.Position.X, wp.Position.Z);
				return;
			}
		}
	}

	// ------------------------------------------------------------------
	// List panel
	// ------------------------------------------------------------------

	private void RebuildList()
	{
		if (_listPanel == null)
		{
			return;
		}
		// Remove all existing rows (keep the header).
		for (int i = _listPanel.GetChildCount() - 1; i >= 1; i--)
		{
			_listPanel.GetChild(i).Free();
		}
		_rows.Clear();

		if (_manager?.Renderer == null)
		{
			return;
		}

		foreach (var wp in _manager.Renderer.Waypoints)
		{
			var row = new WaypointRow { WaypointId = wp.Id };
			row.Box = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop };
			row.Box.AddThemeConstantOverride("separation", 4);

			var swatch = new ColorRect { CustomMinimumSize = new Vector2(14, 14), Color = wp.Color, MouseFilter = Control.MouseFilterEnum.Ignore };
			row.Box.AddChild(swatch);

			row.NameLabel = new Label
			{
				Text = wp.Name,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			row.Box.AddChild(row.NameLabel);

			float dist = _player != null
				? Mathf.Abs(wp.Position.X - _player.GlobalPosition.X + wp.Position.Z - _player.GlobalPosition.Z)
				: 0f;
			row.DistLabel = new Label
			{
				Text = dist < 1000f ? $"{(int)dist}m" : $"{dist / 1000f:0.0}km",
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			row.Box.AddChild(row.DistLabel);

			long wpId = wp.Id;
			row.FocusButton = new Button { Text = "Go", MouseFilter = Control.MouseFilterEnum.Stop };
			row.FocusButton.Pressed += () => FocusWaypoint(wpId);
			row.Box.AddChild(row.FocusButton);

			row.RemoveButton = new Button { Text = "×", MouseFilter = Control.MouseFilterEnum.Stop };
			row.RemoveButton.Pressed += () => RemoveWaypointRow(wpId);
			row.Box.AddChild(row.RemoveButton);

			_listPanel.AddChild(row.Box);
			_rows.Add(row);
		}
	}

	private void FocusWaypoint(long id)
	{
		if (_manager?.Renderer == null)
		{
			return;
		}
		foreach (var wp in _manager.Renderer.Waypoints)
		{
			if (wp.Id == id)
			{
				_viewCenter = new Vector2(wp.Position.X, wp.Position.Z);
				_selectedWaypointId = id;
				RebuildList();
				return;
			}
		}
	}

	/// <summary>Removes the given waypoint from the store and refreshes the list.</summary>
	private void RemoveWaypointRow(long id)
	{
		if (_manager?.Renderer != null)
		{
			_manager.Renderer.RemoveWaypointById(id);
			if (_selectedWaypointId == id)
			{
				_selectedWaypointId = -1;
			}
			RebuildList();
		}
	}

	private void OnWaypointsChanged()
	{
		if (_open)
		{
			RebuildList();
		}
	}

	// ------------------------------------------------------------------
	// Coordinate transforms
	// ------------------------------------------------------------------

	/// <summary>Screens (map-rect local px) → world XZ at the current zoom/center.</summary>
	private Vector2 ScreenToWorld(Vector2 screen)
	{
		float cx = _displayW * 0.5f;
		float cy = _displayH * 0.5f;
		return new Vector2(
			_viewCenter.X + (screen.X - cx) / _zoom,
			_viewCenter.Y + (screen.Y - cy) / _zoom);
	}

	private Vector3 ScreenToWorld3(Vector2 screen)
	{
		Vector2 w = ScreenToWorld(screen);
		// Y: snap to the terrain surface height when the world is available.
		float y = 0f;
		if (_world != null)
		{
			int sy = _world.GetSurfaceHeight((int)Mathf.Floor(w.X), (int)Mathf.Floor(w.Y));
			y = sy >= 0 ? sy + 1f : 0f;
		}
		return new Vector3(w.X, y, w.Y);
	}

	/// <summary>World XZ → map-rect local screen px at the current zoom/center.</summary>
	private Vector2 WorldToScreen(Vector3 world)
	{
		float cx = _displayW * 0.5f;
		float cy = _displayH * 0.5f;
		return new Vector2(
			cx + (world.X - _viewCenter.X) * _zoom,
			cy + (world.Z - _viewCenter.Y) * _zoom);
	}

	// ------------------------------------------------------------------
	// Display rebuild (read the shared map data, blit at zoom, draw overlays)
	// ------------------------------------------------------------------

	/// <summary>
	/// Rebuilds the panel's display image. The map window shows the SAME data the
	/// minimap does (the shared <see cref="MapRenderer"/> — the 2D height+material
	/// data the world pushes as chunks generate), just with a free pan/zoom view.
	///
	/// PERFORMANCE: the display is built at TILE resolution — one
	/// <see cref="MapRenderer.GetTileColor"/> per VISIBLE WORLD TILE (an O(1)
	/// lookup, NO scanning of the world) — and each tile is then blitted to a
	/// (zoom × zoom) pixel block. This is ~displayW/zoom × displayH/zoom
	/// GetTileColor calls (tens of thousands), not displayW × displayH (hundreds
	/// of thousands of pixels). Overlays (player, zombies, hives, waypoints) are
	/// drawn on top in screen space.
	/// </summary>
	private void RebuildDisplay(MapRenderer renderer)
	{
		if (_displayImage == null)
		{
			return;
		}
		var palette = _world.Palette;
		int w = _displayW;
		int h = _displayH;
		float cx = w * 0.5f;
		float cy = h * 0.5f;
		float zoom = _zoom;
		const int CS = UI.ChunkMapData.Size; // 16

		// Image.GetData() returns a COPY — writing to it does not modify the
		// image. We fill a managed buffer and push it in via SetData (one bulk
		// copy; no per-pixel marshaling). Rgba8 = 4 bytes/pixel.
		if (_displayBuf == null || _displayBuf.Length != w * h * 4)
		{
			_displayBuf = new byte[w * h * 4];
		}
		byte[] dstBuf = _displayBuf;

		// The visible world-tile range at the current zoom (the view center ±
		// half the display, in tiles).
		int tileMinX = (int)Mathf.Floor(_viewCenter.X - cx / zoom);
		int tileMaxX = (int)Mathf.Ceil(_viewCenter.X + cx / zoom);
		int tileMinZ = (int)Mathf.Floor(_viewCenter.Y - cy / zoom);
		int tileMaxZ = (int)Mathf.Ceil(_viewCenter.Y + cy / zoom);

		// CAP the tile range to prevent the low-zoom blowup. At zoom=1 on a
		// 800×600 display, the raw range is 800×600 = 480K tiles. Cap the
		// rendered tile count to MaxRenderedTiles (default 256×256 = 65K) by
		// centering the range on the view center and shrinking it if needed.
		// The visual difference is negligible at low zoom (each tile is 1px).
		const int MaxRenderedTiles = 256;
		int tileW = tileMaxX - tileMinX + 1;
		int tileH = tileMaxZ - tileMinZ + 1;
		if (tileW > MaxRenderedTiles || tileH > MaxRenderedTiles)
		{
			int centerX = (tileMinX + tileMaxX) / 2;
			int centerZ = (tileMinZ + tileMaxZ) / 2;
			tileMinX = centerX - MaxRenderedTiles / 2;
			tileMaxX = centerX + MaxRenderedTiles / 2;
			tileMinZ = centerZ - MaxRenderedTiles / 2;
			tileMaxZ = centerZ + MaxRenderedTiles / 2;
			tileW = tileMaxX - tileMinX + 1;
			tileH = tileMaxZ - tileMinZ + 1;
		}

		// Clear to "unknown" first so any sub-pixel gaps left by fractional-zoom
		// tile blocks read as dark, not stale/garbage pixels. Use Array.Fill for
		// speed (a single tight loop, no per-pixel Color allocation).
		System.Array.Fill(dstBuf, (byte)0, 0, dstBuf.Length);
		// Re-stamp the unknown color (0x1A, 0x14, 0x1C, 0xFF) — the fill above
		// zeroed everything, so we just need to set the alpha channel.
		for (int i = 3; i < dstBuf.Length; i += 4)
		{
			dstBuf[i] = 255;
		}

		// For each visible world tile, compute its color via the non-allocating
		// packed path (no Vector2I, no Color allocation) and blit it to the
		// screen-pixel block it occupies. The chunk coord is computed
		// incrementally (it changes every 16 tiles) to avoid the FloorDiv per
		// tile. A single reusable 4-byte pixel pattern avoids per-tile
		// allocation.
		int zInt = Mathf.Max(1, (int)zoom);
		int cx0 = MapRenderer.FloorDiv(tileMinX, CS);
		int cz0 = MapRenderer.FloorDiv(tileMinZ, CS);
		int lx = tileMinX - cx0 * CS;
		int lz = tileMinZ - cz0 * CS;
		byte[] px = new byte[4]; // reusable pixel pattern

		for (int tz = tileMinZ; tz <= tileMaxZ; tz++)
		{
			for (int tx = tileMinX; tx <= tileMaxX; tx++)
			{
				uint packed;
				bool known = renderer.TryGetTileColorPacked(cx0, cz0, lx, lz, palette, out packed);
				if (known)
				{
					px[0] = (byte)(packed & 0xFF);
					px[1] = (byte)((packed >> 8) & 0xFF);
					px[2] = (byte)((packed >> 16) & 0xFF);
					px[3] = (byte)((packed >> 24) & 0xFF);
				}
				else
				{
					px[0] = 18; px[1] = 20; px[2] = 26; px[3] = 255;
				}

				// Screen rect for this tile: its top-left world corner maps to a
				// screen point; the tile spans zoom pixels from there.
				int sx0 = (int)(cx + (tx - _viewCenter.X) * zoom);
				int sy0 = (int)(cy + (tz - _viewCenter.Y) * zoom);
				int sx1 = Mathf.Min(w, sx0 + zInt);
				int sy1 = Mathf.Min(h, sy0 + zInt);
				int sxStart = Mathf.Max(0, sx0);
				int syStart = Mathf.Max(0, sy0);
				if (sxStart < sx1 && syStart < sy1)
				{
					int bw = sx1 - sxStart;
					for (int sy = syStart; sy < sy1; sy++)
					{
						int destOff = (sy * w + sxStart) * 4;
						for (int b = 0; b < bw; b++)
						{
							System.Buffer.BlockCopy(px, 0, dstBuf, destOff + b * 4, 4);
						}
					}
				}
				// Advance the local/chunk coords.
				lx++;
				if (lx >= CS) { lx = 0; cx0++; }
			}
			lz++;
			if (lz >= CS) { lz = 0; cz0++; }
		}

		// Push the filled buffer into the image (one bulk copy). Overlays are
		// drawn separately (DrawOverlays) so they can update every frame (the
		// player arrow tracks the player) without re-blitting the terrain tiles.
		_displayImage.SetData(w, h, false, Image.Format.Rgba8, dstBuf);

		// Update the coordinate readout (the view center's world coords).
		if (_coordsLabel != null)
		{
			_coordsLabel.Text = $"X {(int)_viewCenter.X}   Z {(int)_viewCenter.Y}   zoom {zoom:0.0}";
		}
	}

	/// <summary>
	/// Draws the dynamic overlays (player arrow, waypoint markers) onto the
	/// display image and pushes it to the texture. This runs every frame while
	/// the map is open (the player arrow tracks the player smoothly), but it's
	/// cheap — only a few dozen SetPixel calls (no terrain tile blit).
	/// </summary>
	private void DrawOverlays()
	{
		if (_displayImage == null || _displayImage.GetWidth() != _displayW || _displayImage.GetHeight() != _displayH)
		{
			return;
		}
		DrawEntityOverlays();
		DrawWaypointOverlays();
		_displayTexture.Update(_displayImage);
	}

	/// <summary>Draws the player (arrow), nearby zombies (red dots), and hives (purple dots).</summary>
	private void DrawEntityOverlays()
	{
		// Player: a white arrow at its screen position (pointing up = facing).
		if (_player != null)
		{
			Vector2 p = WorldToScreen(_player.GlobalPosition);
			DrawTriangle(_displayImage, p.X, p.Y, 8f, 0f, Colors.White);
		}
		if (_world == null)
		{
			return;
		}
		// Zombies (the "zombie" group).
		foreach (Node n in GetTree().GetNodesInGroup("zombie"))
		{
			if (n is Node3D nz && GodotObject.IsInstanceValid(nz))
			{
				Vector2 s = WorldToScreen(nz.GlobalPosition);
				if (InDisplay(s))
				{
					DrawDot(_displayImage, s.X, s.Y, 3f, new Color(0.9f, 0.2f, 0.2f));
				}
			}
		}
		// Hives (the world's Hives list).
		foreach (var hive in _world.Hives)
		{
			if (!GodotObject.IsInstanceValid(hive))
			{
				continue;
			}
			Vector3 center = new Vector3(hive.CenterVoxel.X + 0.5f, hive.CenterVoxel.Y, hive.CenterVoxel.Z + 0.5f);
			Vector2 s = WorldToScreen(center);
			if (InDisplay(s))
			{
				DrawDot(_displayImage, s.X, s.Y, 4f, new Color(0.7f, 0.3f, 0.9f));
			}
		}
	}

	private bool InDisplay(Vector2 s)
		=> s.X >= 0 && s.X < _displayW && s.Y >= 0 && s.Y < _displayH;

	/// <summary>Draws the waypoint markers (diamonds) + selection highlight + labels.</summary>
	private void DrawWaypointOverlays()
	{
		if (_manager?.Renderer == null)
		{
			return;
		}
		foreach (var wp in _manager.Renderer.Waypoints)
		{
			Vector2 s = WorldToScreen(wp.Position);
			if (!InDisplay(s))
			{
				continue;
			}
			bool selected = wp.Id == _selectedWaypointId;
			DrawDiamond(_displayImage, s.X, s.Y, selected ? 8f : 6f, wp.Color);
			if (selected)
			{
				// A highlight ring.
				DrawRing(_displayImage, s.X, s.Y, 11f, new Color(1, 1, 1, 0.9f));
			}
			// A small label dot above.
			DrawDot(_displayImage, s.X, s.Y - 10f, 2f, wp.Color);
		}
	}

	// ------------------------------------------------------------------
	// Drawing helpers (shared with the minimap; kept local to avoid coupling)
	// ------------------------------------------------------------------

	private static void DrawDot(Image img, float cx, float cy, float r, Color color)
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
				if (dx * dx + dy * dy <= r * r)
				{
					img.SetPixel(x, y, color);
				}
			}
		}
	}

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

	private static void DrawRing(Image img, float cx, float cy, float r, Color color)
	{
		int x0 = Mathf.Max(0, (int)(cx - r - 1));
		int x1 = Mathf.Min(img.GetWidth() - 1, (int)(cx + r + 1));
		int y0 = Mathf.Max(0, (int)(cy - r - 1));
		int y1 = Mathf.Min(img.GetHeight() - 1, (int)(cy + r + 1));
		for (int y = y0; y <= y1; y++)
		{
			for (int x = x0; x <= x1; x++)
			{
				float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
				if (Mathf.Abs(d - r) <= 1.5f)
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
}
