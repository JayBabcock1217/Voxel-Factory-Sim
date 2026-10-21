using Godot;
using System;
using System.Collections.Generic;
using System.IO;

namespace FactorySim.Voxel;

/// <summary>
/// Low-level reader/writer for the world save file (<c>save.world</c>). A
/// hand-rolled, dependency-free binary format (matching the project's
/// <c>JsonParser</c> style): a magic tag, a version, fixed sections for
/// voxel edits / damaged-voxel health / player transform / inventory / item
/// drops, and a trailing FNV-1a checksum over everything above it.
///
/// Layout (little-endian, all values the same size on every platform):
/// <code>
/// [4 bytes]  magic  'F','S','A','V'
/// [4 bytes]  version (u32, start at 1)
/// [4 bytes]  seed (i32)
/// [4 bytes]  editCount (u32)
///   editCount × [x,y,z (i32 each) + materialId (u8) + 1 pad byte]   (16 bytes each)
/// [4 bytes]  healthCount (u32)
///   healthCount × [x,y,z (i32 each) + health (f32)]                 (16 bytes each)
/// [16 bytes] player position (f32 x3) + yaw, pitch (f32 x2)
/// [4 bytes]  slotCount (u32)  -- always the inventory's TotalSlots
///   slotCount × [materialId (i32) + count (i32)]                    (8 bytes each)
/// [4 bytes]  dropCount (u32)
///   dropCount × [pos (f32 x3) + materialId (i32) + count (i32)]     (24 bytes each)
/// [4 bytes]  trailing checksum (u32, FNV-1a 32-bit over everything above)
/// </code>
///
/// Edits are stored as (world voxel, material id) pairs; material id 0 (air)
/// means the voxel was removed. Only the player's deltas are persisted — the
/// terrain itself is regenerated deterministically from the seed and the edit
/// overlay is re-applied on top of it on load.
///
/// A file can also arrive with a CORRUPTED MAGIC (the first 4 bytes are not
/// 'FSAV') but an otherwise-intact v1 body, or with a missing trailing checksum
/// (4 bytes short of a full v1 file). <see cref="Read"/> auto-detects this via
/// <see cref="TryReadCorruptedV1"/>: it re-derives the magic from the intact
/// body and returns the data with <see cref="Data.Checksum"/> left at 0 so the
/// caller (<see cref="WorldSave.ListSlots"/> / <see cref="WorldSave.LoadFrom"/>)
/// can re-write the file as a proper v1 (restoring the magic + checksum).
/// </summary>
public static class WorldSaveFile
{
	// Paths already reported as corrupt/bad-magic this session. Read() is called
	// on every ListSlots() reconciliation (picker open / delete / rename), so a
	// persistently unreadable file would otherwise spam the same warning on every
	// call. A path is cleared when a later Read succeeds, so a file that is
	// repaired (re-written by ListSlots / LoadFrom) can warn again if it
	// corrupts a second time.
	private static readonly HashSet<string> _warnedCorruptPaths = new();

	/// <summary>File magic: the four bytes 'F','S','A','V'.</summary>
	public static readonly byte[] Magic = { (byte)'F', (byte)'S', (byte)'A', (byte)'V' };

	/// <summary>Current save format version. Bump it when the layout changes.</summary>
	public const int CurrentVersion = 1;

	/// <summary>
	/// Every version this reader knows how to LOAD. A file whose version is in
	/// this set can be read directly (or via <see cref="TryReadCorruptedV1"/>);
	/// a version below the minimum but still recognized is migrated by
	/// <see cref="Migrate"/>. To add a new version: bump <see cref="CurrentVersion"/>
	/// and, if the old layout is still parseable, keep the old version in this set
	/// and add a <see cref="Migrate"/> case that converts it to the current
	/// <see cref="Data"/>.
	/// </summary>
	public static readonly int[] SupportedVersions = { 1 };

	/// <summary>The oldest version <see cref="Read"/> will accept at all.</summary>
	public static int OldestSupportedVersion => SupportedVersions[0];

	/// <summary>True when <paramref name="version"/> is in <see cref="SupportedVersions"/>.</summary>
	public static bool IsSupportedVersion(int version)
	{
		return Array.IndexOf(SupportedVersions, version) >= 0;
	}

