using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using FactorySim;
using FactorySim.Items;

namespace FactorySim.Voxel;

/// <summary>
/// The world save/load service, backed by NAMED SAVE SLOTS. A single
/// <see cref="Node"/> is hosted in both the main menu (<c>Scenes/MainMenu.tscn</c>,
/// which runs the slot picker) and the gameplay scene (<c>Scenes/Main.tscn</c>,
/// which runs the world). Each slot is one file in the OS user-data directory:
/// <c>saves/slot_&lt;id&gt;.world</c> (the <see cref="WorldSaveFile"/> FSAV format),
/// plus an entry in <c>saves/index.json</c> (the <see cref="SaveSlotIndex"/>
/// metadata: name, created/modified timestamps, seed).
///
/// What is persisted (the "delta" model — terrain is regenerated from the seed):
///   - every voxel the player edited (add / break / pickaxe),
///   - damaged-voxel health for voxels still solid,
///   - the inventory (all 28 slots),
///   - the player's position + yaw/pitch,
///   - every live item drop.
///
/// Save triggers (in the gameplay scene, always targeting the ACTIVE slot):
///   - automatically on quit (this node's <see cref="TreeExiting"/>), so both
///     "Quit to Main Menu" (scene change frees the node) and app quit flush;
///   - manually on a configurable key (default <see cref="Key.S"/>).
///
/// Loading: the main menu's slot picker calls <see cref="SetActiveSlot"/> then
/// <see cref="LoadFrom"/> before switching to the gameplay scene. Because the
/// menu and the gameplay scene host DIFFERENT WorldSave node instances, the
/// chosen slot is handed across the scene change via the static
/// <see cref="SessionSlotId"/> / <see cref="SessionSlotName"/>: this node's
/// <see cref="_Ready"/> reads them and loads the slot into
/// <see cref="Snapshot"/> / <see cref="PendingEdits"/> so the world can
/// re-apply it as chunks generate. A missing / corrupt file degrades to a fresh
/// world (it just loads nothing) and is never fatal.
/// </summary>
[GlobalClass]
public partial class WorldSave : Node
{
	[ExportGroup("Save")]
	/// <summary>Master for the manual save key (off = saves only happen on quit).</summary>
	[Export]
	public bool EnableSaveShortcut { get; set; } = true;

	/// <summary>Key that triggers a manual save when <see cref="EnableSaveShortcut"/> is on.</summary>
	[Export]
	public Key SaveKey { get; set; } = Key.S;

	/// <summary>The most recent snapshot written to / read from disk (or null).</summary>
	public WorldSaveFile.Data Snapshot { get; private set; }

	/// <summary>
	/// The pending snapshot awaiting application to a freshly generated world,
	/// or null when there is nothing to apply. The world consumes it by calling
	/// <see cref="ConsumePendingEdits"/> once per chunk (and
	/// <see cref="ConsumePendingPlayer"/> / <see cref="ApplyDrops"/>
	/// for the non-chunk sections).
	/// </summary>
	public WorldSaveFile.Data PendingEdits { get; private set; }

	/// <summary>
	/// The slot id this running session started from (the one the main menu's
	/// picker chose via <see cref="SetActiveSlot"/>), or null when the scene
	/// ran without a slot (e.g. the gameplay scene was run directly).
	/// </summary>
	public string ActiveSlotId { get; private set; }

	/// <summary>The display name of <see cref="ActiveSlotId"/> (or null).</summary>
	public string ActiveSlotName { get; private set; }

	/// <summary>
	/// The slot id the next gameplay scene should load, set by the main menu's
	/// slot picker via <see cref="SetActiveSlot"/> before the scene change.
	/// This is the handoff mechanism across the scene switch: the menu's
	/// WorldSave and the gameplay scene's WorldSave are DIFFERENT node
	/// instances, so the chosen slot is published here (a process-wide static)
	/// and read by the gameplay node's <see cref="_Ready"/>.
	/// </summary>
	public static string SessionSlotId { get; set; }

	/// <summary>The display name paired with <see cref="SessionSlotId"/>.</summary>
	public static string SessionSlotName { get; set; }

