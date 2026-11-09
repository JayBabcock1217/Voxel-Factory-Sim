using System;
using Godot;

namespace FactorySim.Voxel;

/// <summary>
/// Editor-facing appearance settings for ONE voxel material type (grass, dirt, ...).
/// Every property can be tweaked in the inspector — changes are copied onto the
/// shared StandardMaterial3D instances in place, so already-built chunk meshes
/// update live without remeshing. Right-click "Save" turns it into a .tres you
/// can reuse across scenes.
/// </summary>
[GlobalClass]
public partial class VoxelMaterialEntry : Resource
{
	/// <summary>Fired whenever any property of this entry changes.</summary>
	public event Action<VoxelMaterialEntry> PropertyChanged;

	/// <summary>Base color (tints the texture when one is set). The alpha channel drives opacity for transparent materials.</summary>
	[Export] public Color AlbedoColor { get; set; } = new(1f, 1f, 1f);
	public void SetAlbedoColor(Color value)
	{
		if (AlbedoColor != value)
		{
			AlbedoColor = value;
			Raise();
		}
	}

	/// <summary>
	/// Optional human-readable description of this material, shown in item /
	/// recipe tooltips. Empty by default; the UI falls back to the material's
	/// name when this is blank.
	/// </summary>
	[Export(PropertyHint.MultilineText)] public string Description { get; set; } = "";
	public void SetDescription(string value)
	{
		if (Description != value)
		{
			Description = value ?? "";
			Raise();
		}
	}

	/// <summary>Optional texture. Null = flat color.</summary>
	[Export]
	public Texture2D Texture
	{
		get => _texture;
		set
		{
			// TEMP DIAGNOSTIC (remove after verifying): log every non-null
			// Texture assignment so we can see whether the .tres's texture
			// assignments reach C# (property bridge) or not.
			if (value != null)
			{
				GD.Print($"[MatDiag] Texture SET path={value.ResourcePath} class={value.GetClass()}");
			}
			_texture = value;
			Raise();
		}
	}
	private Texture2D _texture;
	/// <summary>
	/// Assigns the texture, guarding against a malformed value. NOTE: the C#
	/// property setter is NOT what Godot's property bridge uses to load a .tres —
	/// the generated <c>SetGodotClassPropertyValue</c> writes the backing field
	/// directly and throws <see cref="InvalidCastException"/> if the stored value
	/// is a non-Texture2D (e.g. a bare <c>Resource</c> placeholder for a texture
	/// whose image was missing at save time). The real defense is
	/// <see cref="VoxelMaterialConfig.SanitizeTextures"/> (run at load), which
	/// clears any such reference BEFORE the engine re-sets properties. This setter
	/// is for C# code paths and keeps the null-check symmetric.
	/// </summary>
	public void SetTexture(Texture2D value)
	{
		if (!ReferenceEquals(Texture, value))
		{
			Texture = value;
			Raise();
		}
	}

