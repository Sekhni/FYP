using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The body of anything a blade can hit, as a set of capsules (head, torso,
/// arms, legs) stored as data and positioned from the bones whenever they
/// are read (GDD section 4). HitCalculator reads these during an attack.
///
/// Each part is a segment between two points given in one bone's local
/// space, plus a radius in metres. Because the points move with the bone,
/// the capsules follow the animation.
///
/// For a humanoid rig, right-click the component and choose
/// "Fill parts from humanoid rig". Parts are drawn in the Scene view when
/// the object is selected.
/// </summary>
public class HitBody : MonoBehaviour
{
    [Serializable]
    public class Part
    {
        public string name = "Part";
        [Tooltip("The bone the part moves with. Empty = this object.")]
        public Transform bone;
        [Tooltip("One end of the capsule's centre line, in the bone's local space.")]
        public Vector3 start;
        [Tooltip("The other end of the capsule's centre line, in the bone's local space.")]
        public Vector3 end;
        [Tooltip("Capsule radius in metres.")]
        [Min(0.01f)]
        public float radius = 0.1f;
    }

    [SerializeField] private List<Part> parts = new List<Part>();

    private static readonly List<HitBody> all = new List<HitBody>();

    private BodyCapsule[] gizmoCapsules;

    /// <summary>Every enabled HitBody in the scene.</summary>
    public static IReadOnlyList<HitBody> All => all;

    /// <summary>The Health this body belongs to (on this object or a parent; may be null).</summary>
    public Health Health { get; private set; }

    /// <summary>Number of body parts (capsules).</summary>
    public int PartCount => parts.Count;

    /// <summary>Radius of the thinnest part: the largest step a sweep can take without skipping it.</summary>
    public float MinRadius
    {
        get
        {
            float min = float.MaxValue;
            foreach (Part p in parts)
                min = Mathf.Min(min, p.radius);
            return parts.Count > 0 ? min : 0f;
        }
    }

    private void Awake()
    {
        Health = GetComponentInParent<Health>();
    }

    private void OnEnable()
    {
        all.Add(this);
    }

    private void OnDisable()
    {
        all.Remove(this);
    }

    /// <summary>Name of a body part, for logs and debug views.</summary>
    public string PartName(int index)
    {
        return parts[index].name;
    }

    /// <summary>
    /// Writes every part's capsule, in world space, into 'into' (which must
    /// hold at least PartCount items). Same order as the parts list. A part
    /// whose bone is empty moves with this object.
    /// </summary>
    public void GetCapsules(BodyCapsule[] into)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            Part p = parts[i];
            Transform bone = p.bone != null ? p.bone : transform;
            into[i] = new BodyCapsule(bone.TransformPoint(p.start), bone.TransformPoint(p.end), p.radius);
        }
    }

    // ------------------------------------------------------------ Setup

    public void ClearParts()
    {
        parts.Clear();
    }

    /// <summary>Adds a part between two points given in 'bone' space.</summary>
    public void AddPart(string partName, Transform bone, Vector3 start, Vector3 end, float radius)
    {
        parts.Add(new Part { name = partName, bone = bone, start = start, end = end, radius = radius });
    }

    /// <summary>
    /// Replaces the parts with a default humanoid body (15 capsules) read from
    /// the Animator's humanoid bones in their current pose. Radii suit an
    /// armoured adult; tune them in the Inspector. Returns false if there is
    /// no humanoid Animator.
    /// </summary>
    public bool FillFromHumanoid()
    {
        Animator animator = GetComponentInChildren<Animator>();
        if (animator == null || !animator.isHuman)
            return false;

        parts.Clear();
        Transform Bone(HumanBodyBones id) => animator.GetBoneTransform(id);

        // Chest, UpperChest, Neck, fingers and toes are optional in a humanoid
        // avatar, so fall back to the nearest bone that every rig has.
        Transform chest = First(Bone(HumanBodyBones.Chest), Bone(HumanBodyBones.UpperChest), Bone(HumanBodyBones.Spine));
        Transform neck = First(Bone(HumanBodyBones.Neck), Bone(HumanBodyBones.Head));

        AddBetween("Head", Bone(HumanBodyBones.Head), null, 0.11f, Vector3.up * 0.18f);
        AddBetween("Upper torso", chest, neck, 0.17f);
        AddBetween("Lower torso", Bone(HumanBodyBones.Hips), chest, 0.16f);
        AddLimb("Left", HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                HumanBodyBones.LeftMiddleProximal, animator);
        AddLimb("Right", HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.RightMiddleProximal, animator);
        AddLeg("Left", HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
               HumanBodyBones.LeftToes, animator);
        AddLeg("Right", HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
               HumanBodyBones.RightToes, animator);
        return true;
    }

    [ContextMenu("Fill parts from humanoid rig")]
    private void FillFromHumanoidMenu()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Fill HitBody parts");
