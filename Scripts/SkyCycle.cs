using Godot;
using System;

// 'Environment' is ambiguous between Godot.Environment and System.Environment;
// alias the Godot one so the scene's Environment resource can be referenced by
// its short name throughout.
using Environment = Godot.Environment;

namespace FactorySim;

/// <summary>
/// The day/night cycle. Drives a full diurnal cycle by rotating the scene's
/// <see cref="DirectionalLight3D"/> (the sun) around the horizon and, in lockstep,
/// adjusting the <see cref="Environment"/> (sun energy, ambient light, fog) so the
/// terrain lighting matches the sky.
///
/// HOW THE SKY IS DRIVEN
/// The sky is a BinbunSky shader sky (<c>res://Textures/SkiesFull/</c>). When its
/// <c>use_directional_light</c> parameter is true (the default for the pack's
/// sky resources), the shader reads <c>LIGHT0_DIRECTION.y</c> — the Y component
/// of the FIRST directional light in the environment — as its day/night blend:
///   * blend > 0  → day (sky_day / horizon_day colours, sun visible toward the light)
///   * blend ≈ 0  → sunset/sunrise (sky_sunset / horizon_sunset colours)
///   * blend < 0  → night (sky_night / horizon_night colours, stars + moon, the
///                  moon is rendered toward -LIGHT0_DIRECTION)
/// So simply animating the DirectionalLight3D's rotation (its forward vector is
/// LIGHT0_DIRECTION) drives the entire sky — sun, moon, stars, cloud tint and the
/// day→sunset→night colour ramp — with no per-frame shader parameter work.
///
/// WHAT THIS SCRIPT ADDS (on top of the self-driven sky)
///   * The sun's ENERGY and the ambient/ambient-sky light are faded with the
///     phase, so the terrain darkens at night and brightens at noon (the sky
///     shader colours the background, but the WorldEnvironment's sun/ambient
///     terms are what light the geometry).
///   * A swappable skybox: <see cref="SkyboxConfig"/> holds the day and night
///     <see cref="Sky"/> resources; the cycle swaps which one is on the
///     WorldEnvironment's <see cref="Environment"/> at the phase midpoint so the
///     night sky (stars, darker palette) shows at night.
///   * Configurable day / night LENGTH (in seconds) and a STATIC time so the
///     cycle can be paused or set to a fixed hour (useful for testing, and for
///     a world that should always be at a certain time of day).
///
/// DISCOVERY
/// The script finds its <see cref="DirectionalLight3D"/> and
/// <see cref="WorldEnvironment"/> by group ("sun_light" / "world_environment"),
/// consistent with how the rest of the project resolves cross-node references.
///
/// BAKED VOXEL LIGHT
/// This is a RENDERING-only system. The per-voxel "SkyLight" flood-fill that
/// bakes light into chunk meshes (see Voxel/SkyLight.cs) is independent and stays
/// static, so caves and interiors keep their baked light regardless of the time
/// of day. Only the real-time directional sun + ambient + sky are animated.
/// </summary>
public partial class SkyCycle : Node
{
	/// <summary>Group the DirectionalLight3D (sun) must be in to be driven.</summary>
	public const string SunLightGroup = "sun_light";

	/// <summary>Group the WorldEnvironment must be in to be driven.</summary>
	public const string WorldEnvironmentGroup = "world_environment";

	/// <summary>
	/// Group the moon's DirectionalLight3D must be in to be driven. The moon is
	/// a second, fainter directional light that is ENABLED only at night (when
	/// the sun is below the horizon) and DISABLED by day, so the scene has a
	/// soft bluish fill + shadows at night. Null-safe: if no node is in this
	/// group (and none is found by the tree walk), the cycle simply never
	/// enables a moon (day/night still works, just no night fill light).
	/// </summary>
	public const string MoonLightGroup = "moon_light";

	// ------------------------------------------------------------------
	// Configuration (all editable in the inspector on this node)
	// ------------------------------------------------------------------