	/// <summary>
	/// Converts a save read at an older supported version into the current
	/// <see cref="Data"/> shape. The input <paramref name="data"/> was already
	/// parsed by the version-specific reader; this performs any field
	/// remapping / defaulting a version bump requires. The identity transform
	/// is the default (v1 → v1). When a future version changes the layout, add
	/// a case here that fills in the new fields and returns a current-version
	/// <see cref="Data"/>.
	/// </summary>
	/// <param name="data">The parsed save from the older version.</param>
	/// <param name="fromVersion">The version the file was written at.</param>
	/// <returns>A <see cref="Data"/> in the current version's shape.</returns>
	public static Data Migrate(Data data, int fromVersion)
	{
		if (fromVersion == CurrentVersion)
		{
			return data; // nothing to do
		}

		// Future version migrations go here, e.g.:
		//   if (fromVersion == 1) { /* v1 -> v2: add new field, re-derive ... */ }
		// For now, any recognized-but-older version is treated as already in
		// the current shape (the layout has not changed since v1).
		return data;
	}

	/// <summary>
	/// The minimum byte length of a valid (or recoverable) save file: the 8-byte
	/// header (magic + version), the seed, the two section counts, the 16-byte
	/// player transform, the inventory count, and the trailing checksum. Files
	/// shorter than this cannot hold a complete save and are rejected.
	/// </summary>
	public const int MinFileLength = 8 + 4 + 4 + 4 + 16 + 4 + 4;

	/// <summary>FNV-1a 32-bit offset basis.</summary>
	private const uint FnvOffset = 2166136261u;

	/// <summary>FNV-1a 32-bit prime.</summary>
	private const uint FnvPrime = 16777619u;

	/// <summary>
	/// One voxel edit in world voxel coordinates. A material id of 0 (air) means
	/// "this voxel was removed".
	/// </summary>
	public readonly struct EditRecord
	{
		/// <summary>World voxel position the edit applies to.</summary>
		public Vector3I Position { get; }

		/// <summary>Material id to set (0 = air / removed).</summary>
		public int MaterialId { get; }

		public EditRecord(Vector3I position, int materialId)
		{
			Position = position;
			MaterialId = materialId;
		}
	}

	/// <summary>One damaged voxel's remaining health (0..100) in world voxel coordinates.</summary>
	public readonly struct HealthRecord
	{
		/// <summary>World voxel position the health applies to.</summary>
		public Vector3I Position { get; }

		/// <summary>Remaining health in percentage points (0..100).</summary>
		public float Health { get; }

		public HealthRecord(Vector3I position, float health)
		{
			Position = position;
			Health = health;
		}
	}

	/// <summary>One live item drop: its world position, material, and count.</summary>
	public readonly struct DropRecord
	{
		/// <summary>World position of the drop.</summary>
		public Vector3 Position { get; }

		/// <summary>The drop's voxel material id.</summary>
		public int MaterialId { get; }

		/// <summary>How many items are in the drop.</summary>
		public int Count { get; }

		public DropRecord(Vector3 position, int materialId, int count)
		{
			Position = position;
			MaterialId = materialId;
			Count = count;
		}
	}

	/// <summary>
	/// A decoded save file: every section, plus the FNV-1a checksum of the
	/// section bytes (everything between the version field and the trailing
	/// checksum). The caller compares <see cref="Checksum"/> against the
	/// stored trailing checksum (see <see cref="Write"/>'s round-trip) to
	/// detect corruption.
	/// </summary>
	public sealed class Data
	{
		/// <summary>World seed the terrain is regenerated from.</summary>
		public int Seed { get; set; }

		/// <summary>Player voxel edits (latest value wins per voxel).</summary>
		public List<EditRecord> Edits { get; set; } = new();

		/// <summary>Damaged-voxel health for still-solid voxels.</summary>
		public List<HealthRecord> Health { get; set; } = new();

		/// <summary>Player position (world units, meters).</summary>
		public Vector3 PlayerPos { get; set; }

		/// <summary>Player yaw (radians, body Y rotation).</summary>
		public float PlayerYaw { get; set; }

		/// <summary>Player pitch (radians, camera X rotation).</summary>
		public float PlayerPitch { get; set; }

		/// <summary>Inventory slots: (materialId, count) per slot.</summary>
		public List<(int MaterialId, int Count)> Inventory { get; set; } = new();

		/// <summary>Live item drops.</summary>
		public List<DropRecord> Drops { get; set; } = new();

		/// <summary>FNV-1a 32-bit checksum of all section bytes.</summary>
		public uint Checksum { get; set; }
	}

