// Octahedral impostors (ADR 0172; TreeImpostor, ImpostorMaterial3D, ImpostorOctahedron in C#). An impostor is one quad per
// instance, turned to the viewer about the baked mesh's bounding-sphere centre, textured from the three baked views
// nearest the view direction. Views are directions in object space, y up, from the centre towards the viewer, on a
// frames × frames grid whose cell (i, j) holds the view from octDecode((i, j) / (frames − 1)): hemi-octahedral (the
// grid's centre looks straight down, its border along the horizon) or full octahedral.
//
// Material block (MaterialParams.From(ImpostorMaterial3D)): foliage2 = centre (xyz, object space), radius (w);
// foliage3 = frames per side, 1 for hemi, bark roughness. Albedo = the albedo atlas, normal = object normal (rgb) + depth
// (a: 0.5 ± 0.5 × distance towards the viewer / radius), emission slot = detail (R thickness, G AO, B leaf mask).
// Include material.slang first.
#pragma once

float3 impostorCenter() { return material.foliage2.xyz; }
float impostorRadius() { return material.foliage2.w; }
float impostorFrames() { return material.foliage3.x; }
bool impostorHemi() { return material.foliage3.y > 0.5; }
float impostorBarkRoughness() { return material.foliage3.z; }

float2 impostorSignNotZero(float2 v) { return float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0); }

// Grid coordinates (0..1)² of a unit direction (hemi: the lower half clamps to the horizon).
float2 impostorEncode(float3 d, bool hemi)
{
    if (hemi)
    {
        d.y = max(d.y, 0.0);
        d /= max(abs(d.x) + abs(d.y) + abs(d.z), 1e-20);
        return float2(d.x + d.z, d.z - d.x) * 0.5 + 0.5;
    }

    d /= max(abs(d.x) + abs(d.y) + abs(d.z), 1e-20);
    float2 p = d.xz;
    if (d.y < 0.0)
        p = (1.0 - abs(p.yx)) * impostorSignNotZero(p);
    return p * 0.5 + 0.5;
}

// The unit direction of grid coordinates (0..1)².
float3 impostorDecode(float2 g, bool hemi)
{
    float2 p = g * 2.0 - 1.0;
    if (hemi)
    {
        float x = (p.x - p.y) * 0.5;
        float z = (p.x + p.y) * 0.5;
        return normalize(float3(x, max(1.0 - abs(x) - abs(z), 0.0), z));
    }

    float3 d = float3(p.x, 1.0 - abs(p.x) - abs(p.y), p.y);
    if (d.y < 0.0)
        d.xz = (1.0 - abs(p.yx)) * impostorSignNotZero(p);
    return normalize(d);
}

// The image axes of a view from `direction` (towards the viewer): right = Y × direction (X looking straight down), up.
void impostorBasis(float3 direction, out float3 right, out float3 up)
{
    float3 r = cross(float3(0.0, 1.0, 0.0), direction);
    right = dot(r, r) < 1e-8 ? float3(1.0, 0.0, 0.0) : normalize(r);
    up = cross(direction, right);
}

// The three frames nearest a view direction: their grid cells (xy, zw of cells01; cell2) and barycentric weights.
struct ImpostorFrames
{
    float2 cell0;
    float2 cell1;
    float2 cell2;
    float3 weights;
};

ImpostorFrames impostorFrames(float3 direction, float frames, bool hemi)
{
    float2 f = impostorEncode(direction, hemi) * (frames - 1.0);
    float2 b = clamp(floor(f), 0.0, frames - 2.0);
    float2 t = saturate(f - b);
    ImpostorFrames o;
    if (t.x + t.y < 1.0)
    {
        o.cell0 = b;
        o.cell1 = b + float2(1.0, 0.0);
        o.cell2 = b + float2(0.0, 1.0);
        o.weights = float3(1.0 - t.x - t.y, t.x, t.y);
    }
    else
    {
        o.cell0 = b + float2(1.0, 0.0);
        o.cell1 = b + float2(0.0, 1.0);
        o.cell2 = b + float2(1.0, 1.0);
        o.weights = float3(1.0 - t.y, 1.0 - t.x, t.x + t.y - 1.0);
    }
    return o;
}

// Where the view of grid cell `cell` saw an object-space point `offset` from the centre, in the cell (0..1 inside it;
// projected along that view onto its image, so the quad's points line up in every view). Linear in the offset, so it
// interpolates across the quad; clamp it per fragment (impostorAtlasUv).
float2 impostorLocal(float2 cell, float3 offset, float frames, float radius, bool hemi)
{
    float3 direction = impostorDecode(cell / (frames - 1.0), hemi);
    float3 right, up;
    impostorBasis(direction, right, up);
    return float2(dot(offset, right), -dot(offset, up)) / (2.0 * radius) + 0.5;
}

float2 impostorAtlasUv(float2 cell, float2 local, float frames)
{
    return (cell + clamp(local, 0.002, 0.998)) / frames;
}

// One impostor vertex: the quad corner `cornerUv` (0..1, v down) turned to the camera.
struct ImpostorVertex
{
    float4 worldPos;
    float3 offset;       // object space, from the centre
    float3 viewWorld;    // unit, from the centre towards the camera
    float distance;      // the instance origin's distance to the camera (visibility ranges)
    float radiusWorld;
    float2 local0;
    float2 local1;
    float2 local2;
    float4 cells01;
    float2 cell2;
    float3 weights;
};

ImpostorVertex impostorVertex(float2 cornerUv, float4x4 model, float3 cameraPosition)
{
    float3x3 m = (float3x3)model;
    float radius = impostorRadius();
    float frames = impostorFrames();
    bool hemi = impostorHemi();
    float3 centerWorld = mul(float4(impostorCenter(), 1.0), model).xyz;
    float3 toCamera = cameraPosition - centerWorld;
    float3 viewWorld = dot(toCamera, toCamera) > 1e-12 ? normalize(toCamera) : float3(0.0, 0.0, 1.0);
    float3 view = normalize(mul(m, viewWorld)); // object space (rows of a rotation × uniform scale)

    float3 right, up;
    impostorBasis(view, right, up);
    float2 c = float2(cornerUv.x * 2.0 - 1.0, 1.0 - cornerUv.y * 2.0);
    ImpostorVertex o;
    o.offset = (right * c.x + up * c.y) * radius;
    o.worldPos = mul(float4(impostorCenter() + o.offset, 1.0), model);
    o.viewWorld = viewWorld;
    o.distance = length(cameraPosition - model[3].xyz);
    o.radiusWorld = radius * length(m[0]);

    ImpostorFrames f = impostorFrames(view, frames, hemi);
    o.local0 = impostorLocal(f.cell0, o.offset, frames, radius, hemi);
    o.local1 = impostorLocal(f.cell1, o.offset, frames, radius, hemi);
    o.local2 = impostorLocal(f.cell2, o.offset, frames, radius, hemi);
    o.cells01 = float4(f.cell0, f.cell1);
    o.cell2 = f.cell2;
    o.weights = f.weights;
    return o;
}