	/// <summary>
	/// The skyboxes to use. <see cref="SkyboxConfig.DaySky"/> is shown when the
	/// sun is up, <see cref="SkyboxConfig.NightSky"/> when it is down. Swap these
	/// (or the other slots in the config) to change the look without code changes.
	/// Null → the cycle does not touch the sky resource (the scene's existing
	/// sky is kept).
	/// </summary>
	[Export] public SkyboxConfig SkyboxConfig { get; set; }

	/// <summary>
	/// Half of the full cycle length, in seconds — the real time the clock
	/// takes to sweep through the DAYLIGHT half of the 24h cycle (06:00→18:00).
	/// The full cycle is DaySeconds + NightSeconds. On the fixed 24h clock the
	/// sun is ALWAYS up 06:00–18:00 (12h) and down 18:00–06:00 (12h), so
	/// DaySeconds and NightSeconds control the CYCLE SPEED, not the day/night
	/// split: setting both to the same value gives a uniform clock speed, and
	/// their SUM is how many real seconds a full 24h day takes. Keep ≥ 1.
	/// </summary>
	[Export(PropertyHint.Range, "1,3600,1")]
	public float DaySeconds { get; set; } = 300f;

	/// <summary>
	/// Half of the full cycle length, in seconds — the real time the clock
	/// takes to sweep through the NIGHT half of the 24h cycle (18:00→06:00).
	/// See <see cref="DaySeconds"/>: on the fixed 24h clock the sun is always
	/// down for 12h (18:00–06:00), so this controls the cycle speed (together
	/// with DaySeconds, their sum = FullCycleSeconds), not the night's length
	/// in clock-hours. Keep ≥ 1.
	/// </summary>
	[Export(PropertyHint.Range, "1,3600,1")]
	public float NightSeconds { get; set; } = 120f;

	/// <summary>
	/// The sun's energy at noon (peak day). Faded toward <see
	/// cref="NightSunEnergy"/> at night.
	/// </summary>
	[Export(PropertyHint.Range, "0,8,0.05")]
	public float DaySunEnergy { get; set; } = 1.0f;

	/// <summary>
	/// The sun's energy at midnight (trough). Usually 0 (no direct sun at night).
	/// </summary>
	[Export(PropertyHint.Range, "0,8,0.05")]
	public float NightSunEnergy { get; set; } = 0.0f;

	/// <summary>
	/// Ambient-light energy at noon. Ambient is what lifts the non-shadowed,
	/// non-directly-lit surfaces; it is faded at night so the world darkens.
	/// </summary>
	[Export(PropertyHint.Range, "0,2,0.02")]
	public float DayAmbientEnergy { get; set; } = 0.6f;

	/// <summary>
	/// Ambient-light energy at midnight. Keep > 0 (a small value) so night is
	/// dim but not pitch black — the sky's ambient-sky term and the baked voxel
	/// light also contribute.
	/// </summary>
	[Export(PropertyHint.Range, "0,2,0.02")]
	public float NightAmbientEnergy { get; set; } = 0.12f;

	/// <summary>
	/// The moon light's energy at its peak (midnight). The moon is a faint,
	/// cool directional light that is only enabled while the sun is below the
	/// horizon. Kept low so night is dim but still lit (and so the moon's
	/// shadows are visible but soft). Faded to 0 as the moon approaches the
	/// horizon (sunset / sunrise).
	/// </summary>
	[Export(PropertyHint.Range, "0,2,0.02")]
	public float MoonEnergy { get; set; } = 0.15f;

	/// <summary>
	/// The moon light's colour. A cool blue-white by default (moonlight). Only
	/// applied while the moon is enabled.
	/// </summary>
	[Export] public Color MoonColor { get; set; } = new Color(0.6f, 0.7f, 1.0f, 1.0f);

	/// <summary>
	/// The phase at which the app starts, in [0, 1) where 0 = midnight, 0.5 =
	/// noon, 0.75 = sunset. Used as the initial time before the cycle advances.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float StartingPhase { get; set; } = 0.5f;

	/// <summary>
	/// When true, the cycle does NOT advance over time — the time of day is held
	/// at <see cref="StaticTime"/> (useful for a world that should always be at a
	/// fixed hour, or while debugging the look of a specific phase).
	/// </summary>
	[Export] public bool Paused { get; set; } = false;

