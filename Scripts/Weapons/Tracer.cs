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
/// </summary>
[GlobalClass]
public partial class Tracer : Node3D
{
	private float _age;
	private float _lifetime = 0.12f;
	private StandardMaterial3D _material;
	private MeshInstance3D _mesh;
	private Vector3 _start;
	private Vector3 _end;

	public void Initialize(Vector3 start, Vector3 end, Color color)
	{
		_start = start;
		_end = end;

		_material = new StandardMaterial3D
		{
			AlbedoColor = color,
			EmissionEnabled = true,
			Emission = color,
			EmissionEnergyMultiplier = 2.5f,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		};

		// A thin unit box along local +Y, scaled to the segment length and
		// oriented to lie along it (LookAt points local +Y at the target).
		var box = new BoxMesh { Size = new Vector3(0.04f, 1f, 0.04f), Material = _material };
		_mesh = new MeshInstance3D
		{
			Name = "TracerMesh",
			Mesh = box,
			MaterialOverride = _material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
		};
		AddChild(_mesh);

		GlobalPosition = _start;
		_mesh.Position = (_end - _start) * 0.5f;
		_mesh.Scale = new Vector3(1f, (_end - _start).Length(), 1f);
		var dir = (_end - _start).Normalized();
		if (dir.LengthSquared() > 0.0001f)
		{
			_mesh.LookAt(_mesh.GlobalPosition + dir, Vector3.Up);
		}
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
