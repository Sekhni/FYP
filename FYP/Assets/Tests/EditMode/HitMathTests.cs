using System;
using NUnit.Framework;
using UnityEngine;

public class HitMathTests
{
    private const float Tol = 1e-4f;

    // A 1 m blade held at the origin, turning a quarter circle from +x to +z.
    private static readonly BladePose QuarterFrom = new BladePose(Vector3.zero, Vector3.right);
    private static readonly BladePose QuarterTo = new BladePose(Vector3.zero, Vector3.forward);

    // Upright capsule (2 m tall, centred on y = 0) standing at 'pos'.
    private static BodyCapsule Post(Vector3 pos, float radius)
    {
        return new BodyCapsule(pos + Vector3.down, pos + Vector3.up, radius);
    }

    // Point at 'distance' from the origin, 'degrees' round from +x towards +z.
    private static Vector3 OnArc(float degrees, float distance)
    {
        float rad = degrees * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * distance;
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.AreEqual(expected.x, actual.x, Tol, "x");
        Assert.AreEqual(expected.y, actual.y, Tol, "y");
        Assert.AreEqual(expected.z, actual.z, Tol, "z");
    }

    // ---- Segment distance ----

    [Test]
    public void CrossingSegments_HaveZeroDistance()
    {
        float d = HitMath.SegmentSegmentDistance(
            new Vector3(-1, 0, 0), new Vector3(1, 0, 0),
            new Vector3(0, -1, 0), new Vector3(0, 1, 0));
        Assert.AreEqual(0f, d, Tol);
    }

    [Test]
    public void SkewSegments_DistanceIsGapBetweenThem()
    {
        // X-axis segment at z = 0, Y-axis segment at z = 2.
        float d = HitMath.SegmentSegmentDistance(
            new Vector3(-1, 0, 0), new Vector3(1, 0, 0),
            new Vector3(0, -1, 2), new Vector3(0, 1, 2));
        Assert.AreEqual(2f, d, Tol);
    }

    [Test]
    public void ParallelSegments_DistanceIsSeparation()
    {
        float d = HitMath.SegmentSegmentDistance(
            new Vector3(0, 0, 0), new Vector3(2, 0, 0),
            new Vector3(1, 0.5f, 0), new Vector3(3, 0.5f, 0));
        Assert.AreEqual(0.5f, d, Tol);
    }

    [Test]
    public void ClosestPointBeyondSegmentEnd_IsClampedToEndpoint()
    {
        // Infinite lines would cross at x = 3, but the first segment stops at x = 1.
        float d = HitMath.SegmentSegmentDistance(
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(3, -1, 0), new Vector3(3, 1, 0));
        Assert.AreEqual(2f, d, Tol);
    }

    [Test]
    public void PointAboveSegment_DistanceIsPerpendicularGap()
    {
        float d = HitMath.SegmentSegmentDistance(
            new Vector3(0, 1, 0), new Vector3(0, 1, 0),
            new Vector3(-1, 0, 0), new Vector3(1, 0, 0));
        Assert.AreEqual(1f, d, Tol);
    }

    [Test]
    public void TwoPoints_DistanceIsStraightLine()
    {
        float d = HitMath.SegmentSegmentDistance(
            Vector3.zero, Vector3.zero, new Vector3(3, 4, 0), new Vector3(3, 4, 0));
        Assert.AreEqual(5f, d, Tol);
    }

    // ---- Single pose ----

    [Test]
    public void BladeWithinReach_TouchesCapsule_AndReportsContact()
    {
        var blade = new BladePose(new Vector3(-1, 0, 0), new Vector3(1, 0, 0));
        var torso = Post(new Vector3(0, 0, 0.25f), 0.2f);

        bool touched = HitMath.BladeTouchesCapsule(blade, 0.06f, torso, out BladeHit hit);

        Assert.IsTrue(touched);
        Assert.AreEqual(0.25f, hit.Distance, Tol);
        Assert.AreEqual(0.26f, hit.Reach, Tol);
        Assert.AreEqual(0.5f, hit.BladeFraction, Tol);
        AssertVector(new Vector3(0, 0, 0), hit.PointOnBlade);
        AssertVector(new Vector3(0, 0, 0.25f), hit.PointOnCapsule);
    }