	/// <summary>
	/// The fixed time of day (in [0,1), 0 = midnight, 0.5 = noon) used when
	/// <see cref="Paused"/> is true.
	/// </summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float StaticTime { get; set; } = 0.5f;

	// ------------------------------------------------------------------
	// Runtime state
	// ------------------------------------------------------------------

	private DirectionalLight3D _sun;
	private DirectionalLight3D _moon;
	private WorldEnvironment _worldEnvironment;
	private Environment _environment;

	/// <summary>The current phase in [0,1): 0 = midnight, 0.5 = noon, 0.75 = sunset.</summary>
	private float _phase;

	/// <summary>The Sky resource currently assigned to the environment.</summary>
	private Sky _currentSky;

	/// <summary>True while the cycle is actively animating (not paused).</summary>
	public bool IsRunning { get; private set; }

	/// <summary>
	/// The current time of day, in [0,1): 0 = midnight, 0.25 = sunrise, 0.5 =
	/// noon, 0.75 = sunset. Read-only (set by the cycle each frame).
	/// </summary>
	public float Phase => _phase;

	/// <summary>
	/// A value in [0,1] describing how "day" it is: 1 at noon (12:00), 0 at
	/// midnight (00:00) and at sunrise/sunset (06:00/18:00, when the sun is on
	/// the horizon). This is the same quantity the sun's energy and ambient are
	/// faded by. It is the sun's altitude (sin of the arc angle), clamped to ≥0
	/// so night reads as 0 — using the same PhaseToArcAngle mapping ApplyPhase
	/// uses, so "how day it is" always matches the sky and the 24h clock.
	/// </summary>
	public float DayAmount
	{
		get
		{
			float sunAltitude = (float)System.Math.Sin(PhaseToArcAngle(_phase));
			return System.Math.Max(0f, sunAltitude);
		}
	}

	/// <summary>Total length of one full cycle (day + night), in seconds.</summary>
	public float FullCycleSeconds => System.Math.Max(1f, DaySeconds + NightSeconds);

	/// <summary>
	/// The phase at which the sun sets (end of the day window). On the fixed
	/// 24h clock this is always 0.75 (18:00); the sun rises at 0.25 (06:00).
	/// </summary>
	public float SunsetPhase => 0.75f;

	/// <summary>
	/// Maps a phase in [0,1) to the sun's ARC ANGLE θ (in radians), on a FIXED
	/// 24-HOUR CLOCK: the sun's position is a pure function of the clock time,
	/// independent of DaySeconds/NightSeconds. The mapping is θ = 2π·phase − π/2:
	///   * phase 0    (midnight) → θ = −π/2  (sun at its nadir, straight down)
	///   * phase 0.25 (06:00)    → θ = 0     (sun on the eastern horizon, sunrise)
	///   * phase 0.5  (12:00)    → θ = π/2   (sun at its zenith, noon)
	///   * phase 0.75 (18:00)    → θ = π     (sun on the western horizon, sunset)
	///   * phase 1    (24:00)    → θ = 3π/2  (midnight again)
	/// The sun's altitude is sin(θ): positive for phase ∈ (0.25, 0.75) (06:00–
	/// 18:00, daylight) and negative otherwise (night). This is exactly what the
	/// sky shader's LIGHT0_DIRECTION.y day/night blend needs, and it GUARANTEES
	/// that "settime 12" puts the sun at its zenith, "settime 0"/"24" at its
	/// nadir, "settime 6" on the sunrise horizon, and "settime 18" on the sunset
	/// horizon — the sun angle always matches the 24-hour clock.
	///
	/// NOTE: because the sun's arc is fixed to the clock, the daylight window is
	/// always 06:00–18:00 (12 hours) in phase terms, regardless of
	/// DaySeconds/NightSeconds. Those two exports now control the CYCLE SPEED
	/// (how many real seconds a full 24h clock takes: FullCycleSeconds =
	/// DaySeconds + NightSeconds), not the day/night SPLIT. The day and night
	/// are always equal in real time (each = half the cycle).
	///
	/// This is the SINGLE source of truth for the phase→arc mapping; both
	/// ApplyPhase (sun + moon) and DayAmount use it so they always agree.
	/// </summary>
	private float PhaseToArcAngle(float phase)
	{
		// θ = 2π·phase − π/2. At phase 0 this is −π/2 (nadir); at 0.5 it's π/2
		// (zenith). The sine of θ gives the sun's altitude directly.
		return 2f * Mathf.Pi * phase - Mathf.Pi / 2f;
	}