	/// <summary>
	/// True when the <see cref="Texture"/> reference is a real, usable
	/// <see cref="Texture2D"/> (non-null, the right type, and not a freed
	/// resource). Used by <see cref="VoxelMaterialConfig.SanitizeTextures"/> to
	/// drop malformed references that would crash the property bridge.
	///
	/// NOTE: this must NOT compare <c>GetClass() == "Texture2D"</c>. An imported
	/// PNG loads as a CONCRETE subclass (e.g. <c>CompressedTexture2D</c> /
	/// <c>ImageTexture</c>), whose <c>GetClass()</c> is the subclass name — not
	/// the base "Texture2D" — so that comparison is false for every real texture
	/// and the sanitizer wrongly wiped all of them. The C# <c>is</c> check tests
	/// the whole Texture2D hierarchy, and a null/detached backing (a freed or
	/// not-yet-loaded native object) is caught by the <c>ReferenceEquals(null)</c>
	/// probe.
	/// </summary>
	public bool HasValidTexture()
	{
		var tex = Texture;
		if (tex == null)
		{
			return false;
		}
		// A genuine Texture2D of any concrete class (ImageTexture,
		// CompressedTexture2D, AtlasTexture, ...). A bare Resource placeholder
		// for a missing file fails this check and is dropped.
		if (!(tex is Texture2D))
		{
			return false;
		}
		// A freed / detached native object surfaces as a wrapper that compares
		// equal to null (or throws when its native handle is touched). Guard so
		// a disposed texture is treated as broken, not valid.
		if (ReferenceEquals(tex, null))
		{
			return false;
		}
		try
		{
			// Touch a native-backed property to force the handle check: a
			// disposed resource throws here.
			_ = tex.GetClass();
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>How many times the texture repeats per voxel face (1 = one tile per face).</summary>
	[Export(PropertyHint.Range, "0.05,32,0.05")] public float TextureScale { get; set; } = 1f;
	public void SetTextureScale(float value)
	{
		if (!Mathf.IsEqualApprox(TextureScale, value))
		{
			TextureScale = Mathf.Max(0.05f, value);
			Raise();
		}
	}

	/// <summary>PerVertex keeps the faceted blocky look; PerPixel interpolates normals smoothly.</summary>
	[Export] public BaseMaterial3D.ShadingModeEnum ShadingMode { get; set; } = BaseMaterial3D.ShadingModeEnum.PerVertex;
	public void SetShadingMode(BaseMaterial3D.ShadingModeEnum value)
	{
		if (ShadingMode != value)
		{
			ShadingMode = value;
			Raise();
		}
	}

	/// <summary>Disabled = opaque. Alpha = smooth alpha blending from the color/texture's alpha channel. Scissor/Hash = hard-edged transparency.</summary>
	[Export] public BaseMaterial3D.TransparencyEnum Transparency { get; set; } = BaseMaterial3D.TransparencyEnum.Disabled;
	public void SetTransparency(BaseMaterial3D.TransparencyEnum value)
	{
		if (Transparency != value)
		{
			Transparency = value;
			Raise();
		}
	}

	/// <summary>Metallic: how strongly the material reflects light sources (0 = matte dielectric, 1 = metal).</summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float Metallic { get; set; }
	public void SetMetallic(float value)
	{
		if (!Mathf.IsEqualApprox(Metallic, value))
		{
			Metallic = Mathf.Clamp(value, 0f, 1f);
			Raise();
		}
	}

	/// <summary>Roughness: how scattered reflections are (0 = mirror-like, 1 = fully diffuse).</summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float Roughness { get; set; } = 1f;
	public void SetRoughness(float value)
	{
		if (!Mathf.IsEqualApprox(Roughness, value))
		{
			Roughness = Mathf.Clamp(value, 0f, 1f);
			Raise();
		}
	}

	/// <summary>Specular model: SchlickGgx (default) = standard physically-based dielectric; Toon = stylized; Disabled = fully matte (no specular at all). The "wet" look is controlled by <see cref="SpecularAmount"/>, not the mode.</summary>
	[Export] public BaseMaterial3D.SpecularModeEnum SpecularMode { get; set; } = BaseMaterial3D.SpecularModeEnum.SchlickGgx;
	public void SetSpecularMode(BaseMaterial3D.SpecularModeEnum value)
	{
		if (SpecularMode != value)
		{
			SpecularMode = value;
			Raise();
		}
	}

	/// <summary>Specular reflection strength (0 = none, 1 = full). Mapped onto the material's MetallicSpecular.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")] public float SpecularAmount { get; set; } = 0.05f;
	public void SetSpecularAmount(float value)
	{
		if (!Mathf.IsEqualApprox(SpecularAmount, value))
		{
			SpecularAmount = Mathf.Clamp(value, 0f, 1f);
			Raise();
		}
	}

	/// <summary>Glow: make the material emit its own light (lava, fireflies...).</summary>
	[Export] public bool EmissionEnabled { get; set; }
	public void SetEmissionEnabled(bool value)
	{
		if (EmissionEnabled != value)
		{
			EmissionEnabled = value;
			Raise();
		}
	}

	/// <summary>Color of the emitted light.</summary>
	[Export] public Color EmissionColor { get; set; } = new(1f, 1f, 1f);
	public void SetEmissionColor(Color value)
	{
		if (EmissionColor != value)
		{
			EmissionColor = value;
			Raise();
		}
	}

	/// <summary>Brightness of the emitted light.</summary>
	[Export(PropertyHint.Range, "0,8,0.05")] public float EmissionEnergy { get; set; } = 1f;
	public void SetEmissionEnergy(float value)
	{
		if (!Mathf.IsEqualApprox(EmissionEnergy, value))
		{
			EmissionEnergy = Mathf.Max(0f, value);
			Raise();
		}
	}

	// ------------------------------------------------------------------
	// Tool stats (drives the voxel health / damage system)
	// ------------------------------------------------------------------

	/// <summary>
	/// When true the material cannot be broken or removed by any tool
	/// (pickaxe damage is ignored, remove-tool skips it). Bedrock uses this.
	/// </summary>
	[Export] public bool Unbreakable { get; set; }
	public void SetUnbreakable(bool value)
	{
		if (Unbreakable != value)
		{
			Unbreakable = value;
			Raise();
		}
	}

	/// <summary>
	/// Base break time for this material in seconds (with the default tool
	/// power of 1.0). The actual damage rate a tool deals to a voxel of this
	/// material is <c>(tool power / this value) * 100% per second</c>.
	/// </summary>
	[Export(PropertyHint.Range, "0.05,60,0.05")] public float BreakTime { get; set; } = 1f;
	public void SetBreakTime(float value)
	{
		if (!Mathf.IsEqualApprox(BreakTime, value))
		{
			BreakTime = Mathf.Max(0.05f, value);
			Raise();
		}
	}

	private void Raise() => PropertyChanged?.Invoke(this);
}
