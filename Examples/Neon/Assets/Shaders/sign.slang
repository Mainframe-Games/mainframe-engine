import mainframe.spatial;
// Neon's signs (World/NeonCity.cs): a made-up word from the signs atlas glowing on a dark panel. On a MultiMesh,
// instanceCustom is (word index, hue, brightness, flicker seed) (CityLayout's CitySign). `vertical` picks the atlas'
// vertical words (the blades sticking out of the facades) or its horizontal ones (the boards over the shops); the panel's
// size in metres is `panel` times the instance's scale, and the word keeps its shape (stretched at most 1.6 times).

[cull(Cull.Disabled)]
struct Material
{
    [color, filter(Filter.Linear), repeat(Repeat.Disabled)] Sampler2D words;
    float vertical = 0.0;
    float2 panel = float2(1.0, 1.0);
    float energy = 6.0;
};

struct Varyings
{
    nointerpolation float4 custom;
    nointerpolation float2 size;
};

float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float3 neon(float h)
{
    float k = floor(frac(h) * 6.0);
    float3 c = float3(1.0, 0.12, 0.62);
    c = k > 0.5 ? float3(0.1, 0.85, 1.0) : c;
    c = k > 1.5 ? float3(0.55, 0.22, 1.0) : c;
    c = k > 2.5 ? float3(1.0, 0.55, 0.12) : c;
    c = k > 3.5 ? float3(0.35, 1.0, 0.55) : c;
    c = k > 4.5 ? float3(1.0, 0.18, 0.2) : c;
    return c;
}

void vertex(inout SurfaceVertex v, Material m, out Varyings o)
{
    o.custom = v.instanceCustom;
    o.size = m.panel * float2(length(mul(float4(1.0, 0.0, 0.0, 0.0), v.modelMatrix).xyz), length(mul(float4(0.0, 1.0, 0.0, 0.0), v.modelMatrix).xyz));
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float index = mod(floor(i.custom.x + 0.5), 8.0);
    float cellAspect = m.vertical > 0.5 ? 0.5 : 2.0;
    // The word's box on the panel (metres), centred, keeping its aspect within a 1.6x stretch.
    float2 box = i.size * 0.9;
    float a = box.x / box.y;
    if (a > cellAspect * 1.6)
    {
        box.x = box.y * cellAspect * 1.6;
    }
    else if (a < cellAspect / 1.6)
    {
        box.y = box.x / (cellAspect / 1.6);
    }
    float2 q = ((s.uv - 0.5) * i.size) / box + 0.5;
    float inside = step(0.0, q.x) * step(q.x, 1.0) * step(0.0, q.y) * step(q.y, 1.0);
    float2 origin = m.vertical > 0.5 ? float2(index * 0.125, 0.5) : float2(mod(index, 4.0) * 0.25, floor(index / 4.0) * 0.25);
    float2 size = m.vertical > 0.5 ? float2(0.125, 0.5) : float2(0.25, 0.25);
    float2 uv = origin + clamp(q, 0.0, 1.0) * size;
    // Close up the strokes are crisp; far away the mips average them with the gaps, so the panel would turn into one
    // flat block of light (which the tonemapper bleaches): there it dims to the average.
    float raw = m.words.Sample(uv).r;
    float far = clamp(length(fwidth(uv)) * 400.0 - 0.5, 0.0, 1.0);
    float letters = lerp(smoothstep(0.05, 0.5, raw), raw * 0.6, far) * inside;
    float halo = m.words.SampleLevel(uv, 4.0).r * inside * (1.0 - far * 0.7);

    // Flicker: most signs hum steadily, some stutter, a few have a dead stroke that sputters.
    float f = i.custom.w;
    float tick = floor(s.time * 12.0 + f * 100.0);
    float stutter = step(0.8, f) * step(0.75, hash11(tick));
    float on = 1.0 - stutter * 0.85;
    float3 colour = neon(i.custom.y);
    float b = i.custom.z <= 0.0 ? 1.0 : i.custom.z;
    s.albedo = float3(0.02, 0.02, 0.025);
    s.roughness = 0.35;
    s.emission = colour * (letters * m.energy + halo * m.energy * 0.4 + 0.08) * b * on;
}
