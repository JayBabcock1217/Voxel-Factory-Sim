shader_type spatial;

// Blends two terrain material textures at every material interface. The mesh
// builder emits each blended face / triangle with a per-vertex albedo tint
// (built-in COLOR) and a per-vertex blend factor (UV2.x): 0 = pure material A,
// 1 = pure material B. The fragment does a per-pixel crossfade between the two
// sampler textures by that factor, then tints by the vertex color. Lighting is
// left to Godot's built-in spatial pipeline (we only set ALBEDO / roughness /
// metallic), so the blend is lit correctly like the rest of the terrain.

uniform sampler2D TextureA : filter_linear_mip_mipmap;
uniform sampler2D TextureB : filter_linear_mip_mipmap;

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
