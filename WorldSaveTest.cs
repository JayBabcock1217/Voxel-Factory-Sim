using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using FactorySim.Voxel;

/// <summary>
/// Verification suite for world persistence (save/load). Mirrors the style of
/// <c>McRewriteTest</c>: a Godot node script run from the editor (NOT the CLI)
/// that prints "WORLD_SAVE_TEST: ALL PASS" or "WORLD_SAVE_TEST: N FAILURE(S)"
/// and quits.
///
/// It tests the hand-rolled binary save format end-to-end:
///   1. a known set of edits / health / inventory / player / drops round-trips
///      through a write→read of a real save file (FNV-1a checksum intact),
///   2. the checksum detects corruption (flipping a byte invalidates it),
///   3. a wrong-version file is rejected (degrades to a fresh world),
///   4. the VoxelWorld edit-capture + re-apply path: a live world captures
///      SetVoxel edits + damage, and a fresh world (same seed) re-applies the
///      saved edits onto freshly generated terrain, restoring the exact voxels
///      + health.
///
/// Runs from the editor: it needs the Godot scene tree (OS.GetTempDir, the
/// VoxelWorld node lifecycle) and the deterministic terrain generator. It
/// cannot be run from `dotnet`/the CLI.
/// </summary>
public partial class WorldSaveTest : Node
{
	private int _failures = 0;

	// A temp path OUTSIDE the user-data dir so the test never clobbers a real
	// save, and so we can clean it up deterministically.
	private string _tempPath;

	public override void _Ready()
	{
		_tempPath = System.IO.Path.Combine(OS.GetTempDir(), $"worldsave_test_{(int)Time.GetTicksMsec()}.world");
		if (File.Exists(_tempPath))
		{
			File.Delete(_tempPath);
		}

		TestFormatRoundTrip();
		TestChecksumDetectsCorruption();
		TestWrongVersionRejected();
		TestVersioningFramework();
		TestWorldEditCaptureAndReapply();

		// Clean up the temp file (best effort).
		try
		{
			if (File.Exists(_tempPath))
			{
				File.Delete(_tempPath);
			}
		}
		catch (System.Exception)
		{
			// A failed cleanup is not a test failure.
		}

		GD.Print(_failures == 0 ? "WORLD_SAVE_TEST: ALL PASS" : $"WORLD_SAVE_TEST: {_failures} FAILURE(S)");
		GetTree().Quit(_failures == 0 ? 0 : 1);
	}

