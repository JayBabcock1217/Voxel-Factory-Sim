using Godot;
using FactorySim.Voxel;

namespace FactorySim.Weapons;

/// <summary>
/// Applies area damage to the voxel world. The rocket launcher uses this to
/// damage EVERY solid voxel in a small sphere around its impact point (see
/// <see cref="WeaponBehavior.BlastRadius"/>). It uses the SAME system the tools
/// use: every affected voxel's damage is written through <see
/// cref="VoxelWorld.DamageVoxel"/> — the shared damage choke point the tools,
/// weapons, and zombies all funnel through. Damage accumulates across blasts
/// (and across any other source's damage), and once a voxel's accumulated
/// damage reaches its full health (<see cref="VoxelHealth.MaxHealth"/>), the
/// voxel is broken via <see cref="VoxelWorld.BreakVoxel"/> (which re-meshes the
/// chunk and raises the <c>VoxelBroken</c> event so the drop pipeline runs).
///
/// The impact voxel takes the full <paramref name="amount"/> (so it always
/// breaks); damage falls off linearly to zero at the sphere's edge, so the
/// result is a smooth crater rather than a hard cube of holes. Unbreakable
/// voxels (bedrock) are skipped — they can't be damaged or broken, the same
/// rule the miner uses — and air voxels are skipped (nothing to damage).
///
/// Because the damage funnels through <see cref="VoxelWorld.DamageVoxel"/>, the
/// voxel's stored health IS the source of truth for when it breaks (the method
/// reads the current health, writes the new one, and persists it) — and the
/// chunk lighting update is triggered for every damaged voxel, so a
/// blast-damaged voxel re-lits its chunk exactly like a tool-damaged one.
/// </summary>
public static class ExplosionApplier
{
	/// <summary>
	/// Damages every voxel within <paramref name="radius"/> (inclusive, a true
	/// sphere) of <paramref name="center"/>, breaking any whose damage reaches
	/// full health (<see cref="VoxelHealth.MaxHealth"/>, 100). Damage profile:
	/// FULL <paramref name="amount"/> for every voxel within HALF the radius
	/// (a flat "core" that always breaks), then a linear drop-off from
	/// <paramref name="amount"/> (at the half-radius) to zero (at the full
	/// radius). So with radius 10: 100 damage to everything within 5 voxels,
	/// tapering linearly to 0 by the 10-voxel edge. Returns the number of
	/// voxels broken. A radius of 0 breaks just the center voxel.
	///
	/// THE SHARED DAMAGE SYSTEM: each affected voxel's damage is written through
	/// <see cref="VoxelWorld.DamageVoxel"/> — the SAME method the tools and
	/// zombies use. That method is the single choke point for voxel damage: it
	/// owns the per-voxel health, the break rule (health reaches 0 → the voxel
	/// breaks via <see cref="VoxelWorld.BreakVoxel"/>), the persistence
	/// (<c>_damagedHealth</c>), and the chunk LIGHTING update (the damaged voxel's
	/// surface is re-lit through the same instant-edit path a break or a place
	/// uses). So a blast-damaged voxel behaves identically to a tool-damaged one,
	/// and its chunk's lighting always converges. Unbreakable voxels (bedrock)
	/// are skipped here (the blast never breaks them), and <see
	/// cref="VoxelWorld.DamageVoxel"/> drops damage to them too, so the two
	/// rules agree (bedrock can't be damaged or broken by any source).
	///
	/// Damage accumulates ACROSS blasts (and across tool/weapon/zombie damage):
	/// DamageVoxel reads the voxel's current health, so a half-damaged voxel
	/// needs less blast damage to break than a fresh one.
	/// </summary>
	public static int Apply(VoxelWorld world, Vector3I center, int radius, float amount)
	{
		if (world == null)
		{
			return 0;
		}

		int r = Mathf.Max(0, radius);
		float rF = Mathf.Max(1f, r);         // full radius (falloff outer edge)
		float coreR = rF * 0.5f;             // inner core: full damage inside this
		// Collect the broken voxels (to break them in ONE batch — one sky BFS
		// + one geometry remesh, not one per voxel. See BreakVoxelBatch).
		var toBreak = new System.Collections.Generic.List<Vector3I>();

		for (int dx = -r; dx <= r; dx++)
		for (int dy = -r; dy <= r; dy++)
		for (int dz = -r; dz <= r; dz++)
		{
			// Only voxels inside the sphere (a true ball, not a cube).
			float distSq = dx * dx + dy * dy + dz * dz;
			if (distSq > r * r)
			{
				continue;
			}

			var v = center + new Vector3I(dx, dy, dz);
			// Only damage SOLID voxels: air has nothing to damage, and
			// unbreakable voxels (bedrock) are skipped (the blast never breaks
			// them — the same rule the miner uses).
			int material = world.GetVoxel(v.X, v.Y, v.Z);
			if (material == VoxelMaterialIds.Air || world.IsUnbreakable(material))
			{
				continue;
			}

			float dist = Mathf.Sqrt(distSq);

			// Damage profile:
			//   dist <= coreR (half radius) → full damage (guaranteed break).
			//   coreR < dist <= r          → linear falloff from `amount` (at
			//                                coreR) down to 0 (at r).
			float damage;
			if (dist <= coreR)
			{
				damage = amount;
			}
			else
			{
				// (r - dist) / (r - coreR): 1.0 at dist=coreR, 0.0 at dist=r.
				damage = amount * ((rF - dist) / (rF - coreR));
			}
			damage = Mathf.Clamp(damage, 0f, float.MaxValue);

			// Write the damage through the SHARED system (VoxelWorld.DamageVoxel,
			// the same method the tools and zombies use). It records the voxel's
			// new health, persists it, and triggers the chunk lighting update —
			// all the bookkeeping a tool break does, from one place. Damage
			// accumulates across blasts (DamageVoxel reads the current health).
			float healthAfter = world.DamageVoxel(v, damage);
			if (healthAfter <= 0f)
			{
				// The voxel's health reached zero: collect it for the batched
				// break (the actual break + light + remesh happens in
				// BreakVoxelBatch below — one BFS + one remesh for all of them).
				toBreak.Add(v);
			}
		}

		if (toBreak.Count == 0)
		{
			return 0;
		}

		// Break all the collected voxels in ONE batch (one sky BFS seeded from
		// all of them + one geometry remesh for the union of their regions).
		// This is the fix for the "massive hitch" — a per-voxel BreakVoxel
		// runs a separate sky BFS + 3×3×3 remesh per voxel (dozens of BFSes
		// for a rocket blast).
		return world.BreakVoxelBatch(toBreak.ToArray());
	}
}
