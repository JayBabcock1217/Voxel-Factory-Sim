using Godot;
using System.Collections.Generic;
using FactorySim.Items;

namespace FactorySim;

/// <summary>
/// Simple first-person character controller.
///
/// Controls:
/// - WASD: move
/// - Space: jump (double-tap to toggle fly mode)
/// - Shift: sprint
/// - Mouse: look around (click to capture the mouse, Escape to release)
///
/// Fly mode:
/// - No gravity; Space ascends, Shift descends.
/// - Double-tap Space to toggle back to walking.
/// - Turning off fly mode drops the player (gravity resumes).
/// </summary>
[GlobalClass]
public partial class Player : CharacterBody3D
{
	[ExportGroup("Movement")]
	/// <summary>Speed while walking, in meters per second.</summary>
	[Export] public float WalkSpeed { get; set; } = 5f;

	/// <summary>Speed while sprinting, in meters per second.</summary>
	[Export] public float SprintSpeed { get; set; } = 9f;

	/// <summary>How quickly horizontal velocity changes on the ground.</summary>
	[Export] public float Acceleration { get; set; } = 10f;

	/// <summary>How quickly horizontal velocity changes in the air.</summary>
	[Export] public float AirAcceleration { get; set; } = 2f;

	/// <summary>Upward velocity applied when jumping.</summary>
	[Export] public float JumpVelocity { get; set; } = 7f;

	/// <summary>Downward acceleration applied while in the air.</summary>
	[Export] public float Gravity { get; set; } = 20f;

	/// <summary>Maximum downward fall speed (magnitude).</summary>
	[Export] public float MaxFallSpeed { get; set; } = 40f;

	[ExportGroup("Flying")]
	/// <summary>Speed while flying, in meters per second.</summary>
	[Export] public float FlySpeed { get; set; } = 10f;

	/// <summary>How quickly fly velocity changes (acceleration in m/s²).</summary>
	[Export] public float FlyAcceleration { get; set; } = 15f;

	/// <summary>Max time (seconds) between two Space presses to count as a double-tap.</summary>
	[Export] public float DoubleTapWindow { get; set; } = 0.3f;

	[ExportGroup("Mouse Look")]
	/// <summary>Mouse sensitivity (radians per pixel).</summary>
	[Export] public float MouseSensitivity { get; set; } = 0.0022f;

	/// <summary>Maximum camera pitch up and down, in degrees.</summary>
	[Export] public float MaxPitchDegrees { get; set; } = 89f;

	/// <summary>Path to the camera that pitches with the mouse.</summary>
	[Export] public NodePath CameraPath { get; set; } = new("Camera3D");

	private Camera3D _camera;
	private float _yaw;
	private float _pitch;
	private Vector2 _mouseMotion;
	private Inventory _inventory;
	private bool _inputEnabled = true;
	private Area3D _pickupArea;

	// Fly mode state
	private bool _flying;
	private float _lastSpacePressTime = -999f; // seconds since _Ready (via Time)

	/// <summary>
	/// When false the character is frozen: no movement input, no mouse look,
	/// no click-to-recapture. Set by the inventory UI when it opens/closes.
	/// </summary>
	public bool InputEnabled => _inputEnabled;

	/// <summary>Enables or disables all player input (movement + look + recapture).</summary>
	public void SetInputEnabled(bool enabled)
	{
		_inputEnabled = enabled;
		if (!enabled)
		{
			// Clear any accumulated mouse delta so a stale look-jump doesn't
			// fire the moment input is re-enabled.
			_mouseMotion = Vector2.Zero;
		}
	}

	/// <summary>
	/// True while the in-game pause menu is open. Set by the pause menu; while
	/// true the Player's Escape handler is disabled so Escape is handled by the
	/// pause menu (which toggles it) instead of the Player's capture/release
	/// toggle. This prevents the two Escape handlers from fighting.
	/// </summary>
	public bool PauseMenuOpen { get; set; }

	/// <summary>True while the player is in fly mode (no gravity, Space ascends, Shift descends).</summary>
	public bool Flying => _flying;

	/// <summary>Toggles fly mode on or off.</summary>
	public void ToggleFly()
	{
		_flying = !_flying;
		if (!_flying)
		{
			// Leaving fly: kill vertical velocity so the player starts falling
			// from rest rather than continuing at the last fly speed.
			var vel = Velocity;
			vel.Y = 0f;
			Velocity = vel;
		}
	}