	/// <summary>
	/// The world seed the next gameplay scene should generate with (published by
	/// the main menu's slot picker for a "New World" choice, alongside
	/// <see cref="SessionHasSeed"/>). Read by the gameplay scene's WorldSave node
	/// in <see cref="_Ready"/> and applied to the world before its generation
	/// config is built. Cleared by the next node that enters the tree so a stale
	/// seed never leaks into a later scene load.
	/// </summary>
	public static int SessionSeed { get; set; }

	/// <summary>
	/// True while a session seed is pending alongside <see cref="SessionSeed"/>
	/// (the "New World" choice): the next WorldSave node's <see cref="_Ready"/>
	/// records it as <see cref="SessionSeed"/> / <see cref="HasSessionSeed"/> on
	/// the instance for the world to read, then clears the statics. Mirrors
	/// <see cref="SessionFresh"/>.
	/// </summary>
	public static bool SessionHasSeed { get; set; }

	/// <summary>
	/// The world seed this running session was started with (read from the
	/// static <see cref="SessionSeed"/> handoff in <see cref="_Ready"/>), or 0
	/// when no session seed was published. The world reads this in its own
	/// <c>_Ready</c> and applies it to <c>VoxelWorld.Seed</c> before building
	/// its generation config.
	/// </summary>
	public int ActiveSessionSeed { get; private set; }

	/// <summary>
	/// True when this running session was started with a published seed
	/// (mirrors the static <see cref="SessionHasSeed"/>); the world applies
	/// the seed only when this is true.
	/// </summary>
	public bool HasSessionSeed { get; private set; }

	/// <summary>
	/// True while a fresh-world session is pending (the "New World" choice in
	/// the picker): the next WorldSave node's <see cref="_Ready"/> clears
	/// <see cref="SessionSlotId"/> and loads nothing — so the world spawns
	/// fresh even though a stale slot id may still be published. Cleared by
	/// the next node that enters the tree.
	/// </summary>
	public static bool SessionFresh { get; set; }

	/// <summary>
	/// The <c>saves/</c> folder inside the OS user-data directory
	/// (e.g. <c>~/.local/share/godot/app_userdata/Factory Sim/saves</c>).
	/// Created on demand by the writers.
	/// </summary>
	public static string SavesDir => System.IO.Path.Combine(OS.GetUserDataDir(), "saves");

	/// <summary>The full path of the slot index file (<c>saves/index.json</c>).</summary>
	public static string IndexPath => System.IO.Path.Combine(SavesDir, SaveSlotIndex.FileName);

	/// <summary>
	/// The full path of a slot's save file: <c>saves/slot_&lt;id&gt;.world</c>
	/// under the OS user-data directory.
	/// </summary>
	public static string SlotPath(string slotId) =>
		System.IO.Path.Combine(SavesDir, $"slot_{slotId}.world");

	public override void _Ready()
	{
		// Resolve this node's active slot from the session handoff: the main
		// menu's picker set SessionSlotId/SessionSlotName (or SessionFresh)
		// before the scene change. A "fresh" session clears the published slot
		// so the world spawns fresh instead of loading a stale one; a missing
		// slot (or a directly-run gameplay scene) simply has no active slot —
		// saves become no-ops until one is chosen.
		//
		// The session seed (a "New World" choice) is independent of the slot
		// handoff: record it on the instance (the world reads it in its own
		// _Ready) and clear the statics at the end so a stale seed never leaks
		// into a later scene load — mirroring how SessionFresh is cleared.
		SessionFresh = false;
		ActiveSessionSeed = WorldSave.SessionHasSeed ? WorldSave.SessionSeed : 0;
		HasSessionSeed = WorldSave.SessionHasSeed;
		WorldSave.SessionSeed = 0;
		WorldSave.SessionHasSeed = false;

		if (string.IsNullOrEmpty(SessionSlotId))
		{
			ActiveSlotId = null;
			ActiveSlotName = null;
			return;
		}

		ActiveSlotId = SessionSlotId;
		ActiveSlotName = SessionSlotName;

		bool loaded = LoadFrom(ActiveSlotId);
		GD.Print(loaded
			? $"[WorldSave] Loaded slot '{ActiveSlotId}' for this session."
			: $"[WorldSave] Starting a fresh world (no usable data for slot '{ActiveSlotId}').");
	}