	// ------------------------------------------------------------------
	// Node lifecycle
	// ------------------------------------------------------------------

	public override void _Ready()
	{
		// Ensure the sun and the world environment are in their expected groups
		// BEFORE we look them up. The scene is supposed to assign these groups
		// (groups = ["sun_light"] / groups = ["world_environment"]), but the
		// Godot editor rewrites the .tscn on save and can silently drop
		// hand-edited group entries — so relying on the scene file alone is
		// fragile. Adding them here in code (the same way VoxelWorld joins
		// "voxel_world") makes the group lookup below work no matter what the
		// scene file contains. We also fall back to a tree walk in case the
		// nodes exist but were never grouped.
		var sun = GetTree()?.GetFirstNodeInGroup(SunLightGroup) as DirectionalLight3D
			?? FindDescendantOfType<DirectionalLight3D>(GetTree()?.Root);
		var worldEnv = GetTree()?.GetFirstNodeInGroup(WorldEnvironmentGroup) as WorldEnvironment
			?? FindDescendantOfType<WorldEnvironment>(GetTree()?.Root);

		var moon = GetTree()?.GetFirstNodeInGroup(MoonLightGroup) as DirectionalLight3D
			?? FindDescendantOfType<DirectionalLight3D>(GetTree()?.Root, skip: sun);

		if (sun != null)
		{
			sun.AddToGroup(SunLightGroup);
		}
		if (worldEnv != null)
		{
			worldEnv.AddToGroup(WorldEnvironmentGroup);
		}
		if (moon != null)
		{
			moon.AddToGroup(MoonLightGroup);
		}

		_sun = GetTree()?.GetFirstNodeInGroup(SunLightGroup) as DirectionalLight3D;
		_moon = GetTree()?.GetFirstNodeInGroup(MoonLightGroup) as DirectionalLight3D;
		_worldEnvironment = GetTree()?.GetFirstNodeInGroup(WorldEnvironmentGroup) as WorldEnvironment;
		_environment = _worldEnvironment?.Environment;

		// The moon starts disabled; ApplyPhase() below will enable it if we
		// start at night. Force the initial energy so a stale scene value is
		// overwritten on the first frame.
		if (_moon != null)
		{
			_moon.LightColor = MoonColor;
		}

		GD.Print($"[SkyCycle] _Ready: sun={(_sun != null ? _sun.Name : "NULL")} " +
			$"moon={(_moon != null ? _moon.Name : "NULL")} " +
			$"worldEnv={(_worldEnvironment != null ? _worldEnvironment.Name : "NULL")} " +
			$"env={(_environment != null ? "set" : "NULL")} " +
			$"Day={DaySeconds}s Night={NightSeconds}s StartingPhase={StartingPhase} Paused={Paused} " +
			$"IsRunning={IsRunning}");

		_phase = Mathf.Clamp(StartingPhase, 0f, 1f);
		_currentSky = _environment?.Sky;

		// Apply the starting look immediately so the first frame is correct.
		ApplyPhase();

		IsRunning = !Paused;
		SetProcess(IsRunning);
	}

	/// <summary>
	/// Pauses or resumes the cycle. When paused, the current time of day is held;
	/// when resumed, it continues from where it left off.
	/// </summary>
	public void SetRunning(bool running)
	{
		if (running == IsRunning)
		{
			return;
		}
		IsRunning = running;
		SetProcess(running);
	}

