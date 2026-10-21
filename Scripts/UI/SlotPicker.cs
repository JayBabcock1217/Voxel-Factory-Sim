using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using FactorySim.Voxel;

namespace FactorySim.UI;

/// <summary>
/// The save/load slot picker, shown over the main menu when Play is pressed.
/// Built entirely in code (the project's UI pattern — StyleBoxFlat panels,
/// MakeButton helpers, centering in _Process). Shows:
///   - a list of existing slots (from <see cref="WorldSave.ListSlots"/>), each
///     row with its name, a human "last saved" relative time, and its seed,
///     plus small Load / Rename / Delete buttons (clicking the row also loads),
///   - a "New World" button (starts a fresh world),
///   - a "Back" button (Esc goes back; Enter loads the top slot).
///
/// Kept as a plain Control that the main menu adds/hides (like
/// <see cref="SettingsMenu"/>), so it lives as a child of the menu and never
/// blocks the menu behind it when closed (MouseFilter Ignore).
///
/// Slot lifecycle decisions:
///   - LOAD: the picker calls <c>WorldSave.SetActiveSlot</c> +
///     <c>WorldSave.LoadFrom</c>, then the host switches to the gameplay scene.
///     The chosen slot rides the scene change via the static
///     <see cref="WorldSave.SessionSlotId"/> handoff (the menu and the game
///     host different WorldSave nodes).
///   - NEW WORLD: <c>WorldSave.CreateSlot</c> allocates the id,
///     <c>SetActiveSlot</c> publishes it, and the world starts fresh (the
///     slot's file is written by the first in-game auto-save on quit — the
///     picker never writes a slot file itself, so a slot that is never played
///     leaves no empty file behind).
/// </summary>
public partial class SlotPicker : Control
{
	/// <summary>Raised when the user chose a slot to LOAD (id, name).</summary>
	public event Action<string, string> LoadRequested;

	/// <summary>Raised when the user chose "New World" (the allocated slot id, its name).</summary>
	public event Action<string, string> NewWorldRequested;

	/// <summary>Raised when the user dismissed the picker (Back / Esc).</summary>
	public event Action Closed;

	private WorldSave _worldSave;
	private Control _host;
	private Control _panel;
	private VBoxContainer _rows;
	private Label _hint;

	// Stashed by ShowPrompt for the deferred DoInvokeOk (CallDeferred can only
	// target real instance methods, not local functions, so the prompt's onOk
	// callback and overlay node are held on the instance until the deferred call
	// fires). Only one prompt is shown at a time, so a single slot is sufficient.
	private Action<string, string> _pendingOk;
	private Control _pendingOverlay;

	public override void _Ready()
	{
		MouseFilter = Control.MouseFilterEnum.Ignore;
		SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);

		_panel = new PanelContainer
		{
			Name = "Panel",
			Visible = false,
			LayoutMode = 3,
			Theme = BuildTheme(),
		};
		_panel.CustomMinimumSize = new Vector2(720, 560);
		AddChild(_panel);

		var vbox = new VBoxContainer
		{
			Name = "Content",
			LayoutMode = 2,
		};
		vbox.AddThemeConstantOverride("separation", 12);
		_panel.AddChild(vbox);

		var title = new Label
		{
			Name = "Title",
			LayoutMode = 2,
			Text = "Select World",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		title.AddThemeFontSizeOverride("font_size", 40);
		title.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		vbox.AddChild(title);

		_hint = new Label
		{
			Name = "Hint",
			LayoutMode = 2,
			Text = "Click a world to load it · Enter loads the most recent · Esc goes back",
			HorizontalAlignment = HorizontalAlignment.Center,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_hint.AddThemeFontSizeOverride("font_size", 18);
		_hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.7f, 0.85f));
		vbox.AddChild(_hint);

		// Scrollable slot list.
		var scroll = new ScrollContainer
		{
			Name = "Scroll",
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(0, 300),
			FollowFocus = true,
		};
		_rows = new VBoxContainer
		{
			Name = "Rows",
			LayoutMode = 2,
		};
		_rows.AddThemeConstantOverride("separation", 8);
		scroll.AddChild(_rows);
		vbox.AddChild(scroll);

		var buttons = new HBoxContainer
		{
			Name = "Buttons",
			LayoutMode = 2,
		};
		buttons.AddThemeConstantOverride("separation", 12);
		buttons.AddChild(MakeButton("New World", OnNewWorld, true));
		buttons.AddChild(MakeButton("Back", Close, false));
		vbox.AddChild(buttons);