	public override void _Input(InputEvent @event)
	{
		// Manual save on a configurable key (mirrors VoxelWorld.RegenerateKey).
		if (!EnableSaveShortcut)
		{
			return;
		}

		if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == SaveKey)
		{
			SaveNow(ActiveSlotId, ActiveSlotName);
		}
	}

	public override void _ExitTree()
	{
		// TreeExiting fires whenever this node leaves the tree — both "Quit to
		// Main Menu" (the scene change frees the whole tree) and app quit. That
		// is the single auto-save-on-quit hook: flush the live state to the
		// ACTIVE slot's file so nothing is lost. Guarded so a node that never
		// entered the tree (or a world that vanished) degrades to a no-op
		// instead of throwing.
		if (!IsInsideTree())
		{
			return;
		}

		var world = GetWorld();
		if (world == null)
		{
			return;
		}

		try
		{
			SaveNow(ActiveSlotId, ActiveSlotName);
		}
		catch (System.Exception e)
		{
			// Auto-save must never crash the quit path.
			GD.PushError($"[WorldSave] Auto-save on exit failed: {e.Message}");
		}
	}

	/// <summary>
	/// Records the slot this running session started from (and publishes it as
	/// the static <see cref="SessionSlotId"/> / <see cref="SessionSlotName"/>
	/// for the next WorldSave node that enters the tree), so the in-game
	/// auto-save (on quit / <c>S</c>) writes back to THIS slot. Call this from
	/// the main menu's slot picker BEFORE changing scene to the gameplay scene.
	/// </summary>
	public void SetActiveSlot(string slotId, string slotName)
	{
		ActiveSlotId = slotId;
		ActiveSlotName = slotName;
		SessionSlotId = slotId;
		SessionSlotName = slotName;
		SessionFresh = false;
	}

	/// <summary>
	/// Publishes a FRESH-World session (the "New World" choice): the next
	/// WorldSave node clears <see cref="SessionSlotId"/> in <see cref="_Ready"/>
	/// and loads nothing, so the world starts fresh even though a stale slot id
	/// may still be published. Call this from the main menu's picker BEFORE
	/// changing scene to the gameplay scene.
	/// </summary>
	public void SetFreshSession()
	{
		SessionSlotId = null;
		SessionSlotName = null;
		SessionFresh = true;
	}

	/// <summary>
	/// Collects the current world state (edits, health, inventory, player,
	/// drops) into a fresh <see cref="WorldSaveFile.Data"/> snapshot. Missing
	/// systems degrade to empty sections (the save still writes a valid file).
	/// </summary>
	public WorldSaveFile.Data CollectSnapshot()
	{
		var data = new WorldSaveFile.Data();

		var world = GetWorld();
		var player = GetPlayer();
		var inventory = GetInventory();

		if (world != null)
		{
			data.Seed = world.Seed;

			// Voxel edits (latest value wins per voxel).
			foreach (var (v, material) in world.GetEdits())
			{
				data.Edits.Add(new WorldSaveFile.EditRecord(v, material));
			}

			// Damaged-voxel health for voxels still solid.
			foreach (var (v, health) in world.GetDamagedHealth())
			{
				if (world.GetVoxel(v.X, v.Y, v.Z) != VoxelMaterialIds.Air)
				{
					data.Health.Add(new WorldSaveFile.HealthRecord(v, health));
				}
			}
		}

		if (player != null)
		{
			data.PlayerPos = player.PositionForSave;
			data.PlayerYaw = player.LookForSave.Yaw;
			data.PlayerPitch = player.LookForSave.Pitch;
		}

		if (inventory != null)
		{
			var (ids, counts) = inventory.SaveState();
			for (int i = 0; i < ids.Length; i++)
			{
				data.Inventory.Add((ids[i], counts[i]));
			}
		}

		// Live item drops (the "voxel_drops" group).
		foreach (Node node in GetTree().GetNodesInGroup("voxel_drops"))
		{
			if (node is VoxelDrop drop && drop.MaterialId != VoxelMaterialIds.Air)
			{
				data.Drops.Add(new WorldSaveFile.DropRecord(
					drop.GlobalPosition, drop.MaterialId, drop.Count));
			}
		}

		return data;
	}

	/// <summary>
	/// Collects the live state and writes it to the slot <paramref name="slotId"/>
	/// (<c>saves/slot_&lt;id&gt;.world</c>), then upserts that slot's index entry
	/// (creating it when new, updating its name/timestamps/seed when it exists).
	/// Returns the slot id on success. When there is no slot id (a session that
	/// ran without one), this is a NO-OP with a one-time warning instead of
	/// writing to a stray file. Throws on a hard I/O failure — callers that must
	/// not throw (the auto-save on quit) are expected to guard this.
	/// </summary>
	public string SaveNow(string slotId, string slotName)
	{
		if (string.IsNullOrEmpty(slotId))
		{
			if (!_warnedNoActiveSlot)
			{
				_warnedNoActiveSlot = true;
				GD.PushWarning("[WorldSave] No active slot for this session — skipping the save (nothing to write to).");
			}
			return null;
		}

		var data = CollectSnapshot();
		string path = SlotPath(slotId);
		WorldSaveFile.Write(path, data);
		Snapshot = data;
		UpsertIndexEntry(slotId, slotName, data.Seed);
		GD.Print($"[WorldSave] World saved to slot '{slotId}' ({path}) " +
			$"({data.Edits.Count} edits, {data.Health.Count} health, " +
			$" {data.Inventory.Count} slots, {data.Drops.Count} drops).");
		return slotId;
	}

	/// <summary>
	/// Reads the slot <paramref name="slotId"/> file into <see cref="Snapshot"/>
	/// and publishes it as <see cref="PendingEdits"/> for the world to apply.
	/// Returns true when a valid snapshot was loaded; false (with a warning)
	/// when the file is missing / corrupt / wrong-version — in which case the
	/// world simply starts fresh.
	/// </summary>
	public bool LoadFrom(string slotId)
	{
		if (string.IsNullOrEmpty(slotId))
		{
			return false;
		}

		string path = SlotPath(slotId);
		var loaded = WorldSaveFile.Read(path);
		if (loaded == null)
		{
			// Read() already pushed the reason (corrupt / wrong version / missing).
			return false;
		}

		// A corrupted-magic / truncated file is detected by Read() and returned
		// with a zero checksum; re-write it as a proper v1 file so it stops
		// tripping the "bad magic" path on every future load.
		if (loaded.Checksum == 0)
		{
			RepairCorruptedSlotFile(path);
		}

		Snapshot = loaded;
		PendingEdits = loaded;
		GD.Print($"[WorldSave] Loaded slot '{slotId}' from '{path}' " +
			$"({loaded.Edits.Count} edits, {loaded.Health.Count} health, " +
			$"{loaded.Inventory.Count} slots, {loaded.Drops.Count} drops).");
		return true;
	}

	/// <summary>
	/// Ensures THIS node has a loaded <see cref="Snapshot"/> for its
	/// <see cref="ActiveSlotId"/>, re-reading the slot file if the snapshot is
	/// missing. This is the world's safety net: the world and the WorldSave node
	/// can disagree about which node holds the snapshot (the menu's WorldSave and
	/// the gameplay scene's WorldSave are different instances), so if the world's
	/// node arrives with a null snapshot it reloads its own active slot directly
	/// from disk. A no-op (returns false) when there is no active slot or the
	/// slot has no usable data.
	/// </summary>
	public bool EnsureLoadedForSession()
	{
		if (Snapshot != null)
		{
			return true; // already loaded
		}
		if (string.IsNullOrEmpty(ActiveSlotId))
		{
			return false; // no slot for this session — nothing to load
		}
		return LoadFrom(ActiveSlotId);
	}

	// ------------------------------------------------------------------
	// Slot management (used by the main menu's slot picker)
	// ------------------------------------------------------------------

	/// <summary>
	/// Lists every slot (most-recently-modified first) from
	/// <c>saves/index.json</c>. A missing index degrades to an empty list; a
	/// corrupt index is REBUILT by scanning <c>saves/slot_*.world</c> (each
	/// file's seed is read from its header) and re-written, so the index
	/// recovers instead of staying broken.
	/// </summary>
	public List<SlotInfo> ListSlots()
	{
		List<SlotInfo> slots = null;
		try
		{
			slots = SaveSlotIndex.Read(IndexPath);
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"[WorldSave] Failed to read the slot index: {e.Message}");
		}

		bool indexReadOk = slots != null;
		if (slots == null)
		{
			slots = new List<SlotInfo>();
		}

		// Reconcile with the files on disk:
		//  - a file whose index entry is missing is re-indexed (its seed from
		//    the file header, its timestamps from the file mtime);
		//  - an index entry whose file is missing (deleted out of band) is
		//    dropped, so the picker never offers a slot it cannot load.
		try
		{
			var slotsById = new Dictionary<string, SlotInfo>();
			foreach (SlotInfo slot in slots)
			{
				slotsById[slot.Id] = slot;
			}

			string dir = SavesDir;
			if (Directory.Exists(dir))
			{
				foreach (string file in Directory.GetFiles(dir, "slot_*.world"))
				{
					string id = System.IO.Path.GetFileNameWithoutExtension(file);
					id = id.Substring("slot_".Length);
					if (slotsById.ContainsKey(id))
					{
						continue; // already indexed
					}

					int seed = 0;
					if (WorldSaveFile.Read(file) is { } data)
					{
						seed = data.Seed;
						// A corrupted-magic / truncated file is detected by Read()
						// and returned with a zero checksum; re-write it as a
						// proper v1 file so the next scan reads it cleanly (no
						// re-parse, no "bad magic" warning).
						if (data.Checksum == 0)
						{
							RepairCorruptedSlotFile(file);
						}
					}
					else
					{
						GD.PushWarning($"[WorldSave] Re-indexed slot file '{file}' that had no index entry (unreadable file — seed 0).");
					}

					long mtime;
					try
					{
						mtime = File.GetLastWriteTimeUtc(file).Ticks - DateTimeOffset.UnixEpoch.Ticks;
					}
					catch (System.Exception)
					{
						mtime = 0;
					}

					var entry = new SlotInfo
					{
						Id = id,
						Name = $"Slot {id}",
						CreatedAt = mtime,
						ModifiedAt = mtime,
						Seed = seed,
					};
					slots.Add(entry);
					slotsById[id] = entry;
				}
			}

			// Drop entries whose slot file no longer exists.
			foreach (SlotInfo slot in slots)
			{
				if (!File.Exists(SlotPath(slot.Id)))
				{
					GD.PushWarning($"[WorldSave] Dropped index entry '{slot.Id}' — its slot file is missing.");
				}
			}
			slots.RemoveAll(s => !File.Exists(SlotPath(s.Id)));
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"[WorldSave] Failed to reconcile the slot index with disk: {e.Message}");
		}

		// A corrupt index (read failed) is rebuilt from the scan above and
		// re-written so it recovers; a missing index is simply empty.
		if (!indexReadOk)
		{
			WriteIndex(slots);
		}

		slots.Sort((a, b) =>
		{
			int byModified = b.ModifiedAt.CompareTo(a.ModifiedAt);
			return byModified != 0 ? byModified : a.Id.CompareTo(b.Id);
		});
		return slots;
	}

	/// <summary>
	/// Allocates a new slot (see <see cref="CreateSlot(string, int)"/>),
	/// recording seed 0 in the index entry. Backward-compat overload for
	/// callers that do not pick a seed.
	/// </summary>
	public string CreateSlot(string name) => CreateSlot(name, seed: 0);

	/// <summary>
	/// Allocates the next free numeric slot id (scanning the index AND the
	/// on-disk files), records a new index entry (name, created/modified = now,
	/// the chosen <paramref name="seed"/>), and returns the id. Documented
	/// choice: NO file is written yet — the slot gets its
	/// <c>slot_&lt;id&gt;.world</c> on its first save (the in-game auto-save on
	/// quit), so "New World" stays cheap and a slot that is never played never
	/// leaves an empty file behind. The seed is recorded in the index so the
	/// picker shows it and the save file re-records it on the first save.
	/// </summary>
	public string CreateSlot(string name, int seed)
	{
		var used = new HashSet<string>();
		foreach (SlotInfo slot in ListSlots())
		{
			used.Add(slot.Id);
		}
		try
		{
			string dir = SavesDir;
			if (Directory.Exists(dir))
			{
				foreach (string file in Directory.GetFiles(dir, "slot_*.world"))
				{
					string fileId = System.IO.Path.GetFileNameWithoutExtension(file);
					used.Add(fileId.Substring("slot_".Length));
				}
			}
		}
		catch (System.Exception)
		{
			// A directory scan failure just means the id might collide; the
			// upsert below still writes a coherent index.
		}

		int id = 0;
		while (used.Contains(id.ToString()))
		{
			id++;
		}

		string slotId = id.ToString();
		long now = NowUnixMs();
		List<SlotInfo> slots;
		try
		{
			slots = SaveSlotIndex.Read(IndexPath);
			slots.Add(new SlotInfo
			{
				Id = slotId,
				Name = string.IsNullOrEmpty(name) ? $"New World {id + 1}" : name,
				CreatedAt = now,
				ModifiedAt = now,
				Seed = seed,
			});
			SaveSlotIndex.Write(IndexPath, slots);
		}
		catch (System.Exception e)
		{
			GD.PushError($"[WorldSave] Could not record new slot '{slotId}' in the index: {e.Message}");
		}

		GD.Print($"[WorldSave] Created slot '{slotId}' ({name}).");
		return slotId;
	}

	/// <summary>
	/// Deletes a slot: removes its <c>slot_&lt;id&gt;.world</c> file (if any) and
	/// its index entry, then re-writes the index. A missing file is not an error
	/// (the slot may never have been saved). The ACTIVE session slot may be
	/// deleted (it is allowed, not guarded); the in-memory snapshot of a session
	/// that already loaded it is left as-is. Returns true when the entry/file
	/// was removed (or there was nothing to remove).
	/// </summary>
	public bool DeleteSlot(string slotId)
	{
		if (string.IsNullOrEmpty(slotId))
		{
			return false;
		}

		string path = SlotPath(slotId);
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (System.Exception e)
		{
			GD.PushError($"[WorldSave] Could not delete slot file '{path}': {e.Message}");
			return false;
		}

		List<SlotInfo> slots;
		try
		{
			slots = SaveSlotIndex.Read(IndexPath);
			slots.RemoveAll(s => s.Id == slotId);
			SaveSlotIndex.Write(IndexPath, slots);
		}
		catch (System.Exception e)
		{
			GD.PushError($"[WorldSave] Could not update the slot index after deleting '{slotId}': {e.Message}");
			return false;
		}

		GD.Print($"[WorldSave] Deleted slot '{slotId}'.");
		return true;
	}

	/// <summary>
	/// Repairs a slot file whose 'FSAV' magic is corrupted (or whose trailing
	/// checksum is missing) but whose v1 body is intact. <see cref="
	/// WorldSaveFile.Read"/> auto-detects this and returns the data with
	/// <see cref="WorldSaveFile.Data.Checksum"/> = 0; this re-writes the data as
	/// a proper v1 file (restoring the magic + a valid trailing checksum) so the
	/// slot loads normally on the next read and stops tripping the "bad magic"
	/// path. A no-op (and never fatal) when the file is not recoverable — a
	/// missing file, a healthy v1 file, or a junk file simply stays as-is.
	/// </summary>
	private static void RepairCorruptedSlotFile(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return;
			}
			// Read() only returns non-null with a zero checksum for a corrupted-
			// magic / truncated file (a healthy v1 file always carries a valid,
			// non-zero trailing checksum).
			var data = WorldSaveFile.Read(path);
			if (data == null || data.Checksum != 0)
			{
				return;
			}
			WorldSaveFile.Write(path, data);
			GD.Print($"[WorldSave] Repaired slot file '{path}' (corrupted magic / missing checksum) to a valid save.");
		}
		catch (System.Exception e)
		{
			// A failed repair must not take down the save/load path — the slot
			// simply stays corrupted (and re-attempts on a later read).
			GD.PushWarning($"[WorldSave] Could not repair slot file '{path}': {e.Message}");
		}
	}

	/// <summary>
	/// Renames a slot in the index (display name only — the file name keeps its
	/// id). No-op when the slot does not exist in the index. Returns true when
	/// the rename was written.
	/// </summary>
	public bool RenameSlot(string slotId, string newName)
	{
		if (string.IsNullOrEmpty(slotId) || string.IsNullOrEmpty(newName))
		{
			return false;
		}

		List<SlotInfo> slots;
		try
		{
			slots = SaveSlotIndex.Read(IndexPath);
			SlotInfo target = null;
			foreach (SlotInfo slot in slots)
			{
				if (slot.Id == slotId)
				{
					target = slot;
					break;
				}
			}
			if (target == null)
			{
				return false;
			}

			target.Name = newName;
			SaveSlotIndex.Write(IndexPath, slots);
		}
		catch (System.Exception e)
		{
			GD.PushError($"[WorldSave] Could not rename slot '{slotId}': {e.Message}");
			return false;
		}

		GD.Print($"[WorldSave] Renamed slot '{slotId}' to '{newName}'.");
		return true;
	}

	// ------------------------------------------------------------------
	// Pending-snapshot consumption (used by the world — unchanged API)
	// ------------------------------------------------------------------

	/// <summary>
	/// Returns and removes this chunk's portion of the pending edits (voxels +
	/// health) from <see cref="PendingEdits"/>. The world calls this once per
	/// chunk, right after that chunk's data is generated, so the edits are
	/// applied before the budget loop meshes the chunk. Returns an empty list
	/// when there is no pending snapshot (nothing to apply).
	/// </summary>
	public List<(Vector3I Voxel, int Material)> ConsumePendingEdits(Vector2I chunkCoord, int chunkSize)
	{
		var result = new List<(Vector3I Voxel, int Material)>();
		if (PendingEdits == null)
		{
			return result;
		}

		int x0 = chunkCoord.X * chunkSize;
		int z0 = chunkCoord.Y * chunkSize;
		var keep = new List<WorldSaveFile.EditRecord>(PendingEdits.Edits.Count);
		foreach (var e in PendingEdits.Edits)
		{
			if (InChunk(e.Position, chunkCoord, chunkSize))
			{
				result.Add((e.Position, e.MaterialId));
			}
			else
			{
				keep.Add(e);
			}
		}
		PendingEdits.Edits = keep;
		return result;
	}

	/// <summary>
	/// Returns and removes this chunk's portion of the pending damaged-voxel
	/// health from <see cref="PendingEdits"/>. Called by the world once per
	/// chunk right after <see cref="ConsumePendingEdits"/>.
	/// </summary>
	public List<(Vector3I Voxel, float Health)> ConsumePendingHealth(Vector2I chunkCoord, int chunkSize)
	{
		var result = new List<(Vector3I Voxel, float Health)>();
		if (PendingEdits == null)
		{
			return result;
		}

		var keep = new List<WorldSaveFile.HealthRecord>(PendingEdits.Health.Count);
		foreach (var h in PendingEdits.Health)
		{
			if (InChunk(h.Position, chunkCoord, chunkSize))
			{
				result.Add((h.Position, h.Health));
			}
			else
			{
				keep.Add(h);
			}
		}
		PendingEdits.Health = keep;
		return result;
	}

	/// <summary>
	/// True when <see cref="PendingEdits"/> still holds any unapplied edits or
	/// health entries (i.e. the world has not yet finished re-applying the
	/// saved world). Used to keep the spawn gate honest when the saved player
	/// position sits in a chunk that is not (0,0).
	/// </summary>
	public bool HasPendingEdits => PendingEdits != null
		&& (PendingEdits.Edits.Count > 0 || PendingEdits.Health.Count > 0);

	/// <summary>
	/// Returns the saved player transform from <see cref="PendingEdits"/> (and
	/// marks it consumed) so the world can restore the player instead of
	/// <c>SpawnPlayerOnTerrain</c>. Returns false when there is no saved
	/// player (fresh world — the normal terrain spawn applies).
	/// </summary>
	public bool ConsumePendingPlayer(out Vector3 position, out float yaw, out float pitch)
	{
		position = Vector3.Zero;
		yaw = 0f;
		pitch = 0f;
		if (PendingEdits == null)
		{
			return false;
		}

		position = PendingEdits.PlayerPos;
		yaw = PendingEdits.PlayerYaw;
		pitch = PendingEdits.PlayerPitch;
		// Mark the player consumed so a later (re)run of the gate does not
		// re-teleport; the chunk-level sections stay until their chunks load.
		PendingEdits.PlayerPos = Vector3.Zero;
		PendingEdits.PlayerYaw = 0f;
		PendingEdits.PlayerPitch = 0f;
		// A snapshot whose only remaining payload was the player is done.
		if (PendingEdits.Edits.Count == 0 && PendingEdits.Health.Count == 0)
		{
			PendingEdits = null;
		}
		return true;
	}

	/// <summary>
	/// Clears every live item drop from the scene (the "voxel_drops" group) and
	/// re-spawns the drops recorded in <paramref name="data"/> at their saved
	/// positions via <see cref="DropSpawner"/>. Called by the world once, after
	/// the spawn chunk is solid, so the restored drops fall onto real terrain.
	/// </summary>
	public void ApplyDrops(WorldSaveFile.Data data)
	{
		if (data == null)
		{
			return;
		}

		foreach (Node node in GetTree().GetNodesInGroup("voxel_drops"))
		{
			if (node is VoxelDrop)
			{
				node.QueueFree();
			}
		}

		foreach (var d in data.Drops)
		{
			DropSpawner.SpawnDrop(this, d.Position, d.MaterialId, d.Count);
		}
	}

	/// <summary>Clears the pending snapshot (e.g. after it has been fully applied).</summary>
	public void ClearPending()
	{
		PendingEdits = null;
	}

	// ------------------------------------------------------------------
	// Index + lookup helpers
	// ------------------------------------------------------------------

	private void UpsertIndexEntry(string slotId, string slotName, int seed)
	{
		List<SlotInfo> slots;
		try
		{
			slots = SaveSlotIndex.Read(IndexPath);
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"[WorldSave] Failed to read the slot index while saving: {e.Message}");
			slots = new List<SlotInfo>();
		}

		long now = NowUnixMs();
		SlotInfo existing = null;
		foreach (SlotInfo slot in slots)
		{
			if (slot.Id == slotId)
			{
				existing = slot;
				break;
			}
		}

		if (existing == null)
		{
			slots.Add(new SlotInfo
			{
				Id = slotId,
				Name = string.IsNullOrEmpty(slotName) ? $"Slot {slotId}" : slotName,
				CreatedAt = now,
				ModifiedAt = now,
				Seed = seed,
			});
		}
		else
		{
			if (!string.IsNullOrEmpty(slotName))
			{
				existing.Name = slotName;
			}
			existing.ModifiedAt = now;
			existing.Seed = seed;
		}

		WriteIndex(slots);
	}

	private void WriteIndex(List<SlotInfo> slots)
	{
		try
		{
			SaveSlotIndex.Write(IndexPath, slots);
		}
		catch (System.Exception e)
		{
			// A failed index write must not take down the save itself — the
			// slot file is the authoritative data; the index self-heals on the
			// next ListSlots() scan.
			GD.PushWarning($"[WorldSave] Failed to write the slot index: {e.Message}");
		}
	}

	private static long NowUnixMs()
	{
		try
		{
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}
		catch (System.Exception)
		{
			return 0;
		}
	}

	private bool _warnedNoActiveSlot;

	private VoxelWorld GetWorld() => GetTree()?.GetFirstNodeInGroup("voxel_world") as VoxelWorld;

	private Player GetPlayer() => GetTree()?.GetFirstNodeInGroup("player") as Player;

	private Inventory GetInventory() => GetTree()?.GetFirstNodeInGroup("inventory") as Inventory;

	private static bool InChunk(Vector3I v, Vector2I chunkCoord, int chunkSize)
	{
		int x0 = chunkCoord.X * chunkSize;
		int z0 = chunkCoord.Y * chunkSize;
		return v.X >= x0 && v.X < x0 + chunkSize && v.Z >= z0 && v.Z < z0 + chunkSize;
	}
}
