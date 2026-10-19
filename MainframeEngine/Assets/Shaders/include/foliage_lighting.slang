// The foliage lighting model (ADR 0151, 0172), shared by Foliage.vk.frag and the impostors (Impostor.vk.frag): PBR or
// Blinn-Phong with the vertex AO on the ambient term, plus light from the first directional light shining through.
// Include frame.slang, shadows.slang, lights.slang, material.slang and foliage.slang first.
#pragma once

// The lit colour of a foliage surface: the one place the shading model is chosen. PBR: the material's roughness and
// metallic (0) × its ORM map (`orm` = materialOrm: AO, roughness, metallic); Blinn-Phong: the roughness-derived
// highlight. `ao` (the vertex AO, × the map's in PBR) darkens only the ambient term; `ssao` is the screen-space AO.
float3 foliageLight(float3 albedo, float3 N, float3 Ngeo, float3 worldPos, float ao, float3 orm, float ssao)
{
    if (material.flags.y == kShadingPbr)
    {
        PbrSurface s;
        s.albedo = albedo;
        s.metallic = orm.z;
        s.roughness = orm.y;
        s.ao = ao * orm.x;
        s.N = N;
        return shadeLightsPbr(s, Ngeo, worldPos, ssao);
    }
    float3 color = shadeLightsBlinnPhong(albedo, N, Ngeo, worldPos, material.params.x, material.params.y, ssao);
    return color - lights.ambientColor.xyz * albedo * (1.0 - ao) * ssao;
}

// Light from the first directional light shining through the leaf: back-lighting where the light hits the other side,
// brighter when looking towards the light, shadowed like the light itself (and by the SSAO's light affect). With a
// thickness map (`thickness` >= 0) it is wrap transmission scaled by (1 − thickness): thin edges glow, dense centres
// stay dark (ADR 0172). With light probes (ADR 0170) the leaf also passes the ambient light reaching its back side (the
// probes' sky and bounce around -N, from the sample foliageLight just took: call it first), so a canopy out of the sun
// still glows green from below.
float3 foliageTranslucency(float3 albedo, float3 N, float3 Ngeo, float3 worldPos, float directAo, float thickness)
{
    float amount = foliageTranslucencyAmount();
    if (amount <= 0.0)
        return float3(0.0);
    float3 ambient = probesActive() && material.flags.y != kShadingUnshaded ? albedo * amount * probeAmbient(gProbeSample, -N) : float3(0.0);
    if (lights.counts.x == 0)
        return ambient;

    DirLight l = lights.dir[0];
    float3 L = normalize(-l.directionIntensity.xyz);
    float3 V = normalize(lights.cameraPosition.xyz - worldPos);
    float view = pow(saturate(dot(V, -L)), foliageTranslucencyScatter());
    float back;
    float shape;
    if (thickness >= 0.0)
    {
        back = saturate((dot(-N, L) + 0.5) / 1.5) * (1.0 - thickness); // wrap(dot(-N, L), 0.5)
        shape = 0.5 + 0.5 * view;
    }
    else
    {
        back = saturate(dot(-N, L));
        shape = 0.4 + 0.6 * view;
    }
    if (back <= 0.0)
        return ambient;
    float3 Nl = dot(Ngeo, L) < 0.0 ? -Ngeo : Ngeo; // the side facing the light, for the receiver offset
    float shadow = lights.counts.w == 0 ? dirShadow(0, worldPos, Nl, L) : 1.0;
    shadow = lerp(1.0, shadow, l.color.w) * directAo;
    return albedo * foliageTranslucencyColor() * amount * l.color.xyz * l.directionIntensity.w * back * shape * shadow + ambient;
}
