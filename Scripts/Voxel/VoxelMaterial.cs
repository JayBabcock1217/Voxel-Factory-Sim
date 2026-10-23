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
	/// The texture for material <paramref name="id"/> (null when the material has
	/// no texture or the id is unconfigured). The inventory / hotbar / recipe UI
	/// uses this to draw the terrain's own texture as the item icon.
	/// </summary>
	public Texture2D GetTexture(int id)
		=> id > 0 && id < Textures.Length ? Textures[id - 1] : null;

	/// <summary>
	/// The shared blend shader (one <see cref="Shader"/> instance per palette)
	/// whose two samplers crossfade two material textures per-pixel. The
	/// per-vertex albedo tint (COLOR) and blend factor (UV2.x) are supplied by
	/// the mesh builder, so a single shader serves every material pair.
	/// </summary>
	private Shader _blendShader;

	/// <summary>
	/// A <see cref="ShaderMaterial"/> that crossfades the two given materials'
	/// textures per-pixel. Cached per unordered (idA, idB) pair so a chunk build
	/// allocates at most one material per distinct pair. The shader samples both
	/// materials' albedo textures (a 1×1 white fallback when a material has none)
	/// and mixes them by the per-vertex blend factor (UV2.x); the per-vertex
	/// albedo tint (COLOR) is applied per-material by the mesh builder.
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

		var matA = Materials[idA - 1];
		var matB = Materials[idB - 1];

		if (_blendShader == null)
		{
			_blendShader = new Shader { Code = BlendShaderCode };
		}

		var sm = new ShaderMaterial();
		sm.SetShader(_blendShader);
		sm.SetShaderParameter(new StringName("TextureA"), matA.AlbedoTexture ?? FallbackTexture);
		sm.SetShaderParameter(new StringName("TextureB"), matB.AlbedoTexture ?? FallbackTexture);
		_blendShaderCache[key] = sm;
		return sm;
	}

	/// <summary>
	/// The spatial-shader source backing <see cref="GetBlendShaderMaterial"/>.
	/// Kept verbatim in C# (and mirrored in MaterialBlendShader.gdshader) so the
	/// blend works without a compiled shader resource.
	/// </summary>
	private const string BlendShaderCode = @"shader_type spatial;

// repeat_enable wraps UVs at the sampler (the shader equivalent of
// StandardMaterial3D.TextureRepeat) so tiled UVs > 1.0 work; source_color
// applies the same sRGB->linear handling the standard materials use.
uniform sampler2D TextureA : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D TextureB : source_color, filter_linear_mipmap, repeat_enable;

varying vec2 blendVarying;

void vertex() {
	// UV2.x carries the per-vertex blend factor (0 = pure A, 1 = pure B).
	blendVarying = UV2;
}

void fragment() {
	// The mesh UVs are tiled by the per-material TextureScale (and in
	// MarchingCubes mode are world-position based), so they routinely exceed
	// 1.0. A raw spatial sampler has no texture_repeat (that is a
	// StandardMaterial3D property), so wrap the UV with fract() to tile exactly
	// like the standard materials — without it, UVs >= 1.0 sample outside the
	// texture and clamp to the edge mip (which reads as white).
	vec2 tuv = fract(UV);
	vec4 ta = texture(TextureA, tuv);
	vec4 tb = texture(TextureB, tuv);
	float f = clamp(blendVarying.x, 0.0, 1.0);
	vec3 blended = mix(ta.rgb, tb.rgb, f);
	ALBEDO = blended * COLOR.rgb;
	ROUGHNESS = 0.9;
	METALLIC = 0.0;
}
";

	/// <summary>
	/// A 1×1 white texture used as the sampler fallback for a material that has
	/// no albedo texture, so the blend shader always samples something.
	/// </summary>
	private static Texture2D FallbackTexture { get; } = ImageTexture.CreateFromImage(
		Image.CreateEmpty(1, 1, false, Image.Format.Rgb8));

	/// <summary>Number of material slots the palette was last sized to (0 = never applied).</summary>
	public int MaterialCount { get; private set; }

	// Blended shader materials cached per (idA, idB) pair by
	// GetBlendShaderMaterial. Cleared in Resize(), where the Materials array
	// is rebuilt (cached materials would then reference stale textures).
	private readonly System.Collections.Generic.Dictionary<int, ShaderMaterial> _blendShaderCache = new();

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
	/// </summary>
	public void Apply(VoxelMaterialConfig cfg)
	{
		var defs = cfg.GetAll(); // id order, id 1 first
		int n = Math.Min(Materials.Length, defs.Length);
		for (int i = 0; i < n; i++)
		{
			if (defs[i].Entry != null)
			{
				ApplyEntry(Materials[i], defs[i].Entry);
			}
			TextureScales[i] = defs[i].Entry != null ? defs[i].Entry.TextureScale : 1f;
			// Expose the texture directly so the UI can draw the same texture
			// it uses on the terrain as the item icon.
			Textures[i] = defs[i].Entry != null ? defs[i].Entry.Texture : null;
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

		// The Materials array is rebuilt below, so any cached blended shader
		// materials would reference stale textures.
		_blendShaderCache.Clear();

		var old = Materials;
		var oldScales = TextureScales;
		var oldTextures = Textures;
		var newMaterials = new StandardMaterial3D[count];
		var newScales = new float[count];
		var newTextures = new Texture2D[count];
		int keep = Math.Min(old.Length, count);
		for (int i = 0; i < keep; i++)
		{
			newMaterials[i] = old[i];
			newScales[i] = oldScales[i];
			newTextures[i] = i < oldTextures.Length ? oldTextures[i] : null;
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
		}

		Materials = newMaterials;
		TextureScales = newScales;
		Textures = newTextures;
		MaterialCount = count;
	}

	private static void ApplyEntry(StandardMaterial3D material, VoxelMaterialEntry entry)
	{
		material.AlbedoColor = entry.AlbedoColor;
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