		Visible = false;
	}

	public override void _Process(double delta)
	{
		if (Visible && _panel != null && _panel.Size != Vector2.Zero)
		{
			Vector2 vp = GetViewport().GetVisibleRect().Size;
			_panel.Position = (vp - _panel.Size) * 0.5f;
		}
	}

	public override void _GuiInput(InputEvent @event)
	{
		// Esc closes the picker; Enter loads the top (most recently modified)
		// slot — the snappy path. Both only fire while the picker is the
		// topmost overlay (this node stops the event, so the main menu's own
		// key handling is unaffected).
		if (!Visible || !(@event is InputEventKey key) || !key.Pressed || key.Echo)
		{
			return;
		}
		if (key.Keycode == Key.Escape)
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
		else if (key.Keycode == Key.Enter || key.Keycode == Key.KpEnter)
		{
			SlotInfo top = null;
			if (_worldSave != null)
			{
				foreach (SlotInfo slot in _worldSave.ListSlots())
				{
					top = slot;
					break;
				}
			}
			if (top != null)
			{
				string slotName = string.IsNullOrEmpty(top.Name) ? $"Slot {top.Id}" : top.Name;
				LoadRequested?.Invoke(top.Id, slotName);
			}
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>
	/// Shows the picker: stores the host (the node to hide while open) and the
	/// <see cref="WorldSave"/> node to query, refreshes the slot list, and
	/// centers the panel.
	/// </summary>
	public void Open(Control host, WorldSave worldSave)
	{
		_host = host;
		_worldSave = worldSave;
		Visible = true;
		MouseFilter = Control.MouseFilterEnum.Stop;
		_panel.Visible = true;
		Refresh();
		CallDeferred(nameof(CenterPanel));
	}

	/// <summary>Hides the picker and restores the host's visibility.</summary>
	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		MouseFilter = Control.MouseFilterEnum.Ignore;
		_panel.Visible = false;
		if (_host != null)
		{
			_host.Visible = true;
		}
		Closed?.Invoke();
	}

	/// <summary>
	/// Re-reads the slots from <see cref="WorldSave.ListSlots"/> and rebuilds
	/// the row list. Call this when the picker opens and after any
	/// create/delete/rename/save.
	/// </summary>
	public void Refresh()
	{
		if (_rows == null)
		{
			return;
		}

		foreach (Node child in _rows.GetChildren())
		{
			child.QueueFree();
		}

		List<SlotInfo> slots = new List<SlotInfo>();
		if (_worldSave != null)
		{
			slots = _worldSave.ListSlots();
		}

		if (slots.Count == 0)
		{
			var empty = new Label
			{
				LayoutMode = 2,
				Text = "No saved worlds yet — start a New World.",
				HorizontalAlignment = HorizontalAlignment.Center,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			empty.AddThemeFontSizeOverride("font_size", 22);
			empty.AddThemeColorOverride("font_color", new Color(0.6f, 0.7f, 0.85f));
			_rows.AddChild(empty);
			return;
		}

		foreach (SlotInfo slot in slots)
		{
			_rows.AddChild(BuildRow(slot));
		}
	}

	private Control BuildRow(SlotInfo slot)
	{
		var row = new PanelContainer
		{
			Name = $"Row_{slot.Id}",
			LayoutMode = 2,
			MouseFilter = Control.MouseFilterEnum.Stop,
			Theme = BuildTheme(),
		};

		var hbox = new HBoxContainer
		{
			LayoutMode = 2,
		};
		hbox.AddThemeConstantOverride("separation", 12);
		row.AddChild(hbox);

		var info = new VBoxContainer
		{
			LayoutMode = 2,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		info.AddThemeConstantOverride("separation", 2);

		var name = new Label
		{
			LayoutMode = 2,
			Text = string.IsNullOrEmpty(slot.Name) ? $"Slot {slot.Id}" : slot.Name,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		name.AddThemeFontSizeOverride("font_size", 24);
		name.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.97f));
		info.AddChild(name);

		var meta = new Label
		{
			LayoutMode = 2,
			Text = $"Saved {FormatRelativeTime(slot.ModifiedAt)} · seed {slot.Seed.ToString(CultureInfo.InvariantCulture)}",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		meta.AddThemeFontSizeOverride("font_size", 16);
		meta.AddThemeColorOverride("font_color", new Color(0.55f, 0.63f, 0.78f));
		info.AddChild(meta);
		hbox.AddChild(info);

		string slotId = slot.Id;
		string slotName = string.IsNullOrEmpty(slot.Name) ? $"Slot {slot.Id}" : slot.Name;

		hbox.AddChild(MakeSmallButton("Load", () => LoadRequested?.Invoke(slotId, slotName)));
		hbox.AddChild(MakeSmallButton("Rename", () => ConfirmRename(slot)));
		hbox.AddChild(MakeSmallButton("Delete", () => ConfirmDelete(slotId, slotName)));

		// Clicking anywhere on the row (outside the small buttons) also loads —
		// the small buttons consume their own clicks, so this only fires on the
		// row's own area (the name/meta text).
		row.GuiInput += @event =>
		{
			if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
			{
				LoadRequested?.Invoke(slotId, slotName);
			}
		};
		return row;
	}

	// ------------------------------------------------------------------
	// Row actions
	// ------------------------------------------------------------------

	private void ConfirmRename(SlotInfo slot)
	{
		string current = string.IsNullOrEmpty(slot.Name) ? $"Slot {slot.Id}" : slot.Name;
		ShowPrompt("Rename World", $"New name for '{current}':", current, (name, _) =>
		{
			if (string.IsNullOrWhiteSpace(name))
			{
				return;
			}
			if (_worldSave != null && _worldSave.RenameSlot(slot.Id, name))
			{
				Refresh();
			}
		});
	}

	private void ConfirmDelete(string slotId, string slotName)
	{
		ShowPrompt("Delete World", $"Delete '{slotName}'? Its save file will be removed.", "", (_, _) =>
		{
			if (_worldSave != null && _worldSave.DeleteSlot(slotId))
			{
				Refresh();
			}
		}, confirmText: "Delete", cancelText: "Keep");
	}

	private void OnNewWorld()
	{
		if (_worldSave == null)
		{
			return;
		}

		// Ask for a world name and a seed BEFORE creating the slot: the seed
		// field is pre-filled with a random int (so the user can see and change
		// it) and a blank/garbage seed falls back to that same random default.
		int count = _worldSave.ListSlots().Count + 1;
		string defaultName = $"New World {count}";
		var rng = new Godot.RandomNumberGenerator();
		rng.Randomize();
		int randomSeed = (int)rng.Randi();
		string seedDefault = randomSeed.ToString(CultureInfo.InvariantCulture);

		ShowPrompt("New World", "Name and seed for the new world:",
			defaultName, (name, seedText) =>
			{
				if (name == null || seedText == null)
				{
					return; // cancelled
				}

				string finalName = string.IsNullOrWhiteSpace(name) ? defaultName : name;
				// Documented choice: a blank or non-numeric seed falls back to the
				// pre-filled RANDOM default (so a blank field still gets a fresh,
				// visible seed instead of silently collapsing to seed 0).
				int seed;
				if (!int.TryParse(seedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
				{
					seed = randomSeed;
				}

				string id = _worldSave.CreateSlot(finalName, seed);
				if (id == null)
				{
					return;
				}

				// Publish the slot for the gameplay scene: the world starts FRESH
				// (no LoadFrom — the slot has no file yet), and the in-game
				// auto-save on quit writes the slot's first file. Publish the
				// chosen seed alongside it so the world can generate from it.
				_worldSave.SetActiveSlot(id, finalName);
				WorldSave.SessionSeed = seed;
				WorldSave.SessionHasSeed = true;
				NewWorldRequested?.Invoke(id, finalName);
			},
			"Seed (any integer, blank = random)", seedDefault,
			"Create", "Cancel");
	}

	/// <summary>
	/// Shows a small centered prompt (a label + up to two editable text fields +
	/// OK / Cancel) inside the picker panel. The first field is always present;
	/// when <paramref name="secondFieldLabel"/> is non-null a second field with
	/// that label is added below it. <paramref name="onOk"/> receives the trimmed
	/// first-field text plus the trimmed second-field text (or nulls when the
	/// user cancelled). Enter in EITHER field confirms (documented choice: with
	/// two fields the user may be typing the seed, so both fields confirm);
	/// Esc cancels.
	/// </summary>
	private void ShowPrompt(string title, string message, string initialValue, Action<string, string> onOk,
		string secondFieldLabel = null, string secondInitialValue = null,
		string confirmText = "OK", string cancelText = "Cancel")
	{
		var overlay = new ColorRect
		{
			Name = "PromptOverlay",
			Color = new Color(0, 0, 0, 0.4f),
			MouseFilter = Control.MouseFilterEnum.Stop,
		};
		overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		AddChild(overlay);

		// Stash the callback + overlay for the deferred DoInvokeOk (CallDeferred
		// can only target real instance methods, not local functions).
		_pendingOk = onOk;
		_pendingOverlay = overlay;

		// The prompt is a full-rect Control (anchors work in a Control), not a
		// PanelContainer — anchoring a container node to the parent is
		// unreliable, so the box is positioned by hand (like CenterPanel).
		var box = new Control
		{
			Name = "PromptBox",
		};
		overlay.AddChild(box);
		overlay.GuiInput += @event =>
		{
			// Clicking outside the box cancels the prompt. Routed through
			// EnqueueOk so the emit returns before the overlay is freed.
			if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left
				&& !box.GetRect().HasPoint(mb.Position))
			{
				EnqueueOk(null, null);
			}
		};

		var panel = new PanelContainer
		{
			LayoutMode = 3,
			Theme = BuildTheme(),
		};
		panel.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
		box.AddChild(panel);

		var vbox = new VBoxContainer
		{
			LayoutMode = 2,
		};
		vbox.AddThemeConstantOverride("separation", 10);
		panel.AddChild(vbox);

		var titleLabel = new Label
		{
			LayoutMode = 2,
			Text = title,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		titleLabel.AddThemeFontSizeOverride("font_size", 28);
		titleLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.95f, 1f));
		vbox.AddChild(titleLabel);

		var messageLabel = new Label
		{
			LayoutMode = 2,
			Text = message,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		messageLabel.AddThemeFontSizeOverride("font_size", 18);
		messageLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.78f, 0.9f));
		vbox.AddChild(messageLabel);

		var field = new LineEdit
		{
			LayoutMode = 2,
			PlaceholderText = "Name",
		};
		field.AddThemeFontSizeOverride("font_size", 20);
		vbox.AddChild(field);

		// Optional second field (the seed): a plain LineEdit with its own label.
		LineEdit secondField = null;
		if (!string.IsNullOrEmpty(secondFieldLabel))
		{
			var secondLabel = new Label
			{
				LayoutMode = 2,
				Text = secondFieldLabel,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			secondLabel.AddThemeFontSizeOverride("font_size", 16);
			secondLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.78f, 0.9f));
			vbox.AddChild(secondLabel);

			secondField = new LineEdit
			{
				LayoutMode = 2,
				PlaceholderText = "0",
			};
			secondField.AddThemeFontSizeOverride("font_size", 20);
			vbox.AddChild(secondField);
		}

		var buttons = new HBoxContainer
		{
			LayoutMode = 2,
		};
		buttons.AddThemeConstantOverride("separation", 10);

		// Every confirm/cancel path must do NOTHING but enqueue the deferred
		// DoInvokeOk and return. The actual work (freeing the overlay AND running
		// onOk) happens in DoInvokeOk on the NEXT deferred-call pass. This is what
		// makes the Delete flow safe: onOk -> DeleteSlot -> Refresh() queue-frees
		// the slot row that hosts the emitting button, but only AFTER that
		// button's Pressed signal has already returned. If onOk ran synchronously
		// inside the handler, the emit would still be on the stack when the
		// button was freed and Godot's C# signal machinery would throw "Invalid
		// instance access on object" -> crash.
		string ReadFirst() => field.Text?.Trim() ?? "";
		string ReadSecond() => secondField != null ? secondField.Text?.Trim() ?? "" : "";

		buttons.AddChild(MakeSmallButton(confirmText, () =>
			EnqueueOk(ReadFirst(), ReadSecond())));
		buttons.AddChild(MakeSmallButton(cancelText, () =>
			EnqueueOk(null, null)));
		vbox.AddChild(buttons);

		// Enqueue the deferred work. Reads the field text NOW (while the fields
		// are still live) and defers the rest. CallDeferred can only target a
		// real instance method, so DoInvokeOk reads the stashed _pendingOk /
		// _pendingOverlay set at the top of ShowPrompt.
		void EnqueueOk(string first, string second)
		{
			CallDeferred(nameof(DoInvokeOk), new Variant[] { first, second });
		}

		// Enter in the field confirms; Esc cancels. With a second field, Enter in
		// EITHER field confirms (documented above). All of these route through
		// EnqueueOk so the signal emit returns before any node is freed.
		field.TextSubmitted += _ => EnqueueOk(ReadFirst(), ReadSecond());
		field.GuiInput += @event =>
		{
			if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
			{
				EnqueueOk(null, null);
				GetViewport().SetInputAsHandled();
			}
		};

		if (secondField != null)
		{
			secondField.TextSubmitted += _ => EnqueueOk(ReadFirst(), ReadSecond());
			secondField.GuiInput += @event =>
			{
				if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
				{
					EnqueueOk(null, null);
					GetViewport().SetInputAsHandled();
				}
			};
		}

		// Center the prompt box in the viewport and focus the first field. The
		// panel's size is only known after the layout pass (deferred). The
		// second-field arguments are always passed (null when absent) so the
		// CallDeferred variant list matches the method's parameter list.
		CallDeferred(nameof(FinishPromptSetup),
			new Variant[] { box, panel, field, initialValue, secondField, secondInitialValue });
	}

	private void FinishPromptSetup(Control box, Control panel, LineEdit field, string initialValue,
		LineEdit secondField = null, string secondInitialValue = null)
	{
		if (!IsInstanceValid(panel) || panel.Size == Vector2.Zero)
		{
			return;
		}
		Vector2 vp = GetViewport().GetVisibleRect().Size;
		box.Position = (vp - panel.Size) * 0.5f;
		field.Text = initialValue;
		field.CaretColumn = field.Text.Length;
		field.GrabFocus();
		if (secondField != null && IsInstanceValid(secondField))
		{
			secondField.Text = secondInitialValue ?? "";
			secondField.CaretColumn = secondField.Text.Length;
		}
	}

	/// <summary>
	/// Deferred target for the prompt's confirm/cancel paths (see
	/// <see cref="ShowPrompt"/>). CallDeferred can only invoke real instance
	/// methods, so this reads the stashed <see cref="_pendingOk"/> /
	/// <see cref="_pendingOverlay"/> set by <see cref="ShowPrompt"/>. The
	/// deferral lets the current signal emit return before any node is freed
	/// (the OK handler may free the slot row that hosts the emitting button, or
	/// the prompt's own button), avoiding "Invalid instance access on object".
	/// </summary>
	private void DoInvokeOk(string first, string second)
	{
		if (_pendingOverlay == null || !IsInstanceValid(_pendingOverlay))
		{
			_pendingOk = null;
			_pendingOverlay = null;
			return;
		}
		// Free the overlay (and everything under it), then run the callback.
		_pendingOverlay.QueueFree();
		var ok = _pendingOk;
		_pendingOk = null;
		_pendingOverlay = null;
		try
		{
			ok?.Invoke(first, second);
		}
		catch (System.Exception e)
		{
			// An exception escaping a deferred call tears the process down.
			// Catch it so the game keeps running and the cause is visible.
			GD.PushError("[SlotPicker] prompt callback threw: " + e);
		}
	}

	// ------------------------------------------------------------------
	// Styling helpers (the project's StyleBoxFlat pattern)
	// ------------------------------------------------------------------

	private static Theme BuildTheme()
	{
		var theme = new Theme();
		var panel = new StyleBoxFlat
		{
			BgColor = new Color(0.09f, 0.11f, 0.17f, 0.98f),
			BorderColor = new Color(0.35f, 0.45f, 0.65f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 24f,
			ContentMarginTop = 20f,
			ContentMarginRight = 24f,
			ContentMarginBottom = 20f,
			ShadowColor = new Color(0, 0, 0, 0.45f),
			ShadowSize = 14,
			ShadowOffset = new Vector2(0, 6),
		};
		theme.SetStylebox("panel", "PanelContainer", panel);

		var rowBox = new StyleBoxFlat
		{
			BgColor = new Color(0.14f, 0.18f, 0.28f, 1f),
			BorderColor = new Color(0.3f, 0.4f, 0.6f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 14f,
			ContentMarginTop = 8f,
			ContentMarginRight = 14f,
			ContentMarginBottom = 8f,
		};
		theme.SetStylebox("panel", "SlotRow", rowBox);

		return theme;
	}

	private Button MakeButton(string text, Action pressed, bool primary)
	{
		var button = new Button
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(220, 48),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			Text = text,
		};
		button.AddThemeFontSizeOverride("font_size", 22);
		var normal = new StyleBoxFlat
		{
			BgColor = primary
				? new Color(0.16f, 0.3f, 0.42f, 1f)
				: new Color(0.14f, 0.18f, 0.28f, 1f),
			BorderColor = primary
				? new Color(0.4f, 0.65f, 0.8f, 1f)
				: new Color(0.35f, 0.45f, 0.65f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 20f,
			ContentMarginTop = 8f,
			ContentMarginRight = 20f,
			ContentMarginBottom = 8f,
		};
		var hover = (StyleBoxFlat)normal.Duplicate();
		hover.BgColor = primary
			? new Color(0.22f, 0.4f, 0.55f, 1f)
			: new Color(0.2f, 0.27f, 0.42f, 1f);
		hover.BorderColor = new Color(0.55f, 0.68f, 0.9f, 1f);
		var pressedStyle = (StyleBoxFlat)normal.Duplicate();
		pressedStyle.BgColor = new Color(0.1f, 0.13f, 0.2f, 1f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", pressedStyle);
		button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddThemeColorOverride("font_hover_color", Colors.White);
		button.Pressed += pressed;
		return button;
	}

	private Button MakeSmallButton(string text, Action pressed)
	{
		var button = new Button
		{
			LayoutMode = 2,
			CustomMinimumSize = new Vector2(84, 34),
			Text = text,
		};
		button.AddThemeFontSizeOverride("font_size", 16);
		var normal = new StyleBoxFlat
		{
			BgColor = new Color(0.18f, 0.23f, 0.36f, 1f),
			BorderColor = new Color(0.4f, 0.5f, 0.7f, 1f),
			BorderWidthLeft = 1,
			BorderWidthTop = 1,
			BorderWidthRight = 1,
			BorderWidthBottom = 1,
			ContentMarginLeft = 10f,
			ContentMarginTop = 4f,
			ContentMarginRight = 10f,
			ContentMarginBottom = 4f,
		};
		var hover = (StyleBoxFlat)normal.Duplicate();
		hover.BgColor = new Color(0.26f, 0.34f, 0.5f, 1f);
		var pressedStyle = (StyleBoxFlat)normal.Duplicate();
		pressedStyle.BgColor = new Color(0.1f, 0.13f, 0.2f, 1f);
		button.AddThemeStyleboxOverride("normal", normal);
		button.AddThemeStyleboxOverride("hover", hover);
		button.AddThemeStyleboxOverride("pressed", pressedStyle);
		button.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.95f));
		button.AddThemeColorOverride("font_hover_color", Colors.White);
		button.Pressed += pressed;
		return button;
	}

	// ------------------------------------------------------------------
	// Time formatting
	// ------------------------------------------------------------------

	/// <summary>
	/// Formats a unix-millisecond timestamp as a human relative time:
	/// "just now" (&lt; 60s), "5m ago", "3h ago", "2d ago", otherwise the
	/// calendar date. A missing timestamp (0) reads "never".
	/// </summary>
	public static string FormatRelativeTime(long unixMs)
	{
		if (unixMs <= 0)
		{
			return "never";
		}

		try
		{
			long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			long deltaMs = now - unixMs;
			if (deltaMs < 0)
			{
				deltaMs = 0; // a future timestamp (clock skew) reads "just now"
			}

			if (deltaMs < 60_000)
			{
				return "just now";
			}
			long minutes = deltaMs / 60_000;
			if (minutes < 60)
			{
				return $"{minutes}m ago";
			}
			long hours = minutes / 60;
			if (hours < 24)
			{
				return $"{hours}h ago";
			}
			long days = hours / 24;
			if (days < 7)
			{
				return $"{days}d ago";
			}

			var date = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
			return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		}
		catch (System.Exception)
		{
			return "unknown";
		}
	}

	private void CenterPanel()
	{
		if (_panel == null || !IsInstanceValid(_panel) || _panel.Size == Vector2.Zero)
		{
			return;
		}
		Vector2 vp = GetViewport().GetVisibleRect().Size;
		_panel.Position = (vp - _panel.Size) * 0.5f;
	}
}
