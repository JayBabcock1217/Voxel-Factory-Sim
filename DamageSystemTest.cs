using Godot;
using System.Collections.Generic;
using FactorySim.Voxel;

/// <summary>
/// Verification suite for the SHARED voxel-damage system. Confirms that
/// VoxelWorld.DamageVoxel — the single choke point every damage source
/// (tools, weapons, explosions, zombies) funnels through — behaves like the
/// tool path: it writes the voxel's health, persists it, and TRIGGERS THE
/// CHUNK LIGHTING UPDATE (the instant-edit path + a light-dirty full rebuild),
/// so a weapon/blast-damaged voxel re-lits its chunk exactly like a
/// tool-damaged one.
///
/// Mirrors the WorldSaveTest / McRewriteTest style: a Godot node script run
/// from the editor (NOT the CLI) that prints "DAMAGE_SYSTEM_TEST: ALL PASS" or
/// "DAMAGE_SYSTEM_TEST: N FAILURE(S)" and quits.
///
/// Runs from the editor: it needs the Godot scene tree (the VoxelWorld node
/// lifecycle) and a synchronously-created chunk.
/// </summary>
public partial class DamageSystemTest : Node
{
	private int _failures = 0;

	public override void _Ready()
	{
		TestDamageTriggersChunkLightUpdate();
		TestDamagePersistsAndCombines();
		TestUnbreakableAndAirTakeNoDamage();
		TestDamageTriggersSectionLightDirty();

		GD.Print(_failures == 0 ? "DAMAGE_SYSTEM_TEST: ALL PASS" : $"DAMAGE_SYSTEM_TEST: {_failures} FAILURE(S)");
		GetTree().Quit(_failures == 0 ? 0 : 1);
	}

	// ------------------------------------------------------------------
	// A VoxelWorld with one synchronously-created, all-solid chunk.
	// ------------------------------------------------------------------
	private (VoxelWorld World, VoxelChunk Chunk) MakeWorld()
	{
		var world = new VoxelWorld
		{
			ChunkSizeXZ = 16,
			ChunkHeight = 128,
		};
		world._Ready();

		var data = new ChunkData(16, 128, 16);
		// Fill the chunk with a solid, breakable material (Placed) so the damage
		// path has a real solid voxel to work on.
		for (int y = 0; y < 128; y++)
		{
			for (int z = 0; z < 16; z++)
			{
				for (int x = 0; x < 16; x++)
				{
					data.Set(x, y, z, VoxelMaterialIds.Placed);
				}
			}
		}
		var chunk = new VoxelChunk { Name = "Chunk_0_0" };
		chunk.Initialize(Vector2I.Zero, data);
		world.RegisterChunkForTest(Vector2I.Zero, chunk);
		chunk.DataReady = true;
		return (world, chunk);
	}

	// ------------------------------------------------------------------
	// TEST 1: damaging a voxel triggers the chunk lighting update.
	// ------------------------------------------------------------------
	private void TestDamageTriggersChunkLightUpdate()
	{
		var (world, chunk) = MakeWorld();
		var v = new Vector3I(8, 60, 8);

		// A fresh chunk: not light-dirty, not instant-satisfied.
		Check("light: fresh chunk is not section-light-dirty", !chunk.SectionLightDirty);

		// Damage the voxel (below break threshold — it stays solid).
		float health = world.DamageVoxelForTest(v, 30f);
		Check("light: damage below threshold leaves voxel solid (health 70)",
			Mathf.IsEqualApprox(health, 70f));
		Check("light: damaged voxel is still Placed",
			world.GetVoxel(v.X, v.Y, v.Z) == VoxelMaterialIds.Placed);

		// The chunk lighting update must have been triggered. Two independent
		// signals prove it:
		//  * the chunk is marked light-dirty so the budget loop runs the async
		//    FULL rebuild (the authoritative sky-light re-bake) — the part that
		//    was missing before this change, AND
		//  * the instant (sub-cell) path's edit is REGISTERED on the world: the
		//    damaged voxel's chunk is now the world's pending instant-rebuild
		//    chunk (the _instantRebuildChunk the _Process instant path applies).
		//    (InstantSatisfied itself is set by that path at APPLY time in
		//    _Process, which does not run in this headless test — so we assert on
		//    the registration that the apply consumes, not the apply's flag.)
		Check("light: damage marks the chunk SECTION-LIGHT-DIRTY (full rebuild will run)",
			chunk.SectionLightDirty);
		Check("light: damage registers the chunk as the pending instant-rebuild edit",
			ReferenceEquals(world.PendingInstantRebuildChunkForTest, chunk));
	}

