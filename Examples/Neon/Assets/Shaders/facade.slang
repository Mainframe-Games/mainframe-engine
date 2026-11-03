import mainframe.spatial;
// Neon's towers (World/NeonCity.cs): every kit tower is a MultiMesh instance whose instanceCustom is (window seed, accent
// hue, lit fraction, style), all 0..1 (CityLayout.TowerPlacement.Custom). One shader draws the kit's five surfaces, told
// apart by `part`: 0 the facade, 1 roofs, structure and clutter, 2 the accent trims, 3 the beacons, 4 work lamps.
//
// The facade is a procedural wall, not a texture. uv.x is kit metres along the face; the storeys come from the kit height
// so that a tower stretched up keeps a whole number of storeys between the kit's ledges (`ledge_every`). A window has a
// recessed pane (the reveal shows at an angle), mullions, and behind the glass a room in parallax: interior mapping of a
// box several windows wide, with a lamp, a back wall, furniture and blinds. No light reaches the city (ADR 0218: its light
// is emission and glow), so city_light() fakes what would: the street's neon rising up the walls and the sky above.

struct Material
{
    float part = 0.0;
    float ground_y = -70.0;
    float storey = 3.5;
    float ledge_every = 17.5;
    float window_energy = 1.5;
    float trim_energy = 3.6;
    // The menu: 0 flat windows and plain walls, 1 rooms, reveals and wall detail.
    float detail = 1.0;
    // The menu: scales the lit share of the windows.
    float lit_scale = 1.0;
    // The menu: scales the trims and bands.
    float neon_scale = 1.0;
};

struct Varyings
{
    float3 world;
    float3 normal;
    nointerpolation float4 custom;
    nointerpolation float2 scale;
};

float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float hash12(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float value_noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1.0, 0.0)), u.x), lerp(hash12(i + float2(0.0, 1.0)), hash12(i + float2(1.0, 1.0)), u.x), u.y);
}

// The city's neon palette: magenta, cyan, violet, amber, acid green, hot red.
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

// The light a surface would get: the street's colours rising up the walls, fading by the 20th floor, under a dim sky.
float3 city_light(float3 n, float height, float3 p)
{
    float street = exp(-max(height, 0.0) / 42.0);
    float a = value_noise(p.xz * 0.011 + 7.0);
    float b = value_noise(p.xz * 0.027 + 19.0);
    float3 low = lerp(float3(1.0, 0.13, 0.46), float3(0.14, 0.5, 1.0), smoothstep(0.32, 0.68, a));
    low = lerp(low, float3(1.0, 0.46, 0.16), smoothstep(0.55, 0.8, b));
    float3 sky = float3(0.16, 0.085, 0.27) * (0.5 + 0.5 * n.y);
    // The glow over downtown, ahead of the window wall, rims the faces that look at it.
    float rim = max(dot(n, float3(-0.12, 0.0, -0.99)), 0.0);
    return sky * 0.5 + low * street * (0.42 + 0.6 * max(-n.y, 0.0)) + float3(0.42, 0.12, 0.42) * rim * 0.09;
}

// What wet glass mirrors: the glow at the horizon, the dark sky, and rows of the city's lights.
float3 glass_reflection(float3 r)
{
    float up = r.y;
    float3 c = lerp(float3(0.3, 0.075, 0.27), float3(0.016, 0.02, 0.05), smoothstep(0.0, 0.42, up));
    c = up < 0.0 ? lerp(float3(0.3, 0.075, 0.27), float3(0.09, 0.03, 0.1), smoothstep(0.0, -0.35, up)) : c;
    float2 q = float2(atan2(r.x, r.z) * 11.0, up * 19.0);
    float2 g = floor(q);
    float2 f = frac(q) - 0.5;
    float on = step(0.74, hash12(g)) * (1.0 - smoothstep(0.0, 0.9, abs(up) * 2.4));
    float dot_ = 1.0 - smoothstep(0.1, 0.42, length(f * float2(1.0, 1.6)));
    return c + lerp(float3(1.0, 0.62, 0.34), neon(hash12(g + 3.0)), step(0.6, hash12(g + 9.0))) * on * dot_ * 0.75;
}

