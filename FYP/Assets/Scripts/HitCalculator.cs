using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Physics-free blade hit detection for Blade & Reflex (GDD section 4).
///
/// The blade is a segment between two points fixed in the sword bone's local
/// space (the GDD describes two bones; one bone with two offsets is the same
/// thing for a rigid sword). Every frame of a swing's active window, in
/// LateUpdate (after animation has posed every skeleton), this sweeps the
/// blade with HitMath from last frame's pose to this frame's against the
/// capsules of every other HitBody, which move over the same frame. Each
/// target can be hit once per swing; when it is, TargetHit fires and
/// CombatStateMachine applies the damage.
///
/// The window is driven by CombatStateMachine.StateChanged (from the
/// ActiveStart / ActiveEnd Animation Events). Those events fire part-way
/// through a frame, so the frame the window opens is swept from the previous
/// frame's blade pose, and the frame it closes is still swept once. A window
/// shorter than one frame therefore still gets one sweep. A swing cut short
/// by a stun stops at once.
///
/// No colliders, rigidbodies, triggers or physics queries are involved.
/// With drawDebug on, the Scene view shows the blade, the in-between blade
/// poses of the last frame, and the last hit (body part, closest points).
/// </summary>
[RequireComponent(typeof(CombatStateMachine))]
[DefaultExecutionOrder(1000)]   // read bones after any other LateUpdate adjusts them
public class HitCalculator : MonoBehaviour
{
    [Header("Blade (read from the sword bone every frame)")]
    [Tooltip("The bone the sword is attached to (mixamorig:Sword_joint on the Paladin rig; " +
             "Tools > Blade and Reflex > Set Up Hit Detection fills it in).")]
    [SerializeField] private Transform bladeBone;
    [Tooltip("Where the blade starts (just above the cross-guard), in the sword bone's local space. " +
             "Default measured from the Paladin sword mesh.")]
    [SerializeField] private Vector3 hiltLocal = new Vector3(0.027f, -0.009f, 0.104f);
    [Tooltip("The blade tip, in the sword bone's local space. Default measured from the Paladin sword mesh.")]
    [SerializeField] private Vector3 tipLocal = new Vector3(0.207f, -0.052f, 0.811f);
    [Tooltip("Half the blade's thickness, in metres.")]
    [Min(0f)]
    [SerializeField] private float bladeRadius = 0.03f;

    [Header("Debug")]
    [Tooltip("Draw the blade, last frame's sweep and the last hit in the Scene view.")]
    [SerializeField] private bool drawDebug = true;
    [Tooltip("Log every hit with its body part, timing and distances.")]
    [SerializeField] private bool logHits = true;

    /// <summary>A target was hit for the first time this swing.</summary>
    public event Action<HitBody, BladeHit> TargetHit;

    // Last frame's and this frame's capsules for one target.
    private class TargetState
    {
        public BodyCapsule[] Previous;
        public BodyCapsule[] Current;
        public int Swing = -1;   // swing the capsules belong to
        public int Frame = -1;   // frame they were read on
    }

    private CombatStateMachine combat;
    private readonly Dictionary<HitBody, TargetState> targets = new Dictionary<HitBody, TargetState>();
    private readonly HashSet<HitBody> hitThisSwing = new HashSet<HitBody>();
    private readonly List<HitBody> bodyBuffer = new List<HitBody>();
    private readonly List<HitBody> deadKeys = new List<HitBody>();

    // Swing window, set from StateChanged and acted on in LateUpdate.
    private bool swingStarting;
    private bool swingActive;
    private bool swingFinishing;
    private int swingId;
    private CombatState previousState = CombatState.Idle;

    // The blade as of the end of the previous frame.
    private BladePose lastBlade;
    private int lastBladeFrame = -1;

    // Kept for the debug view.
    private BladePose debugFrom, debugTo;
    private int debugSteps;
    private bool hasDebugSweep;
    private bool hasLastHit;
    private BladeHit lastHit;
    private BodyCapsule lastHitCapsule;

    public bool HasBlade => bladeBone != null;

    private void Awake()
    {
        combat = GetComponent<CombatStateMachine>();
        combat.StateChanged += OnStateChanged;
        if (bladeBone == null)
            Debug.LogWarning($"HitCalculator on {name}: no blade bone set, so attacks can't hit anything.");
    }

    private void OnDestroy()
    {
        if (combat != null)
            combat.StateChanged -= OnStateChanged;
    }

    private void OnDisable()
    {
        swingStarting = swingActive = swingFinishing = false;
        lastBladeFrame = -1;
    }

    private void OnStateChanged(CombatState next)
    {
        CombatState previous = previousState;
        previousState = next;

        if (next == CombatState.Active)
        {
            swingStarting = true;
            swingFinishing = false;
        }
        else if (next == CombatState.Recovery && (swingActive || swingStarting))
        {
            // Normal end: the event fired part-way through this frame, so the
            // frame is still swept, then the swing stops.
            swingFinishing = true;
        }
        else if (next == CombatState.Idle && previous == CombatState.Recovery)
        {
            // Normal Idle after Recovery (possibly in the same frame as
            // ActiveEnd): leave a pending final sweep alone.
        }
        else
        {
            // Stunned, or the fail-safe forcing Idle: the swing is cut off now.
            swingStarting = swingActive = swingFinishing = false;
        }
    }