	// ------------------------------------------------------------------
	// Save / restore (world persistence)
	// ------------------------------------------------------------------

	/// <summary>
	/// The player's world position for saving. The <c>WorldSave</c> service
	/// reads this (with <see cref="LookForSave"/>) when collecting a snapshot;
	/// it is the authoritative position the world restores on load. NOTE: this
	/// is the CharacterBody3D's ORIGIN, which is the CENTER of the collision
	/// capsule (the capsule is a direct child with no offset). So a player
	/// standing on the ground has its origin about 0.9 ABOVE the surface top,
	/// not AT it. The world restores this exact value on load (no re-clamp), so
	/// the save/restore is symmetric and the player resumes at their saved spot.
	/// </summary>
	public Vector3 PositionForSave => GlobalPosition;

	/// <summary>
	/// The player's current yaw (body Y rotation) and pitch (camera X rotation),
	/// in radians, for saving. These back the private <c>_yaw</c>/<c>_pitch</c>
	/// fields that <see cref="UpdateMouseLook"/> applies to the body/camera.
	/// </summary>
	public (float Yaw, float Pitch) LookForSave => (_yaw, _pitch);

	/// <summary>
	/// Restores the player's transform from a saved world: sets the position and
	/// the private yaw/pitch (and snaps the body + camera to match) so the next
	/// <see cref="UpdateMouseLook"/> continues from the saved view. Called by the
	/// <c>WorldSave</c> service instead of the world's terrain spawn when a save
	/// exists.
	/// </summary>
	public void RestoreFromSave(Vector3 position, float yaw, float pitch)
	{
		GlobalPosition = position;
		_yaw = Mathf.Wrap(yaw, -Mathf.Pi, Mathf.Pi);
		_pitch = Mathf.Clamp(pitch, -Mathf.DegToRad(MaxPitchDegrees), Mathf.DegToRad(MaxPitchDegrees));

		// Snap the body yaw and camera pitch immediately so the view is correct
		// before the next physics tick (UpdateMouseLook would also do it, but it
		// only runs while the mouse is captured and the player may start frozen).
		var bodyRotation = Rotation;
		bodyRotation.Y = _yaw;
		Rotation = bodyRotation;

		if (_camera != null)
		{
			var cameraRotation = _camera.Rotation;
			cameraRotation.X = _pitch;
			_camera.Rotation = cameraRotation;
		}
	}

	/// <summary>
	/// True while the inventory UI is open. The Inventory node lives in the
	/// "inventory" group and is looked up lazily, then cached; the IsOpen
	/// state is read as a property (string name) rather than a typed member
	/// so the check degrades gracefully if the node is ever a different type.
	/// </summary>
	private bool InventoryOpen
	{
		get
		{
			if (_inventory == null)
			{
				_inventory = GetTree().GetFirstNodeInGroup("inventory") as Inventory;
			}
			return _inventory != null && (bool)_inventory.Get("IsOpen");
		}
	}

