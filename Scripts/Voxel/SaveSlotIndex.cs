using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FactorySim.Voxel;

/// <summary>
/// One entry in the save-slot index: the metadata the slot picker shows
/// (display name, created/modified timestamps, world seed) for a save slot.
/// The slot's own file (<c>slot_&lt;id&gt;.world</c>) is the authoritative save;
/// this record is only the index bookkeeping.
/// </summary>
public sealed class SlotInfo
{
	/// <summary>The slot id (the number in <c>slot_&lt;id&gt;.world</c>), as a string.</summary>
	public string Id { get; set; } = "";

	/// <summary>The display name shown in the slot picker.</summary>
	public string Name { get; set; } = "";

	/// <summary>Unix milliseconds the slot was created.</summary>
	public long CreatedAt { get; set; }

	/// <summary>Unix milliseconds the slot was last saved.</summary>
	public long ModifiedAt { get; set; }

	/// <summary>The world seed the slot's world was generated from.</summary>
	public int Seed { get; set; }
}

/// <summary>
/// The <c>saves/index.json</c> file: a small hand-rolled, dependency-free JSON
/// index of every save slot (mirroring the flat-JSON style of
/// <see cref="FactorySim.UI.SettingsStore"/>). Each slot is a small object with
/// the metadata in <see cref="SlotInfo"/>; the slot's own binary file is
/// <c>slot_&lt;id&gt;.world</c> in the same folder.
///
/// Shape:
/// <code>
/// {
///   "Slots": [
///     { "Id": "0", "Name": "My World", "CreatedAt": 1700000000000,
///       "ModifiedAt": 1700000001000, "Seed": 1337 },
///     ...
///   ]
/// }
/// </code>
///
/// A missing file degrades to an empty index; a corrupt one is reported (and
/// treated as empty) so the slot picker never crashes on it.
/// </summary>
public static class SaveSlotIndex
{
	/// <summary>File name of the index inside the <c>saves/</c> folder.</summary>
	public const string FileName = "index.json";

	// Paths already reported as corrupt/unreadable this session. The index is
	// re-read on every ListSlots() call (picker open, delete, rename), so without
	// this guard a persistently corrupt file would spam the same warning on every
	// call. A path is cleared when a later Read succeeds, so a file that is
	// repaired (re-written by ListSlots) can warn again if it corrupts a second
	// time.
	private static readonly HashSet<string> _warnedCorruptPaths = new();

	/// <summary>
	/// Reads the index at <paramref name="path"/>. Returns an empty list when
	/// the file is missing; a corrupt / unreadable file degrades to an empty
	/// list with a <c>PushWarning</c> (at most once per path per corrupt episode).
	/// </summary>
	public static List<SlotInfo> Read(string path)
	{
		var result = new List<SlotInfo>();
		if (!File.Exists(path))
		{
			_warnedCorruptPaths.Remove(path);
			return result;
		}

		string json;
		try
		{
			json = File.ReadAllText(path);
		}
		catch (System.Exception e)
		{
			ReportCorrupt(path, $"Could not read '{path}': {e.Message}");
			return result;
		}

		if (!TryParseSlots(json, out List<SlotInfo> slots))
		{
			ReportCorrupt(path, $"'{path}' is corrupt — treating the slot index as empty.");
			return result;
		}

		// The file read cleanly this time; clear any prior corrupt flag so a
		// future corruption (e.g. an interrupted write) can warn again.
		_warnedCorruptPaths.Remove(path);

		foreach (SlotInfo slot in slots)
		{
			if (string.IsNullOrEmpty(slot.Id))
			{
				continue; // skip malformed entries
			}
			result.Add(slot);
		}
		return result;
	}