	/// <summary>
	/// Jumps the time of day to <paramref name="phase"/> (in [0,1), 0 = midnight,
	/// 0.5 = noon) and applies it immediately. The cycle then continues from
	/// there (if running).
	/// </summary>
	public void SetPhase(float phase)
	{
		_phase = PosMod(phase, 1f);
		ApplyPhase();
	}

	private double _lastCycleLogSec;

	public override void _Process(double delta)
	{
		if (!IsRunning)
		{
			return;
		}
		float dt = (float)delta;
		_phase = PosMod(_phase + dt / FullCycleSeconds, 1f);
		ApplyPhase();

		// Diagnostic (temporary): confirm the cycle is advancing and applying.
		// Logs once per second with the current phase + sun state.
		if (Time.GetTicksMsec() / 1000.0 - _lastCycleLogSec >= 1.0)
		{
			_lastCycleLogSec = Time.GetTicksMsec() / 1000.0;
			// Print the sun's actual FORWARD vector (-Z of its basis) so we can
			// confirm LIGHT0_DIRECTION is pointing up (y>0) during the day and
			// down (y<0) at night — that's what the sky shader reads.
			Vector3 fwd = _sun != null ? -_sun.GlobalBasis.Z : Vector3.Zero;
			Vector3 mFwd = _moon != null ? -_moon.GlobalBasis.Z : Vector3.Zero;
			GD.Print($"[SkyCycle] phase={_phase:F3} dayAmt={DayAmount:F3} " +
				$"sun(on={(_sun != null && _sun.Visible)},E={(_sun != null ? _sun.LightEnergy.ToString("F2") : "NULL")},fwd=({fwd.X:F2},{fwd.Y:F2},{fwd.Z:F2})) " +
				$"moon(on={(_moon != null && _moon.Visible)},E={(_moon != null ? _moon.LightEnergy.ToString("F2") : "NULL")},fwd=({mFwd.X:F2},{mFwd.Y:F2},{mFwd.Z:F2})) " +
				$"amb={(_environment != null ? _environment.AmbientLightEnergy.ToString("F2") : "NULL")}");
		}
	}

	// ------------------------------------------------------------------
	// Phase → world
	// ------------------------------------------------------------------

