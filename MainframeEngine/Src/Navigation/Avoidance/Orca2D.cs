// Ported from RVO2 Library 2.0 (Agent.cpp: computeNewVelocity, linearProgram1–3), C++ → C#.
//   Copyright 2008 University of North Carolina at Chapel Hill
//   Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with
//   the License. A copy is in LICENSE-RVO2.txt next to this file, or at http://www.apache.org/licenses/LICENSE-2.0.
//   Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on
//   an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
//   specific language governing permissions and limitations under the License.
//   Authors: Jur van den Berg, Stephen J. Guy, Jamie Snape, Ming C. Lin, Dinesh Manocha (https://gamma.cs.unc.edu/RVO2/).
// Changes (Mainframe Engine, MIT for the changes): neighbours and lines in caller-owned spans (no allocation); a
// per-neighbour responsibility (0.5 reciprocal, 1 for neighbours that do not avoid back) instead of the fixed 0.5;
// exactly head-on relative velocities tilted to one side (BreakSymmetry) instead of the examples' random perturbation;
// scalar dot/det so every CPU gives the same bits; the zero-length guards noted below.

using System.Numerics;

namespace MainframeEngine;

/// <summary>An ORCA half-plane: velocities left of <see cref="Direction"/> through <see cref="Point"/> are allowed.</summary>
internal struct OrcaLine
{
    public Vector2 Point;
    public Vector2 Direction;
}

/// <summary>One polygon obstacle vertex (RVO2's <c>Obstacle</c>): the edge from it to <see cref="Next"/>.</summary>
internal struct OrcaObstacleVertex
{
    public Vector2 Point;
    public Vector2 UnitDir; // towards the next vertex
    public int Next;
    public int Previous;
    public bool IsConvex;
}

/// <summary>An agent-like neighbour (another agent or a radius obstacle).</summary>
internal struct OrcaNeighbor
{
    public Vector2 Position;
    public Vector2 Velocity;
    public float Radius;
    public float Responsibility; // 0.5: avoids back (reciprocal); 1: does not (an obstacle, a lower-priority view)
}

/// <summary>
/// The ORCA solver of RVO2 2.0 (van den Berg et al., "Reciprocal n-Body Collision Avoidance", 2011): builds the
/// half-planes of polygon obstacle edges and of neighbours, then solves the 2D linear programs for the velocity closest
/// to the preferred one inside the max-speed circle. Static, allocation-free: the caller owns every buffer.
/// </summary>
internal static class Orca2D
{
    private const float Epsilon = 0.00001f;

