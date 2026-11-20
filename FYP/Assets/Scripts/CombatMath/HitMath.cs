using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A body part for hit detection: a line segment from A to B with a radius.
/// A capsule with A == B is a sphere (for example the head).
/// </summary>
public struct BodyCapsule
{
    public Vector3 A;
    public Vector3 B;
    public float Radius;

    public BodyCapsule(Vector3 a, Vector3 b, float radius)
    {
        A = a;
        B = b;
        Radius = radius;
    }
}

/// <summary>
/// The blade at one moment: a line segment from the hilt to the tip.
/// </summary>
public struct BladePose
{
    public Vector3 Hilt;
    public Vector3 Tip;

    public BladePose(Vector3 hilt, Vector3 tip)
    {
        Hilt = hilt;
        Tip = tip;
    }
}

/// <summary>
/// Everything about one blade contact, so a hit can be logged, explained and
/// drawn in a debug view without redoing any maths.
/// </summary>
public struct BladeHit
{
    /// <summary>When it happened: 0 = the previous frame's poses, 1 = this frame's.</summary>
    public float SweepFraction;
    /// <summary>Which body part was hit (index into the list passed to the sweep, 0 for a single capsule).</summary>
    public int CapsuleIndex;
    /// <summary>The blade at the moment of contact.</summary>
    public BladePose Pose;
    /// <summary>Where along the blade it touched: 0 = hilt, 1 = tip.</summary>
    public float BladeFraction;
    /// <summary>Closest point on the blade's centre line.</summary>
    public Vector3 PointOnBlade;
    /// <summary>Closest point on the capsule's centre line.</summary>
    public Vector3 PointOnCapsule;
    /// <summary>Gap between the two centre lines (0 = they cross).</summary>
    public float Distance;
    /// <summary>Blade radius + capsule radius. It is a hit when Distance &lt;= Reach.</summary>
    public float Reach;
}

/// <summary>
/// Hit detection maths for Blade & Reflex (GDD section 4).
/// Pure maths on positions: no colliders, rigidbodies, triggers or physics
/// queries, so results don't depend on the physics step and can be unit tested
/// with fixed inputs. HitCalculator (planned) feeds it bone positions each frame.
///
/// The blade is a segment with a small thickness, each body part is a capsule.
/// A hit is when the shortest distance between the blade segment and the
/// capsule's segment is no more than bladeRadius + capsule radius.
///
/// Between two frames the blade and the body are swept together in sub-steps.
/// The number of steps is chosen so that, relative to the body, the blade tip
/// moves no more than maxStepDistance per step (pass the smallest capsule
/// radius), so a fast swing can't pass through a target between frames.
/// A contact that only grazes the very edge can still fall between two
/// checks. The step count is capped at maxSteps; a swing that would need
/// more takes slightly bigger steps.
/// </summary>
public static class HitMath
{
    /// <summary>Default cap on sub-steps per frame.</summary>
    public const int DefaultMaxSubSteps = 128;

    // Squared lengths below this count as zero (segment is really a point).
    private const float Epsilon = 1e-8f;
    // Segments closer than about 0.06 degrees to parallel are treated as parallel.
    private const float ParallelTolerance = 1e-6f;

    // ---- Segment distance ----

