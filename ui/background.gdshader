shader_type canvas_item;

// Looping
uniform float loop_time : hint_range(1.0, 30.0) = 10.0;

// Swirl + pattern controls
uniform float swirl_strength : hint_range(0.0, 10.0) = 3.0;
uniform float pattern_scale : hint_range(0.5, 5.0) = 1.5;

// Pixelation
// "virtual" resolution of the effect – lower = chunkier pixels
uniform float pixel_resolution : hint_range(32.0, 512.0) = 160.0;

// Color steps (posterization)
uniform int color_steps : hint_range(2, 16) = 6;

// Colors
uniform vec3 color_a : source_color = vec3(0.03, 0.0, 0.08);   // very dark purple
uniform vec3 color_b : source_color = vec3(0.45, 0.18, 0.7);   // mid purple
uniform vec3 color_c : source_color = vec3(0.95, 0.7, 1.0);    // light lavender/pink


void fragment() {
    float phase = (TIME / loop_time) * 6.2831853; // 2π

    // --- PIXELATE UV ---
    // Snap UV to a grid of `pixel_resolution` units
    vec2 pixel_uv = floor(UV * pixel_resolution) / pixel_resolution;

    // Use pixel_uv instead of UV for everything below
    vec2 uv = (pixel_uv * 2.0 - 1.0) * pattern_scale;

    float r = length(uv);
    float angle = atan(uv.y, uv.x);

    float swirl = swirl_strength * (0.2 + 0.8 * r) * sin(phase + r * 4.0);
    angle += swirl;

    vec2 p = vec2(cos(angle), sin(angle)) * r;

    float wave = 0.0;
    wave += sin(8.0 * p.x + 6.0 * p.y + phase * 2.0);
    wave += 0.5 * sin(12.0 * p.y - phase * 3.0);
    wave += 0.35 * sin(10.0 * p.x + 14.0 * p.y + phase * 1.5);

    wave = wave * 0.25 + 0.5;

    float vignette = smoothstep(1.2, 0.3, r);

    vec3 base = mix(color_a, color_b, wave);
    vec3 final_color = mix(base, color_c, wave * vignette);

    // --- PIXELATE COLORS (posterize) ---
    vec3 quantized = floor(final_color * float(color_steps)) / float(color_steps);

    COLOR = vec4(quantized, 1.0);
}
