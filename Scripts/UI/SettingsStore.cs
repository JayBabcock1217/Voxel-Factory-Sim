using Godot;

namespace FactorySim.UI;

/// <summary>
/// Application settings that persist across runs. Serialized to a small JSON file
/// in the user data directory (so it works the same in the editor and a built
/// game) and exposed as a process-wide singleton via <see cref="Instance"/> so
/// both the main menu and the in-game pause menu read/write the same values.
///
/// Every setting is applied live through <see cref="Apply"/>: display settings
/// change the window immediately, and world settings are pushed onto the loaded
/// <see cref="FactorySim.Voxel.VoxelWorld"/> (chunk + LOD render distance are
/// picked up by the world's per-frame load/unload loop, no regeneration needed).
///
/// The settings object is a plain C# class (not a <see cref="Resource"/>), so it
/// is (de)serialized by hand with a small, self-contained JSON reader rather than
/// Godot's <see cref="Json"/> (which has no object-binding in C#).
/// </summary>
public sealed class SettingsStore
{
	/// <summary>Process-wide singleton, created on first access.</summary>
	public static SettingsStore Instance { get; } = new();

	private static string SavePath =>
		System.IO.Path.Combine(OS.GetUserDataDir(), "settings.json");

	private bool _initialized;

	// --- Display ---
	public bool Fullscreen { get; set; }
	public int ResolutionWidth { get; set; } = 1920;
	public int ResolutionHeight { get; set; } = 1080;
	public bool VSync { get; set; } = true;
	public int FpsCap { get; set; } = 0; // 0 = unlimited

	// --- Rendering ---
	public int MeshMode { get; set; } = 1; // 0 = Faces, 1 = MarchingCubes
	public bool ShadowsEnabled { get; set; } = true;
	public bool FogEnabled { get; set; }
	public float FogDensity { get; set; } = 0.004f;
	public float AmbientLightEnergy { get; set; } = 0.3f;
	public float SunIntensity { get; set; } = 1.0f;

	// --- World ---
	public int ChunkRenderDistance { get; set; } = 16;
	public int LodRenderDistance { get; set; } = 256;
	public bool LodEnabled { get; set; } = true;

	private SettingsStore() { }

	/// <summary>
	/// Loads settings from disk once (on the first call). If no file exists yet,
	/// the code defaults are used and written out so the file exists afterwards.
	/// A corrupt or partial file degrades gracefully to the defaults.
	/// </summary>
	public void Initialize()
	{
		if (_initialized)
		{
			return;
		}
		_initialized = true;

		if (System.IO.File.Exists(SavePath))
		{
			try
			{
				string json = System.IO.File.ReadAllText(SavePath);
				var root = JsonParser.ParseObject(json);
				if (root != null)
				{
					Fullscreen = JsonParser.GetBool(root, "Fullscreen", Fullscreen);
					ResolutionWidth = JsonParser.GetInt(root, "ResolutionWidth", ResolutionWidth);
					ResolutionHeight = JsonParser.GetInt(root, "ResolutionHeight", ResolutionHeight);
					VSync = JsonParser.GetBool(root, "VSync", VSync);
					FpsCap = JsonParser.GetInt(root, "FpsCap", FpsCap);
					MeshMode = JsonParser.GetInt(root, "MeshMode", MeshMode);
					ShadowsEnabled = JsonParser.GetBool(root, "ShadowsEnabled", ShadowsEnabled);
					FogEnabled = JsonParser.GetBool(root, "FogEnabled", FogEnabled);
					FogDensity = JsonParser.GetFloat(root, "FogDensity", FogDensity);
					AmbientLightEnergy = JsonParser.GetFloat(root, "AmbientLightEnergy", AmbientLightEnergy);
					SunIntensity = JsonParser.GetFloat(root, "SunIntensity", SunIntensity);
					ChunkRenderDistance = JsonParser.GetInt(root, "ChunkRenderDistance", ChunkRenderDistance);
					LodRenderDistance = JsonParser.GetInt(root, "LodRenderDistance", LodRenderDistance);
					LodEnabled = JsonParser.GetBool(root, "LodEnabled", LodEnabled);
				}
			}
			catch (System.Exception e)
			{
				GD.PushWarning($"[SettingsStore] Failed to load settings, using defaults: {e.Message}");
			}
		}
		else
		{
			Save();
		}

		Sanitize();
	}