    [Test]
    public void BladeJustOutOfReach_Misses()
    {
        var blade = new BladePose(new Vector3(-1, 0, 0), new Vector3(1, 0, 0));
        var torso = Post(new Vector3(0, 0, 0.27f), 0.2f);

        Assert.IsFalse(HitMath.BladeTouchesCapsule(blade, 0.06f, torso, out _));
    }

    [Test]
    public void BladeExactlyAtReach_CountsAsHit()
    {
        // Gap 0.5, reach 0.25 + 0.25 = 0.5. All values are exact in float.
        var blade = new BladePose(new Vector3(-1, 0, 0.5f), new Vector3(1, 0, 0.5f));
        var post = Post(Vector3.zero, 0.25f);

        Assert.IsTrue(HitMath.BladeTouchesCapsule(blade, 0.25f, post, out BladeHit hit));
        Assert.AreEqual(hit.Reach, hit.Distance, Tol);
    }

    [Test]
    public void BladeOverCapsuleEnd_HitsTheRoundedCap()
    {
        // Capsule from y = 0 to y = 1, radius 0.2. Its top is a half-sphere
        // reaching y = 1.2, so a blade at y = 1.15 touches it and one at 1.25 doesn't.
        var capsule = new BodyCapsule(Vector3.zero, Vector3.up, 0.2f);
        var low = new BladePose(new Vector3(-1, 1.15f, 0), new Vector3(1, 1.15f, 0));
        var high = new BladePose(new Vector3(-1, 1.25f, 0), new Vector3(1, 1.25f, 0));

        Assert.IsTrue(HitMath.BladeTouchesCapsule(low, 0f, capsule, out BladeHit hit));
        AssertVector(Vector3.up, hit.PointOnCapsule);
        Assert.IsFalse(HitMath.BladeTouchesCapsule(high, 0f, capsule, out _));
    }

    // ---- Interpolation and step count ----

    [Test]
    public void InterpolatePose_EndsMatchInputs()
    {
        var from = new BladePose(Vector3.zero, Vector3.right);
        var to = new BladePose(Vector3.up, new Vector3(0, 1, 2));

        BladePose start = HitMath.InterpolatePose(from, to, 0f);
        BladePose end = HitMath.InterpolatePose(from, to, 1f);

        AssertVector(from.Hilt, start.Hilt);
        AssertVector(from.Tip, start.Tip);
        AssertVector(to.Hilt, end.Hilt);
        AssertVector(to.Tip, end.Tip);
    }

    [Test]
    public void InterpolatePose_MidSwing_TipStaysOnArc()
    {
        BladePose mid = HitMath.InterpolatePose(QuarterFrom, QuarterTo, 0.5f);

        AssertVector(OnArc(45f, 1f), mid.Tip);
        Assert.AreEqual(1f, (mid.Tip - mid.Hilt).magnitude, Tol);
    }

    [Test]
    public void InterpolatePose_LengthChangesLinearly()
    {
        var shortBlade = new BladePose(Vector3.zero, Vector3.right);
        var longBlade = new BladePose(Vector3.zero, Vector3.right * 2f);

        BladePose mid = HitMath.InterpolatePose(shortBlade, longBlade, 0.5f);

        AssertVector(Vector3.right * 1.5f, mid.Tip);
    }

    [Test]
    public void SubStepCount_StationaryBlade_IsOne()
    {
        Assert.AreEqual(1, HitMath.SubStepCount(QuarterFrom, QuarterFrom, 0.1f));
    }

    [Test]
    public void SubStepCount_QuarterTurn_NoStepMovesTipTooFar()
    {
        const float maxStep = 0.1f;
        int steps = HitMath.SubStepCount(QuarterFrom, QuarterTo, maxStep);

        Vector3 lastTip = QuarterFrom.Tip;
        for (int i = 1; i <= steps; i++)
        {
            Vector3 tip = HitMath.InterpolatePose(QuarterFrom, QuarterTo, (float)i / steps).Tip;
            Assert.LessOrEqual(Vector3.Distance(lastTip, tip), maxStep + Tol, $"step {i} of {steps}");
            lastTip = tip;
        }
    }

