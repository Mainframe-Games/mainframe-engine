#version 450

layout(location = 0) in vec4 fragColor;
layout(location = 0) out vec4 outColor;

float near = 0.1;
float far  = 1000.0;

// Linearize Vulkan depth (NDC z in [0, 1])
float LinearizeDepth(float depth) {
    return near * far / (far - depth * (far - near));
}

void main() {
    float depth = 1.0 - LinearizeDepth(gl_FragCoord.z) / far;
    outColor = vec4(vec3(depth), depth) * fragColor;
}
