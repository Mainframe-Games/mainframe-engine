// The editor grid's lines, computed per pixel from the world position (no line geometry): anti-aliased at their width
// in pixels at any distance or zoom. Minor lines every cell, major every 10; when minor lines come closer than
// kMinSpacing pixels the level steps up ×10, cross-fading so nothing pops. Coloured X (red, along z = 0) and Z (blue,
// along x = 0) axes. Fades with distance and where the lines of one direction crowd together at grazing angles.
#include "frame.slang"

// 1: drawn after post-processing into a display-encoded target (ADR 0169; see SceneGrid.vk.frag). 0: the linear HDR
// scene target.
[vk::constant_id(0)] const bool kDisplayOutput = false;

struct Push
{
    float4 params; // x = pixel scale, y = plane height, z = base cell size, w = unused
};

[[vk::push_constant]] ConstantBuffer<Push> push;

static const float kMinSpacing = 10.0; // pixels between minor lines before the level steps up
static const float kMinorWidth = 1.0;  // points
static const float kMajorWidth = 1.75;
static const float kAxisWidth = 2.5;
static const float4 kMinor = float4(0.62, 0.62, 0.64, 0.30); // sRGB, straight alpha
static const float4 kMajor = float4(0.74, 0.74, 0.76, 0.55);
static const float4 kAxisX = float4(0.96, 0.26, 0.30, 0.95);
static const float4 kAxisZ = float4(0.24, 0.50, 1.00, 0.95);

// Coverage of the lines every `cell` world units, `width` pixels wide; `footprint` = fwidth(pos), taken once outside: the
// cell size can differ between the pixels of a quad (at a level boundary), so fwidth(pos / cell) would jump there and
// leave stray dots. Lines of one direction whose spacing falls under a few pixels fade out (they would merge into moiré),
// each direction on its own.
float lines(float2 pos, float2 footprint, float cell, float width)
{
    float2 coord = pos / cell;
    float2 deriv = max(footprint / cell, 1e-6);               // cells per pixel along x and z
    float2 dist = abs(frac(coord + 0.5) - 0.5) / deriv;       // pixels to the nearest line
    float2 cover = saturate(width * 0.5 + 0.5 - dist);
    cover *= saturate(1.0 / (deriv * 3.0) - 1.0);             // spacing below 3 px → gone by 1.5 px
    return max(cover.x, cover.y);
}

// Coverage of one line through 0 (an axis): `value` is the world distance across it.
float axisLine(float value, float width)
{
    float deriv = max(fwidth(value), 1e-6);
    return saturate(width * 0.5 + 0.5 - abs(value) / deriv);
}

struct FsOut
{
    float4 color : SV_Target0;
    float depth : SV_DepthLessEqual;
};

// The grid lies on the floors it shows: pulled towards the camera by 2 pixels' footprint so it does not z-fight them.
float gridDepth(float depth)
{
    float near = frame.clip.x;
    float far  = frame.clip.y;
    float pixel = 2.0 / (frame.projection[1][1] * frame.viewport.y);
    if (frame.projection[2][3] == 0.0)
        return saturate(depth - 2.0 * pixel / (far - near));
    float z = max(linearizeDepth(depth) * (1.0 - 2.0 * pixel), near);
    return saturate(far * (z - near) / (z * (far - near)));
}

[shader("fragment")]
FsOut main([[vk::location(0)]] float3 world, float4 fragCoord : SV_Position)
{
    float scale = push.params.x;
    float baseCell = push.params.z;
    float2 pos = world.xz;

    // Level of detail from the screen footprint (world units per pixel) of the plane here.
    float2 footprint = fwidth(pos);
    float perPixel = max(length(footprint) * 0.7071, 1e-6);
    float lod = max(0.0, log10(perPixel * kMinSpacing * scale / baseCell));
    float level = floor(lod);
    float fade = lod - level;
    float cellA = baseCell * pow(10.0, level); // minor
    float cellB = cellA * 10.0;                 // major (minor at the next level)
    float cellC = cellB * 10.0;                 // major at the next level

    float a = lines(pos, footprint, cellA, kMinorWidth * scale) * (1.0 - fade);
    float b = lines(pos, footprint, cellB, lerp(kMajorWidth, kMinorWidth, fade) * scale);
    float c = lines(pos, footprint, cellC, kMajorWidth * scale);
    float4 color = kMinor * float4(1.0, 1.0, 1.0, a);
    float4 major = lerp(kMajor, kMinor, fade) * float4(1.0, 1.0, 1.0, b);
    if (major.a > color.a)
        color = major;
    float4 top = kMajor * float4(1.0, 1.0, 1.0, c);
    if (top.a > color.a)
        color = top;

    float ax = axisLine(pos.y, kAxisWidth * scale); // the X axis runs along z = 0
    float az = axisLine(pos.x, kAxisWidth * scale);
    if (ax * kAxisX.a > color.a)
        color = float4(kAxisX.rgb, ax * kAxisX.a);
    if (az * kAxisZ.a > color.a)
        color = float4(kAxisZ.rgb, az * kAxisZ.a);

    // Fade out towards the horizon: with the camera's height above the plane (a high camera sees farther), and where
    // the plane is seen nearly edge-on.
    float3 toCamera = frame.cameraPosition.xyz - world;
    float distance = length(toCamera);
    float height = abs(frame.cameraPosition.y - push.params.y);
    float reach = min(frame.clip.y * 0.8, max(height * 60.0, 40.0 * baseCell));
    float distanceFade = 1.0 - smoothstep(reach * 0.35, reach, distance);
    float grazing = saturate(abs(toCamera.y) / max(distance, 1e-6) * 12.0); // under ~5° of elevation fades out
    color.a *= distanceFade * lerp(0.25, 1.0, grazing);
    if (color.a <= 0.002)
        discard;

    FsOut o;
    if (kDisplayOutput)
        o.color = float4(color.rgb, color.a);
    else
        o.color = float4(srgbToLinear(color.rgb), color.a);
    o.depth = gridDepth(fragCoord.z);
    return o;
}
