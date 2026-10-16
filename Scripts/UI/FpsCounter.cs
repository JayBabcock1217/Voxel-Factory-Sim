using Godot;
using System;

namespace FactorySim.UI;

/// <summary>
/// A small FPS readout pinned to the top-right corner of the screen. Built
/// entirely in code (a <see cref="CanvasLayer"/> + <see cref="Label"/>), so it
/// needs no scene file. Add a single <c>FpsCounter</c> node to the scene tree
/// (e.g. as a child of the root of the main scene) and it installs itself.
///
/// The label sits on its own CanvasLayer above the game view and uses
/// <c>MouseFilter = Ignore</c>, so it never intercepts clicks meant for the
/// world or the inventory UI.
/// </summary>
public partial class FpsCounter : Node
{
	private CanvasLayer _layer;
	private Label _label;

	private int _frames;
	private double _elapsed;

	public override void _Ready()
	{
		_layer = new CanvasLayer { Layer = 100 };

		_label = new Label
		{
			Text = "-- FPS",
			HorizontalAlignment = HorizontalAlignment.Right,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_label.AddThemeFontSizeOverride("font_size", 20);
		// A subtle outline keeps the text readable over bright terrain.
		_label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
		_label.AddThemeConstantOverride("outline_size", 4);

		// Anchor to the top-right corner, with a small margin.
		_label.AnchorLeft = 1f;
		_label.AnchorTop = 0f;
		_label.AnchorRight = 1f;
		_label.AnchorBottom = 0f;
		_label.Position = new Vector2(-96, 8);
		_label.Size = new Vector2(88, 28);

		_layer.AddChild(_label);
		AddChild(_layer);
	}

	public override void _Process(double delta)
	{
		_frames++;
		_elapsed += delta;

		// Refresh the readout once per second so the number is stable.
		if (_elapsed >= 1.0)
		{
			int fps = (int)Math.Round(_frames / _elapsed);
			if (_label != null)
				_label.Text = $"{fps} FPS";
			_frames = 0;
			_elapsed = 0;
		}
	}
}