	/// <summary>
	/// Emits the <paramref name="message"/> for a corrupt/unreadable index file
	/// at most once per path per corrupt episode. The index is re-read on every
	/// <see cref="WorldSave.ListSlots"/> call, so a persistently corrupt file
	/// would otherwise spam the same warning on every picker open / delete /
	/// rename. The path is tracked in <see cref="_warnedCorruptPaths"/> and
	/// cleared by a successful <see cref="Read"/> (see above).
	/// </summary>
	private static void ReportCorrupt(string path, string message)
	{
		if (_warnedCorruptPaths.Add(path))
		{
			GD.PushWarning($"[SaveSlotIndex] {message}");
		}
	}

	/// <summary>
	/// Writes the index at <paramref name="path"/> (creating the parent folder
	/// if needed). Throws on a hard I/O failure — callers that must not throw
	/// (e.g. the auto-save path) are expected to guard this.
	/// </summary>
	public static void Write(string path, List<SlotInfo> slots)
	{
		string json = FormatSlots(slots);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(
			System.IO.Path.GetFullPath(path)) ?? "");
		File.WriteAllText(path, json);
	}

	// ------------------------------------------------------------------
	// Parser
	// ------------------------------------------------------------------

	/// <summary>
	/// Parses the index JSON into slot records. Returns false (leaving
	/// <paramref name="slots"/> null) on any structural failure so the caller
	/// can degrade to "no slots" instead of a partial read.
	/// </summary>
	private static bool TryParseSlots(string json, out List<SlotInfo> slots)
	{
		slots = null;
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}

		int i = 0;
		SkipWs(json, ref i);
		if (i >= json.Length || json[i] != '{')
		{
			return false;
		}
		i++; // consume '{'

		// The root object has a single key, "Slots", whose value is an array.
		SkipWs(json, ref i);
		if (i >= json.Length || json[i] != '"')
		{
			return false;
		}
		string key = ReadString(json, ref i);
		if (key == null || key != "Slots")
		{
			return false;
		}
		SkipWs(json, ref i);
		if (i >= json.Length || json[i] != ':')
		{
			return false;
		}
		i++; // consume ':'

		SkipWs(json, ref i);
		if (i >= json.Length || json[i] != '[')
		{
			return false;
		}
		i++; // consume '['

		slots = new List<SlotInfo>();
		while (true)
		{
			SkipWs(json, ref i);
			if (i >= json.Length)
			{
				return false;
			}
			if (json[i] == ']')
			{
				i++; // consume ']' (empty array)
				break; // end of array
			}
			if (json[i] != '{')
			{
				return false; // expected a slot object
			}

			if (!ParseSlotObject(json, ref i, out SlotInfo slot))
			{
				return false;
			}
			slots.Add(slot);

			SkipWs(json, ref i);
			if (i < json.Length && json[i] == ',')
			{
				i++; // consume ','
			}
			else if (i < json.Length && json[i] == ']')
			{
				i++; // consume ']' (end of the slots array)
				break;
			}
			else
			{
				return false;
			}
		}

		// Tolerate trailing whitespace only; a trailing root '}' is expected.
		SkipWs(json, ref i);
		if (i >= json.Length || json[i] != '}')
		{
			return false;
		}
		return true;
	}

	/// <summary>
	/// Parses one <c>{ "Id": ..., ... }</c> slot object, advancing
	/// <paramref name="i"/> past the closing '}'. Returns false on any
	/// structural failure.
	/// </summary>
	private static bool ParseSlotObject(string json, ref int i, out SlotInfo slot)
	{
		slot = null;
		// Assumes json[i] == '{'
		i++;
		slot = new SlotInfo();
		while (true)
		{
			SkipWs(json, ref i);
			if (i >= json.Length)
			{
				return false;
			}
			if (json[i] == '}')
			{
				i++; // consume '}'
				return true;
			}
			if (json[i] != '"')
			{
				return false;
			}

			string key = ReadString(json, ref i);
			if (key == null)
			{
				return false;
			}
			SkipWs(json, ref i);
			if (i >= json.Length || json[i] != ':')
			{
				return false;
			}
			i++; // consume ':'
			SkipWs(json, ref i);
			if (i >= json.Length)
			{
				return false;
			}

			// Value: a string or a bare number.
			string raw;
			bool isString;
			if (json[i] == '"')
			{
				raw = ReadString(json, ref i);
				isString = true;
			}
			else
			{
				int start = i;
				while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']')
				{
					i++;
				}
				raw = json.Substring(start, i - start).Trim();
				isString = false;
			}
			if (raw == null)
			{
				return false;
			}

			switch (key)
			{
				case "Id":
					if (isString)
					{
						slot.Id = raw;
					}
					break;
				case "Name":
					if (isString)
					{
						slot.Name = raw;
					}
					break;
				case "CreatedAt":
					long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long createdAt);
					slot.CreatedAt = createdAt;
					break;
				case "ModifiedAt":
					long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long modifiedAt);
					slot.ModifiedAt = modifiedAt;
					break;
				case "Seed":
					int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed);
					slot.Seed = seed;
					break;
				default:
					break; // unknown keys are ignored forward-compatibly
			}

			SkipWs(json, ref i);
			if (i < json.Length && json[i] == ',')
			{
				i++; // consume ','
			}
			else if (i < json.Length && json[i] == '}')
			{
				i++; // consume the object's closing '}' (last field had no
				     // trailing comma, so the number reader stopped at '}')
				return true;
			}
			else
			{
				return false;
			}
		}
	}

	private static string ReadString(string json, ref int i)
	{
		// Assumes json[i] == '"'
		int start = ++i;
		while (i < json.Length && json[i] != '"')
		{
			if (json[i] == '\\' && i + 1 < json.Length)
			{
				i++;
			}
			i++;
		}
		if (i >= json.Length)
		{
			return null;
		}
		string value = json.Substring(start, i - start);
		i++; // consume closing quote
		return value;
	}

	private static void SkipWs(string json, ref int i)
	{
		while (i < json.Length && char.IsWhiteSpace(json[i]))
		{
			i++;
		}
	}

	// ------------------------------------------------------------------
	// Formatter
	// ------------------------------------------------------------------

	private static string FormatSlots(List<SlotInfo> slots)
	{
		var sb = new StringBuilder();
		sb.Append("{\n");
		sb.Append("\t\"Slots\": [");
		for (int i = 0; i < slots.Count; i++)
		{
			SlotInfo slot = slots[i];
			if (i > 0)
			{
				sb.Append(',');
			}
			sb.Append('\n');
			sb.Append("\t\t{ \"Id\": ").Append(Quote(slot.Id))
				.Append(", \"Name\": ").Append(Quote(slot.Name))
				.Append(", \"CreatedAt\": ").Append(slot.CreatedAt.ToString(CultureInfo.InvariantCulture))
				.Append(", \"ModifiedAt\": ").Append(slot.ModifiedAt.ToString(CultureInfo.InvariantCulture))
				.Append(", \"Seed\": ").Append(slot.Seed.ToString(CultureInfo.InvariantCulture))
				.Append(" }");
		}
		if (slots.Count > 0)
		{
			sb.Append('\n');
			sb.Append("\t]");
		}
		else
		{
			sb.Append("]");
		}
		sb.Append('\n');
		sb.Append('}');
		return sb.ToString();
	}

	/// <summary>
	/// JSON-escapes a string for the index file. Escape handling mirrors the
	/// parser's (a backslash before any character is preserved), so names with
	/// quotes, backslashes, or unicode round-trip cleanly.
	/// </summary>
	private static string Quote(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "\"\"";
		}

		var sb = new StringBuilder(value.Length + 2);
		sb.Append('"');
		foreach (char c in value)
		{
			switch (c)
			{
				case '"':
					sb.Append("\\\"");
					break;
				case '\\':
					sb.Append("\\\\");
					break;
				case '\n':
					sb.Append("\\n");
					break;
				case '\r':
					sb.Append("\\r");
					break;
				case '\t':
					sb.Append("\\t");
					break;
				default:
					sb.Append(c);
					break;
			}
		}
		sb.Append('"');
		return sb.ToString();
	}
}