    /// <summary>
    /// The new velocity of an agent at <paramref name="position"/>. <paramref name="obstacleEdges"/> are the indices of
    /// the first vertices of the obstacle edges to consider, nearest first; <paramref name="neighbors"/> nearest first.
    /// <paramref name="lines"/> must hold <c>obstacleEdges.Length + neighbors.Length</c> lines and
    /// <paramref name="projected"/> as many.
    /// </summary>
    public static Vector2 ComputeNewVelocity(Vector2 position, Vector2 velocity, Vector2 preferredVelocity, float radius, float maxSpeed,
        float timeHorizon, float timeHorizonObstacles, float timeStep, ReadOnlySpan<OrcaObstacleVertex> vertices,
        ReadOnlySpan<int> obstacleEdges, ReadOnlySpan<OrcaNeighbor> neighbors, Span<OrcaLine> lines, Span<OrcaLine> projected)
    {
        var count = 0;
        var invTimeHorizonObst = 1f / timeHorizonObstacles;
        var radiusSq = radius * radius;

        // Obstacle ORCA lines.
        for (var i = 0; i < obstacleEdges.Length; i++)
        {
            var index1 = obstacleEdges[i];
            var obstacle1 = vertices[index1];
            var index2 = obstacle1.Next;
            var obstacle2 = vertices[index2];
            var relativePosition1 = obstacle1.Point - position;
            var relativePosition2 = obstacle2.Point - position;

            // Already covered by a previous obstacle line?
            var alreadyCovered = false;
            for (var j = 0; j < count; j++)
            {
                if (Det(invTimeHorizonObst * relativePosition1 - lines[j].Point, lines[j].Direction) - invTimeHorizonObst * radius >= -Epsilon &&
                    Det(invTimeHorizonObst * relativePosition2 - lines[j].Point, lines[j].Direction) - invTimeHorizonObst * radius >= -Epsilon)
                {
                    alreadyCovered = true;
                    break;
                }
            }

            if (alreadyCovered)
                continue;

            var distSq1 = AbsSq(relativePosition1);
            var distSq2 = AbsSq(relativePosition2);
            var obstacleVector = obstacle2.Point - obstacle1.Point;
            var s = Dot(-relativePosition1, obstacleVector) / AbsSq(obstacleVector);
            var distSqLine = AbsSq(-relativePosition1 - s * obstacleVector);
            OrcaLine line;

            if (s < 0f && distSq1 <= radiusSq)
            {
                // Collision with the left vertex; ignored if non-convex.
                if (obstacle1.IsConvex && distSq1 > 0f)
                    lines[count++] = new OrcaLine { Point = Vector2.Zero, Direction = Normalize(new Vector2(-relativePosition1.Y, relativePosition1.X)) };
                continue;
            }

            if (s > 1f && distSq2 <= radiusSq)
            {
                // Collision with the right vertex; ignored if non-convex or if the neighbouring edge takes care of it.
                if (obstacle2.IsConvex && distSq2 > 0f && Det(relativePosition2, obstacle2.UnitDir) >= 0f)
                    lines[count++] = new OrcaLine { Point = Vector2.Zero, Direction = Normalize(new Vector2(-relativePosition2.Y, relativePosition2.X)) };
                continue;
            }

            if (s >= 0f && s < 1f && distSqLine <= radiusSq)
            {
                // Collision with the edge.
                lines[count++] = new OrcaLine { Point = Vector2.Zero, Direction = -obstacle1.UnitDir };
                continue;
            }

            // No collision: compute the legs. Viewed obliquely, both legs can come from one vertex; legs extend the
            // cut-off line at a non-convex vertex.
            Vector2 leftLegDirection, rightLegDirection;
            if (s < 0f && distSqLine <= radiusSq)
            {
                // The left vertex defines the velocity obstacle.
                if (!obstacle1.IsConvex)
                    continue;
                index2 = index1;
                obstacle2 = obstacle1;
                var leg1 = MathF.Sqrt(distSq1 - radiusSq);
                leftLegDirection = new Vector2(relativePosition1.X * leg1 - relativePosition1.Y * radius, relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
                rightLegDirection = new Vector2(relativePosition1.X * leg1 + relativePosition1.Y * radius, -relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
            }
            else if (s > 1f && distSqLine <= radiusSq)
            {
                // The right vertex defines the velocity obstacle.
                if (!obstacle2.IsConvex)
                    continue;
                index1 = index2;
                obstacle1 = obstacle2;
                var leg2 = MathF.Sqrt(distSq2 - radiusSq);
                leftLegDirection = new Vector2(relativePosition2.X * leg2 - relativePosition2.Y * radius, relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
                rightLegDirection = new Vector2(relativePosition2.X * leg2 + relativePosition2.Y * radius, -relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
            }
            else
            {
                // The usual situation.
                if (obstacle1.IsConvex)
                {
                    var leg1 = MathF.Sqrt(distSq1 - radiusSq);
                    leftLegDirection = new Vector2(relativePosition1.X * leg1 - relativePosition1.Y * radius, relativePosition1.X * radius + relativePosition1.Y * leg1) / distSq1;
                }
                else
                {
                    leftLegDirection = -obstacle1.UnitDir; // the left leg extends the cut-off line
                }

                if (obstacle2.IsConvex)
                {
                    var leg2 = MathF.Sqrt(distSq2 - radiusSq);
                    rightLegDirection = new Vector2(relativePosition2.X * leg2 + relativePosition2.Y * radius, -relativePosition2.X * radius + relativePosition2.Y * leg2) / distSq2;
                }
                else
                {
                    rightLegDirection = obstacle1.UnitDir; // the right leg extends the cut-off line
                }
            }

            // Legs never point into the neighbouring edge at a convex vertex: take that edge's cut-off line instead. A
            // velocity projected on such a "foreign" leg adds no constraint.
            var leftNeighbor = vertices[obstacle1.Previous];
            var isLeftLegForeign = false;
            var isRightLegForeign = false;
            if (obstacle1.IsConvex && Det(leftLegDirection, -leftNeighbor.UnitDir) >= 0f)
            {
                leftLegDirection = -leftNeighbor.UnitDir;
                isLeftLegForeign = true;
            }

            if (obstacle2.IsConvex && Det(rightLegDirection, obstacle2.UnitDir) <= 0f)
            {
                rightLegDirection = obstacle2.UnitDir;
                isRightLegForeign = true;
            }

            // The cut-off centres, and where the current velocity projects.
            var leftCutoff = invTimeHorizonObst * (obstacle1.Point - position);
            var rightCutoff = invTimeHorizonObst * (obstacle2.Point - position);
            var cutoffVec = rightCutoff - leftCutoff;
            var sameVertex = index1 == index2;
            var t = sameVertex ? 0.5f : Dot(velocity - leftCutoff, cutoffVec) / AbsSq(cutoffVec);
            var tLeft = Dot(velocity - leftCutoff, leftLegDirection);
            var tRight = Dot(velocity - rightCutoff, rightLegDirection);

            if (t < 0f && tLeft < 0f || sameVertex && tLeft < 0f && tRight < 0f)
            {
                // Project on the left cut-off circle.
                var unitW = Normalize(velocity - leftCutoff);
                line.Direction = new Vector2(unitW.Y, -unitW.X);
                line.Point = leftCutoff + radius * invTimeHorizonObst * unitW;
                lines[count++] = line;
                continue;
            }

            if (t > 1f && tRight < 0f)
            {
                // Project on the right cut-off circle.
                var unitW = Normalize(velocity - rightCutoff);
                line.Direction = new Vector2(unitW.Y, -unitW.X);
                line.Point = rightCutoff + radius * invTimeHorizonObst * unitW;
                lines[count++] = line;
                continue;
            }

            // Project on the left leg, the right leg or the cut-off line, whichever is closest to the velocity.
            var distSqCutoff = t < 0f || t > 1f || sameVertex ? float.PositiveInfinity : AbsSq(velocity - (leftCutoff + t * cutoffVec));
            var distSqLeft = tLeft < 0f ? float.PositiveInfinity : AbsSq(velocity - (leftCutoff + tLeft * leftLegDirection));
            var distSqRight = tRight < 0f ? float.PositiveInfinity : AbsSq(velocity - (rightCutoff + tRight * rightLegDirection));

            if (distSqCutoff <= distSqLeft && distSqCutoff <= distSqRight)
            {
                line.Direction = -obstacle1.UnitDir;
                line.Point = leftCutoff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
                lines[count++] = line;
                continue;
            }

            if (distSqLeft <= distSqRight)
            {
                if (isLeftLegForeign)
                    continue;
                line.Direction = leftLegDirection;
                line.Point = leftCutoff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
                lines[count++] = line;
                continue;
            }

            if (isRightLegForeign)
                continue;
            line.Direction = -rightLegDirection;
            line.Point = rightCutoff + radius * invTimeHorizonObst * new Vector2(-line.Direction.Y, line.Direction.X);
            lines[count++] = line;
        }

        var obstacleLineCount = count;
        var invTimeHorizon = 1f / timeHorizon;

        // Neighbour ORCA lines.
        for (var i = 0; i < neighbors.Length; i++)
        {
            ref readonly var other = ref neighbors[i];
            var relativePosition = other.Position - position;
            var relativeVelocity = velocity - other.Velocity;
            var distSq = AbsSq(relativePosition);
            relativeVelocity = BreakSymmetry(relativePosition, relativeVelocity, distSq);
            var combinedRadius = radius + other.Radius;
            var combinedRadiusSq = combinedRadius * combinedRadius;
            OrcaLine line;
            Vector2 u;

            if (distSq > combinedRadiusSq)
            {
                // No collision. w: from the cut-off centre to the relative velocity.
                var w = relativeVelocity - invTimeHorizon * relativePosition;
                var wLengthSq = AbsSq(w);
                var dotProduct1 = Dot(w, relativePosition);
                if (dotProduct1 < 0f && dotProduct1 * dotProduct1 > combinedRadiusSq * wLengthSq)
                {
                    // Project on the cut-off circle.
                    var wLength = MathF.Sqrt(wLengthSq);
                    var unitW = w / wLength;
                    line.Direction = new Vector2(unitW.Y, -unitW.X);
                    u = (combinedRadius * invTimeHorizon - wLength) * unitW;
                }
                else
                {
                    // Project on the legs.
                    var leg = MathF.Sqrt(distSq - combinedRadiusSq);
                    line.Direction = Det(relativePosition, w) > 0f
                        ? new Vector2(relativePosition.X * leg - relativePosition.Y * combinedRadius, relativePosition.X * combinedRadius + relativePosition.Y * leg) / distSq
                        : -new Vector2(relativePosition.X * leg + relativePosition.Y * combinedRadius, -relativePosition.X * combinedRadius + relativePosition.Y * leg) / distSq;
                    var dotProduct2 = Dot(relativeVelocity, line.Direction);
                    u = dotProduct2 * line.Direction - relativeVelocity;
                }
            }
            else
            {
                // Collision: project on the cut-off circle of the time step.
                var invTimeStep = 1f / timeStep;
                var w = relativeVelocity - invTimeStep * relativePosition;
                var wLength = MathF.Sqrt(AbsSq(w));
                if (wLength <= Epsilon)
                {
                    // RVO2 divides by zero here. The simulation moves coincident neighbours apart by index first, so
                    // this needs an exactly matching velocity too; any fixed direction keeps the result finite.
                    w = new Vector2(1f, 0f);
                    wLength = 1f;
                }

                var unitW = w / wLength;
                line.Direction = new Vector2(unitW.Y, -unitW.X);
                u = (combinedRadius * invTimeStep - wLength) * unitW;
            }

            line.Point = velocity + other.Responsibility * u;
            lines[count++] = line;
        }

        var all = lines[..count];
        var result = Vector2.Zero;
        var lineFail = LinearProgram2(all, maxSpeed, preferredVelocity, false, ref result);
        if (lineFail < count)
            LinearProgram3(all, obstacleLineCount, lineFail, maxSpeed, ref result, projected);
        return result;
    }

    /// <summary>
    /// Exactly head-on (the relative velocity along the line between the two, approaching), ORCA's truncation only slows
    /// both down and they stall face to face; RVO2's examples add random noise to the preferred velocities for this. Here
    /// the relative velocity tilts by 1 % towards the left of the line to the other instead: the same rule seen from both
    /// agents, so they pass each other on the same side, and deterministic.
    /// </summary>
    private static Vector2 BreakSymmetry(Vector2 relativePosition, Vector2 relativeVelocity, float distSq)
    {
        var speedSq = AbsSq(relativeVelocity);
        var cross = Det(relativePosition, relativeVelocity);
        if (Dot(relativePosition, relativeVelocity) <= 0f || cross * cross > 1e-6f * distSq * speedSq || distSq <= 0f)
            return relativeVelocity;
        var scale = 0.01f * MathF.Sqrt(speedSq / distSq);
        return relativeVelocity + new Vector2(-relativePosition.Y * scale, relativePosition.X * scale);
    }

    private static bool LinearProgram1(ReadOnlySpan<OrcaLine> lines, int lineNo, float radius, Vector2 optVelocity, bool directionOpt, ref Vector2 result)
    {
        var line = lines[lineNo];
        var dotProduct = Dot(line.Point, line.Direction);
        var discriminant = dotProduct * dotProduct + radius * radius - AbsSq(line.Point);
        if (discriminant < 0f)
            return false; // the max-speed circle fully invalidates this line

        var sqrtDiscriminant = MathF.Sqrt(discriminant);
        var tLeft = -dotProduct - sqrtDiscriminant;
        var tRight = -dotProduct + sqrtDiscriminant;
        for (var i = 0; i < lineNo; i++)
        {
            var denominator = Det(line.Direction, lines[i].Direction);
            var numerator = Det(lines[i].Direction, line.Point - lines[i].Point);
            if (MathF.Abs(denominator) <= Epsilon)
            {
                // (Almost) parallel.
                if (numerator < 0f)
                    return false;
                continue;
            }

            var t = numerator / denominator;
            if (denominator >= 0f)
                tRight = MathF.Min(tRight, t); // line i bounds this line on the right
            else
                tLeft = MathF.Max(tLeft, t);   // on the left
            if (tLeft > tRight)
                return false;
        }

        if (directionOpt)
        {
            result = Dot(optVelocity, line.Direction) > 0f ? line.Point + tRight * line.Direction : line.Point + tLeft * line.Direction;
        }
        else
        {
            var t = Dot(line.Direction, optVelocity - line.Point);
            result = t < tLeft ? line.Point + tLeft * line.Direction : t > tRight ? line.Point + tRight * line.Direction : line.Point + t * line.Direction;
        }

        return true;
    }

    private static int LinearProgram2(ReadOnlySpan<OrcaLine> lines, float radius, Vector2 optVelocity, bool directionOpt, ref Vector2 result)
    {
        if (directionOpt)
            result = optVelocity * radius;
        else if (AbsSq(optVelocity) > radius * radius)
            result = Normalize(optVelocity) * radius;
        else
            result = optVelocity;

        for (var i = 0; i < lines.Length; i++)
        {
            if (Det(lines[i].Direction, lines[i].Point - result) <= 0f)
                continue;
            // The result breaks constraint i: compute a new optimum.
            var tempResult = result;
            if (!LinearProgram1(lines, i, radius, optVelocity, directionOpt, ref result))
            {
                result = tempResult;
                return i;
            }
        }

        return lines.Length;
    }

    private static void LinearProgram3(ReadOnlySpan<OrcaLine> lines, int obstacleLineCount, int beginLine, float radius, ref Vector2 result, Span<OrcaLine> projected)
    {
        var distance = 0f;
        for (var i = beginLine; i < lines.Length; i++)
        {
            if (Det(lines[i].Direction, lines[i].Point - result) <= distance)
                continue;

            // The result breaks constraint i: minimise the largest penetration, keeping the obstacle lines hard.
            lines[..obstacleLineCount].CopyTo(projected);
            var projectedCount = obstacleLineCount;
            for (var j = obstacleLineCount; j < i; j++)
            {
                OrcaLine line;
                var determinant = Det(lines[i].Direction, lines[j].Direction);
                if (MathF.Abs(determinant) <= Epsilon)
                {
                    if (Dot(lines[i].Direction, lines[j].Direction) > 0f)
                        continue; // parallel, same direction
                    line.Point = 0.5f * (lines[i].Point + lines[j].Point); // opposite directions
                }
                else
                {
                    line.Point = lines[i].Point + Det(lines[j].Direction, lines[i].Point - lines[j].Point) / determinant * lines[i].Direction;
                }

                line.Direction = Normalize(lines[j].Direction - lines[i].Direction);
                projected[projectedCount++] = line;
            }

            var tempResult = result;
            var proj = projected[..projectedCount];
            if (LinearProgram2(proj, radius, new Vector2(-lines[i].Direction.Y, lines[i].Direction.X), true, ref result) < projectedCount)
                result = tempResult; // floating-point error only: keep the current result (RVO2)
            distance = Det(lines[i].Direction, lines[i].Point - result);
        }
    }

    // Scalar maths: the same bits on every CPU (no SIMD dot products).
    internal static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

    internal static float Det(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    internal static float AbsSq(Vector2 v) => v.X * v.X + v.Y * v.Y;

    internal static Vector2 Normalize(Vector2 v)
    {
        var length = MathF.Sqrt(AbsSq(v));
        return length > 0f ? new Vector2(v.X / length, v.Y / length) : Vector2.Zero;
    }

    /// <summary>The squared distance from <paramref name="c"/> to the segment a–b (RVO2's <c>distSqPointLineSegment</c>).</summary>
    internal static float DistSqPointSegment(Vector2 a, Vector2 b, Vector2 c)
    {
        var ab = b - a;
        var lengthSq = AbsSq(ab);
        var r = lengthSq > 0f ? Dot(c - a, ab) / lengthSq : 0f;
        if (r < 0f)
            return AbsSq(c - a);
        if (r > 1f)
            return AbsSq(c - b);
        return AbsSq(c - (a + r * ab));
    }

    /// <summary>RVO2's <c>leftOf</c>: &gt; 0 when <paramref name="c"/> is left of the line a → b.</summary>
    internal static float LeftOf(Vector2 a, Vector2 b, Vector2 c) => Det(a - c, b - a);
}