// One room behind the glass: `p` the point on the pane, `d` the ray (both metres, x along the wall, y up, z out of the
// wall), the box x0..x1, y0..y1 and `depth` deep, `rs` the room's random number. Returns its colour, lit to 1.
float3 room(float3 p, float3 d, float x0, float x1, float y0, float y1, float depth, float rs)
{
    float tx = ((d.x > 0.0 ? x1 : x0) - p.x) / d.x;
    float ty = ((d.y > 0.0 ? y1 : y0) - p.y) / d.y;
    float tz = (-depth - p.z) / d.z;
    float hit = min(tx, min(ty, tz));
    float3 h = p + d * hit;
    float2 size = float2(x1 - x0, y1 - y0);
    float2 b = (h.xy - float2(x0, y0)) / size;
    float z = -h.z / depth; // 0 at the glass, 1 at the back wall

    float3 paint = lerp(float3(0.85, 0.74, 0.6), float3(0.62, 0.68, 0.74), step(0.6, rs));
    paint = lerp(paint, float3(0.6, 0.42, 0.34), step(0.82, rs));
    float3 lamp = float3(h.x - (x0 + x1) * 0.5, y1 - h.y, (h.z + depth * 0.5));
    float pool = 1.0 / (1.0 + dot(lamp, lamp) * 0.16);
    float3 c;
    if (hit == tz)
    {
        c = paint * (0.5 + 0.5 * pool);
        // A door or a picture on the back wall, a shelf of books on some.
        float doorX = 0.2 + 0.6 * frac(rs * 7.3);
        float door = step(abs(b.x - doorX), 0.09) * step(b.y, 0.72);
        c = lerp(c, c * 0.42, door);
        float art = step(abs(b.x - frac(doorX + 0.45)), 0.13) * step(abs(b.y - 0.58), 0.13) * step(0.4, frac(rs * 3.1));
        c = lerp(c, neon(rs * 9.0) * 0.7, art);
        float shelf = step(0.72, frac(rs * 5.7)) * step(0.25, b.y) * step(b.y, 0.85) * step(frac(b.y * 5.0), 0.8);
        c = lerp(c, float3(0.2, 0.13, 0.1) * (0.6 + 0.8 * hash12(floor(b * float2(38.0, 5.0)))), shelf * step(abs(b.x - doorX), 0.3) * (1.0 - door));
    }
    else if (hit == ty)
    {
        if (d.y > 0.0)
        {
            // The ceiling: bright round the lamp, a strip light in offices.
            float strip = step(0.5, frac(rs * 11.0)) * step(0.5, frac(h.z * 0.42)) * step(abs(frac(b.x * 3.0) - 0.5), 0.09);
            c = float3(0.86, 0.84, 0.78) * (0.42 + 1.1 * pool) + float3(1.4) * strip;
        }
        else
        {
            // The floor: carpet or boards.
            float boards = 0.82 + 0.18 * step(0.5, frac(h.x * 2.4 + rs * 9.0));
            c = lerp(float3(0.3, 0.2, 0.14), float3(0.16, 0.17, 0.2), step(0.5, rs)) * boards * (0.45 + 0.8 * pool);
        }
    }
    else
    {
        c = paint * (0.34 + 0.55 * pool) * (1.0 - 0.25 * z);
    }

    // Furniture on a card across the room: a sofa or desk, a lamp, sometimes someone standing.
    float tc = (-depth * (0.38 + 0.26 * frac(rs * 13.0)) - p.z) / d.z;
    if (tc < hit)
    {
        float3 q = p + d * tc;
        float2 e = (q.xy - float2(x0, y0)) / size;
        float seatX = frac(rs * 17.0);
        float seat = step(abs(e.x - seatX), 0.24) * step(e.y, 0.3 + 0.12 * step(abs(e.x - seatX + 0.2), 0.05));
        float person = step(0.72, frac(rs * 23.0)) * (step(length((e - float2(frac(rs * 29.0), 0.68)) * float2(size.x / size.y, 1.0)), 0.075) + step(abs(e.x - frac(rs * 29.0)), 0.045 * size.y / size.x) * step(e.y, 0.6));
        c = lerp(c, float3(0.05, 0.045, 0.05), clamp(seat + person, 0.0, 1.0));
    }

    return c * (0.55 + 0.45 * z);
}

