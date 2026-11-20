using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// "Tools > Blade and Reflex > Set Up Animation Variety" gives the Player
/// several clips for idles, attacks and hit reactions. Safe to run again:
/// each step is skipped if already done. These are asset changes saved
/// straight to disk; Ctrl+Z does not undo them (use version control).
///
/// 1. Idle: the idle point of the Locomotion 2D blend tree becomes a 1D
///    "Idle Variants" tree on IdleVariant (four idle clips);
///    PlayerController drifts between them while standing still.
/// 2. Attacks: three light swings, states Attack_Light_1..3 (AttackLight
///    trigger + AttackIndex 0-2), and the old attack state becomes
///    Attack_Heavy (AttackHeavy trigger). Each light clip gets the
///    ActiveStart / ActiveEnd / AttackEnd Animation Events, placed where the
///    sword tip moves fastest (measured: tip speed above ~15 m/s).
/// 3. Hit reactions: HitReact plays one of two clips (HitVariant) and
///    BlockImpact one of two (BlockHitVariant); CombatStateMachine picks.
///
/// This file must stay inside a folder called "Editor".
/// </summary>
public static class AnimationVarietySetup
{
    private const string ControllerPath = "Assets/Animation/Player/PlayerAnimator.controller";
    private const string Anim = "Assets/Animation/";

    private static readonly string[] IdleClips =
    {
        Anim + "Player/Idle.anim",
        Anim + "Player/Idle 1.anim",
        Anim + "sword and shield idle (2).fbx",
        Anim + "sword and shield idle (3).fbx",
    };

    // Light swing clips and their event times (fraction of the clip):
    // ActiveStart, ActiveEnd, AttackEnd. Measured by sampling each clip on
    // the Paladin in 2% steps and tracking the blade tip (mixamorig:Sword_joint
    // + HitCalculator's tipLocal): ActiveStart/ActiveEnd bracket the frames
    // where the tip moves faster than ~15 m/s (clip played at 1x); AttackEnd is where the swing
    // has settled, chosen by eye (about 0.70).
    private static readonly (string path, float start, float end, float done)[] LightAttacks =
    {
        (Anim + "sword and shield slash.fbx",     0.35f, 0.48f, 0.70f),
        (Anim + "sword and shield slash (5).fbx", 0.35f, 0.50f, 0.70f),
        (Anim + "sword and shield slash (3).fbx", 0.43f, 0.54f, 0.72f),
    };
    private const float LightAttackSpeed = 1.5f;   // playback speed of the light swing states
    private const float HeavyAttackSpeed = 1.2f;   // the heavy swing plays slower than before (1.5)

    private static readonly string[] HitClips =
    {
        Anim + "sword and shield impact (2).fbx",
        Anim + "sword and shield impact (3).fbx",
    };
    private static readonly string[] BlockHitClips =
    {
        Anim + "sword and shield impact.fbx",
        Anim + "sword and shield block (2).fbx",
    };