	/// <summary>
	/// Serializes <paramref name="data"/> to <paramref name="path"/> (little-endian,
	/// trailing FNV-1a checksum). Creates the parent directory if needed. Throws on
	/// an I/O error — the caller is expected to degrade gracefully.
	/// </summary>
	public static void Write(string path, Data data)
	{
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(
			System.IO.Path.GetFullPath(path)) ?? "");

		using var stream = File.Create(path);
		using var w = new BinaryWriter(stream);

		w.Write(Magic);
		w.Write(CurrentVersion);
		w.Write(data.Seed);

		// Edits.
		w.Write(data.Edits.Count);
		foreach (var e in data.Edits)
		{
			w.Write(e.Position.X);
			w.Write(e.Position.Y);
			w.Write(e.Position.Z);
			w.Write((byte)e.MaterialId);
			w.Write((byte)0); // padding
		}

		// Damaged-voxel health.
		w.Write(data.Health.Count);
		foreach (var h in data.Health)
		{
			w.Write(h.Position.X);
			w.Write(h.Position.Y);
			w.Write(h.Position.Z);
			w.Write(h.Health);
		}

		// Player transform.
		w.Write(data.PlayerPos.X);
		w.Write(data.PlayerPos.Y);
		w.Write(data.PlayerPos.Z);
		w.Write(data.PlayerYaw);
		w.Write(data.PlayerPitch);

		// Inventory slots.
		w.Write(data.Inventory.Count);
		foreach (var (materialId, count) in data.Inventory)
		{
			w.Write(materialId);
			w.Write(count);
		}

		// Item drops.
		w.Write(data.Drops.Count);
		foreach (var d in data.Drops)
		{
			w.Write(d.Position.X);
			w.Write(d.Position.Y);
			w.Write(d.Position.Z);
			w.Write(d.MaterialId);
			w.Write(d.Count);
		}

