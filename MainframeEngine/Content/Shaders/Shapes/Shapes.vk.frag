#version 450

layout(location = 0) in vec3 inWorldPos;
layout(location = 1) in vec3 inNormal;

layout(location = 0) out vec4 outColor;

#define MAX_DIR_LIGHTS    4
#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS   8

struct DirLight {
    vec4 directionIntensity; // xyz = direction, w = intensity
    vec4 color;              // xyz = color
};

struct PointLight {
    vec4 positionRange;  // xyz = position, w = range
    vec4 colorIntensity; // xyz = color,    w = intensity
};

struct SpotLight {
    vec4 positionRange;      // xyz = position,  w = range
    vec4 directionIntensity; // xyz = direction, w = intensity
    vec4 colorInner;         // xyz = color,     w = cos(innerAngle)
    vec4 outerPad;           // x   = cos(outerAngle)
};

layout(set = 1, binding = 0) uniform LightsUBO {
    vec4       ambientColor;
    vec4       cameraPosition;
    ivec4      counts;           // x = numDir, y = numPoint, z = numSpot
    DirLight   dir[MAX_DIR_LIGHTS];
    PointLight point[MAX_POINT_LIGHTS];
    SpotLight  spot[MAX_SPOT_LIGHTS];
} lights;

layout(push_constant) uniform PushConstants {
    mat4 model;
    vec4 color;
} pc;

float attenuate(float dist, float range)
{
    float x = clamp(1.0 - dist / range, 0.0, 1.0);
    return x * x;
}

vec3 calcDir(DirLight l, vec3 N, vec3 V, vec3 base)
{
    vec3  L    = normalize(-l.directionIntensity.xyz);
    vec3  H    = normalize(L + V);
    float diff = max(dot(N, L), 0.0);
    float spec = pow(max(dot(N, H), 0.0), 32.0);
    return l.color.xyz * l.directionIntensity.w * (diff + spec * 0.3) * base;
}

vec3 calcPoint(PointLight l, vec3 N, vec3 V, vec3 pos, vec3 base)
{
    vec3  toLight = l.positionRange.xyz - pos;
    float dist    = length(toLight);
    vec3  L       = normalize(toLight);
    vec3  H       = normalize(L + V);
    float diff    = max(dot(N, L), 0.0);
    float spec    = pow(max(dot(N, H), 0.0), 32.0);
    float atten   = attenuate(dist, l.positionRange.w);
    return l.colorIntensity.xyz * l.colorIntensity.w * (diff + spec * 0.3) * atten * base;
}

vec3 calcSpot(SpotLight l, vec3 N, vec3 V, vec3 pos, vec3 base)
{
    vec3  toLight  = l.positionRange.xyz - pos;
    float dist     = length(toLight);
    vec3  L        = normalize(toLight);
    vec3  H        = normalize(L + V);
    float cosTheta = dot(-L, normalize(l.directionIntensity.xyz));
    float inner    = l.colorInner.w;
    float outer    = l.outerPad.x;
    float spot     = clamp((cosTheta - outer) / max(inner - outer, 0.0001), 0.0, 1.0);
    float diff     = max(dot(N, L), 0.0);
    float spec     = pow(max(dot(N, H), 0.0), 32.0);
    float atten    = attenuate(dist, l.positionRange.w);
    return l.colorInner.xyz * l.directionIntensity.w * (diff + spec * 0.3) * spot * atten * base;
}

void main()
{
    vec3 N    = normalize(inNormal);
    vec3 V    = normalize(lights.cameraPosition.xyz - inWorldPos);
    vec3 base = pc.color.xyz;

    vec3 result = lights.ambientColor.xyz * base;

    for (int i = 0; i < lights.counts.x; i++)
        result += calcDir(lights.dir[i], N, V, base);

    for (int i = 0; i < lights.counts.y; i++)
        result += calcPoint(lights.point[i], N, V, inWorldPos, base);

    for (int i = 0; i < lights.counts.z; i++)
        result += calcSpot(lights.spot[i], N, V, inWorldPos, base);

    outColor = vec4(result, 1.0);
}
