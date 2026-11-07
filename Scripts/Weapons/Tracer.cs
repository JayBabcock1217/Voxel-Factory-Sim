using Godot;

namespace FactorySim.Weapons;

/// <summary>
/// A short-lived tracer line drawn from a muzzle point to an impact point, so
/// the player can SEE where a raycast (hitscan) shot went — the "visual debug"
/// for the SMG and shotgun. It is a thin box scaled to the segment's length and
/// oriented to lie along it, bright for a moment and then fading out.
///
/// One node is spawned per pellet (the shotgun fires several at once), parented
/// to the shared "Tracers" container under the scene root (created by
/// <see cref="TracerSpawner"/>). Tracers are purely visual — they do no damage
/// and have no physics; they exist only to make hitscan shots visible.
///
/// The tracer node itself is positioned at the START point (the camera). A
/// single child MeshInstance3D (a box) is placed at the midpoint of the segment,
/// scaled to the segment length, and rotated so its long axis (local +Y) lies
/// along the segment direction (an explicit Basis, no LookAt, so the orientation
/// is deterministic).
///
/// VISIBILITY TUNING: a 60-unit line drawn at a fixed 0.05-unit width is
/// sub-pixel at distance, so only the near portion registered on screen (it
/// looked like a short stub). The box is now WIDER (0.12), the lifetime is
/// LONGER (0.35s), and the fade HOLDS at full brightness for the first 40% of
/// the life before easing out — so the whole line is clearly visible the entire
/// way out, not just the middle.
/// </summary>
[GlobalClass]
public partial class Tracer : Node3D
{
	// How long the tracer lives (seconds). Long enough to be tracked by the eye.
	private const float Lifetime = 0.35f;
	// The fraction of the lifetime spent at full brightness before fading.
	private const float HoldFraction = 0.4f;
	// The box's cross-section width (world units). Kept thin so the tracer
	// reads as a "line" rather than a beam; the earlier 0.12 looked chunky.
	private const float BoxWidth = 0.03f;

	private float _age;
	private StandardMaterial3D _material;
	private MeshInstance3D _mesh;

	public void Initialize(Vector3 start, Vector3 end, Color color)
	{
		_material = new StandardMaterial3D
		{
			AlbedoColor = color,
			EmissionEnabled = true,
			Emission = color,
			EmissionEnergyMultiplier = 3f,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};

		// The tracer node sits at the START (camera) so the line clearly
		// originates from the player's viewpoint. The child mesh is the box.
		GlobalPosition = start;

		Vector3 segment = end - start;
		float length = segment.Length();
		if (length < 0.001f)
		{
			// Degenerate segment (start == end); nothing to draw.
			return;
		}
		Vector3 dir = segment / length;

		// A box, unit-tall along local +Y, scaled to the segment length. The
		// cross-section (BoxWidth) is wide enough to stay visible at distance.
		var box = new BoxMesh
		{
			Size = new Vector3(BoxWidth, 1f, BoxWidth),
			Material = _material,
		};
		_mesh = new MeshInstance3D
		{
			Name = "TracerMesh",
			Mesh = box,
			MaterialOverride = _material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_mesh);

		// Orient the box so its local +Y axis lies along the segment, and scale
		// it to the segment length. CRITICAL: the scale must be baked INTO the
		// basis (the Y column scaled by `length`) rather than set via
		// _mesh.Scale and then _mesh.Basis — setting Basis REPLACES the basis
		// columns and would clobber the scale that Scale wrote into them,
		// leaving the box at unit length (a ~1-unit stub no matter how far the
		// shot goes). Baking it into the basis avoids that.
		var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f
			? Vector3.Right
			: Vector3.Up;
		var x = up.Cross(dir).Normalized();
		var z = x.Cross(dir).Normalized();
		var y = dir * length; // Y column carries the length scale
		_mesh.Basis = new Basis(x, y, z);

		// Place the box at the midpoint of the segment (local to the tracer
		// node, which is at `start`). The box spans -length/2..+length/2 along
		// local +Y, so centering it on the midpoint puts its ends at start/end.
		_mesh.Position = segment * 0.5f;
	}

	public override void _Process(double delta)
	{
		_age += (float)delta;
		float t = _age / Lifetime;
		if (t >= 1f)
		{
			if (!IsQueuedForDeletion())
			{
				QueueFree();
			}
			return;
		}
		// Hold at full brightness for the first HoldFraction of the life, then
		// ease the rest out. This keeps the WHOLE line visible for a moment
		// (so the player can see the full trajectory) before it fades, rather
		// than fading linearly from frame one (which made only the bright
		// middle portion readable).
		float alpha;
		if (t <= HoldFraction)
		{
			alpha = 1f;
		}
		else
		{
			float fadeT = (t - HoldFraction) / (1f - HoldFraction); // 0..1
			alpha = 1f - fadeT * fadeT; // ease-out (quadratic)
		}
		var c = _material.AlbedoColor;
		_material.AlbedoColor = new Color(c, alpha);
		_material.Emission = new Color(c, alpha);
	}
}