	/// <summary>
	/// Applies the current <see cref="_phase"/> to the sun and environment:
	/// rotates the sun to its altitude, fades the sun's energy + the ambient,
	/// and swaps the day/night skybox when the sun crosses the horizon.
	/// </summary>
	private void ApplyPhase()
	{
		if (_sun == null && _environment == null)
		{
			return;
		}

		float dayAmt = DayAmount; // 0 (night) .. 1 (noon)

		// --- Sun rotation ---
		// The sun traces a continuous arc over the course of the WHOLE cycle so
		// the light direction never snaps (a snap would make the sky shader's
		// sunset ramp jump). We map phase → a single angle θ:
		//   * θ = 0          → sunrise (sun on the eastern horizon, altitude 0)
		//   * θ = π/2        → noon   (sun at its zenith, altitude 90°)
		//   * θ = π          → sunset (sun on the western horizon, altitude 0)
		//   * θ = 3π/2       → midnight (sun at its lowest, straight down)
		//   * θ = 2π         → sunrise again
		// The sun's altitude (the Y component of its direction vector) is
		// sin(θ), which is positive for θ ∈ (0, π) (day) and negative for
		// θ ∈ (π, 2π) (night) — exactly the behaviour the sky shader wants
		// (LIGHT0_DIRECTION.y > 0 = day, < 0 = night).
		//
		// CRITICAL: the DirectionalLight3D shines along its -Z (forward) axis,
		// and the sky shader reads LIGHT0_DIRECTION — the direction the light
		// POINTS (its forward vector). So the light's forward must be
		// (-cosθ, +sinθ, 0): at noon (θ=π/2) the forward is (0,+1,0) = straight
		// UP (light shines down onto the ground from the sky, and
		// LIGHT0_DIRECTION.y = +1 = day). The OLD code used a single Z-axis
		// rotation (0,0,θ-π/2), which at noon gave forward (0,-1,0) = straight
		// DOWN — the light pointed into the ground all day, so the sky shader
		// saw LIGHT0_DIRECTION.y ≈ -1 (night) the whole time and nothing
		// visibly changed even though the phase/energy numbers advanced.
		//
		// The phase→arc mapping is the FIXED 24h clock (see PhaseToArcAngle):
		// θ = 2π·phase − π/2, so phase 0 = midnight (nadir), 0.25 = 06:00
		// (sunrise), 0.5 = 12:00 (noon, zenith), 0.75 = 18:00 (sunset), 1 =
		// 24:00 (midnight). The sun angle always matches the clock, so
		// "settime 12" → sun at zenith, "settime 0" → sun at nadir, etc.
		float arcAngle = PhaseToArcAngle(_phase);

		// Point the light's -Z (forward) along (-cosθ, +sinθ, 0). We build the
		// basis directly so the forward is exact: the Z column is -forward
		// (because forward = -Z ⇒ Z = -forward), X is any perpendicular in the
		// arc plane, Y = Z × X. This is equivalent to look_at but written out so
		// the forward is unambiguous. At noon θ=π/2: forward (0,1,0), Z (0,-1,0).
		float cosT = Mathf.Cos(arcAngle);
		float sinT = Mathf.Sin(arcAngle);
		Vector3 fwd = new Vector3(-cosT, sinT, 0f);   // where the light points
		Vector3 zCol = -fwd;                          // (cosT, -sinT, 0)
		Vector3 xCol = new Vector3(sinT, cosT, 0f);   // ⊥ to fwd, in the plane
		Vector3 yCol = zCol.Cross(xCol);              // right-handed completion
		_sun.GlobalBasis = new Basis(xCol, yCol, zCol);

		// --- Sun energy + ENABLE (on the DirectionalLight3D, not the Env) ---
		// The sun's energy fades with altitude so the scene brightens at noon
		// and darkens toward the horizon and night. The sun is ENABLED only
		// while it is above the horizon (sin(θ) > 0) and DISABLED at night, so
		// it stops casting a shadow map and contributing light when it's down.
		float sunUp = Mathf.Max(0f, (float)System.Math.Sin(arcAngle)); // 0 at horizon, 1 at noon
		// The sun is "up" while its altitude is >= 0 (on or above the horizon).
		// Using >= (not >) means at EXACTLY sunrise/sunset (altitude 0) the sun
		// is still enabled and the moon is off — so "settime 0.25" (sunrise)
		// shows the sun on the horizon, not the moon. (With a strict >, a phase
		// landing exactly on the horizon would disable the sun and enable the
		// moon, which looked like "the time is inverted.")
		bool sunAbove = (float)System.Math.Sin(arcAngle) >= 0f;
		_sun.Visible = sunAbove;
		_sun.LightEnergy = Mathf.Lerp(NightSunEnergy, DaySunEnergy, sunUp);

		// --- Moon (second directional light, night only) ---
		// The moon is ENABLED only while the sun is BELOW the horizon
		// (sin(θ) < 0) and DISABLED by day, so it never double-lights the scene.
		// When the sun is disabled, the moon becomes the first enabled
		// directional light (LIGHT0) and drives the sky shader's day/night
		// blend — so the moon's forward must point DOWN (y < 0) to keep the sky
		// in "night" mode, and the sky shader then renders the moon's disc
		// toward -LIGHT0_DIRECTION (up in the sky).
		//
		// On the fixed 24h clock the NIGHT window is phase [0.75, 1) ∪ [0,
		// 0.25) (18:00 → 06:00). The moon arcs across it: φ = 0 at sunset
		// (18:00, phase 0.75, rising), φ = π/2 at midnight (phase 0, zenith,
		// shining straight down), φ = π at sunrise (06:00, phase 0.25,
		// setting). We measure the night progress from 0.75 forward through 1.0
		// and wrapping to 0.25. Its energy fades to 0 at the horizon (soft in/out).
		const float NightStartPhase = 0.75f; // sunset (18:00)
		const float NightLenPhase = 0.5f;     // night spans 0.5 of the cycle (12h)
		if (_moon != null)
		{
			bool moonUp = !sunAbove;
			_moon.Visible = moonUp;
			if (moonUp)
			{
				// Night progress 0..1 from sunset (0.75) through midnight (0) to
				// sunrise (0.25). Handle the wraparound past phase 1.0.
				float nightT = (_phase >= NightStartPhase)
					? (_phase - NightStartPhase) / NightLenPhase
					: (_phase + 1f - NightStartPhase) / NightLenPhase;
				float phi = nightT * Mathf.Pi; // 0..π over the night
				float cosP = Mathf.Cos(phi);
				float sinP = Mathf.Sin(phi);
				// Moon forward: (cosφ, -sinφ, 0). At midnight φ=π/2 → (0,-1,0)
				// (shining straight down onto the ground). y = -sinφ ≤ 0 → the
				// sky shader stays in night mode; -forward.y = sinφ ≥ 0 → the
				// moon disc renders up in the sky.
				Vector3 mFwd = new Vector3(cosP, -sinP, 0f);
				Vector3 mZ = -mFwd;
				Vector3 mX = new Vector3(sinP, cosP, 0f);
				Vector3 mY = mZ.Cross(mX);
				_moon.GlobalBasis = new Basis(mX, mY, mZ);
				// Fade the moon's energy with its altitude (0 at the horizon,
				// MoonEnergy at the zenith) so it softly fades in at sunset and
				// out at sunrise rather than popping on/off.
				_moon.LightEnergy = MoonEnergy * sinP;
			}
			else
			{
				_moon.LightEnergy = 0f;
			}
		}

		// --- Ambient light (on the Environment) ---
		// The ambient (sky) light lifts non-shadowed surfaces; fade it at night
		// so the world darkens. The sky's own ambient-sky term (from the
		// shader sky) also contributes and tracks the sky colour automatically.
		_environment.AmbientLightSource = Environment.AmbientSource.Sky;
		_environment.AmbientLightEnergy = Mathf.Lerp(NightAmbientEnergy, DayAmbientEnergy, dayAmt);

		// --- Skybox swap ---
		// Show the day skybox while the sun is above the horizon (dayAmt > 0)
		// and the night skybox when it is below. The swap happens exactly at the
		// sunrise/sunset boundary; the sky shader's own day↔night colour blend
		// (driven by the light direction) smooths the transition, so a hard
		// resource swap at the horizon is not visible.
		if (SkyboxConfig != null)
		{
			Sky wanted = dayAmt > 0f ? SkyboxConfig.DaySky : SkyboxConfig.NightSky;
			if (wanted != null && wanted != _currentSky)
			{
				_environment.Sky = wanted;
				_currentSky = wanted;
			}
		}
	}