		// Trailing FNV-1a over every byte written above the checksum.
		stream.Seek(0, SeekOrigin.Begin);
		byte[] sectionBytes = new byte[(int)stream.Position];
		stream.Read(sectionBytes, 0, sectionBytes.Length);
		uint checksum = Fnv1a(sectionBytes);
		w.Write(checksum);
		w.Flush();
	}

	/// <summary>
	/// Reads and validates the save file at <paramref name="path"/>. Returns
	/// <c>null</c> (with a <c>PushWarning</c> describing why) when the file is
	/// missing, the magic/version is wrong, the layout is truncated, or the
	/// trailing checksum does not match — so the caller can degrade to a fresh
	/// world without crashing.
	/// </summary>
	public static Data Read(string path)
	{
		if (!File.Exists(path))
		{
			return null; // no save yet: a fresh world
		}

		byte[] bytes;
		try
		{
			bytes = File.ReadAllBytes(path);
		}
		catch (System.Exception e)
		{
			ReportCorrupt(path, $"Could not read '{path}': {e.Message}");
			return null;
		}

		// A file with a corrupted magic (first 4 bytes not 'FSAV') but an
		// otherwise-intact v1 body — possibly missing its trailing checksum — is
		// recovered by re-deriving the structure from the intact body. The
		// returned data carries a zero checksum so the caller re-writes it as a
		// proper v1 (restoring magic + checksum) instead of the slot staying
		// unreadable ("bad magic") on every open.
		if (bytes[0] != Magic[0] && TryReadCorruptedV1(bytes, out Data recovered))
		{
			// The body parsed; the file is recoverable (the caller re-writes it).
			// Clear any prior corrupt flag.
			_warnedCorruptPaths.Remove(path);
			return recovered;
		}

		using var ms = new MemoryStream(bytes);
		var r = new BinaryReader(ms);
		try
		{
			// Magic.
			for (int i = 0; i < Magic.Length; i++)
			{
				if (r.ReadByte() != Magic[i])
				{
					ReportCorrupt(path, $"'{path}' is not a save file (bad magic).");
					return null;
				}
			}

			int version = r.ReadInt32();
			if (!IsSupportedVersion(version))
			{
				ReportCorrupt(path, $"'{path}' is version {version}; supported {OldestSupportedVersion}..{CurrentVersion}. Starting a fresh world.");
				return null;
			}

			var data = new Data();
			data.Seed = r.ReadInt32();

			// Edits.
			int editCount = ReadCount(r);
			data.Edits = new List<EditRecord>(editCount);
			for (int i = 0; i < editCount; i++)
			{
				int x = r.ReadInt32();
				int y = r.ReadInt32();
				int z = r.ReadInt32();
				int materialId = r.ReadByte();
				r.ReadByte(); // padding
				data.Edits.Add(new EditRecord(new Vector3I(x, y, z), materialId));
			}

			// Health.
			int healthCount = ReadCount(r);
			data.Health = new List<HealthRecord>(healthCount);
			for (int i = 0; i < healthCount; i++)
			{
				int x = r.ReadInt32();
				int y = r.ReadInt32();
				int z = r.ReadInt32();
				float health = r.ReadSingle();
				data.Health.Add(new HealthRecord(new Vector3I(x, y, z), health));
			}

			// Player transform.
			data.PlayerPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
			data.PlayerYaw = r.ReadSingle();
			data.PlayerPitch = r.ReadSingle();

			// Inventory.
			int slotCount = ReadCount(r);
			data.Inventory = new List<(int, int)>(slotCount);
			for (int i = 0; i < slotCount; i++)
			{
				int materialId = r.ReadInt32();
				int count = r.ReadInt32();
				data.Inventory.Add((materialId, count));
			}

			// Drops.
			int dropCount = ReadCount(r);
			data.Drops = new List<DropRecord>(dropCount);
			for (int i = 0; i < dropCount; i++)
			{
				Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				int materialId = r.ReadInt32();
				int count = r.ReadInt32();
				data.Drops.Add(new DropRecord(pos, materialId, count));
			}

			uint storedChecksum = r.ReadUInt32();
			uint computed = Fnv1a(bytes, 0, bytes.Length - 4); // everything above the checksum
			data.Checksum = computed;
			if (storedChecksum != computed)
			{
				ReportCorrupt(path, $"'{path}' failed its checksum (stored 0x{storedChecksum:X8}, computed 0x{computed:X8}). Starting a fresh world.");
				return null;
			}

			// The file read cleanly; clear any prior corrupt flag so a future
			// corruption (e.g. an interrupted write) can warn again.
			_warnedCorruptPaths.Remove(path);

			// If the file was written at an older supported version, convert it
			// to the current shape before handing it to the caller. The caller
			// re-writes the file at CurrentVersion on its next save, so the
			// migration is one-time and lossless (Migrate is pure).
			return Migrate(data, version);
		}
		catch (System.Exception e)
		{
			ReportCorrupt(path, $"'{path}' is corrupt or truncated: {e.Message}. Starting a fresh world.");
			return null;
		}
		finally
		{
			r.Dispose();
			ms.Dispose();
		}
	}

	/// <summary>
	/// Computes the FNV-1a 32-bit hash of <paramref name="bytes"/>. Used to
	/// verify a save's trailing checksum and (in tests) to confirm a
	/// write→read round-trip is byte-stable.
	/// </summary>
	public static uint Fnv1a(byte[] bytes, int offset = 0, int count = -1)
	{
		if (count < 0)
		{
			count = bytes.Length - offset;
		}

		uint hash = FnvOffset;
		for (int i = offset; i < offset + count; i++)
		{
			hash ^= bytes[i];
			hash *= FnvPrime;
		}
		return hash;
	}

	/// <summary>
	/// Reads the 8-byte header (magic + version) of <paramref name="path"/> and
	/// reports whether it is a save file of the current version. Never throws:
	/// a missing/corrupt file simply reports <c>false</c>. Used to decide
	/// "is there a valid save to load?" before reading the whole file.
	/// </summary>
	public static bool IsValidSaveFile(string path)
	{
		try
		{
			using var stream = File.OpenRead(path);
			if (stream.Length < Magic.Length + 4)
			{
				return false;
			}

			var reader = new BinaryReader(stream);
			for (int i = 0; i < Magic.Length; i++)
			{
				if (reader.ReadByte() != Magic[i])
				{
					return false;
				}
			}

			return reader.ReadInt32() == CurrentVersion;
		}
		catch (System.Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Reads a section count (u32) and range-checks it against
	/// <see cref="int.MaxValue"/> so a corrupt/huge count fails the read (and
	/// <see cref="Read"/> degrades to a fresh world) instead of throwing an
	/// unhandled <c>OverflowException</c>.
	/// </summary>
	private static int ReadCount(BinaryReader r)
	{
		uint count = r.ReadUInt32();
		if (count > int.MaxValue)
		{
			throw new System.IO.InvalidDataException("section count out of range");
		}
		return (int)count;
	}

	/// <summary>
	/// Emits the <paramref name="message"/> for a corrupt/unreadable save file
	/// at most once per path per corrupt episode. <see cref="Read"/> is called
	/// on every <see cref="WorldSave.ListSlots"/> reconciliation, so a
	/// persistently unreadable file would otherwise spam the same warning on
	/// every picker open / delete / rename. The path is tracked in
	/// <see cref="_warnedCorruptPaths"/> and cleared by a successful read (see
	/// <see cref="Read"/>).
	/// </summary>
	private static void ReportCorrupt(string path, string message)
	{
		if (_warnedCorruptPaths.Add(path))
		{
			GD.PushWarning($"[WorldSaveFile] {message}");
		}
	}

	// ------------------------------------------------------------------
	// Corrupted-magic / truncated-v1 recovery
	// ------------------------------------------------------------------

	/// <summary>
	/// Recovers a save file whose 4-byte 'FSAV' magic is corrupted (or whose
	/// trailing checksum is missing) but whose v1 body is otherwise intact.
	/// Parses the body using the v1 layout (version @ 4, seed @ 8, then the
	/// section counts / records exactly as <see cref="Read"/> does) WITHOUT
	/// checking the magic or the trailing checksum. Returns the recovered
	/// <see cref="Data"/> with <see cref="Data.Checksum"/> left at 0 (so the
	/// caller knows to re-write the file as a proper v1) and true on success;
	/// false (leaving <paramref name="data"/> null) when the body is not a
	/// structurally valid v1 save. A junk file fails the structural checks
	/// (impossible section counts, truncated reads) and is reported as
	/// unreadable rather than recovered.
	/// </summary>
	private static bool TryReadCorruptedV1(byte[] bytes, out Data data)
	{
		data = null;
		// A full v1 file is MinFileLength bytes; a file missing only its trailing
		// checksum is MinFileLength - 4. Anything shorter cannot be a complete
		// save. (The caller has already confirmed the magic is not 'FSAV'.)
		if (bytes.Length < MinFileLength - 4)
		{
			return false;
		}

		try
		{
			using var ms = new MemoryStream(bytes);
			var r = new BinaryReader(ms);

			// Skip the (corrupted) 4-byte magic; the version field follows.
			r.ReadBytes(4);
			int version = r.ReadInt32();
			if (!IsSupportedVersion(version))
			{
				return false; // not a recognized body — do not recover
			}

			var result = new Data();
			result.Seed = r.ReadInt32();

			// Edits.
			int editCount = ReadCount(r);
			result.Edits = new List<EditRecord>(editCount);
			for (int i = 0; i < editCount; i++)
			{
				int x = r.ReadInt32();
				int y = r.ReadInt32();
				int z = r.ReadInt32();
				int materialId = r.ReadByte();
				r.ReadByte(); // padding
				result.Edits.Add(new EditRecord(new Vector3I(x, y, z), materialId));
			}

			// Health.
			int healthCount = ReadCount(r);
			result.Health = new List<HealthRecord>(healthCount);
			for (int i = 0; i < healthCount; i++)
			{
				int x = r.ReadInt32();
				int y = r.ReadInt32();
				int z = r.ReadInt32();
				float health = r.ReadSingle();
				result.Health.Add(new HealthRecord(new Vector3I(x, y, z), health));
			}

			// Player transform.
			result.PlayerPos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
			result.PlayerYaw = r.ReadSingle();
			result.PlayerPitch = r.ReadSingle();

			// Inventory.
			int slotCount = ReadCount(r);
			result.Inventory = new List<(int, int)>(slotCount);
			for (int i = 0; i < slotCount; i++)
			{
				int materialId = r.ReadInt32();
				int count = r.ReadInt32();
				result.Inventory.Add((materialId, count));
			}

			// Drops.
			int dropCount = ReadCount(r);
			result.Drops = new List<DropRecord>(dropCount);
			for (int i = 0; i < dropCount; i++)
			{
				Vector3 pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
				int materialId = r.ReadInt32();
				int count = r.ReadInt32();
				result.Drops.Add(new DropRecord(pos, materialId, count));
			}

			// The body parsed completely. We deliberately do NOT verify a
			// trailing checksum (it may be missing/corrupted); the caller
			// re-writes the file as a proper v1, which recomputes it.
			data = result;
			return true;
		}
		catch (System.Exception)
		{
			return false; // truncated / malformed body — treat as unreadable
		}
	}
}
