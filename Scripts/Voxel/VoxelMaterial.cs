using Godot;
using System;

namespace FactorySim.Voxel;

/// <summary>
/// Canonical voxel material ids. Id 0 is always air (no material); solid
/// materials start at 1.
///
/// These are the DEFAULT material ids. Generation is data-driven: it reads the
/// ids configured on each biome / structure, so a world can use any id up to
/// 255. The constants below are the defaults plus the "tool / sentinel" ids:
/// <see cref="Placed"/> is the material the add-voxel tool stamps, and the
/// mesh builders treat id 0 (air) as their "no material" sentinel.
/// </summary>
public static class VoxelMaterialIds
{
	public const int Air = 0;
	public const int Grass = 1;
	public const int Dirt = 2;
	public const int Stone = 3;
	public const int CaveStone = 4;
	public const int Trunk = 5;
	public const int Leaves = 6;
	public const int Rock = 7;
	public const int Placed = 8; // material used by the "add voxel" tool
	public const int Sand = 9;
	public const int Snow = 10;
	public const int Ice = 11;
	public const int Cactus = 12;
	public const int CoalOre = 13;
	public const int IronOre = 14;
	public const int CopperOre = 15;
	public const int GoldOre = 16;
	public const int Bedrock = 17; // unbreakable, fills the world floor (y 0..1)

	// Crafting products (not generated in terrain; only obtainable via crafting).
	public const int Plank = 18;
	public const int IronPlate = 19;
	public const int Coal = 20;
	public const int StoneBrick = 21;
	public const int MachineFrame = 22;

	// ------------------------------------------------------------------
	// Tool items (not placeable voxels; they live in the inventory and are
	// selected from the hotbar to break blocks). These ids are NOT solid
	// materials — the world never stamps them, and the mesh/palette builders
	// never see them. They exist so a tool is a first-class inventory item
	// (see ItemStack.ToolType / ItemRegistry).
	// ------------------------------------------------------------------
	public const int ToolShovel = 100;
	public const int ToolPickaxe = 101;
	public const int ToolAxe = 102;
	public const int ToolSword = 103;

	/// <summary>First id in the tool block (tools are a contiguous range).</summary>
	public const int ToolFirst = ToolShovel;

	/// <summary>Last id in the tool block (inclusive).</summary>
	public const int ToolLast = ToolSword;

	/// <summary>True when <paramref name="id"/> is a tool item id (not a placeable material).</summary>
	public static bool IsToolId(int id) => id >= ToolFirst && id <= ToolLast;

	// ------------------------------------------------------------------
	// Machine items (not placeable voxels; they live in the inventory and, when
	// placed, spawn a Machine node with its imported mesh — the factory design
	// system). These ids are NOT solid materials: the world never stamps them
	// and the mesh/palette builders never see them. They exist so a machine is
	// a first-class inventory item (see ItemStack.MachineType / ItemRegistry).
	//
	// The range starts at 200 (above the tool block, 100-103) and is deliberately
	// wide (up to 299) so many future machine types can be added without
	// touching the range. The MachineType enum's underlying values are the
	// machine item ids (the same convention as ToolType).
	// ------------------------------------------------------------------
	public const int MachineFirst = 200;
	public const int MachineLast = 299;

	/// <summary>True when <paramref name="id"/> is in the machine item id range (a broad test; a machine also needs a registered definition).</summary>
	public static bool IsMachineId(int id) => id >= MachineFirst && id <= MachineLast;

	/// <summary>True when <paramref name="id"/> is a placeable material (a solid voxel you can hold and place).</summary>
	public static bool IsPlaceableMaterialId(int id) => id > 0 && !IsToolId(id) && !IsMachineId(id);
}

/// <summary>
/// Full-health value for a freshly generated or placed voxel. The voxel health
/// system stores health as a percentage of this value (100.0 = undamaged).
/// </summary>
public static class VoxelHealth
{
	/// <summary>Full health (percentage). Every voxel starts at this value.</summary>
	public const float MaxHealth = 100f;
}

