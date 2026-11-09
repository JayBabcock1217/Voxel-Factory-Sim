using Godot;

namespace FactorySim;

/// <summary>
/// The configurable set of SKYBOXES for the day/night cycle. Each slot is a
/// <see cref="Sky"/> resource (the BinbunSky shader skies under
/// <c>res://Textures/SkiesFull/</c>), swappable in the inspector so a world can
/// pick a different look (basic / cold / stylized / experimental) without code
/// changes.
///
/// <see cref="DaySky"/> and <see cref="NightSky"/> are the two slots the
/// <see cref="SkyCycle"/> crossfades between as the sun rises and sets. The
/// other slots (stylized, cold, ...) are extra options the user can drag into
/// either slot. The durations (day / night length) live on the
/// <see cref="SkyCycle"/> node itself so the timing is tuned in the same place
/// the cycle is driven.
/// </summary>
[GlobalClass]
public partial class SkyboxConfig : Resource
{
	/// <summary>
	/// The skybox shown at full day (sun high). This is the dominant sky the
	/// player sees most of the cycle.
	/// </summary>
	[Export] public Sky DaySky { get; set; }

	/// <summary>
	/// The skybox shown at full night (sun below the horizon). The cycle
	/// crossfades day → night at the horizon, so the transition blends between
	/// these two resources.
	/// </summary>
	[Export] public Sky NightSky { get; set; }

	/// <summary>
	/// An alternate stylized skybox (BinbunSky "stylized" set) — available as a
	/// drop-in option for either the day or night slot.
	/// </summary>
	[Export] public Sky StylizedSky { get; set; }

	/// <summary>
	/// An alternate cold-palette skybox (BinbunSky "cold" set) — available as a
	/// drop-in option for either the day or night slot.
	/// </summary>
	[Export] public Sky ColdSky { get; set; }
}