#endif
        if (!FillFromHumanoid())
            Debug.LogWarning($"HitBody on {name}: no humanoid Animator found, parts left unchanged.");
#if UNITY_EDITOR
        UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    private void AddLimb(string side, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand,
                         HumanBodyBones finger, Animator animator)
    {
        Transform u = animator.GetBoneTransform(upper);
        Transform l = animator.GetBoneTransform(lower);
        Transform h = animator.GetBoneTransform(hand);
        AddBetween(side + " upper arm", u, l, 0.07f);
        AddBetween(side + " forearm", l, h, 0.06f);
        // No finger bone: reach 10 cm on along the forearm's direction.
        Vector3 along = h != null && l != null ? (h.position - l.position).normalized * 0.1f : Vector3.zero;
        AddBetween(side + " hand", h, animator.GetBoneTransform(finger), 0.05f, along);
    }

    private void AddLeg(string side, HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones foot,
                        HumanBodyBones toes, Animator animator)
    {
        Transform u = animator.GetBoneTransform(upper);
        Transform l = animator.GetBoneTransform(lower);
        Transform f = animator.GetBoneTransform(foot);
        AddBetween(side + " thigh", u, l, 0.09f);
        AddBetween(side + " shin", l, f, 0.07f);
        // No toe bone: reach 12 cm forward from the ankle.
        AddBetween(side + " foot", f, animator.GetBoneTransform(toes), 0.05f, transform.forward * 0.12f);
    }

    private static Transform First(params Transform[] bones)
    {
        foreach (Transform t in bones)
            if (t != null)
                return t;
        return null;
    }

    // A part on 'from' reaching to 'to' (or, if 'to' is missing, to 'fallbackOffset'
    // from 'from' in world space). Skipped if 'from' is missing.
    private void AddBetween(string partName, Transform from, Transform to, float radius,
                            Vector3 fallbackOffset = default)
    {
        if (from == null)
        {
            Debug.LogWarning($"HitBody on {name}: rig has no bone for '{partName}', part skipped.");
            return;
        }

        Vector3 endWorld = to != null ? to.position : from.position + fallbackOffset;
        AddPart(partName, from, Vector3.zero, from.InverseTransformPoint(endWorld), radius);
    }

    // ------------------------------------------------------------ Debug drawing

    private void OnDrawGizmosSelected()
    {
        if (parts.Count == 0)
            return;

        if (gizmoCapsules == null || gizmoCapsules.Length != parts.Count)
            gizmoCapsules = new BodyCapsule[parts.Count];
        GetCapsules(gizmoCapsules);
        Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.8f);
        foreach (BodyCapsule c in gizmoCapsules)
            DrawCapsuleGizmo(c);
    }

    /// <summary>Draws a capsule as two wire spheres joined by four lines.</summary>
    public static void DrawCapsuleGizmo(BodyCapsule c)
    {
        Gizmos.DrawWireSphere(c.A, c.Radius);
        Gizmos.DrawWireSphere(c.B, c.Radius);

        Vector3 axis = c.B - c.A;
        if (axis.sqrMagnitude < 1e-8f)
            return;
        Vector3 side = Vector3.Cross(axis, Mathf.Abs(axis.normalized.y) < 0.9f ? Vector3.up : Vector3.right).normalized * c.Radius;
        Vector3 side2 = Vector3.Cross(axis.normalized, side);
        Gizmos.DrawLine(c.A + side, c.B + side);
        Gizmos.DrawLine(c.A - side, c.B - side);
        Gizmos.DrawLine(c.A + side2, c.B + side2);
        Gizmos.DrawLine(c.A - side2, c.B - side2);
    }
}