	// ------------------------------------------------------------------
	// TEST 2: damage persists (survives a SetVoxel re-write) and combines.
	// ------------------------------------------------------------------
	//
	// THE KEY BEHAVIOUR this change fixes: SetVoxel previously cleared the
	// damaged-health bookkeeping on EVERY write, so DamageVoxel's own chunk-light
	// update (which re-applies the voxel through SetVoxel) would have wiped the
	// damage it just recorded. The fix makes SetVoxel preserve the damaged entry
	// while the voxel is below full health. This test verifies:
	//   (a) damage persists across the no-op SetVoxel write DamageVoxel makes,
	//   (b) damage COMBINES across repeated calls (each reads the current health),
	//   (c) a genuine replace (material change) still resets health to full.
	// ------------------------------------------------------------------
	private void TestDamagePersistsAndCombines()
	{
		var (world, _) = MakeWorld();
		var v = new Vector3I(4, 64, 4);

		// Directly inspect the chunk's raw health storage around the damage to
		// see whether SetVoxel (the chunk-light update inside DamageVoxel)
		// resets it.
		float h1 = world.DamageVoxelForTest(v, 20f);
		Check("combine: first damage -> health 80", Mathf.IsEqualApprox(h1, 80f));

		float h2 = world.DamageVoxelForTest(v, 30f);
		Check("combine: damage COMBINES (20+30) -> health 50", Mathf.IsEqualApprox(h2, 50f));

		// The world's stored health must agree with the combined damage (this is
		// what the tool reads to derive the shared baseline, and what a save
		// captures).
		Check("combine: world GetVoxelHealth agrees (50)",
			Mathf.IsEqualApprox(world.GetVoxelHealth(v), 50f));
		Check("combine: damaged-health persistence recorded (50)",
			world.GetDamagedHealth().ContainsKey(v)
				&& Mathf.IsEqualApprox(world.GetDamagedHealth()[v], 50f));

		// Damage down to exactly 0 (the break threshold) — the voxel is at zero
		// health but NOT yet broken (no SetVoxel/Air write). Persistence should
		// drop it (health < MaxHealth is false... actually 0 < 100 is true, so it
		// stays recorded at 0). Verify the read is 0.
		float h3 = world.DamageVoxelForTest(v, 50f);
		Check("combine: damage to zero -> health 0", Mathf.IsEqualApprox(h3, 0f));

		// A GENUINE replace (material change) resets health to full and clears the
		// stale damage entry.
		world.SetVoxelForTest(v, VoxelMaterialIds.Stone);
		Check("replace: a material change resets health to full",
			Mathf.IsEqualApprox(world.GetVoxelHealth(v), VoxelHealth.MaxHealth));
		Check("replace: material change clears the damaged-health entry",
			!world.GetDamagedHealth().ContainsKey(v));
	}

	// ------------------------------------------------------------------
	// TEST 3: air and unbreakable voxels take no damage.
	// ------------------------------------------------------------------
	private void TestUnbreakableAndAirTakeNoDamage()
	{
		var (world, chunk) = MakeWorld();

		// Air: set a voxel to air, damage it — health stays full and no damage
		// is recorded (air has nothing to damage). We verify via the health
		// read + persistence (the chunk flags are read-only, but a no-op damage
		// path never writes them).
		var airV = new Vector3I(2, 70, 2);
		world.SetVoxelForTest(airV, VoxelMaterialIds.Air);
		float airHealth = world.DamageVoxelForTest(airV, 100f);
		Check("air: air voxel reports full health",
			Mathf.IsEqualApprox(airHealth, VoxelHealth.MaxHealth));
		Check("air: air voxel not recorded as damaged",
			!world.GetDamagedHealth().ContainsKey(airV));

		// Unbreakable (bedrock): a bedrock voxel takes no damage and is never
		// recorded (the shared rule the miner and the blast use).
		var rockV = new Vector3I(3, 72, 3);
		world.SetVoxelForTest(rockV, VoxelMaterialIds.Bedrock);
		float rockHealth = world.DamageVoxelForTest(rockV, 100f);
		Check("bedrock: unbreakable voxel takes no damage (stays full)",
			Mathf.IsEqualApprox(rockHealth, VoxelHealth.MaxHealth));
		Check("bedrock: unbreakable voxel not recorded as damaged",
			!world.GetDamagedHealth().ContainsKey(rockV));
	}

	// ------------------------------------------------------------------
	// TEST 4: repeated damage on DIFFERENT voxels in the same chunk each trigger
	// a chunk-light update (a blast damaging many voxels re-lits the chunk once
	// per frame, with the last write's instant path + the light-dirty flag).
	// ------------------------------------------------------------------
	private void TestDamageTriggersSectionLightDirty()
	{
		var (world, chunk) = MakeWorld();

		// Simulate a blast: damage several voxels in the same chunk.
		world.DamageVoxelForTest(new Vector3I(10, 80, 10), 25f);
		world.DamageVoxelForTest(new Vector3I(11, 80, 10), 25f);
		world.DamageVoxelForTest(new Vector3I(10, 81, 10), 25f);

		Check("blast: chunk is section-light-dirty after multi-voxel damage",
			chunk.SectionLightDirty);
		Check("blast: chunk is registered as the pending instant-rebuild edit",
			ReferenceEquals(world.PendingInstantRebuildChunkForTest, chunk));
	}

	// ------------------------------------------------------------------
	private void Check(string name, bool ok)
	{
		if (ok)
		{
			GD.Print($"  PASS  {name}");
		}
		else
		{
			_failures++;
			GD.Print($"  FAIL  {name}");
		}
	}
}
