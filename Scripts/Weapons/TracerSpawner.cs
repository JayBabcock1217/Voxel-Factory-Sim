using Godot;

namespace FactorySim.Weapons;

/// <summary>
/// Spawns <see cref="Tracer"/> nodes — the visual debug lines for raycast
/// (hitscan) shots. Mirrors <see cref="ProjectileSpawner"/>: a shared
/// "Tracers" container under the scene root. Tracers are cheap and short-lived
/// (a few tenths of a second), so even the shotgun's 8-pellet spread stays
/// within a small node count.
/// </summary>
public static class TracerSpawner
{
	/// <summary>Draws a tracer line from <paramref name="start"/> to <paramref name="end"/>.</summary>
	public static Tracer Spawn(Node root, Vector3 start, Vector3 end, Color color)
	{
		if (root == null)
		{
			return null;
		}
		Node3D tracersRoot = root.GetNodeOrNull<Node3D>("Tracers");
		if (tracersRoot == null)
		{
			tracersRoot = new Node3D { Name = "Tracers" };
			root.AddChild(tracersRoot);
		}
		var tracer = new Tracer { Name = "Tracer" };
		tracersRoot.AddChild(tracer);
		tracer.Initialize(start, end, color);
		return tracer;
	}
}