	/// <summary>
	/// Positive modulo: returns <c>x - m * floor(x / m)</c>, always in [0, m).
	/// (C# % can return negative for negative x, which would break the phase
	/// wrap; this keeps the phase in [0,1) regardless of direction.)
	/// </summary>
	private static float PosMod(float x, float m)
	{
		float r = x % m;
		return r < 0f ? r + m : r;
	}

	/// <summary>
	/// Depth-first search for the first descendant of <paramref name="root"/>
	/// that is a <typeparamref name="T"/> (or is itself), optionally skipping a
	/// specific node (<paramref name="skip"/>). Used as a fallback when the
	/// expected group is empty (e.g. the scene editor dropped the group entry),
	/// so the cycle can still find the sun / moon / world environment. When
	/// looking for the MOON we pass the sun as <paramref name="skip"/> so the
	/// walk doesn't just grab the sun (the first DirectionalLight3D in tree
	/// order). Returns null when <paramref name="root"/> is null or no such
	/// node exists.
	/// </summary>
	private static T FindDescendantOfType<T>(Node root, Node skip = null) where T : Node
	{
		if (ReferenceEquals(root, skip))
		{
			return null;
		}
		if (root is T match)
		{
			return match;
		}
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			var result = FindDescendantOfType<T>(root.GetChild(i), skip);
			if (result != null)
			{
				return result;
			}
		}
		return null;
	}
}
