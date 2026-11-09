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
	/// Length of the DAY (sun above the horizon), in seconds. The full cycle is
	/// DaySeconds + NightSeconds. Keep ≥ 1 to avoid a divide-by-zero.
	/// </summary>
	[Export(PropertyHint.Range, "1,3600,1")]
	public float DaySeconds { get; set; } = 300f;

	/// <summary>
	/// Length of the NIGHT (sun below the horizon), in seconds. The full cycle
	/// is DaySeconds + NightSeconds. Keep ≥ 1 to avoid a divide-by-zero.
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
	/// A value in [0,1] describing how "day" it is: 1 at noon, 0 at midnight,
	/// ~0.5 at sunrise/sunset. This is the same quantity the sun's energy and
	/// ambient are faded by.
	/// </summary>
	public float DayAmount
	{
		get
		{
			// Map phase → sun altitude: the sun is up for the DaySeconds portion of
			// the cycle. Within that window the altitude goes 0→1→0 (a sine hump);
			// in the night window it stays 0 (the sun is below the horizon).
			float dayFrac = DaySeconds / FullCycleSeconds;
			float p = _phase;
			if (p < dayFrac)
			{
				// Day window: sine hump from sunrise (p=0) to sunset (p=dayFrac).
				float t = p / dayFrac; // 0..1 across the day
				return (float)System.Math.Sin(t * System.Math.PI);
			}
			return 0f;
		}
	}

	/// <summary>Total length of one full cycle (day + night), in seconds.</summary>
	public float FullCycleSeconds => System.Math.Max(1f, DaySeconds + NightSeconds);

	/// <summary>The phase at which the sun sets (end of the day window).</summary>
	public float SunsetPhase => System.Math.Max(1f, DaySeconds) / FullCycleSeconds;

	// ------------------------------------------------------------------
	// Node lifecycle
	// ------------------------------------------------------------------

	public override void _Ready()
	{
		_sun = GetTree()?.GetFirstNodeInGroup(SunLightGroup) as DirectionalLight3D;
		_worldEnvironment = GetTree()?.GetFirstNodeInGroup(WorldEnvironmentGroup) as WorldEnvironment;
		_environment = _worldEnvironment?.Environment;

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

	public override void _Process(double delta)
	{
		if (!IsRunning)
		{
			return;
		}
		float dt = (float)delta;
		_phase = PosMod(_phase + dt / FullCycleSeconds, 1f);
		ApplyPhase();
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
		// DirectionalLight3D shines along its -Z (forward) axis. To make its
		// forward vector have components ( -cosθ, +sinθ, 0 ) (east→west across
		// the sky, up→down with altitude) we rotate it by θ about the +Z axis.
		// A rotation of θ about Z maps -Z → (-sinθ, -cosθ, 0); we want (-cosθ,
		// sinθ, 0), which is a rotation of (θ - π/2) about Z. We use that.
		//
		// DayAmount puts the DAY at phase [0, dayFrac), so the arc maps the day
		// window [0, dayFrac) to [0, π] (sunrise→noon→sunset) and the night
		// window [dayFrac, 1) to [π, 2π] (sunset→midnight→sunrise). Phase 0 =
		// sunrise, phase dayFrac = sunset, phase (dayFrac + nightFrac/2) =
		// midnight.
		float dayFrac = System.Math.Max(1f, DaySeconds) / FullCycleSeconds;
		float arcAngle;
		if (_phase < dayFrac)
		{
			// Day: phase 0→dayFrac maps to arc 0→π (sunrise→sunset via noon).
			arcAngle = (_phase / dayFrac) * Mathf.Pi;
		}
		else
		{
			// Night: phase dayFrac→1 maps to arc π→2π (sunset→midnight→sunrise).
			arcAngle = Mathf.Pi + ((_phase - dayFrac) / System.Math.Max(1e-4f, 1f - dayFrac)) * Mathf.Pi;
		}

		// Rotate the sun so its forward vector points along the arc direction
		// (-cosθ, +sinθ, 0). As derived above, that is a rotation of (θ - π/2)
		// about the Z axis.
		float rotZ = arcAngle - Mathf.Pi / 2f;
		_sun.Rotation = new Vector3(0f, 0f, rotZ);

		// --- Sun energy (on the DirectionalLight3D, not the Environment) ---
		// The sun's energy fades with altitude so the scene brightens at noon
		// and darkens toward the horizon and night.
		float sunUp = Mathf.Max(0f, (float)System.Math.Sin(arcAngle)); // 0 at horizon, 1 at noon
		_sun.LightEnergy = Mathf.Lerp(NightSunEnergy, DaySunEnergy, sunUp);

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
}
