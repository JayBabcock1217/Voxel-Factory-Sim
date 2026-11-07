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
	// The box's cross-section width (world units). Wide enough to be visible
	// at range, narrow enough to read as a "line".
	private const float BoxWidth = 0.12f;

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

		// Place the box at the midpoint of the segment (local to the tracer
		// node, which is at `start`).
		_mesh.Position = segment * 0.5f;
		_mesh.Scale = new Vector3(1f, length, 1f);

		// Orient the box so its local +Y axis lies along the segment direction.
		// Build a Basis whose Y column is the direction, with an arbitrary
		// (non-parallel) up vector for the remaining axes.
		var up = Mathf.Abs(dir.Dot(Vector3.Up)) > 0.99f
			? Vector3.Right
			: Vector3.Up;
		var x = up.Cross(dir).Normalized();
		var y = dir;
		var z = x.Cross(y).Normalized();
		_mesh.Basis = new Basis(x, y, z);
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
