shader_type spatial;
render_mode cull_back;

// ---------------------------------------------------------------------------
// VoxelBase — a PBR surface shader for voxel terrain materials.
//
// Parameters (set on the ShaderMaterial, visible in the inspector):
//   Albedo    — base color texture (null = flat white)
//   Normal    — optional normal map for surface detail
//   Metallic  — 0..1 (0 = dielectric, 1 = metal)
//   Roughness — 0..1 (0 = mirror, 1 = fully diffuse)
//   UV Scale  — texture repeats per voxel face (1.0 = one tile per face)
//
// Usage:
//   1. Create a ShaderMaterial and assign this shader to it.
//   2. Assign the Albedo / Normal textures and tune the other parameters.
//   3. Assign the ShaderMaterial to VoxelMaterialEntry.CustomMaterial.
// ---------------------------------------------------------------------------

// Base color texture. hint_default_white: when null, samples pure white.
uniform sampler2D albedo : source_color, hint_default_white;

// Optional tangent-space normal map. Null = flat surface (no normal detail).
uniform sampler2D normal_map : hint_normal;

// PBR parameters.
uniform float metallic : hint_range(0.0, 1.0) = 0.0;
uniform float roughness : hint_range(0.0, 1.0) = 1.0;
uniform float specular : hint_range(0.0, 1.0) = 0.05;

// Texture repeats per voxel face (1.0 = one tile per face, 2.0 = 4 tiles).
uniform float uv_scale : hint_range(0.05, 32.0) = 1.0;

// Normal map strength (0 = flat surface, 1 = full normal map).
uniform float normal_strength : hint_range(0.0, 1.0) = 1.0;

varying float vLight;

void vertex() {
	// UV2.x carries the per-vertex sky light (0 = enclosed/near-black,
	// 1 = open to sky). The mesh builder emits it from the flood-filled
	// sky-light field. Without this multiply, caves and enclosed geometry
	// stay bright instead of darkening to near-black.
	vLight = UV2.x;
}

void fragment() {
	// UVs are pre-scaled by the mesh builder (TextureScales), so use them
	// directly. fract() wraps for tiling beyond 1.0 (a raw spatial sampler
	// has no texture_repeat).
	vec2 uv = fract(UV);

	// Sample the albedo texture (white fallback when no texture is assigned).
	// Multiply by the per-vertex sky light so enclosed geometry (caves)
	// darkens to near-black, matching the built-in voxel shader.
	ALBEDO = texture(albedo, uv).rgb * clamp(vLight, 0.0, 1.0);

	// Normal map: Godot's NORMAL_MAP output is in tangent space and is
	// automatically transformed to world space using the mesh's TBN.
	// A null normal map samples white (1,1,1) → (0,0,0) flat, so it's
	// safe to always sample. Scale by normal_strength to blend toward flat.
	vec3 n = texture(normal_map, uv).rgb * 2.0 - 1.0;
	NORMAL_MAP = mix(vec3(0.0, 0.0, 0.0), n, normal_strength);

	METALLIC = metallic;
	ROUGHNESS = roughness;
	SPECULAR = specular;
}