    /// <summary>
    /// Squared shortest distance between segments p1-q1 and p2-q2.
    /// s and t are where the closest points lie along each segment (0-1).
    /// Follows Ericson, Real-Time Collision Detection, section 5.1.9; the
    /// one-letter variable names match the book so the code can be checked
    /// against it.
    /// </summary>
    public static float SegmentSegmentDistanceSq(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
                                                 out float s, out float t)
    {
        Vector3 d1 = q1 - p1;
        Vector3 d2 = q2 - p2;
        Vector3 r = p1 - p2;
        float a = Vector3.Dot(d1, d1);
        float e = Vector3.Dot(d2, d2);
        float f = Vector3.Dot(d2, r);

        if (a <= Epsilon && e <= Epsilon)
        {
            // Both segments are points.
            s = t = 0f;
            return r.sqrMagnitude;
        }

        if (a <= Epsilon)
        {
            // First segment is a point.
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            float c = Vector3.Dot(d1, r);
            if (e <= Epsilon)
            {
                // Second segment is a point.
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                float b = Vector3.Dot(d1, d2);
                float denom = a * e - b * b;

                // Parallel segments: any s works, pick 0 and let t follow.
                s = denom > ParallelTolerance * a * e ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                t = (b * s + f) / e;

                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }

        Vector3 c1 = p1 + d1 * s;
        Vector3 c2 = p2 + d2 * t;
        return (c1 - c2).sqrMagnitude;
    }

    /// <summary>Shortest distance between segments p1-q1 and p2-q2.</summary>
    public static float SegmentSegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
    {
        return Mathf.Sqrt(SegmentSegmentDistanceSq(p1, q1, p2, q2, out _, out _));
    }

    // ---- Single pose ----

    /// <summary>
    /// Does the blade, at a single pose, touch the capsule? 'hit' is filled in
    /// either way (Distance and Reach show how close a miss was), with
    /// SweepFraction 0 and CapsuleIndex 0.
    /// </summary>
    public static bool BladeTouchesCapsule(BladePose blade, float bladeRadius, BodyCapsule capsule,
                                           out BladeHit hit)
    {
        float distSq = SegmentSegmentDistanceSq(blade.Hilt, blade.Tip, capsule.A, capsule.B,
                                                out float s, out float t);
        float reach = bladeRadius + capsule.Radius;

        hit = new BladeHit
        {
            Pose = blade,
            BladeFraction = s,
            PointOnBlade = Vector3.Lerp(blade.Hilt, blade.Tip, s),
            PointOnCapsule = Vector3.Lerp(capsule.A, capsule.B, t),
            Distance = Mathf.Sqrt(distSq),
            Reach = reach
        };
        return distSq <= reach * reach;
    }

    // ---- Sweeping ----

    /// <summary>
    /// The blade between two frames, at fraction t (0 = from, 1 = to).
    /// The hilt moves in a straight line; the blade direction turns along the
    /// arc, so the tip follows the swing's curve instead of cutting the corner.
    /// A flip of exactly 180 degrees has no single arc, so Unity picks one; real
    /// swings move far less than that per frame.
    /// </summary>
    public static BladePose InterpolatePose(BladePose from, BladePose to, float t)
    {
        Vector3 hilt = Vector3.Lerp(from.Hilt, to.Hilt, t);

        Vector3 fromDir = from.Tip - from.Hilt;
        Vector3 toDir = to.Tip - to.Hilt;
        float length = Mathf.Lerp(fromDir.magnitude, toDir.magnitude, t);

        Vector3 dir;
        if (fromDir.sqrMagnitude <= Epsilon || toDir.sqrMagnitude <= Epsilon)
            dir = Vector3.Lerp(fromDir, toDir, t).normalized;
        else
            dir = Vector3.Slerp(fromDir.normalized, toDir.normalized, t);

        return new BladePose(hilt, hilt + dir * length);
    }

    /// <summary>A body part between two frames, at fraction t (both ends move in straight lines).</summary>
    public static BodyCapsule InterpolateCapsule(BodyCapsule from, BodyCapsule to, float t)
    {
        return new BodyCapsule(Vector3.Lerp(from.A, to.A, t),
                               Vector3.Lerp(from.B, to.B, t),
                               Mathf.Lerp(from.Radius, to.Radius, t));
    }

    /// <summary>
    /// Upper bound on how far any point of the blade (the tip moves furthest)
    /// travels along the path InterpolatePose follows from one pose to the next.
    /// </summary>
    public static float BladeTravelBound(BladePose from, BladePose to)
    {
        float hiltTravel = Vector3.Distance(from.Hilt, to.Hilt);

        Vector3 fromDir = from.Tip - from.Hilt;
        Vector3 toDir = to.Tip - to.Hilt;
        float fromLength = fromDir.magnitude;
        float toLength = toDir.magnitude;
        float angle = fromDir.sqrMagnitude > Epsilon && toDir.sqrMagnitude > Epsilon
            ? Vector3.Angle(fromDir, toDir) * Mathf.Deg2Rad
            : 0f;

        return hiltTravel + angle * Mathf.Max(fromLength, toLength) + Mathf.Abs(fromLength - toLength);
    }

    /// <summary>
    /// How many sub-steps are needed so that something moving 'travel' metres
    /// moves no more than maxStepDistance per step. Always between 1 and
    /// maxSteps (a maxSteps below 1 counts as 1). A maxStepDistance of 0 or less means "as many as allowed".
    /// </summary>
    public static int SubStepCount(float travel, float maxStepDistance, int maxSteps = DefaultMaxSubSteps)
    {
        maxSteps = Mathf.Max(1, maxSteps);
        if (!(maxStepDistance > 0f))   // also catches NaN
            return maxSteps;

        // Check before converting to int: a huge or NaN ratio would overflow.
        float raw = travel / maxStepDistance;
        if (!(raw < maxSteps))
            return maxSteps;
        if (!(raw > 0f))
            return 1;
        return Mathf.CeilToInt(raw);
    }

    /// <summary>
    /// Sub-steps needed for the blade alone (target standing still). A helper
    /// for tests and debugging; the sweeps also add the target's movement.
    /// </summary>
    public static int SubStepCount(BladePose from, BladePose to, float maxStepDistance,
                                   int maxSteps = DefaultMaxSubSteps)
    {
        return SubStepCount(BladeTravelBound(from, to), maxStepDistance, maxSteps);
    }

    /// <summary>
    /// Sweeps the blade against one body part, both moving from their previous
    /// pose to their current one, and reports the first sub-step where they
    /// touch. For a target that hasn't moved, pass the same capsule twice.
    /// Set includeStart to false when the previous poses were already checked
    /// last frame (every frame of the active window after the first).
    /// maxStepDistance: largest gap allowed between checks; pass the smallest
    /// capsule radius. On a miss, 'hit' is default.
    /// </summary>
    public static bool SweepBladeVsCapsule(BladePose previousBlade, BladePose currentBlade, float bladeRadius,
                                           BodyCapsule previousCapsule, BodyCapsule currentCapsule,
                                           float maxStepDistance, bool includeStart, out BladeHit hit,
                                           int maxSteps = DefaultMaxSubSteps)
    {
        float travel = BladeTravelBound(previousBlade, currentBlade)
                     + CapsuleTravel(previousCapsule, currentCapsule);
        int steps = SubStepCount(travel, maxStepDistance, maxSteps);

        for (int i = includeStart ? 0 : 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            BladePose blade = InterpolatePose(previousBlade, currentBlade, t);
            BodyCapsule capsule = InterpolateCapsule(previousCapsule, currentCapsule, t);
            if (BladeTouchesCapsule(blade, bladeRadius, capsule, out hit))
            {
                hit.SweepFraction = t;
                return true;
            }
        }

        hit = default;
        return false;
    }

    /// <summary>
    /// Sweeps the blade against a whole body (one capsule per body part, the
    /// same parts in the same order in both lists) and reports the earliest
    /// contact. If two parts are touched at the same sub-step, the one the
    /// blade is deepest into (largest Reach - Distance) wins; on equal depth
    /// the lower index wins. CapsuleIndex says
    /// which part was hit. maxStepDistance and includeStart work as in
    /// SweepBladeVsCapsule. On a miss, 'hit' is default.
    /// </summary>
    public static bool SweepBladeVsBody(BladePose previousBlade, BladePose currentBlade, float bladeRadius,
                                        IReadOnlyList<BodyCapsule> previousBody, IReadOnlyList<BodyCapsule> currentBody,
                                        float maxStepDistance, bool includeStart, out BladeHit hit,
                                        int maxSteps = DefaultMaxSubSteps)
    {
        if (previousBody == null || currentBody == null)
            throw new ArgumentNullException(previousBody == null ? nameof(previousBody) : nameof(currentBody));
        if (previousBody.Count != currentBody.Count)
            throw new ArgumentException("previousBody and currentBody must list the same body parts.");

        float bodyTravel = 0f;
        for (int p = 0; p < currentBody.Count; p++)
            bodyTravel = Mathf.Max(bodyTravel, CapsuleTravel(previousBody[p], currentBody[p]));

        float travel = BladeTravelBound(previousBlade, currentBlade) + bodyTravel;
        int steps = SubStepCount(travel, maxStepDistance, maxSteps);

        for (int i = includeStart ? 0 : 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            BladePose blade = InterpolatePose(previousBlade, currentBlade, t);

            bool found = false;
            hit = default;
            for (int p = 0; p < currentBody.Count; p++)
            {
                BodyCapsule capsule = InterpolateCapsule(previousBody[p], currentBody[p], t);
                if (BladeTouchesCapsule(blade, bladeRadius, capsule, out BladeHit candidate)
                    && (!found || candidate.Reach - candidate.Distance > hit.Reach - hit.Distance))
                {
                    candidate.SweepFraction = t;
                    candidate.CapsuleIndex = p;
                    hit = candidate;
                    found = true;
                }
            }

            if (found)
                return true;
        }

        hit = default;
        return false;
    }

    // Furthest either end of the capsule moves between the two frames.
    private static float CapsuleTravel(BodyCapsule from, BodyCapsule to)
    {
        return Mathf.Max(Vector3.Distance(from.A, to.A), Vector3.Distance(from.B, to.B));
    }
}
