shader_type spatial;

// Blends two terrain material textures at every material interface. The mesh
// builder emits each blended face with a per-vertex blend factor (UV2.x):
// 0 = pure material A (lower id), 1 = pure material B (higher id) — a vertical
// gradient across the face. The fragment crossfades the two sampler textures by
// that factor. Lighting is left to Godot's built-in spatial pipeline (we only
// set ALBEDO / roughness / metallic), so the blend is lit correctly like the
// rest of the terrain.
//
// Four correctness gotchas are baked in (kept verbatim with BlendShaderCode in
// VoxelMaterial.cs — edit BOTH together):
//  * :source_color — the material textures are sRGB; a plain sampler reads sRGB
//    bytes as linear (overexposed wash). source_color gives the same sRGB->linear
//    handling StandardMaterial3D applies.
//  * fract(UV) — the mesh UVs are tiled by the per-material TextureScale and
//    exceed 1.0; a raw spatial sampler has no texture_repeat.
//  * The ALBEDO MUST be tinted by each material's AlbedoColor, mixed by the
//    blend factor — StandardMaterial3D computes ALBEDO = AlbedoColor *
//    albedoTexture, so skipping the tint made the blend visibly LIGHTER than the
//    plain materials. Tinting both sides and mixing makes the blend's albedo
//    path identical to the standard one.
//  * (No WORLD_POSITION built-in exists; this minimal version needs no world
//    position, so nothing to capture. If a world-position effect is added later,
//    use (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz in vertex().)

// repeat_enable wraps UVs at the sampler (the shader equivalent of
// StandardMaterial3D.TextureRepeat) so tiled UVs > 1.0 work; source_color
// applies the same sRGB->linear handling the standard materials use.
uniform sampler2D TextureA : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D TextureB : source_color, filter_linear_mipmap, repeat_enable;
// Per-material albedo tint, applied exactly like StandardMaterial3D does:
// ALBEDO = tint * texture. Without this the blend skips the tint and reads
// lighter than the plain (non-blended) materials.
uniform vec4 TintA;
uniform vec4 TintB;

varying float vFactor;
varying float vLight;

void vertex() {
	// UV2.x carries the per-vertex blend factor (0 = pure A / lower id,
	// 1 = pure B / higher id). UV2.y carries the per-vertex sky light (0..1)
	// — the mesh builder stores it there so the blend factor (UV2.x) is free.
	vFactor = UV2.x;
	vLight = UV2.y;
}

void fragment() {
	// Tiled UVs: wrap with fract() to tile beyond 1.0, since a raw spatial
	// sampler has no texture_repeat.
	vec2 tuv = fract(UV);
	vec4 ta = texture(TextureA, tuv) * TintA;
	vec4 tb = texture(TextureB, tuv) * TintB;
	float f = clamp(vFactor, 0.0, 1.0);
	// The samplers are :source_color and the tint is the linear AlbedoColor,
	// so each side is EXACTLY what StandardMaterial3D produces for that
	// material's ALBEDO — mixing them gives a crossfade that matches the
	// plain materials at f=0 and f=1 and reads the same brightness in between.
	vec3 albedo = mix(ta.rgb, tb.rgb, f);
	// Sky light darkens enclosed geometry (caves): scale the crossfade by the
	// per-vertex light (0..1), same as the plain-surface shader.
	ALBEDO = albedo * clamp(vLight, 0.0, 1.0);
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
