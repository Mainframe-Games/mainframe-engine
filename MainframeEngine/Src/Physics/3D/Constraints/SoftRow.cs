using System.Runtime.CompilerServices;

namespace MainframeEngine;

/// <summary>
/// Soft constraint coefficients (Erin Catto's, as Box2D's <c>b2MakeSoft</c>): a spring of <c>frequency</c> Hz and
/// <c>dampingRatio</c> solved implicitly inside the solver, stable at any stiffness and independent of the bodies'
/// masses. A row's impulse is <c>-massScale · m · (Cdot + biasRate · C) - (1 - massScale) · accumulated</c>, with
/// <c>m</c> the row's effective mass and <c>C</c> its position error. Shared by the 6-DOF springs and the active-ragdoll
/// drives (G9.10, ADR 0234).
/// </summary>
internal static class SoftRow
{
    /// <summary>
    /// The coefficients for a substep of <paramref name="h"/> seconds. A frequency of 0 (or less) is a rigid row:
    /// bias rate 0, mass scale 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Coefficients(float frequency, float dampingRatio, float h, out float biasRate, out float massScale)
    {
        if (!(frequency > 0f))
        {
            biasRate = 0f;
            massScale = 1f;
            return;
        }

        var omega = 2f * MathF.PI * frequency;
        var a1 = 2f * dampingRatio + h * omega;
        var a2 = h * omega * a1;
        var a3 = 1f / (1f + a2);
        biasRate = omega / a1;
        massScale = a2 * a3;
    }

    /// <summary>The highest spring frequency the engine passes for a tick rate: a quarter of it, like the PD springs.</summary>
    public static float ClampFrequency(float frequency, float tickRate) => MathF.Min(frequency, tickRate * 0.25f);
}