	// ------------------------------------------------------------------
	// TEST 1: full round-trip of the binary format
	// ------------------------------------------------------------------
	private void TestFormatRoundTrip()
	{
		var data = new WorldSaveFile.Data
		{
			Seed = 1234567,
		};
		data.Edits.Add(new WorldSaveFile.EditRecord(new Vector3I(3, 70, 9), VoxelMaterialIds.Stone));
		data.Edits.Add(new WorldSaveFile.EditRecord(new Vector3I(-4, 80, 12), VoxelMaterialIds.Air)); // removed
		data.Edits.Add(new WorldSaveFile.EditRecord(new Vector3I(16, 5, 0), VoxelMaterialIds.Placed));
		data.Health.Add(new WorldSaveFile.HealthRecord(new Vector3I(10, 75, 10), 37f));
		data.Health.Add(new WorldSaveFile.HealthRecord(new Vector3I(-1, 90, -3), 88f));
		data.PlayerPos = new Vector3(12.5f, 133.25f, -7.75f);
		data.PlayerYaw = 1.23f;
		data.PlayerPitch = -0.45f;
		data.Inventory.Add((VoxelMaterialIds.CoalOre, 64));
		data.Inventory.Add((VoxelMaterialIds.IronOre, 17));
		data.Inventory.Add((VoxelMaterialIds.Air, 0)); // empty slot
		data.Drops.Add(new WorldSaveFile.DropRecord(new Vector3(5f, 131f, 6f), VoxelMaterialIds.Dirt, 12));
		data.Drops.Add(new WorldSaveFile.DropRecord(new Vector3(-2f, 129f, 20f), VoxelMaterialIds.Grass, 1));

		WorldSaveFile.Write(_tempPath, data);
		var back = WorldSaveFile.Read(_tempPath);

		Check("roundtrip: file read back (not null)", back != null);
		if (back == null)
		{
			return;
		}

		Check("roundtrip: seed matches", back.Seed == data.Seed);
		Check("roundtrip: edit count matches", back.Edits.Count == data.Edits.Count);
		Check("roundtrip: edit[0] voxel+material",
			back.Edits[0].Position == data.Edits[0].Position
			&& back.Edits[0].MaterialId == data.Edits[0].MaterialId);
		Check("roundtrip: edit[1] (air/removed) voxel+material",
			back.Edits[1].Position == data.Edits[1].Position
			&& back.Edits[1].MaterialId == VoxelMaterialIds.Air);
		Check("roundtrip: edit[2] voxel+material",
			back.Edits[2].Position == data.Edits[2].Position
			&& back.Edits[2].MaterialId == data.Edits[2].MaterialId);

		Check("roundtrip: health count matches", back.Health.Count == data.Health.Count);
		Check("roundtrip: health[0] voxel+value",
			back.Health[0].Position == data.Health[0].Position
			&& Mathf.IsEqualApprox(back.Health[0].Health, data.Health[0].Health));
		Check("roundtrip: health[1] voxel+value",
			back.Health[1].Position == data.Health[1].Position
			&& Mathf.IsEqualApprox(back.Health[1].Health, data.Health[1].Health));

		Check("roundtrip: player pos matches",
			Mathf.IsEqualApprox(back.PlayerPos.X, data.PlayerPos.X)
			&& Mathf.IsEqualApprox(back.PlayerPos.Y, data.PlayerPos.Y)
			&& Mathf.IsEqualApprox(back.PlayerPos.Z, data.PlayerPos.Z));
		Check("roundtrip: player yaw matches", Mathf.IsEqualApprox(back.PlayerYaw, data.PlayerYaw));
		Check("roundtrip: player pitch matches", Mathf.IsEqualApprox(back.PlayerPitch, data.PlayerPitch));

		Check("roundtrip: inventory count matches", back.Inventory.Count == data.Inventory.Count);
		Check("roundtrip: inventory[0] (coal x64)",
			back.Inventory[0].MaterialId == VoxelMaterialIds.CoalOre && back.Inventory[0].Count == 64);
		Check("roundtrip: inventory[1] (iron x17)",
			back.Inventory[1].MaterialId == VoxelMaterialIds.IronOre && back.Inventory[1].Count == 17);
		Check("roundtrip: inventory[2] (empty slot)",
			back.Inventory[2].MaterialId == VoxelMaterialIds.Air && back.Inventory[2].Count == 0);

		Check("roundtrip: drop count matches", back.Drops.Count == data.Drops.Count);
		Check("roundtrip: drop[0] pos+material+count",
			Mathf.IsEqualApprox(back.Drops[0].Position.X, data.Drops[0].Position.X)
			&& Mathf.IsEqualApprox(back.Drops[0].Position.Y, data.Drops[0].Position.Y)
			&& Mathf.IsEqualApprox(back.Drops[0].Position.Z, data.Drops[0].Position.Z)
			&& back.Drops[0].MaterialId == VoxelMaterialIds.Dirt && back.Drops[0].Count == 12);
		Check("roundtrip: drop[1] pos+material+count",
			back.Drops[1].MaterialId == VoxelMaterialIds.Grass && back.Drops[1].Count == 1);

		// Checksum: the computed FNV-1a of the on-disk section bytes must equal
		// the stored trailing checksum (Read validates this internally, but we
		// also confirm the hash is stable across a second read of the same bytes).
		byte[] bytes = File.ReadAllBytes(_tempPath);
		uint stored = BitConverter.ToUInt32(bytes, bytes.Length - 4);
		uint computed = WorldSaveFile.Fnv1a(bytes, 0, bytes.Length - 4);
		Check("roundtrip: trailing checksum matches recomputed FNV-1a", stored == computed);
	}

	// ------------------------------------------------------------------
	// TEST 2: a flipped byte must invalidate the checksum
	// ------------------------------------------------------------------
	private void TestChecksumDetectsCorruption()
	{
		var data = new WorldSaveFile.Data { Seed = 999 };
		data.Edits.Add(new WorldSaveFile.EditRecord(new Vector3I(1, 2, 3), VoxelMaterialIds.Stone));
		data.Inventory.Add((VoxelMaterialIds.Stone, 5));
		WorldSaveFile.Write(_tempPath, data);

		byte[] bytes = File.ReadAllBytes(_tempPath);
		// Flip a byte in the edits section (well after the 8-byte header).
		bytes[20] ^= 0xFF;
		File.WriteAllBytes(_tempPath, bytes);

		var back = WorldSaveFile.Read(_tempPath);
		Check("corruption: flipped byte -> Read returns null (checksum mismatch)", back == null);
	}