    [MenuItem("Tools/Blade and Reflex/Set Up Animation Variety")]
    private static void SetUp()
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            Debug.LogWarning($"Set Up Animation Variety: no Animator Controller at {ControllerPath}.");
            return;
        }

        // Clip import settings first (reimports must finish before clips are used).
        // The .anim idles already loop; the FBX idles have to be told to.
        foreach (string idle in IdleClips)
            if (idle.EndsWith(".fbx"))
                MakeFbxClipLoop(idle);
        foreach (var a in LightAttacks)
            AddAttackEvents(a.path, a.start, a.end, a.done);

        AddParameter(controller, "IdleVariant", AnimatorControllerParameterType.Float);
        AddParameter(controller, "HitVariant", AnimatorControllerParameterType.Float);
        AddParameter(controller, "BlockHitVariant", AnimatorControllerParameterType.Float);
        AddParameter(controller, "AttackIndex", AnimatorControllerParameterType.Int);
        AddParameter(controller, "AttackHeavy", AnimatorControllerParameterType.Trigger);

        AnimatorStateMachine sm = controller.layers[0].stateMachine;
        SetUpIdle(controller, sm);
        SetUpAttacks(controller, sm);
        SetUpVariants(controller, FindState(sm, "HitReact"), "Hit Variants", "HitVariant", HitClips);
        SetUpVariants(controller, FindState(sm, "BlockImpact"), "Block Hit Variants", "BlockHitVariant", BlockHitClips);

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        Undo.ClearUndo(controller);
        Debug.Log("Animation variety set up (see any warnings above): idles, three light swings + heavy (hold attack), hit and block reactions. " +
                  "(Asset changes are saved to disk and can't be undone with Ctrl+Z.)");
    }

    // ------------------------------------------------------------ Idle

    private static void SetUpIdle(AnimatorController controller, AnimatorStateMachine sm)
    {
        AnimatorState locomotion = FindState(sm, "Locomotion");
        if (!(locomotion?.motion is BlendTree tree))
        {
            Debug.LogWarning("Set Up Animation Variety: Locomotion has no blend tree. Run Set Up Lock-On first.");
            return;
        }

        ChildMotion[] children = tree.children;
        for (int i = 0; i < children.Length; i++)
        {
            if (children[i].position != Vector2.zero)
                continue;   // the idle sits at zero velocity
            if (children[i].motion is BlendTree existing && existing.name == "Idle Variants")
                return;     // already done

            children[i].motion = CreateTree(controller, "Idle Variants", "IdleVariant", IdleClips);
            tree.children = children;
            EditorUtility.SetDirty(tree);
            return;
        }
        Debug.LogWarning("Set Up Animation Variety: no idle (0, 0) point in the Locomotion blend tree.");
    }

    // ------------------------------------------------------------ Attacks

    private static void SetUpAttacks(AnimatorController controller, AnimatorStateMachine sm)
    {
        // Each step below is safe to repeat: the rename and trigger swap give
        // the same result again, and existing light states are skipped.
        AnimatorState heavy = FindState(sm, "Attack_Light") ?? FindState(sm, "Attack_Heavy");
        AnimatorState locomotion = FindState(sm, "Locomotion");
        if (heavy == null || locomotion == null)
        {
            Debug.LogWarning("Set Up Animation Variety: expected 'Attack_Light' and 'Locomotion' states.");
            return;
        }

        // The old attack becomes the heavy attack.
        heavy.name = "Attack_Heavy";
        heavy.speed = HeavyAttackSpeed;
        foreach (AnimatorStateTransition t in sm.anyStateTransitions)
        {
            if (t.destinationState != heavy)
                continue;
            foreach (AnimatorCondition c in t.conditions)
                t.RemoveCondition(c);
            t.AddCondition(AnimatorConditionMode.If, 0f, "AttackHeavy");
        }

        // Exit timing copied from the heavy state's transition back to Locomotion.
        float exitDuration = 0.2f;
        foreach (AnimatorStateTransition t in heavy.transitions)
            if (t.destinationState == locomotion)
                exitDuration = t.duration;

        for (int i = 0; i < LightAttacks.Length; i++)
        {
            if (FindState(sm, $"Attack_Light_{i + 1}") != null)
                continue;   // already added on an earlier run

            AnimationClip clip = LoadClip(LightAttacks[i].path);
            if (clip == null)
            {
                Debug.LogWarning($"Set Up Animation Variety: no clip in {LightAttacks[i].path}.");
                continue;
            }

            AnimatorState state = sm.AddState($"Attack_Light_{i + 1}", StatePosition(sm, heavy) + new Vector3(220f, (i - 1) * 60f, 0f));
            state.motion = clip;
            state.tag = "Attack";
            state.speed = LightAttackSpeed;

            AnimatorStateTransition enter = sm.AddAnyStateTransition(state);
            enter.AddCondition(AnimatorConditionMode.If, 0f, "AttackLight");
            enter.AddCondition(AnimatorConditionMode.Equals, i, "AttackIndex");
            enter.hasExitTime = false;
            enter.duration = 0.05f;
            enter.canTransitionToSelf = false;

            AnimatorStateTransition exit = state.AddTransition(locomotion);
            exit.hasExitTime = true;
            exit.exitTime = LightAttacks[i].done;
            exit.duration = exitDuration;
        }
    }

    private static void AddAttackEvents(string path, float start, float end, float done)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
            return;

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
            clips = importer.defaultClipAnimations;
        if (clips.Length == 0)
            return;
        foreach (AnimationEvent e in clips[0].events)
            if (e.functionName == "ActiveStart")
                return;   // already done

        var events = new List<AnimationEvent>(clips[0].events)
        {
            new AnimationEvent { functionName = "ActiveStart", time = start },
            new AnimationEvent { functionName = "ActiveEnd", time = end },
            new AnimationEvent { functionName = "AttackEnd", time = done },
        };
        clips[0].events = events.ToArray();   // ModelImporter event times are fractions of the clip
        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    // ------------------------------------------------------------ Reactions

    private static void SetUpVariants(AnimatorController controller, AnimatorState state, string treeName,
                                      string parameter, string[] clipPaths)
    {
        if (state == null)
        {
            Debug.LogWarning($"Set Up Animation Variety: no state for '{treeName}'.");
            return;
        }
        if (state.motion is BlendTree existing && existing.name == treeName)
            return;   // already done

        state.motion = CreateTree(controller, treeName, parameter, clipPaths);
    }

    // ------------------------------------------------------------ Helpers

    // A 1D blend tree on 'parameter' with one clip at each whole number 0, 1, 2...
    private static BlendTree CreateTree(AnimatorController controller, string treeName, string parameter, string[] clipPaths)
    {
        var tree = new BlendTree
        {
            name = treeName,
            blendType = BlendTreeType.Simple1D,
            blendParameter = parameter,
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy
        };
        AssetDatabase.AddObjectToAsset(tree, controller);
        float threshold = 0f;
        foreach (string path in clipPaths)
        {
            AnimationClip clip = LoadClip(path);
            if (clip == null)
            {
                Debug.LogWarning($"Set Up Animation Variety: no clip in {path}; left out of {treeName}.");
                continue;
            }
            tree.AddChild(clip, threshold);
            threshold += 1f;
        }
        return tree;
    }

    private static void MakeFbxClipLoop(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as ModelImporter;
        if (importer == null)
            return;
        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
            clips = importer.defaultClipAnimations;
        if (clips.Length == 0 || clips[0].loopTime)
            return;
        clips[0].loopTime = true;
        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    private static AnimationClip LoadClip(string path)
    {
        foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            if (o is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                return clip;
        return null;
    }

    // Where a state sits in the Animator window (stored on the state machine, not the state).
    private static Vector3 StatePosition(AnimatorStateMachine machine, AnimatorState state)
    {
        foreach (ChildAnimatorState child in machine.states)
            if (child.state == state)
                return child.position;
        return Vector3.zero;
    }

    private static AnimatorState FindState(AnimatorStateMachine machine, string stateName)
    {
        foreach (ChildAnimatorState child in machine.states)
            if (child.state.name == stateName)
                return child.state;
        foreach (ChildAnimatorStateMachine sub in machine.stateMachines)
        {
            AnimatorState found = FindState(sub.stateMachine, stateName);
            if (found != null)
                return found;
        }
        return null;
    }

    private static void AddParameter(AnimatorController controller, string paramName, AnimatorControllerParameterType type)
    {
        foreach (AnimatorControllerParameter p in controller.parameters)
            if (p.name == paramName)
                return;
        controller.AddParameter(paramName, type);
    }
}