	/// <summary>Clamps values into sane ranges so a hand-edited file can't break things.</summary>
	private void Sanitize()
	{
		ResolutionWidth = Mathf.Clamp(ResolutionWidth, 640, 3840);
		ResolutionHeight = Mathf.Clamp(ResolutionHeight, 360, 2160);
		FpsCap = Mathf.Clamp(FpsCap, 0, 360);
		MeshMode = Mathf.Clamp(MeshMode, 0, 1);
		FogDensity = Mathf.Clamp(FogDensity, 0f, 0.1f);
		AmbientLightEnergy = Mathf.Clamp(AmbientLightEnergy, 0f, 2f);
		SunIntensity = Mathf.Clamp(SunIntensity, 0f, 5f);
		ChunkRenderDistance = Mathf.Clamp(ChunkRenderDistance, 1, 32);
		LodRenderDistance = Mathf.Clamp(LodRenderDistance, 1, 256);
		if (LodRenderDistance < ChunkRenderDistance)
		{
			LodRenderDistance = ChunkRenderDistance;
		}
	}

	/// <summary>Persists the current settings to the user data directory.</summary>
	public void Save()
	{
		try
		{
			// Hand-rolled JSON: the property names match the C# property names
			// exactly, so the reader round-trips cleanly.
			string json =
				"{\n" +
				$"\t\"Fullscreen\": {(Fullscreen ? "true" : "false")},\n" +
				$"\t\"ResolutionWidth\": {ResolutionWidth},\n" +
				$"\t\"ResolutionHeight\": {ResolutionHeight},\n" +
				$"\t\"VSync\": {(VSync ? "true" : "false")},\n" +
				$"\t\"FpsCap\": {FpsCap},\n" +
				$"\t\"MeshMode\": {MeshMode},\n" +
				$"\t\"ShadowsEnabled\": {(ShadowsEnabled ? "true" : "false")},\n" +
				$"\t\"FogEnabled\": {(FogEnabled ? "true" : "false")},\n" +
				$"\t\"FogDensity\": {FogDensity},\n" +
				$"\t\"AmbientLightEnergy\": {AmbientLightEnergy},\n" +
				$"\t\"SunIntensity\": {SunIntensity},\n" +
				$"\t\"ChunkRenderDistance\": {ChunkRenderDistance},\n" +
				$"\t\"LodRenderDistance\": {LodRenderDistance},\n" +
				$"\t\"LodEnabled\": {(LodEnabled ? "true" : "false")}\n" +
				"}";
			System.IO.File.WriteAllText(SavePath, json);
		}
		catch (System.Exception e)
		{
			GD.PushWarning($"[SettingsStore] Failed to save settings: {e.Message}");
		}
	}

	/// <summary>
	/// Applies the display + rendering + world settings to the running game.
	/// Display changes take effect immediately (window size / fullscreen /
	/// VSync / FPS cap); rendering changes update the environment and lights;
	/// world changes are pushed onto the loaded voxel world if there is one.
	/// </summary>
	public void Apply()
	{
		ApplyDisplay();
		ApplyRendering();
	}

	/// <summary>Applies only the window/display settings (resolution + fullscreen + VSync + FPS cap).</summary>
	public void ApplyDisplay()
	{
		if (Fullscreen)
		{
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
		}
		else
		{
			DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
			DisplayServer.WindowSetSize(new Vector2I(ResolutionWidth, ResolutionHeight));
		}

		// VSync.
		DisplayServer.WindowSetVsyncMode(VSync ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);

		// FPS cap: 0 = unlimited.
		Engine.MaxFps = FpsCap;
	}