	public override void _Ready()
	{
		// Safety net for stale scene data: a `= null` export override loads as exactly 0,
		// which would leave the player floating in zero-g with a dead mouse.
		SanitizeExports();

		// VoxelWorld (and any other systems) locate the player via this group.
		AddToGroup("player");

		// A stale `CameraPath = null` override loads as an empty path; fall back to a
		// "Camera3D" child instead of hard-erroring in GetNode().
		var cameraPath = CameraPath.IsEmpty ? new NodePath("Camera3D") : CameraPath;
		_camera = GetNodeOrNull<Camera3D>(cameraPath);
		if (_camera == null)
		{
			GD.PushWarning($"[Player] No camera at '{cameraPath}' under {Name} — mouse look disabled.");
		}

		// The pickup area detects drop areas (collision layer 4) and collects
		// them immediately when the player walks close — no polling.
		_pickupArea = GetNodeOrNull<Area3D>("PickupArea");
		if (_pickupArea != null)
		{
			_pickupArea.AreaEntered += OnPickupAreaEntered;
		}

		CaptureMouse();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		// Escape is owned by the pause menu while it's present: the menu toggles
		// itself on Escape, so the Player must not also run its capture/release
		// toggle on the same press (or the two would fight over the mouse).
		if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape && !PauseMenuOpen)
		{
			if (Input.MouseMode == Input.MouseModeEnum.Captured)
			{
				ReleaseMouse();
			}
			else
			{
				CaptureMouse();
			}
		}
		else if (@event is InputEventMouseMotion motion
			&& Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			// Accumulate the mouse delta between physics ticks. This replaces
			// Input.GetMouseMotion(), which was removed in Godot 4.8.
			_mouseMotion += motion.Relative;
		}
	}

	public override void _Input(InputEvent @event)
	{
		// Double-tap Space to toggle fly mode. Must be handled here (not in
		// _PhysicsProcess) so the timing window is measured against actual
		// key presses, not physics ticks.
		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Space)
		{
			float now = (float)Time.GetTicksMsec() / 1000f;
			float elapsed = now - _lastSpacePressTime;
			_lastSpacePressTime = now;

			if (elapsed < DoubleTapWindow)
			{
				ToggleFly();
			}
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		var dt = (float)delta;

		// Frozen while the inventory is open: no look, no recapture, no movement.
		// Gravity still applies so the player settles on the ground.
		if (_inputEnabled)
		{
			var captured = Input.MouseMode == Input.MouseModeEnum.Captured;
			if (captured)
			{
				UpdateMouseLook(_mouseMotion);
			}
			// Consume the accumulated mouse delta once per physics tick.
			_mouseMotion = Vector2.Zero;

			if (!captured && Input.IsMouseButtonPressed(MouseButton.Left))
			{
				CaptureMouse();
			}
		}

		if (_inputEnabled)
		{
			UpdateVelocity(dt);
		}
		else
		{
			ApplyGravityOnly(dt);
		}

		MoveAndSlide();
	}

	/// <summary>
	/// Gravity without movement input (used when the player is frozen, e.g.
	/// inventory open). Kills horizontal momentum so the player stops in
	/// place; no WASD, no jump.
	/// </summary>
	private void ApplyGravityOnly(float dt)
	{
		var vel = Velocity;
		// Stop horizontal movement immediately — the player should not keep
		// sliding from momentum accumulated before the inventory opened.
		vel.X = 0f;
		vel.Z = 0f;
		vel.Y -= Gravity * dt;
		if (IsOnFloor())
		{
			vel.Y = 0f;
		}
		else
		{
			vel.Y = Mathf.Max(vel.Y, -MaxFallSpeed);
		}
		Velocity = vel;
	}

	private void UpdateMouseLook(Vector2 mouseMotion)
	{
		_yaw -= mouseMotion.X * MouseSensitivity;
		_pitch -= mouseMotion.Y * MouseSensitivity;

		var maxPitch = Mathf.DegToRad(MaxPitchDegrees);
		_pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);
		_yaw = Mathf.Wrap(_yaw, -Mathf.Pi, Mathf.Pi);

		// The body yaws with the mouse; the camera pitches. The Rotation
		// property can't be member-mutated directly, so read it, modify a
		// local copy, and write it back.
		var bodyRotation = Rotation;
		bodyRotation.Y = _yaw;
		Rotation = bodyRotation;

		if (_camera == null)
		{
			return; // no camera found in _Ready — the body yaw above still works.
		}

		var cameraRotation = _camera.Rotation;
		cameraRotation.X = _pitch;
		_camera.Rotation = cameraRotation;
	}

	private void UpdateVelocity(float dt)
	{
		if (_flying)
		{
			UpdateFlyVelocity(dt);
		}
		else
		{
			UpdateWalkVelocity(dt);
		}
	}

	/// <summary>
	/// Walking/jumping velocity: gravity, jump on Space, WASD horizontal
	/// movement with ground/air acceleration.
	/// </summary>
	private void UpdateWalkVelocity(float dt)
	{
		// Work on a local copy and write it back once, since the Velocity
		// property can't be member-mutated directly.
		var vel = Velocity;

		// Gravity.
		vel.Y -= Gravity * dt;

		if (IsOnFloor())
		{
			if (Input.IsActionJustPressed("jump"))
			{
				vel.Y = JumpVelocity;
			}
			else
			{
				vel.Y = 0f;
			}
		}
		else
		{
			vel.Y = Mathf.Max(vel.Y, -MaxFallSpeed);
		}

		// Input direction relative to the body's facing direction.
		var input = new Vector3(
			Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left"),
			0f,
			Input.GetActionStrength("move_back") - Input.GetActionStrength("move_forward"));
		if (input.Length() > 1f)
		{
			input = input.Normalized();
		}

		var speed = Input.IsActionPressed("sprint") ? SprintSpeed : WalkSpeed;
		var accel = (IsOnFloor() ? Acceleration : AirAcceleration) * dt;

		var targetVelocity = GlobalBasis * (input * speed);
		var horizontal = new Vector3(vel.X, 0f, vel.Z).MoveToward(targetVelocity, accel);

		vel.X = horizontal.X;
		vel.Z = horizontal.Z;

		Velocity = vel;
	}

	/// <summary>
	/// Fly mode velocity: no gravity. Space ascends, Shift descends, WASD
	/// strafes in the camera's facing plane (including looking up/down).
	/// </summary>
	private void UpdateFlyVelocity(float dt)
	{
		var vel = Velocity;

		// Input direction relative to the camera's facing direction (includes
		// pitch, so looking up and pressing W flies upward-forward).
		var input = new Vector3(
			Input.GetActionStrength("move_right") - Input.GetActionStrength("move_left"),
			0f,
			Input.GetActionStrength("move_back") - Input.GetActionStrength("move_forward"));
		if (input.Length() > 1f)
		{
			input = input.Normalized();
		}

		// Vertical: Space = up, Shift = down.
		float vertical = 0f;
		if (Input.IsActionPressed("jump"))
		{
			vertical += 1f;
		}
		if (Input.IsActionPressed("sprint"))
		{
			vertical -= 1f;
		}

		// Build the target velocity in the camera's local space.
		var target = _camera != null
			? _camera.GlobalBasis * (input * FlySpeed + new Vector3(0f, vertical * FlySpeed, 0f))
			: GlobalBasis * (input * FlySpeed + new Vector3(0f, vertical * FlySpeed, 0f));

		// Accelerate toward the target.
		float accel = FlyAcceleration * dt;
		vel = vel.MoveToward(target, accel);

		Velocity = vel;
	}

	private void CaptureMouse()
	{
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void ReleaseMouse()
	{
		Input.MouseMode = Input.MouseModeEnum.Visible;
	}

	/// <summary>
	/// Fired when a drop's pickup area enters the player's PickupArea.
	/// The drop's DropPickup area (collision layer 4) is detected by the
	/// player's PickupArea (collision mask 4).
	/// </summary>
	private void OnPickupAreaEntered(Area3D area)
	{
		// The area is the drop's DropPickup child; its parent is the VoxelDrop.
		if (area.GetParent() is FactorySim.Items.VoxelDrop drop)
		{
			drop.Pickup();
		}
	}

	/// <summary>
	/// Snaps degenerate export values back to their code defaults. A `= null` scene
	/// override loads as exactly 0 for a float (the editor writes these when it saves
	/// the scene without knowing the C# default); without this, Gravity = 0 makes the
	/// player hover and MaxFallSpeed = 0 means it can never fall. Fallbacks must match
	/// the property initializers above.
	/// </summary>
	private void SanitizeExports()
	{
		var fixes = new List<string>();

		if (WalkSpeed <= 0f) { WalkSpeed = 5f; fixes.Add("WalkSpeed -> 5"); }
		if (SprintSpeed <= 0f) { SprintSpeed = 9f; fixes.Add("SprintSpeed -> 9"); }
		if (Acceleration <= 0f) { Acceleration = 10f; fixes.Add("Acceleration -> 10"); }
		if (AirAcceleration <= 0f) { AirAcceleration = 2f; fixes.Add("AirAcceleration -> 2"); }
		if (JumpVelocity <= 0f) { JumpVelocity = 7f; fixes.Add("JumpVelocity -> 7"); }
		if (Gravity <= 0f) { Gravity = 20f; fixes.Add("Gravity -> 20"); }
		if (MaxFallSpeed <= 0f) { MaxFallSpeed = 40f; fixes.Add("MaxFallSpeed -> 40"); }
		if (FlySpeed <= 0f) { FlySpeed = 10f; fixes.Add("FlySpeed -> 10"); }
		if (FlyAcceleration <= 0f) { FlyAcceleration = 15f; fixes.Add("FlyAcceleration -> 15"); }
		if (DoubleTapWindow <= 0f) { DoubleTapWindow = 0.3f; fixes.Add("DoubleTapWindow -> 0.3"); }
		if (MouseSensitivity <= 0f) { MouseSensitivity = 0.0022f; fixes.Add("MouseSensitivity -> 0.0022"); }
		if (MaxPitchDegrees <= 0f) { MaxPitchDegrees = 89f; fixes.Add("MaxPitchDegrees -> 89"); }

		if (fixes.Count > 0)
		{
			GD.PushWarning("[Player] Degenerate export values restored to defaults: " + string.Join(", ", fixes));
		}
	}
}
