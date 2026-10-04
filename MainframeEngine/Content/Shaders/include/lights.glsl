// Lights UBO (LightEnvironment.WriteUbo, std140, 1200 bytes with the default limits) and Blinn-Phong shading
// with shadows. Include shadows.glsl first. Define LIGHTS_SET / LIGHTS_BINDING before including to override
// where the UBO lives (default: the per-frame set 0, binding 1, next to the camera data).
// Colours in the UBO are linear (LightEnvironment converts the sRGB-authored values when packing).
#ifndef MF_LIGHTS_GLSL
#define MF_LIGHTS_GLSL

#include "common.glsl"

#ifndef MF_SHADOWS_GLSL
#error "Include shadows.glsl before lights.glsl"
#endif

#ifndef LIGHTS_SET
#define LIGHTS_SET 0
#endif
#ifndef LIGHTS_BINDING
#define LIGHTS_BINDING 1
#endif

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

layout(set = LIGHTS_SET, binding = LIGHTS_BINDING) uniform LightsUBO {
    vec4       ambientColor;
    vec4       cameraPosition;
    ivec4      counts;           // x = numDir, y = numPoint, z = numSpot, w = 1: no shadow maps for this view
    DirLight   dir[MAX_DIR_LIGHTS];
    PointLight point[MAX_POINT_LIGHTS];
    SpotLight  spot[MAX_SPOT_LIGHTS];
} lights;

float attenuate(float dist, float range)
{
    float x = clamp(1.0 - dist / range, 0.0, 1.0);
    return x * x;
}

// Blinn-Phong: (diffuse + highlight of strength `specular`, exponent `shininess`) × base colour.
vec3 calcDir(DirLight l, vec3 N, vec3 V, vec3 base, float shadow, float specular, float shininess)
{
    vec3  L    = normalize(-l.directionIntensity.xyz);
    vec3  H    = normalize(L + V);
    float diff = max(dot(N, L), 0.0);
    float spec = pow(max(dot(N, H), 0.0), shininess);
    return l.color.xyz * l.directionIntensity.w * (diff + spec * specular) * base * shadow;
}

vec3 calcPoint(PointLight l, vec3 N, vec3 V, vec3 pos, vec3 base, float shadow, float specular, float shininess)
{
    vec3  toLight = l.positionRange.xyz - pos;
    float dist    = length(toLight);
    vec3  L       = normalize(toLight);
    vec3  H       = normalize(L + V);
    float diff    = max(dot(N, L), 0.0);
    float spec    = pow(max(dot(N, H), 0.0), shininess);
    float atten   = attenuate(dist, l.positionRange.w);
    return l.colorIntensity.xyz * l.colorIntensity.w * (diff + spec * specular) * atten * base * shadow;
}

vec3 calcSpot(SpotLight l, vec3 N, vec3 V, vec3 pos, vec3 base, float shadow, float specular, float shininess)
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
    float spec     = pow(max(dot(N, H), 0.0), shininess);
    float atten    = attenuate(dist, l.positionRange.w);
    return l.colorInner.xyz * l.directionIntensity.w * (diff + spec * specular) * spot * atten * base * shadow;
}

// Ambient + every light, each shadowed by its map when it has one (ShadowSystem decides which lights cast).
// `specular` scales the Blinn-Phong highlight of exponent `shininess` (materials: StandardMaterial3D). `Ngeo` is the
// geometric normal, used for the shadow receivers' normal offset (a normal-mapped N would make it noisy).
vec3 shadeLightsBlinnPhong(vec3 base, vec3 N, vec3 Ngeo, vec3 worldPos, float specular, float shininess)
{
    vec3 V = normalize(lights.cameraPosition.xyz - worldPos);
    vec3 result = lights.ambientColor.xyz * base;
    bool shadowsOn = lights.counts.w == 0;

    for (int i = 0; i < lights.counts.x; i++)
    {
        vec3  L      = normalize(-lights.dir[i].directionIntensity.xyz);
        float shadow = shadowsOn ? dirShadow(i, worldPos, Ngeo, L) : 1.0;
        result += calcDir(lights.dir[i], N, V, base, shadow, specular, shininess);
    }

    for (int i = 0; i < lights.counts.y; i++)
    {
        vec4  pr     = lights.point[i].positionRange;
        float shadow = shadowsOn ? pointShadow(i, worldPos, Ngeo, pr.xyz, pr.w) : 1.0;
        result += calcPoint(lights.point[i], N, V, worldPos, base, shadow, specular, shininess);
    }

    for (int i = 0; i < lights.counts.z; i++)
    {
        vec3  lightPos = lights.spot[i].positionRange.xyz;
        vec3  L        = normalize(lightPos - worldPos);
        float shadow   = shadowsOn ? spotShadow(i, worldPos, Ngeo, L, lightPos) : 1.0;
        result += calcSpot(lights.spot[i], N, V, worldPos, base, shadow, specular, shininess);
    }

    if (shadowsOn)
        result *= shadowCascadeTint(worldPos);
    return result;
}

// The default highlight (strength 0.3, exponent 32): Spine and anything without a material.
vec3 shadeLights(vec3 base, vec3 N, vec3 worldPos)
{
    return shadeLightsBlinnPhong(base, N, N, worldPos, 0.3, 32.0);
}

#endif