    [Test]
    public void SubStepCount_IsCappedAtMaxSteps()
    {
        Assert.AreEqual(HitMath.DefaultMaxSubSteps, HitMath.SubStepCount(100f, 0.01f));
        Assert.AreEqual(10, HitMath.SubStepCount(100f, 0.01f, maxSteps: 10));
    }

    [Test]
    public void SubStepCount_BadInputs_StayInRange()
    {
        Assert.AreEqual(32, HitMath.SubStepCount(1f, 0f, maxSteps: 32), "no step limit uses the cap");
        Assert.AreEqual(32, HitMath.SubStepCount(1f, float.NaN, maxSteps: 32), "NaN step uses the cap");
        Assert.AreEqual(1, HitMath.SubStepCount(1f, 0.1f, maxSteps: 0), "cap is at least 1");
        Assert.AreEqual(32, HitMath.SubStepCount(1f, 1e-30f, maxSteps: 32), "huge ratio uses the cap");
        Assert.AreEqual(32, HitMath.SubStepCount(float.NaN, 0.1f, maxSteps: 32), "NaN travel uses the cap");
    }

    // ---- Sweeping one capsule ----

    [Test]
    public void FastSwing_HitsTargetBetweenFrames()
    {
        // Target at 45 degrees: neither frame's pose touches it, only the sub-steps can.
        BodyCapsule target = Post(OnArc(45f, 0.8f), 0.1f);

        Assert.IsFalse(HitMath.BladeTouchesCapsule(QuarterFrom, 0f, target, out _));
        Assert.IsFalse(HitMath.BladeTouchesCapsule(QuarterTo, 0f, target, out _));

        bool hit = HitMath.SweepBladeVsCapsule(QuarterFrom, QuarterTo, 0f, target, target,
                                               0.1f, includeStart: true, out BladeHit info);

        Assert.IsTrue(hit);
        Assert.That(info.SweepFraction, Is.InRange(0.35f, 0.5f));
    }

    [Test]
    public void FastSwing_TipFollowsArc_NotStraightChord()
    {
        // Target near the tip at 45 degrees. A straight line between the two
        // tip positions passes 0.71 m from the hilt and would miss it; the
        // tip's real arc (radius 1 m) goes through it.
        BodyCapsule target = Post(OnArc(45f, 0.95f), 0.05f);

        Assert.IsTrue(HitMath.SweepBladeVsCapsule(QuarterFrom, QuarterTo, 0f, target, target,
                                                  0.05f, includeStart: true, out _));
    }

    [Test]
    public void Sweep_ReportsFirstContact()
    {
        // Blade slides along +z from z = -1 to z = 1 (40 steps of 5 cm) past a
        // sphere at the origin with radius 0.12. Step 17 is at z = -0.15 (miss),
        // step 18 at z = -0.10 (hit), so first contact is at fraction 0.45.
        var from = new BladePose(new Vector3(-0.5f, 0, -1), new Vector3(0.5f, 0, -1));
        var to = new BladePose(new Vector3(-0.5f, 0, 1), new Vector3(0.5f, 0, 1));
        var sphere = new BodyCapsule(Vector3.zero, Vector3.zero, 0.12f);

        bool hit = HitMath.SweepBladeVsCapsule(from, to, 0f, sphere, sphere,
                                               0.05f, includeStart: true, out BladeHit info);

        Assert.IsTrue(hit);
        Assert.AreEqual(0.45f, info.SweepFraction, Tol);
        Assert.AreEqual(0.1f, info.Distance, Tol);
        AssertVector(new Vector3(0, 0, -0.1f), info.PointOnBlade);
        AssertVector(Vector3.zero, info.PointOnCapsule);
        AssertVector(new Vector3(0.5f, 0, -0.1f), info.Pose.Tip);
    }

    [Test]
    public void Sweep_ExcludingStart_SkipsPreviousPose()
    {
        // Only the starting pose touches the target. When the start was
        // already checked last frame it must not count again.
        var from = new BladePose(new Vector3(0, 0, 0), new Vector3(1, 0, 0));
        var to = new BladePose(new Vector3(0, 1, 0), new Vector3(1, 1, 0));
        var sphere = new BodyCapsule(new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0, 0), 0.1f);

