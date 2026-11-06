using Godot;

namespace FactorySim.Weapons;

/// <summary>
/// A short-lived tracer line drawn from a muzzle point to an impact point, so
/// the player can SEE where a raycast (hitscan) shot went — the "visual debug"
/// for the SMG and shotgun. It is a thin box scaled to the segment's length and
/// oriented to lie along it, fading out over a few tenths of a second.
///
/// One node is spawned per pellet (the shotgun fires several at once), parented
/// to the shared "Tracers" container under the scene root (created by
/// <see cref="TracerSpawner"/>). Tracers are purely visual — they do no damage
/// and have no physics; they exist only to make hitscan shots visible.
///
/// The tracer node itself is positioned at the START point (the muzzle, just in
/// front of the camera). A single child MeshInstance3D (a thin box) is placed at
/// the midpoint of the segment, scaled to the segment length, and rotated so its
/// long axis (local +Y) lies along the segment direction. This is done with an
/// explicit Basis (no LookAt) so the orientation is deterministic and does not
/// depend on the node's current rotation.
/// </summary>
[GlobalClass]
public partial class Tracer : Node3D
{
	private float _age;
	private float _lifetime = 0.15f;
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

		// The tracer node sits at the START (muzzle) so the line clearly
		// originates from the camera. The child mesh is the visible box.
		GlobalPosition = start;

		Vector3 segment = end - start;
		float length = segment.Length();
		if (length < 0.001f)
		{
			// Degenerate segment (start == end); nothing to draw.
			return;
		}
		Vector3 dir = segment / length;

		// A thin box, unit-tall along local +Y, scaled to the segment length.
		var box = new BoxMesh
		{
			Size = new Vector3(0.05f, 1f, 0.05f),
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
		float t = _lifetime > 0f ? _age / _lifetime : 1f;
		if (t >= 1f)
		{
			if (!IsQueuedForDeletion())
			{
				QueueFree();
			}
			return;
		}
		// Fade out as the tracer ages.
		float alpha = Mathf.Clamp(1f - t, 0f, 1f);
		var c = _material.AlbedoColor;
		_material.AlbedoColor = new Color(c, alpha);
		_material.Emission = new Color(c, alpha);
	}
}