void vertex(inout SurfaceVertex v, out Varyings o)
{
    o.world = mul(float4(v.vertex, 1.0), v.modelMatrix).xyz;
    o.normal = normalize(mul(float4(v.normal, 0.0), v.modelMatrix).xyz);
    o.custom = v.instanceCustom;
    o.scale = float2(length(v.modelMatrix[0].xyz), length(v.modelMatrix[1].xyz));
}

void fragment(inout SurfaceFragment s, Material m, Varyings i)
{
    float seed = i.custom.x * 173.0;
    float hue = i.custom.y;
    float style = i.custom.w;
    float3 accent = neon(hue);
    float3 n = normalize(i.normal);
    float height = i.world.y - m.ground_y;

    if (m.part > 3.5)
    {
        // Work lamps on roofs and ledges: small, steady, cold or warm per tower.
        s.albedo = float3(0.04);
        s.emission = lerp(float3(1.0, 0.82, 0.6), float3(0.75, 0.88, 1.0), step(0.5, frac(seed))) * 3.2;
    }
    else if (m.part > 2.5)
    {
        // Aviation beacons: a slow red blink, out of step per tower.
        float blink = step(0.82, frac(s.time * 0.5 + i.custom.x * 7.0));
        s.albedo = float3(0.05, 0.0, 0.0);
        s.emission = float3(1.0, 0.05, 0.02) * (0.4 + 14.0 * blink);
    }
    else if (m.part > 1.5)
    {
        // Accent trims: the tower's neon colour, breathing slowly; a few towers chase a light round their bands.
        float breathe = 0.75 + 0.25 * sin(s.time * 0.8 + i.custom.x * 20.0);
        float chase = step(0.75, style) * step(0.85, frac(s.uv.x / 40.0 - s.time * 0.25));
        s.albedo = float3(0.02);
        s.roughness = 0.4;
        s.emission = accent * m.trim_energy * m.neon_scale * (breathe + chase * 2.0);
    }
    else if (m.part > 0.5)
    {
        // Roofs, ledges, structure and clutter: dark wet concrete and steel in the city's light. Up-facing surfaces are
        // roofing (panels and gravel, gloss where the water stands); the rest gets seams and rust from the rain.
        float2 aa2 = fwidth(i.world.xz) + 1e-4;
        float top = smoothstep(0.7, 0.95, n.y);
        float grit = value_noise(i.world.xz * 0.9) * 0.6 + value_noise(i.world.xz * 3.1) * 0.4;
        float2 tile = abs(frac(i.world.xz / 2.4) - 0.5);
        float seam = (1.0 - smoothstep(0.46, 0.49, max(tile.x, tile.y))) * clamp(1.0 - max(aa2.x, aa2.y) * 2.0, 0.0, 1.0);
        float streak = value_noise(float2(dot(i.world.xz, float2(0.7, 0.7)) * 1.4, i.world.y * 0.08));
        float3 side = lerp(float3(0.075, 0.075, 0.085), float3(0.11, 0.08, 0.065), smoothstep(0.55, 0.8, streak));
        float3 flat_ = lerp(float3(0.055, 0.055, 0.062), float3(0.095, 0.09, 0.09), grit) * (0.8 + 0.2 * seam);
        float3 albedo = lerp(side, flat_, top);
        float puddle = smoothstep(0.55, 0.72, value_noise(i.world.xz * 0.21)) * top;
        s.albedo = albedo * (1.0 - 0.5 * puddle);
        s.roughness = lerp(0.5, 0.08, puddle);
        s.metallic = 0.1;
        s.emission = albedo * city_light(n, height, i.world) * 1.6;
    }
    else
    {
        float height_scale = max(i.scale.y, 0.05);
        // Storeys: a whole number between the kit's ledges, so a ledge always lands on a floor line.
        float span = m.ledge_every * height_scale;
        float floors_per = max(2.0, floor(span / m.storey + 0.5));
        float floor_h = span / floors_per;
        float kind = frac(style * 5.0 + i.custom.x);       // the wall: curtain, ribbon, fins or punched
        float bay = 6.0;                                    // kit metres between the piers (the kit's BAY)
        float column = bay / (2.0 + floor(frac(style * 7.13) * 3.0));
        float2 cell = float2(s.uv.x / column, height / floor_h);
        float2 id = floor(cell);
        float2 f = frac(cell);
        float2 aa = fwidth(cell) + 1e-4;
        float column_m = column * i.scale.x;

        // Curtain walls are mostly glass, ribbon walls long and low, punched walls small windows in a thick wall.
        float curtain = step(kind, 0.3);
        float ribbon = step(0.3, kind) * step(kind, 0.55);
        float fins = step(0.55, kind) * step(kind, 0.72);
        float2 lo = float2(0.17, 0.26);
        float2 hi = float2(0.83, 0.82);
        lo = lerp(lo, float2(0.03, 0.09), curtain);
        hi = lerp(hi, float2(0.97, 0.93), curtain);
        lo = lerp(lo, float2(0.0, 0.3), ribbon);
        hi = lerp(hi, float2(1.0, 0.86), ribbon);
        lo = lerp(lo, float2(0.14, 0.05), fins);
        hi = lerp(hi, float2(0.86, 0.95), fins);

        bool shop = id.y < 0.5;                   // the ground floor: shopfronts
        if (shop)
        {
            lo = float2(0.03, 0.05);
            hi = float2(0.97, 0.62);
        }

        // Rooms of two to four windows are lit together; the lit fraction is the tower's.
        float roomWidth = 2.0 + floor(hash12(float2(id.y, seed)) * 3.0);
        float room_i = floor(id.x / roomWidth);
        float r = hash12(float2(room_i, id.y) + seed);
        float rs = hash12(float2(room_i * 3.3, id.y * 1.7) + seed * 0.73);
        // Whole floors go dark (offices after hours), the rest are lit at the tower's fraction.
        float floorDark = step(0.74, hash12(float2(id.y * 0.61, seed + 5.0)));
        float lit = step(r, clamp(i.custom.z * 0.62 * m.lit_scale, 0.0, 1.0)) * (1.0 - floorDark);
        float tint = hash12(float2(room_i * 1.7, id.y * 3.1) + seed * 0.37);
        float3 warm = float3(1.0, 0.66, 0.38);
        float3 cool = float3(0.72, 0.86, 1.0);
        float3 colour = tint < 0.6 ? warm : (tint < 0.95 ? cool : neon(tint * 5.0 + hue));
        float bright = lerp(0.22, 1.0, pow(hash12(float2(room_i, id.y) * 1.3 + seed), 2.0));
        // A few windows flicker, a few more show a television's blue.
        float fl = hash12(float2(id.y, room_i) * 1.9 + seed);
        float tv = step(0.93, fl) * (0.6 + 0.4 * sin(s.time * 9.0 + fl * 50.0) * sin(s.time * 3.7 + id.x));
        colour = lerp(colour, float3(0.3, 0.5, 1.0), step(0.93, fl));
        bright *= lerp(1.0, tv, step(0.93, fl));
        bright *= 1.0 - step(0.992, fl) * step(0.5, frac(sin(s.time * 23.0 + id.x * 7.0) * 43.0));
        if (shop)
        {
            room_i = floor(id.x / 3.0);
            lit = step(0.12, hash12(float2(room_i * 0.37, seed)));
            colour = lerp(float3(1.0, 0.78, 0.55), neon(hash12(float2(room_i, seed)) + hue), 0.35);
            bright = 1.15;
        }

        // How close this is: the pane's frame, rooms and wall details only exist above a few pixels a window.
        float fine = clamp(1.6 - max(aa.x, aa.y) * 9.0, 0.0, 1.0) * m.detail;
        float glass = 0.0;
        float3 inside = colour * bright;
        float3 wall_glow = float3(0.0);
        float3 V = normalize(s.cameraPositionWorld - i.world);

        float2 pane = f;
        float reveal = 0.0;
        if (fine > 0.0)
        {
            // The wall's own axes (UV.x may run either way round it) and the ray in metres.
            float3 T = normalize(cross(float3(0.0, 1.0, 0.0), n));
            float along = dot(T, ddx(i.world)) * ddx(s.uv.x) + dot(T, ddy(i.world)) * ddy(s.uv.x);
            T *= along < 0.0 ? -1.0 : 1.0;
            float3 d = float3(-dot(V, T), -V.y, -max(dot(V, n), 0.04));
            float recess = lerp(0.32, 0.1, curtain);
            float2 slide = d.xy / -d.z * recess;
            pane = f + slide / float2(column_m, floor_h);
            reveal = recess;
            float3 p = float3((id.x + pane.x) * column_m, (id.y + pane.y) * floor_h, 0.0);
            float cells = shop ? 3.0 : roomWidth;
            float x0 = room_i * cells * column_m;
            float x1 = x0 + cells * column_m;
            float y0 = id.y * floor_h;
            float y1 = y0 + floor_h * 0.86;
            float depth = shop ? 4.0 : 5.0 + 2.5 * rs;
            float3 interior = room(p, d, x0, x1, y0, y1, depth, rs);
            if (shop)
            {
                // Shelves of goods along the back: rows of small coloured blocks.
                interior *= 0.7 + 0.6 * value_noise(float2(p.x * 3.0, p.y * 7.0));
            }
            else
            {
                // Blinds or curtains close to the glass on many windows, down to a different height in each.
                float bl = hash12(float2(id.x * 2.3, id.y) + seed);
                float drop = step(0.45, bl) * lerp(0.2, 0.95, frac(bl * 7.0));
                float slats = 0.72 + 0.28 * step(0.5, frac(pane.y * floor_h * 13.0));
                float covered = step(1.0 - drop, pane.y);
                interior = lerp(interior, lerp(float3(0.82, 0.78, 0.7), colour, 0.5) * 0.55 * slats, covered);
            }

            inside = lerp(inside, interior * colour * (0.35 + bright) * 0.95, fine);
        }

        float2 aw = max(aa, float2(1e-3));
        float wx = smoothstep(lo.x - aw.x, lo.x + aw.x, pane.x) * (1.0 - smoothstep(hi.x - aw.x, hi.x + aw.x, pane.x));
        float wy = smoothstep(lo.y - aw.y, lo.y + aw.y, pane.y) * (1.0 - smoothstep(hi.y - aw.y, hi.y + aw.y, pane.y));
        glass = wx * wy;

        // The opening in the wall, unshifted: between it and the shifted pane the reveal shows.
        float ox = smoothstep(lo.x - aw.x, lo.x + aw.x, f.x) * (1.0 - smoothstep(hi.x - aw.x, hi.x + aw.x, f.x));
        float oy = smoothstep(lo.y - aw.y, lo.y + aw.y, f.y) * (1.0 - smoothstep(hi.y - aw.y, hi.y + aw.y, f.y));
        float opening = ox * oy;
        glass *= opening;

        // Mullions split the pane: a centre bar on wide windows, a transom under the top light.
        if (fine > 0.0)
        {
            float wide = step(2.2, column_m * (hi.x - lo.x));
            float bar = wide * (1.0 - smoothstep(0.006, 0.006 + aw.x, abs(pane.x - 0.5)));
            float transom = (1.0 - smoothstep(0.008, 0.008 + aw.y, abs(pane.y - 0.72)));
            glass *= 1.0 - max(bar, transom) * fine;
        }

        float3 emission = shop ? inside * lit * glass * m.window_energy * 1.4 : inside * lit * glass * m.window_energy;

        // Wet glass: dark panes mirror the city, lit ones a little.
        float3 R = reflect(-V, n);
        float fres = 0.05 + 0.75 * pow(1.0 - max(dot(V, n), 0.0), 4.0);
        float3 mirror = glass_reflection(normalize(R + float3(0.0, 0.0, 0.012 * sin(i.world.y * 2.3 + s.uv.x))));
        emission += mirror * fres * glass * lerp(1.0, 0.45, lit) * (0.35 + 0.65 * m.detail);

        // The wall: what it is made of, weathered by the rain, in the city's light.
        float streaks = value_noise(float2(s.uv.x * 0.9 + seed, height * 0.035));
        float grime = 0.62 + 0.38 * smoothstep(0.25, 0.7, streaks);
        float patched = 0.86 + 0.14 * step(0.5, hash12(float2(floor(s.uv.x / bay), floor(id.y / 3.0)) + seed));
        float3 stuff = kind < 0.3 ? float3(0.05, 0.056, 0.068) : (kind < 0.72 ? float3(0.13, 0.125, 0.12) : float3(0.1, 0.066, 0.056));
        stuff = lerp(stuff, stuff * float3(1.05, 1.0, 0.9) * 0.8, frac(style * 19.0));
        float3 albedo = stuff * grime * patched;
        // Joints: floor lines and panel joints, the shadow under each sill.
        float joint = 0.0;
        if (fine > 0.0)
        {
            float floorLine = 1.0 - smoothstep(0.0, 0.02 + aw.y, min(f.y, 1.0 - f.y));
            float panelLine = (1.0 - smoothstep(0.0, 0.015 + aw.x, min(f.x, 1.0 - f.x))) * (1.0 - ribbon);
            float sill = (1.0 - smoothstep(0.0, 0.05, lo.y - f.y)) * step(f.y, lo.y) * ox;
            joint = max(max(floorLine, panelLine) * 0.6, sill * 0.75) * fine;
        }

        albedo *= 1.0 - joint;
        float wall_fine = 1.0 - opening;
        // Inside the reveal the wall is lit by the room when the room is lit.
        float in_reveal = opening * (1.0 - glass) * step(0.001, reveal);
        emission += inside * lit * in_reveal * 0.5;
        wall_glow = albedo * city_light(n, height, i.world) * 2.2;

        // Colour washing the walls from signs and screens: soft pools at the low floors.
        float pool = smoothstep(0.58, 0.82, value_noise(float2(s.uv.x * 0.05 + seed, height * 0.06)));
        wall_glow += albedo * neon(hash12(float2(floor(s.uv.x * 0.05 + seed), 2.0)) + hue) * pool * exp(-height / 75.0) * 1.8;
        emission += wall_glow * (wall_fine + in_reveal * 0.3);

        // Fascia over the shops: lit letters on dark, one sign per shop, the shop's own colour.
        if (shop)
        {
            float fy = smoothstep(0.66, 0.68, f.y) * (1.0 - smoothstep(0.93, 0.95, f.y));
            float2 letters = float2((s.uv.x / column) * 2.6, f.y * 7.0);
            float2 li = floor(letters);
            float letter = step(0.42, hash12(li + float2(room_i, seed))) * step(0.12, frac(letters.x)) * step(0.1, frac(letters.y));
            float shopOn = step(0.15, hash12(float2(room_i * 1.3, seed)));
            float3 fc = neon(hash12(float2(room_i * 0.7, seed)) + hue);
            emission = lerp(emission, fc * (0.18 + 2.2 * letter * fine + 0.6 * (1.0 - fine)) * shopOn, fy);
        }

        // Accent bands: some towers have a neon line every few storeys.
        float every = floor(lerp(8.0, 20.0, frac(style * 3.7)));
        float bandOn = step(0.62, frac(style * 11.0)) * step(mod(id.y, every), 0.5) * step(1.5, id.y);
        float band = bandOn * (1.0 - smoothstep(0.022, 0.022 + aw.y * 2.0, f.y));
        emission += accent * band * m.trim_energy * m.neon_scale * 0.5;

        // Far away the grid is smaller than a pixel: fade to its average so it does not shimmer, keeping the rough pattern
        // of lit and dark floors.
        float blur = clamp(max(aa.x, aa.y) * 1.6 - 0.35, 0.0, 1.0);
        float area = (hi.x - lo.x) * (hi.y - lo.y);
        float clump = 0.45 + 1.1 * hash12(floor(float2(s.uv.x / 14.0, cell.y / 3.0)) + seed);
        float3 average = lerp(warm, cool, 0.4) * clamp(i.custom.z * m.lit_scale, 0.0, 1.0) * clump * area * 0.62 * 0.74 * 0.5 * m.window_energy * 0.9
            + accent * m.trim_energy * m.neon_scale * 0.5 * bandOn * 0.05 + albedo * city_light(n, height, i.world) * 1.6 * (1.0 - area);
        s.emission = lerp(emission, average, blur);

        float glassAmount = lerp(glass, area, blur);
        s.albedo = lerp(albedo, float3(0.008, 0.01, 0.016), glassAmount);
        s.roughness = lerp(0.62, 0.1, glassAmount);
        s.metallic = lerp(0.05, 0.5, glassAmount);
    }
}
