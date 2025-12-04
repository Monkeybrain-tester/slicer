shader_type canvas_item;

// Loop length in seconds
uniform float loop_time : hint_range(1.0, 30.0) = 8.0;

// Overall scale of the pattern
uniform float pattern_scale : hint_range(0.3, 5.0) = 1.2;

// Waviness
uniform float wave_amplitude : hint_range(0.0, 0.5) = 0.15;
uniform float wave_frequency : hint_range(1.0, 10.0) = 4.0;

// Pixelation
uniform float pixel_resolution : hint_range(32.0, 512.0) = 160.0;

// Color posterization
uniform int color_steps : hint_range(2, 16) = 6;

// Colors (purple-leaning)
uniform vec3 color_a : source_color = vec3(0.03, 0.0, 0.08);
uniform vec3 color_b : source_color = vec3(0.4, 0.15, 0.7);
uniform vec3 color_c : source_color = vec3(0.95, 0.7, 1.0);

void fragment() {
    // Perfectly looping phase
    float phase = (TIME / loop_time) * 6.2831853; // 2π

    // Pixelate UV first
    vec2 pixel_uv = floor(UV * pixel_resolution) / pixel_resolution;

    // Centered coordinates
    vec2 uv = (pixel_uv * 2.0 - 1.0) * pattern_scale;

    // Base “bands” value: combination of horizontal + vertical waves
    float bands = 0.0;

    // Horizontal flowing waves
    bands += sin(uv.x * wave_frequency + phase * 2.0);
    bands += 0.6 * sin(uv.x * wave_frequency * 1.7 - phase * 1.3);

    // Vertical waves for cross pattern
    bands += 0.5 * sin(uv.y * wave_frequency * 1.3 + phase * 1.5);
    bands += 0.4 * sin(uv.y * wave_frequency * 2.2 - phase * 2.3);

    // Normalize bands from roughly [-something, +something] → [0,1]
    bands = bands * 0.18 + 0.5;

    // Apply a subtle “wobble” to the coordinates themselves
    vec2 displaced_uv = uv;
    displaced_uv.y += wave_amplitude * sin(uv.x * wave_frequency * 1.5 + phase);
    displaced_uv.x += wave_amplitude * 0.5 * sin(uv.y * wave_frequency * 1.8 - phase * 0.7);

    // Use displaced_uv to modulate bands a bit more
    float detail = sin(displaced_uv.x * 8.0 + displaced_uv.y * 6.0 + phase * 1.2);
    bands += 0.15 * detail;
    bands = clamp(bands, 0.0, 1.0);

    // Soft vignette so edges aren’t too busy
    float r = length(uv);
    float vignette = smoothstep(1.4, 0.4, r);

    vec3 base = mix(color_a, color_b, bands);
    vec3 final_color = mix(base, color_c, bands * vignette);

    // Posterize colors for pixel feel
    vec3 quantized = floor(final_color * float(color_steps)) / float(color_steps);

    COLOR = vec4(quantized, 1.0);
}