    private void LateUpdate()
    {
        if (bladeBone == null)
            return;

        BladePose blade = ReadBlade();
        bool haveLastFrame = lastBladeFrame == Time.frameCount - 1;

        if (swingStarting)
        {
            swingStarting = false;
            BeginSwing();
            // Sweep this first frame from where the blade was last frame. That
            // pose is from just before the window opened, so it isn't tested
            // on its own.
            Sweep(haveLastFrame ? lastBlade : blade, blade, startPoseInWindow: !haveLastFrame);
        }
        else if (swingActive)
        {
            Sweep(haveLastFrame ? lastBlade : blade, blade, startPoseInWindow: true);
        }

        if (swingFinishing)
        {
            swingFinishing = false;
            swingActive = false;
        }

        lastBlade = blade;
        lastBladeFrame = Time.frameCount;
    }

    private void BeginSwing()
    {
        swingActive = true;
        swingId++;
        hitThisSwing.Clear();
        hasLastHit = false;
        hasDebugSweep = false;

        // Forget targets that have been destroyed.
        deadKeys.Clear();
        foreach (HitBody key in targets.Keys)
            if (key == null)
                deadKeys.Add(key);
        foreach (HitBody key in deadKeys)
            targets.Remove(key);
    }

    private void Sweep(BladePose from, BladePose to, bool startPoseInWindow)
    {
        debugFrom = from;
        debugTo = to;
        debugSteps = 1;
        hasDebugSweep = true;

        // Copy the list: a TargetHit handler might disable a body mid-loop.
        bodyBuffer.Clear();
        bodyBuffer.AddRange(HitBody.All);

        foreach (HitBody body in bodyBuffer)
        {
            if (body == null || body.transform.IsChildOf(transform) || body.PartCount == 0
                || hitThisSwing.Contains(body) || (body.Health != null && body.Health.IsDead))
                continue;

            TargetState state = GetState(body);
            body.GetCapsules(state.Current);

            // Last frame's capsules only count if they were read last frame,
            // in this swing. Otherwise the target is treated as standing still.
            bool hasPrevious = state.Swing == swingId && state.Frame == Time.frameCount - 1;
            BodyCapsule[] fromBody = hasPrevious ? state.Previous : state.Current;

            // Test the starting pose only if it belongs to the window and
            // wasn't already tested against this target last frame.
            bool includeStart = startPoseInWindow && !hasPrevious;

            float maxStep = body.MinRadius;
            debugSteps = Mathf.Max(debugSteps, HitMath.SubStepCount(from, to, maxStep));

            if (HitMath.SweepBladeVsBody(from, to, bladeRadius, fromBody, state.Current,
                                         maxStep, includeStart, out BladeHit hit))
            {
                hitThisSwing.Add(body);
                hasLastHit = true;
                lastHit = hit;
                lastHitCapsule = HitMath.InterpolateCapsule(fromBody[hit.CapsuleIndex],
                                                            state.Current[hit.CapsuleIndex], hit.SweepFraction);

                if (logHits)
                    Debug.Log($"[Hit] {name} blade hit {body.name} ({body.PartName(hit.CapsuleIndex)}) " +
                              $"at {hit.SweepFraction:0.00} through the frame, {hit.BladeFraction:0.00} along the blade, " +
                              $"gap {hit.Distance:0.000} <= reach {hit.Reach:0.000}");

                TargetHit?.Invoke(body, hit);
            }

            // A TargetHit handler may have cut this swing off (e.g. a parry
            // stunning the attacker): no more hits this frame.
            bool cutOff = !swingActive;

            // Swap so this frame's capsules become next frame's previous ones.
            BodyCapsule[] swap = state.Previous;
            state.Previous = state.Current;
            state.Current = swap;
            state.Swing = swingId;
            state.Frame = Time.frameCount;

            if (cutOff)
                break;
        }
    }

    private BladePose ReadBlade()
    {
        return new BladePose(bladeBone.TransformPoint(hiltLocal), bladeBone.TransformPoint(tipLocal));
    }

    private TargetState GetState(HitBody body)
    {
        if (!targets.TryGetValue(body, out TargetState state) || state.Current.Length != body.PartCount)
        {
            state = new TargetState
            {
                Previous = new BodyCapsule[body.PartCount],
                Current = new BodyCapsule[body.PartCount]
            };
            targets[body] = state;
        }
        return state;
    }

    // ------------------------------------------------------------ Debug drawing

    private void OnDrawGizmos()
    {
        if (!drawDebug || bladeBone == null)
            return;

        // The blade now (white), with its thickness at the tip.
        BladePose blade = ReadBlade();
        Gizmos.color = Color.white;
        Gizmos.DrawLine(blade.Hilt, blade.Tip);
        Gizmos.DrawWireSphere(blade.Tip, bladeRadius);

        if (!Application.isPlaying)
            return;

        // The blade poses tested during the last swept frame of the current
        // or most recent swing (faint yellow). This is the step count for a
        // target standing still; a moving target gets more steps.
        if (hasDebugSweep)
        {
            Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.35f);
            for (int i = 0; i <= debugSteps; i++)
            {
                BladePose p = HitMath.InterpolatePose(debugFrom, debugTo, (float)i / debugSteps);
                Gizmos.DrawLine(p.Hilt, p.Tip);
            }
        }

        // The last hit (kept until the next swing starts): blade at contact, the part it touched, and the closest points.
        if (hasLastHit)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(lastHit.Pose.Hilt, lastHit.Pose.Tip);
            HitBody.DrawCapsuleGizmo(lastHitCapsule);
            Gizmos.DrawSphere(lastHit.PointOnBlade, 0.02f);
            Gizmos.DrawSphere(lastHit.PointOnCapsule, 0.02f);
            Gizmos.DrawLine(lastHit.PointOnBlade, lastHit.PointOnCapsule);
        }
    }
}