	/// <summary>
	/// Applies rendering settings (mesh mode, shadows, fog, lighting, wireframe)
	/// to the loaded world's environment and lights. No-op in the main menu.
	/// </summary>
	private void ApplyRendering()
	{
		var tree = Engine.GetMainLoop() as SceneTree;
		if (tree == null)
		{
			return;
		}

		var world = tree.GetFirstNodeInGroup("voxel_world") as FactorySim.Voxel.VoxelWorld;
		if (world == null)
		{
			return;
		}

		// Mesh mode: setting the property triggers a live rebuild of all chunks.
		world.MeshMode = (FactorySim.Voxel.VoxelMeshMode)MeshMode;

		// Shadows, fog, ambient light, sun intensity via the WorldEnvironment.
		var worldEnv = FindWorldEnvironment(world.GetTree().Root);
		if (worldEnv != null && worldEnv.Environment != null)
		{
			var env = worldEnv.Environment;

			// Shadows: toggle on the directional light.
			var sun = FindDirectionalLight(world.GetTree().Root);
			if (sun != null)
			{
				sun.ShadowEnabled = ShadowsEnabled;
				sun.LightEnergy = SunIntensity;
			}

			// Fog.
			env.FogEnabled = FogEnabled;
			env.FogDensity = FogDensity;

			// Ambient light: use the Color source and set its energy + color.
			env.AmbientLightSource = Environment.AmbientSource.Color;
			env.AmbientLightEnergy = AmbientLightEnergy;
			env.AmbientLightColor = Colors.White;
		}

	}

	private static WorldEnvironment FindWorldEnvironment(Node root)
	{
		if (root is WorldEnvironment we)
		{
			return we;
		}
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			var result = FindWorldEnvironment(root.GetChild(i));
			if (result != null)
			{
				return result;
			}
		}
		return null;
	}

	private static DirectionalLight3D FindDirectionalLight(Node root)
	{
		if (root is DirectionalLight3D light)
		{
			return light;
		}
		for (int i = 0; i < root.GetChildCount(); i++)
		{
			var result = FindDirectionalLight(root.GetChild(i));
			if (result != null)
			{
				return result;
			}
		}
		return null;
	}


}

/// <summary>
/// A tiny, dependency-free JSON reader for the flat <c>{ "name": scalar }</c>
/// objects <see cref="SettingsStore"/> persists. Supports string, integer and
/// boolean values only — which is all the settings file needs. A parse failure
/// returns <c>null</c> so the caller can fall back to defaults.
/// </summary>
internal static class JsonParser
{
	/// <summary>Parses a flat JSON object into a name→raw-value map, or null on failure.</summary>
	public static System.Collections.Generic.Dictionary<string, string> ParseObject(string json)
	{
		var result = new System.Collections.Generic.Dictionary<string, string>();
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		int i = 0;
		SkipWs(json, ref i);
		if (i >= json.Length || json[i] != '{')
		{
			return null;
		}
		i++; // consume '{'

		while (true)
		{
			SkipWs(json, ref i);
			if (i >= json.Length)
			{
				return null;
			}
			if (json[i] == '}')
			{
				break; // end of object
			}
			if (json[i] != '"')
			{
				return null; // expected a key
			}

			string key = ReadString(json, ref i);
			if (key == null)
			{
				return null;
			}

			SkipWs(json, ref i);
			if (i >= json.Length || json[i] != ':')
			{
				return null;
			}
			i++; // consume ':'

			SkipWs(json, ref i);
			if (i >= json.Length)
			{
				return null;
			}
			string value = ReadValue(json, ref i);
			if (value == null)
			{
				return null;
			}
			result[key] = value;

			SkipWs(json, ref i);
			if (i < json.Length && json[i] == ',')
			{
				i++; // consume ','
			}
			else if (i < json.Length && json[i] == '}')
			{
				break;
			}
			else
			{
				return null;
			}
		}

		return result;
	}

	private static string ReadValue(string json, ref int i)
	{
		// string
		if (json[i] == '"')
		{
			return ReadString(json, ref i);
		}
		// true / false / number: read until a delimiter
		int start = i;
		while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']' && json[i] != ':')
		{
			i++;
		}
		string token = json.Substring(start, i - start).Trim();
		return token.Length == 0 ? null : token;
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

	public static int GetInt(System.Collections.Generic.Dictionary<string, string> obj, string key, int fallback)
		=> obj != null && obj.TryGetValue(key, out string v) && int.TryParse(v, out int n) ? n : fallback;

	public static bool GetBool(System.Collections.Generic.Dictionary<string, string> obj, string key, bool fallback)
		=> obj != null && obj.TryGetValue(key, out string v) && bool.TryParse(v, out bool b) ? b : fallback;

	public static float GetFloat(System.Collections.Generic.Dictionary<string, string> obj, string key, float fallback)
		=> obj != null && obj.TryGetValue(key, out string v) && float.TryParse(v, System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : fallback;
}
