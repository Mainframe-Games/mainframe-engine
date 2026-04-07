#version 450

layout(location = 0) in vec2 inUV;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec3 inWorldPos;
layout(location = 3) in vec3 inNormal;

layout(location = 0) out vec4 outColor;

#define MAX_DIR_LIGHTS    4
#define MAX_POINT_LIGHTS 16
#define MAX_SPOT_LIGHTS   8
#define MAX_SHADOW_DIR    4
#define MAX_SHADOW_SPOT   8
#define MAX_SHADOW_POINT  4

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

layout(set = 2, binding = 0) uniform ShadowMatricesUBO {
    mat4 dirLightSpace[MAX_SHADOW_DIR];
    mat4 spotLightSpace[MAX_SHADOW_SPOT];
} shadowMat;

layout(set = 2, binding = 1) uniform sampler2DShadow dirShadowMaps[MAX_SHADOW_DIR];
layout(set = 2, binding = 2) uniform sampler2DShadow spotShadowMaps[MAX_SHADOW_SPOT];
layout(set = 2, binding = 3) uniform samplerCube     pointShadowMaps[MAX_SHADOW_POINT];

layout(set = 3, binding = 0) uniform sampler2D uTexture;

// ── Shadow helpers ─────────────────────────────────────────────────────────────

const float kBias2D    = 0.001;
const float kBiasPoint = 0.015;

float sampleDirShadow(int i, vec3 worldPos)
{
    vec4 lsPos = shadowMat.dirLightSpace[i] * vec4(worldPos, 1.0);
    lsPos /= lsPos.w;
    vec2  uv    = lsPos.xy * 0.5 + 0.5;
    float depth = lsPos.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || depth < 0.0 || depth > 1.0)
        return 1.0;
    return texture(dirShadowMaps[i], vec3(uv, depth - kBias2D));
}

float sampleSpotShadow(int i, vec3 worldPos)
{
    vec4 lsPos = shadowMat.spotLightSpace[i] * vec4(worldPos, 1.0);
    lsPos /= lsPos.w;
    vec2  uv    = lsPos.xy * 0.5 + 0.5;
    float depth = lsPos.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || depth < 0.0 || depth > 1.0)
        return 1.0;
    return texture(spotShadowMaps[i], vec3(uv, depth - kBias2D));
}

float samplePointShadow(int i, vec3 worldPos, vec3 lightPos, float lightRange)
{
    vec3  fragToLight  = worldPos - lightPos;
    float currentDepth = length(fragToLight) / lightRange;
    float closestDepth = texture(pointShadowMaps[i], fragToLight).r;
    return currentDepth - kBiasPoint > closestDepth ? 0.0 : 1.0;
}

// ── Lighting ───────────────────────────────────────────────────────────────────

float attenuate(float dist, float range)
{
    float x = clamp(1.0 - dist / range, 0.0, 1.0);
    return x * x;
}

vec3 calcDir(DirLight l, vec3 N, vec3 V, vec3 base, float shadow)
{
    vec3  L    = normalize(-l.directionIntensity.xyz);
    vec3  H    = normalize(L + V);
    float diff = max(dot(N, L), 0.0);
    float spec = pow(max(dot(N, H), 0.0), 32.0);
    return l.color.xyz * l.directionIntensity.w * (diff + spec * 0.3) * base * shadow;
}

vec3 calcPoint(PointLight l, vec3 N, vec3 V, vec3 pos, vec3 base, float shadow)
{
    vec3  toLight = l.positionRange.xyz - pos;
    float dist    = length(toLight);
    vec3  L       = normalize(toLight);
    vec3  H       = normalize(L + V);
    float diff    = max(dot(N, L), 0.0);
    float spec    = pow(max(dot(N, H), 0.0), 32.0);
    float atten   = attenuate(dist, l.positionRange.w);
    return l.colorIntensity.xyz * l.colorIntensity.w * (diff + spec * 0.3) * atten * base * shadow;
}

vec3 calcSpot(SpotLight l, vec3 N, vec3 V, vec3 pos, vec3 base, float shadow)
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
    return l.colorInner.xyz * l.directionIntensity.w * (diff + spec * 0.3) * spot * atten * base * shadow;
}

void main()
{
    vec4 texColor = inColor * texture(uTexture, inUV);
    if (texColor.a < 0.01) discard;

    vec3 N    = normalize(inNormal);
    vec3 V    = normalize(lights.cameraPosition.xyz - inWorldPos);
    vec3 base = texColor.rgb;

    vec3 result = lights.ambientColor.xyz * base;

    int numShadowDir   = min(lights.counts.x, MAX_SHADOW_DIR);
    int numShadowSpot  = min(lights.counts.z, MAX_SHADOW_SPOT);
    int numShadowPoint = min(lights.counts.y, MAX_SHADOW_POINT);

    for (int i = 0; i < lights.counts.x; i++) {
        float shadow = (i < numShadowDir) ? sampleDirShadow(i, inWorldPos) : 1.0;
        result += calcDir(lights.dir[i], N, V, base, shadow);
    }

    for (int i = 0; i < lights.counts.y; i++) {
        float shadow = (i < numShadowPoint)
            ? samplePointShadow(i, inWorldPos, lights.point[i].positionRange.xyz, lights.point[i].positionRange.w)
            : 1.0;
        result += calcPoint(lights.point[i], N, V, inWorldPos, base, shadow);
    }

    for (int i = 0; i < lights.counts.z; i++) {
        float shadow = (i < numShadowSpot) ? sampleSpotShadow(i, inWorldPos) : 1.0;
        result += calcSpot(lights.spot[i], N, V, inWorldPos, base, shadow);
    }

    outColor = vec4(result, texColor.a);
}