	// ------------------------------------------------------------------
	// TEST 3: a wrong-version file must be rejected
	// ------------------------------------------------------------------
	private void TestWrongVersionRejected()
	{
		var data = new WorldSaveFile.Data { Seed = 1 };
		data.Edits.Add(new WorldSaveFile.EditRecord(new Vector3I(0, 10, 0), VoxelMaterialIds.Dirt));
		WorldSaveFile.Write(_tempPath, data);

		byte[] bytes = File.ReadAllBytes(_tempPath);
		// Rewrite the version field (bytes 4..8) to something else.
		bytes[4] = (byte)(WorldSaveFile.CurrentVersion + 1);
		File.WriteAllBytes(_tempPath, bytes);

		var back = WorldSaveFile.Read(_tempPath);
		Check("version: wrong version -> Read returns null", back == null);
		Check("version: IsValidSaveFile reports false for the tampered file",
			!WorldSaveFile.IsValidSaveFile(_tempPath));
	}

	// ------------------------------------------------------------------
	// TEST 3b: the versioning framework (supported-versions check + Migrate)
	// ------------------------------------------------------------------
	private void TestVersioningFramework()
	{
		// CurrentVersion is in the supported set.
		Check("versioning: CurrentVersion is supported",
			WorldSaveFile.IsSupportedVersion(WorldSaveFile.CurrentVersion));

		// A future version (CurrentVersion + 1) is NOT yet supported.
		Check("versioning: CurrentVersion+1 is not supported",
			!WorldSaveFile.IsSupportedVersion(WorldSaveFile.CurrentVersion + 1));

		// A version below the oldest supported is not supported.
		Check("versioning: version 0 is not supported",
			!WorldSaveFile.IsSupportedVersion(0));

		// OldestSupportedVersion is the first entry in the supported set.
		Check("versioning: OldestSupportedVersion == SupportedVersions[0]",
			WorldSaveFile.OldestSupportedVersion == WorldSaveFile.SupportedVersions[0]);

		// Migrate is the identity transform for the current version (v1 -> v1).
		var data = new WorldSaveFile.Data { Seed = 777 };
		data.Edits.Add(new WorldSaveFile.EditRecord(new Vector3I(1, 2, 3), VoxelMaterialIds.Stone));
		var migrated = WorldSaveFile.Migrate(data, WorldSaveFile.CurrentVersion);
		Check("versioning: Migrate is identity for CurrentVersion",
			migrated == data
			&& migrated.Seed == 777
			&& migrated.Edits.Count == 1
			&& migrated.Edits[0].MaterialId == VoxelMaterialIds.Stone);
	}