        Assert.IsTrue(HitMath.SweepBladeVsCapsule(from, to, 0f, sphere, sphere, 0.5f, includeStart: true, out _));
        Assert.IsFalse(HitMath.SweepBladeVsCapsule(from, to, 0f, sphere, sphere, 0.5f, includeStart: false, out _));
    }

    [Test]
    public void Sweep_ExcludingStart_StillChecksFinalPose()
    {
        // Only the final pose touches the target, so back-to-back frames leave no gap.
        var from = new BladePose(new Vector3(0, 1, 0), new Vector3(1, 1, 0));
        var to = new BladePose(new Vector3(0, 0, 0), new Vector3(1, 0, 0));
        var sphere = new BodyCapsule(new Vector3(0.5f, 0, 0), new Vector3(0.5f, 0, 0), 0.1f);

        bool hit = HitMath.SweepBladeVsCapsule(from, to, 0f, sphere, sphere,
                                               0.5f, includeStart: false, out BladeHit info);

        Assert.IsTrue(hit);
        Assert.AreEqual(1f, info.SweepFraction, Tol);
    }

    [Test]
    public void Sweep_MissesTargetOutOfPath()
    {
        BodyCapsule behind = Post(new Vector3(-1, 0, -1), 0.2f);

        Assert.IsFalse(HitMath.SweepBladeVsCapsule(QuarterFrom, QuarterTo, 0.05f, behind, behind,
                                                   0.1f, includeStart: true, out _));
    }

    [Test]
    public void Sweep_TargetBackingOutOfReach_IsMissed()
    {
        // Same swing as FastSwing_HitsTargetBetweenFrames, but the target steps
        // back from 0.8 m to 3 m during the frame, so by the time the blade
        // reaches 45 degrees it is beyond the tip.
        BodyCapsule before = Post(OnArc(45f, 0.8f), 0.1f);
        BodyCapsule after = Post(OnArc(45f, 3f), 0.1f);

        Assert.IsTrue(HitMath.SweepBladeVsCapsule(QuarterFrom, QuarterTo, 0f, before, before,
                                                  0.1f, includeStart: true, out _), "standing still: hit");
        Assert.IsFalse(HitMath.SweepBladeVsCapsule(QuarterFrom, QuarterTo, 0f, before, after,
                                                   0.1f, includeStart: true, out _), "backing away: miss");
    }

    [Test]
    public void FastTarget_PassingStillBlade_IsCaught()
    {
        // The blade doesn't move; a small sphere crosses it in one frame. Only
        // the target's own movement makes the sweep take sub-steps, so this
        // fails if the step count ignores target movement.
        var blade = new BladePose(new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0));
        var before = new BodyCapsule(new Vector3(0, 0, -1), new Vector3(0, 0, -1), 0.05f);
        var after = new BodyCapsule(new Vector3(0, 0, 1), new Vector3(0, 0, 1), 0.05f);

        Assert.IsFalse(HitMath.BladeTouchesCapsule(blade, 0f, before, out _));
        Assert.IsFalse(HitMath.BladeTouchesCapsule(blade, 0f, after, out _));
        Assert.IsTrue(HitMath.SweepBladeVsCapsule(blade, blade, 0f, before, after,
                                                  0.05f, includeStart: true, out _));
    }

    [Test]
    public void ZeroLengthBlade_SweepsLikeAMovingPoint()
    {
        var from = new BladePose(new Vector3(-1, 0, 0), new Vector3(-1, 0, 0));
        var to = new BladePose(new Vector3(1, 0, 0), new Vector3(1, 0, 0));
        var sphere = new BodyCapsule(Vector3.zero, Vector3.zero, 0.1f);

        Assert.IsTrue(HitMath.SweepBladeVsCapsule(from, to, 0f, sphere, sphere,
                                                  0.05f, includeStart: true, out _));
    }

    // ---- Sweeping a whole body ----

    [Test]
    public void Body_EarliestPartInSwingWins()
    {
        // Listed first but reached later (60 degrees), versus listed second
        // and reached earlier (30 degrees). The earlier one must win.
        BodyCapsule[] body = { Post(OnArc(60f, 0.8f), 0.1f), Post(OnArc(30f, 0.8f), 0.1f) };

        bool hit = HitMath.SweepBladeVsBody(QuarterFrom, QuarterTo, 0f, body, body,
                                            0.1f, includeStart: true, out BladeHit info);

        Assert.IsTrue(hit);
        Assert.AreEqual(1, info.CapsuleIndex);
        Assert.Less(info.SweepFraction, 0.5f);
    }

    [Test]
    public void Body_NoPartInPath_Misses()
    {
        BodyCapsule[] body = { Post(new Vector3(-1, 0, -1), 0.2f), Post(new Vector3(0, 0, -2), 0.2f) };

        Assert.IsFalse(HitMath.SweepBladeVsBody(QuarterFrom, QuarterTo, 0f, body, body,
                                                0.1f, includeStart: true, out _));
    }

    [Test]
    public void Body_FastMovingPart_IsCaught()
    {
        // Still blade; part 0 stays far away, part 1 crosses the blade in one frame.
        var blade = new BladePose(new Vector3(-0.5f, 0, 0), new Vector3(0.5f, 0, 0));
        BodyCapsule far = Post(new Vector3(0, 0, 5), 0.2f);
        BodyCapsule[] before = { far, new BodyCapsule(new Vector3(0, 0, -1), new Vector3(0, 0, -1), 0.05f) };
        BodyCapsule[] after = { far, new BodyCapsule(new Vector3(0, 0, 1), new Vector3(0, 0, 1), 0.05f) };

        bool hit = HitMath.SweepBladeVsBody(blade, blade, 0f, before, after,
                                            0.05f, includeStart: true, out BladeHit info);

        Assert.IsTrue(hit);
        Assert.AreEqual(1, info.CapsuleIndex);
    }

    [Test]
    public void Body_SameStepTie_DeepestPartWins()
    {
        // Both parts touch the still blade at once. The thin arm's centre line
        // is closer (0.04 vs 0.1), but the blade is far deeper into the torso
        // (0.2 inside vs 0.01), so the torso wins.
        var blade = new BladePose(new Vector3(-1, 0, 0), new Vector3(1, 0, 0));
        BodyCapsule arm = Post(new Vector3(-0.5f, 0, 0.04f), 0.05f);
        BodyCapsule torso = Post(new Vector3(0.5f, 0, 0.1f), 0.3f);

        // Both orders, so list position can't decide it.
        BodyCapsule[] armFirst = { arm, torso };
        BodyCapsule[] torsoFirst = { torso, arm };

        Assert.IsTrue(HitMath.SweepBladeVsBody(blade, blade, 0f, armFirst, armFirst,
                                               0.05f, includeStart: true, out BladeHit a));
        Assert.AreEqual(1, a.CapsuleIndex);

        Assert.IsTrue(HitMath.SweepBladeVsBody(blade, blade, 0f, torsoFirst, torsoFirst,
                                               0.05f, includeStart: true, out BladeHit b));
        Assert.AreEqual(0, b.CapsuleIndex);
    }

    [Test]
    public void Body_NullPartList_Throws()
    {
        BodyCapsule[] body = { Post(Vector3.zero, 0.1f) };

        Assert.Throws<ArgumentNullException>(() =>
            HitMath.SweepBladeVsBody(QuarterFrom, QuarterTo, 0f, null, body, 0.1f, includeStart: true, out _));
        Assert.Throws<ArgumentNullException>(() =>
            HitMath.SweepBladeVsBody(QuarterFrom, QuarterTo, 0f, body, null, 0.1f, includeStart: true, out _));
    }

    [Test]
    public void Body_MismatchedPartLists_Throw()
    {
        BodyCapsule[] one = { Post(Vector3.zero, 0.1f) };
        BodyCapsule[] two = { Post(Vector3.zero, 0.1f), Post(Vector3.one, 0.1f) };

        Assert.Throws<ArgumentException>(() =>
            HitMath.SweepBladeVsBody(QuarterFrom, QuarterTo, 0f, one, two, 0.1f, includeStart: true, out _));
    }
}