/// <summary>
/// The shared set of materials used by every chunk mesh.
/// Index into <see cref="Materials"/> is <c>materialId - 1</c> (id 0 = air has no
/// material, so the array is sized to the number of material defs, not 256).
///
/// The StandardMaterial3D instances are created ONCE and then mutated in place
/// by <see cref="Apply(VoxelMaterialConfig)"/>, so inspector edits to the
/// material config update every already-built chunk without remeshing. When the
/// material list changes (count or ids), the palette resizes; the caller flags
/// the chunks for a rebuild because their material indices then shift.
/// </summary>
public sealed class VoxelMaterialPalette
{
	/// <summary>Material for each solid voxel type (index = material id - 1).</summary>
	public StandardMaterial3D[] Materials { get; private set; } = Array.Empty<StandardMaterial3D>();

	/// <summary>Per-material texture tiling, consumed by the mesh builders when they emit UVs.</summary>
	public float[] TextureScales { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// The texture for each solid voxel type (index = material id - 1). Null when
	/// the material has no texture (flat color). Exposed so the inventory /
	/// hotbar / recipe UI can draw the SAME texture it uses on the terrain as an
	/// item icon. Updated in place by <see cref="Apply"/>.
	/// </summary>
	public Texture2D[] Textures { get; private set; } = Array.Empty<Texture2D>();

	/// <summary>
	/// The per-material albedo tint (index = material id - 1), mirroring
	/// <see cref="VoxelMaterialEntry.AlbedoColor"/>. The blend shader applies
	/// this tint exactly like <see cref="StandardMaterial3D"/> does
	/// (ALBEDO = tint × texture) so the crossfade reads the same brightness as
	/// the plain materials. Updated in place by <see cref="Apply"/>.
	/// </summary>
	public Color[] Tints { get; private set; } = Array.Empty<Color>();

	/// <summary>
	/// The texture for material <paramref name="id"/> (null when the material has
	/// no texture or the id is unconfigured). The inventory / hotbar / recipe UI
	/// uses this to draw the terrain's own texture as the item icon.
	/// </summary>
	public Texture2D GetTexture(int id)
		=> id > 0 && id < Textures.Length ? Textures[id - 1] : null;

	/// <summary>Number of material slots the palette was last sized to (0 = never applied).</summary>
	public int MaterialCount { get; private set; }

	/// <summary>
	/// Whether the material-blend crossfade is enabled (mirrors
	/// <see cref="VoxelMaterialConfig.EnableBlend"/>). The mesh builders use this
	/// to decide whether material-interface faces crossfade. Set by the world in
	/// <c>ApplyMaterials</c>; a change requires a remesh (the world handles that
	/// via the config's BlendChanged event).
	/// </summary>
	public bool BlendEnabled { get; set; }

	// ------------------------------------------------------------------
	// Material-blend crossfade
	// ------------------------------------------------------------------

	/// <summary>
	/// The shared blend shader (one <see cref="Shader"/> instance per palette)
	/// whose two samplers crossfade two material textures per-pixel. The
	/// per-vertex blend factor (UV2.x) is supplied by the mesh builder, so a
	/// single shader serves every material pair.
	/// </summary>
	private Shader _blendShader;

	// Blended shader materials cached per (idA, idB) pair. Cleared in Resize(),
	// where the Materials/Textures arrays are rebuilt (cached materials would then
	// reference stale textures).
	private readonly System.Collections.Generic.Dictionary<int, ShaderMaterial> _blendShaderCache = new();

	/// <summary>
	/// A <see cref="ShaderMaterial"/> that crossfades the two given materials'
	/// textures per-pixel. Cached per unordered (idA, idB) pair so a chunk build
	/// allocates at most one material per distinct pair. The shader samples both
	/// materials' albedo textures (a 1×1 white fallback when a material has none)
	/// and mixes them by the per-vertex blend factor (UV2.x: 0 = pure A / lower
	/// id, 1 = pure B / higher id). The pair is ALWAYS ordered by id: A = lower
	/// id, B = higher id, so the same material is returned for (a,b) and (b,a).
	/// </summary>
	public ShaderMaterial GetBlendShaderMaterial(int idA, int idB)
	{
		if (idA > idB)
		{
			int t = idA;
			idA = idB;
			idB = t;
		}
		int key = idA * 256 + idB;
		if (_blendShaderCache.TryGetValue(key, out var cached))
		{
			return cached;
		}

		if (_blendShader == null)
		{
			_blendShader = new Shader { Code = BlendShaderCode };
		}

		var sm = new ShaderMaterial();
		sm.SetShader(_blendShader);
		sm.SetShaderParameter(new StringName("TextureA"), Textures[idA - 1] ?? FallbackTexture);
		sm.SetShaderParameter(new StringName("TextureB"), Textures[idB - 1] ?? FallbackTexture);
		// Each material's AlbedoColor tint, applied in the shader exactly like
		// StandardMaterial3D (ALBEDO = tint * texture) so the blend reads the
		// same brightness as the plain materials. Out-of-range = white tint.
		sm.SetShaderParameter(new StringName("TintA"), Tints.Length > idA - 1 ? Tints[idA - 1] : Colors.White);
		sm.SetShaderParameter(new StringName("TintB"), Tints.Length > idB - 1 ? Tints[idB - 1] : Colors.White);
		_blendShaderCache[key] = sm;
		return sm;
	}

	/// <summary>
	/// Drops all cached blend shader materials so they are rebuilt on the next
	/// mesh build. Call this when a material's <see cref="VoxelMaterialEntry
	/// .AlbedoColor"/> (the tint baked into the cached ShaderMaterials) changes,
	/// or its texture changes — otherwise a cached pair keeps the stale tint.
	/// </summary>
	public void ClearBlendCache() => _blendShaderCache.Clear();

	/// <summary>
	/// The spatial-shader source backing <see cref="GetBlendShaderMaterial"/>.
	/// Kept verbatim in C# (and mirrored in MaterialBlendShader.gdshader) so the
	/// blend works without a compiled shader resource.
	///
	/// Four correctness gotchas are baked in (all learned the hard way):
	///  * The samplers MUST be declared :source_color — the material textures are
	///    sRGB, and a plain sampler reads sRGB bytes as linear values, which the
	///    engine then converts back to sRGB on output, producing an overexposed
	///    wash. source_color gives the SAME sRGB->linear handling StandardMaterial3D
	///    applies, so the blend matches the rest of the terrain.
	///  * The mesh UVs are tiled by the per-material TextureScale and routinely
	///    exceed 1.0; a raw spatial sampler has no texture_repeat, so wrap with
	///    fract(UV) to tile exactly like the standard materials.
	///  * The ALBEDO MUST be tinted by each material's AlbedoColor, mixed by the
	///    blend factor — StandardMaterial3D computes ALBEDO = AlbedoColor ×
	///    albedoTexture, so skipping the tint (as the first version did) made the
	///    blend visibly LIGHTER than the plain materials. Tinting both sides and
	///    mixing makes the blend's albedo path identical to the standard one.
	///  * Godot spatial shaders have NO WORLD_POSITION built-in. VERTEX is in
	///    model (local) space in vertex(), so the world position is
	///    (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz.
	/// </summary>
	private const string BlendShaderCode = @"shader_type spatial;

uniform sampler2D TextureA : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D TextureB : source_color, filter_linear_mipmap, repeat_enable;
// Per-material albedo tint, applied exactly like StandardMaterial3D does:
// ALBEDO = tint * texture. Without this the blend skips the tint and reads
// lighter than the plain (non-blended) materials.
uniform vec4 TintA;
uniform vec4 TintB;

varying float vFactor;

void vertex() {
	// UV2.x carries the per-vertex blend factor (0 = pure A / lower id,
	// 1 = pure B / higher id). The mesh builder computes it as a vertical
	// gradient across the face.
	vFactor = UV2.x;
}

void fragment() {
	// Tiled UVs (see the gotchas in the C# doc): wrap with fract() to tile
	// beyond 1.0, since a raw spatial sampler has no texture_repeat.
	vec2 tuv = fract(UV);
	vec4 ta = texture(TextureA, tuv) * TintA;
	vec4 tb = texture(TextureB, tuv) * TintB;
	float f = clamp(vFactor, 0.0, 1.0);
	// The samplers are :source_color and the tint is the linear AlbedoColor,
	// so each side is EXACTLY what StandardMaterial3D produces for that
	// material's ALBEDO — mixing them gives a crossfade that matches the
	// plain materials at f=0 and f=1 and reads the same brightness in between.
	ALBEDO = mix(ta.rgb, tb.rgb, f);
	// Match the standard materials' PBR response EXACTLY. The plain materials
	// (VoxelMaterialEntry defaults) are: Roughness 1.0, Metallic 0.0, and
	// SchlickGgx specular with MetallicSpecular 0.05. In a raw spatial shader
	// the SchlickGgx specular strength is the SPECULAR output — leaving it at
	// the 0.5 default adds ~10x the sheen the plain material has, which is what
	// made the blend read BRIGHTER. Pinning SPECULAR to 0.05 (and ROUGHNESS to
	// 1.0) reproduces the plain material's diffuse + subtle specular, so the
	// crossfade reads the same brightness as the non-blended surfaces.
	ROUGHNESS = 1.0;
	SPECULAR = 0.05;
	METALLIC = 0.0;
}
";

	/// <summary>
	/// A 1×1 white texture used as the sampler fallback for a material that has
	/// no albedo texture, so the blend shader always samples something.
	/// </summary>
	private static Texture2D FallbackTexture { get; } = ImageTexture.CreateFromImage(
		Image.CreateEmpty(1, 1, false, Image.Format.Rgb8));

	public VoxelMaterialPalette()
	{
		// Sized lazily by Resize(); start empty so a palette can exist before a
		// config is wired (the world and the test suite apply a config at setup).
	}

	/// <summary>True when <paramref name="id"/> is a valid, configured solid material id (1..count).</summary>
	public bool HasMaterial(int id) => id > 0 && id <= Materials.Length;

	/// <summary>
	/// Copies every config value onto the material instances. Instances already
	/// occupy id-ordered slots (slot <c>i</c> = the <c>i</c>-th def in id order),
	/// so each is mutated in place — a live update that needs no remesh. When the
	/// material SET changes (count or ids), the caller resizes via
	/// <see cref="Resize"/> and flags chunks for a rebuild.
	///
	/// Brighter-look rule: a material that has a TEXTURE gets a white AlbedoColor
	/// tint, so the texture shows at full brightness (the texture carries the
	/// color). Untextured materials keep their configured AlbedoColor (it IS the
	/// color — there is no texture to brighten). This applies to BOTH the
	/// StandardMaterial3D (plain surfaces) and the blend shader's tint, so the
	/// crossfade and the plain materials read identically.
	/// </summary>
	public void Apply(VoxelMaterialConfig cfg)
	{
		var defs = cfg.GetAll(); // id order, id 1 first
		int n = Math.Min(Materials.Length, defs.Length);
		for (int i = 0; i < n; i++)
		{
			if (defs[i].Entry == null)
			{
				continue;
			}

			bool hasTexture = defs[i].Entry.Texture != null;
			// A textured material shows the texture at full brightness (white
			// tint); an untextured one keeps its configured color.
			Color tint = hasTexture ? Colors.White : defs[i].Entry.AlbedoColor;

			ApplyEntry(Materials[i], defs[i].Entry, tint);
			TextureScales[i] = defs[i].Entry.TextureScale;
			// Expose the texture directly so the UI can draw the same texture
			// it uses on the terrain as the item icon.
			Textures[i] = defs[i].Entry.Texture;
			Tints[i] = tint;
		}
	}

	/// <summary>
	/// Resizes the palette to <paramref name="count"/> slots. Existing slots are
	/// kept (and re-<see cref="Apply"/>-ed in place); new slots get a fresh
	/// StandardMaterial3D with the shared faceted defaults. Call this when the
	/// material list's count changes, then <see cref="Apply"/> to fill values.
	/// </summary>
	public void Resize(int count)
	{
		if (count < 0)
		{
			count = 0;
		}

		// The Materials/Textures arrays are rebuilt below, so any cached blended
		// shader materials would reference stale textures.
		_blendShaderCache.Clear();

		var old = Materials;
		var oldScales = TextureScales;
		var oldTextures = Textures;
		var oldTints = Tints;
		var newMaterials = new StandardMaterial3D[count];
		var newScales = new float[count];
		var newTextures = new Texture2D[count];
		var newTints = new Color[count];
		int keep = Math.Min(old.Length, count);
		for (int i = 0; i < keep; i++)
		{
			newMaterials[i] = old[i];
			newScales[i] = oldScales[i];
			newTextures[i] = i < oldTextures.Length ? oldTextures[i] : null;
			newTints[i] = i < oldTints.Length ? oldTints[i] : Colors.White;
		}
		for (int i = keep; i < count; i++)
		{
			// The faceted "flat shaded" look (Astroneer-style) is achieved
			// geometrically in MeshBuilder: each triangle owns its 3 vertices
			// and they all carry the face normal, so no face ever blends
			// normals with a neighbour. Cheap on integrated graphics.
		// SchlickGgx is the standard physically-based model (healthy diffuse +
		// a little specular). The "wet" sheen came from the DEFAULT
		// MetallicSpecular of 0.5, so we lower it to 0.05 (subtle, not mirror-
		// like). ApplyEntry re-applies the per-material SpecularMode /
		// SpecularAmount (→ MetallicSpecular) from the entry.
			newMaterials[i] = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex,
				Roughness = 1.0f,
				SpecularMode = BaseMaterial3D.SpecularModeEnum.SchlickGgx,
				MetallicSpecular = 0.05f,
			};
			newScales[i] = 1f;
			newTextures[i] = null;
			newTints[i] = Colors.White;
		}

		Materials = newMaterials;
		TextureScales = newScales;
		Textures = newTextures;
		Tints = newTints;
		MaterialCount = count;
	}

	/// <summary>
	/// Copies a config entry onto a material. <paramref name="albedoTint"/> is
	/// the AlbedoColor to apply — normally the entry's own color, but a white
	/// tint for textured materials (so the texture shows at full brightness;
	/// see <see cref="Apply"/>).
	/// </summary>
	private static void ApplyEntry(StandardMaterial3D material, VoxelMaterialEntry entry, Color albedoTint)
	{
		material.AlbedoColor = albedoTint;
		material.AlbedoTexture = entry.Texture;

		// Repeat mode is what lets TextureScale tile beyond one 0..1 face:
		// without it the sampler clamps at UV (1,1).
		material.TextureRepeat = entry.Texture != null;

		material.ShadingMode = entry.ShadingMode;
		material.Transparency = entry.Transparency;
		material.Metallic = entry.Metallic;
		material.Roughness = entry.Roughness;
		material.SpecularMode = entry.SpecularMode;
		material.MetallicSpecular = entry.SpecularAmount;
		material.EmissionEnabled = entry.EmissionEnabled;
		material.Emission = entry.EmissionColor;
		material.EmissionEnergyMultiplier = entry.EmissionEnergy;
	}
}