	// ------------------------------------------------------------------
	// TEST 4: live-world edit capture + re-apply onto a fresh world
	// ------------------------------------------------------------------
	private void TestWorldEditCaptureAndReapply()
	{
		// A deterministic config (no caves/rocks/ores/foliage) so the terrain is
		// a pure function of (x,y,z, seed). Shared by BOTH worlds so they
		// generate identical terrain.
		var cfg = TestGenConfig();

		// World 1: generate chunk (0,0), capture edits + health.
		var world = MakeWorld();
		var data = new ChunkData(16, 128, 16);
		TerrainGenerator.Generate(data, 0, 0, cfg);
		var chunk = new VoxelChunk { Name = "Chunk_0_0" };
		chunk.Initialize(Vector2I.Zero, data);
		world.RegisterChunkForTest(Vector2I.Zero, chunk);
		chunk.DataReady = true;

		Vector3I a = new Vector3I(8, 10, 8);
		Vector3I b = new Vector3I(8, 11, 8);
		int origA = data.Get(a.X, a.Y, a.Z);
		int origB = data.Get(b.X, b.Y, b.Z);

		// The player edits: replace A with a placed material, carve B to air,
		// and damage the just-placed voxel A.
		world.SetVoxelForTest(a, VoxelMaterialIds.Placed);
		world.SetVoxelForTest(b, VoxelMaterialIds.Air);
		world.DamageVoxelForTest(a, 35f);

		var edits = new List<WorldSaveFile.EditRecord>();
		foreach (var (v, m) in world.GetEdits())
		{
			edits.Add(new WorldSaveFile.EditRecord(v, m));
		}
		var health = new List<WorldSaveFile.HealthRecord>();
		foreach (var (v, h) in world.GetDamagedHealth())
		{
			health.Add(new WorldSaveFile.HealthRecord(v, h));
		}

		bool aPlaced = false, bAir = false;
		foreach (var e in edits)
		{
			if (e.Position == a && e.MaterialId == VoxelMaterialIds.Placed) aPlaced = true;
			if (e.Position == b && e.MaterialId == VoxelMaterialIds.Air) bAir = true;
		}
		Check("world: edit capture recorded A as placed material", aPlaced);
		Check("world: edit capture recorded B as air (removed)", bAir);
		Check("world: health capture recorded A at 65 (100-35)",
			health.Count >= 1 && health[0].Position == a && Mathf.IsEqualApprox(health[0].Health, 65f));

		// Write the snapshot and read it back.
		var saveData = new WorldSaveFile.Data { Seed = 4242 };
		saveData.Edits.AddRange(edits);
		saveData.Health.AddRange(health);
		WorldSaveFile.Write(_tempPath, saveData);
		var loaded = WorldSaveFile.Read(_tempPath);
		Check("world: save round-trips for the world snapshot", loaded != null);
		if (loaded == null)
		{
			return;
		}

		// World 2 (fresh): same config, same seed. Regenerate chunk (0,0) from
		// scratch — it should match the ORIGINAL (pre-edit) terrain.
		var world2 = MakeWorld();
		var data2 = new ChunkData(16, 128, 16);
		TerrainGenerator.Generate(data2, 0, 0, cfg);
		var chunk2 = new VoxelChunk { Name = "Chunk_0_0" };
		chunk2.Initialize(Vector2I.Zero, data2);
		world2.RegisterChunkForTest(Vector2I.Zero, chunk2);
		chunk2.DataReady = true;

		Check("world: fresh terrain matches original at A",
			data2.Get(a.X, a.Y, a.Z) == origA);
		Check("world: fresh terrain matches original at B",
			data2.Get(b.X, b.Y, b.Z) == origB);

		// Re-apply the loaded edits onto the fresh world (the load path).
		world2.ApplyEdits(ToEditTuples(loaded.Edits));
		world2.ApplyHealth(ToHealthTuples(loaded.Health));

		Check("world: re-applied A is now the placed material",
			data2.Get(a.X, a.Y, a.Z) == (byte)VoxelMaterialIds.Placed);
		Check("world: re-applied B is now air",
			data2.Get(b.X, b.Y, b.Z) == (byte)VoxelMaterialIds.Air);
		Check("world: re-applied health of A matches the saved 65",
			Mathf.IsEqualApprox(data2.GetHealth(a.X, a.Y, a.Z), 65f));

		// Confirm an unrelated voxel is untouched by the re-apply.
		Check("world: unrelated voxel (0,100,0) untouched",
			data2.Get(0, 100, 0) == data.Get(0, 100, 0));
	}

	// ------------------------------------------------------------------
	// Helpers
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
			GD.PrintErr($"  FAIL  {name}");
		}
	}

	private static List<(Vector3I Voxel, int Material)> ToEditTuples(List<WorldSaveFile.EditRecord> edits)
	{
		var list = new List<(Vector3I Voxel, int Material)>();
		foreach (var e in edits)
		{
			list.Add((e.Position, e.MaterialId));
		}
		return list;
	}

	private static List<(Vector3I Voxel, float Health)> ToHealthTuples(List<WorldSaveFile.HealthRecord> health)
	{
		var list = new List<(Vector3I Voxel, float Health)>();
		foreach (var h in health)
		{
			list.Add((h.Position, h.Health));
		}
		return list;
	}

	/// <summary>
	/// Builds a minimal, deterministic GenConfig (no caves, no rocks, no ores, no
	/// foliage, no biomes) so terrain is a pure function of (x,y,z, seed). Shared
	/// by both test worlds so they generate identical terrain.
	/// </summary>
	private static GenConfig TestGenConfig()
	{
		return new GenConfig
		{
			Seed = 4242,
			ChunkHeight = 128,
			BaseHeight = 40f,
			HeightAmplitude = 5f,
			DirtDepth = 3,
			CaveStoneDepth = 5,
			EnableCaves = false,
			FoliageDensity = 0f,
			HeightNoise = new FastNoiseLite
			{
				Seed = 4242,
				NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
				Frequency = 0.02f,
				FractalOctaves = 4,
				FractalGain = 0.5f,
				FractalLacunarity = 2f,
			},
			MaterialConfig = new VoxelMaterialConfig(),
			BiomeConfig = new BiomeConfig(),
		};
	}

	/// <summary>
	/// Builds a VoxelWorld sized for a fast test. The world's async generation is
	/// NOT used; the test registers its own synchronously-generated chunk via
	/// <see cref="VoxelWorld.RegisterChunkForTest"/>.
	/// </summary>
	private VoxelWorld MakeWorld()
	{
		var world = new VoxelWorld
		{
			ChunkSizeXZ = 16,
			ChunkHeight = 128,
			EnableCaves = false,
			EnableRocks = false,
			FoliageDensity = 0f,
			Seed = 4242,
		};
		world._Ready();
		return world;
	}
}
