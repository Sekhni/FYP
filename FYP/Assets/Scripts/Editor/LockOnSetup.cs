using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// "Tools > Blade and Reflex > Set Up Lock-On" prepares everything lock-on
/// needs. Safe to run again: each step is skipped if it is already done.
///
/// 1. Strafe animations: makes the walk-back and strafe-walk clips loop, and
///    replaces the Locomotion state's 1D blend tree (driven by Speed) with a
///    2D one driven by MoveX / MoveZ, with each clip placed at the speed and
///    direction it actually travels (measured from the clips).
/// 2. Camera: replaces ThirdPersonCamera on the Main Camera with a Cinemachine
///    Brain, and builds a "Camera Rig" with a LockOnCamera, a pivot, a free
///    camera and a lock-on camera.
///
/// This file must stay inside a folder called "Editor".
/// </summary>
public static class LockOnSetup
{
    private const string ControllerPath = "Assets/Animation/Player/PlayerAnimator.controller";
    private const string BlendTreeName = "Locomotion 2D";

    // Clip, and the ground velocity it travels at (x = sideways, y = forwards, m/s).
    private static readonly (string path, Vector2 velocity)[] LocomotionClips =
    {
        ("Assets/Animation/Player/Idle.anim",                  new Vector2(0f, 0f)),
        ("Assets/Animation/Player/Walk.anim",                  new Vector2(0f, 1.43f)),
        ("Assets/Animation/Player/Run.anim",                   new Vector2(0f, 3.95f)),
        ("Assets/Animation/sword and shield walk (2).fbx",     new Vector2(0f, -1.09f)),   // walk backward
        ("Assets/Animation/sword and shield strafe.fbx",       new Vector2(1.19f, 0f)),    // strafe right
        ("Assets/Animation/sword and shield strafe (2).fbx",   new Vector2(-1.05f, 0f)),   // strafe left
    };

    [MenuItem("Tools/Blade and Reflex/Set Up Lock-On")]
    private static void SetUpLockOn()
    {
        PlayerController player = Object.FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            Debug.LogWarning("Set Up Lock-On: no PlayerController found in the scene.");
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Set Up Lock-On");
        int undoGroup = Undo.GetCurrentGroup();

        SetUpLocomotion();
        SetUpCamera(player);

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(player.gameObject.scene);
        Debug.Log("Lock-on set up. Save the scene to keep it. In Play Mode press Tab to lock on.");
    }

    // ------------------------------------------------------------ Animation

    private static void SetUpLocomotion()
    {
        // The FBX clips must loop to be used in a blend tree.
        foreach (var (path, _) in LocomotionClips)
            if (path.EndsWith(".fbx"))
                MakeFbxClipLoop(path);

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null)
        {
            Debug.LogWarning($"Set Up Lock-On: no Animator Controller at {ControllerPath}.");
            return;
        }

        AddFloatParameter(controller, "MoveX");
        AddFloatParameter(controller, "MoveZ");

        AnimatorState locomotion = FindState(controller.layers[0].stateMachine, "Locomotion");
        if (locomotion == null)
        {
            Debug.LogWarning("Set Up Lock-On: no 'Locomotion' state in the Animator Controller.");
            return;
        }

        if (locomotion.motion is BlendTree existing && existing.name == BlendTreeName)
            return;   // already done

        Undo.RecordObject(locomotion, "Set Up Lock-On");
        var tree = new BlendTree
        {
            name = BlendTreeName,
            blendType = BlendTreeType.FreeformDirectional2D,
            blendParameter = "MoveX",
            blendParameterY = "MoveZ",
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy
        };
        AssetDatabase.AddObjectToAsset(tree, controller);
        foreach (var (path, velocity) in LocomotionClips)
        {
            AnimationClip clip = LoadClip(path);
            if (clip == null)
            {
                Debug.LogWarning($"Set Up Lock-On: no animation clip in {path}; it was left out of the blend tree.");
                continue;
            }
            tree.AddChild(clip, velocity);
        }

        // Swap in the new tree; drop the old one and the reverse-playback trick
        // (the walk-back clip replaces playing the walk backwards).
        Motion old = locomotion.motion;
        locomotion.motion = tree;
        locomotion.speed = 1f;
        locomotion.speedParameterActive = false;
        if (old is BlendTree oldTree && AssetDatabase.GetAssetPath(oldTree) == ControllerPath)
            Object.DestroyImmediate(oldTree, true);
        RemoveParameter(controller, "MoveDirection");

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
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

    private static void AddFloatParameter(AnimatorController controller, string paramName)
    {
        foreach (AnimatorControllerParameter p in controller.parameters)
            if (p.name == paramName)
                return;
        controller.AddParameter(paramName, AnimatorControllerParameterType.Float);
    }

    private static void RemoveParameter(AnimatorController controller, string paramName)
    {
        AnimatorControllerParameter[] parameters = controller.parameters;
        for (int i = 0; i < parameters.Length; i++)
            if (parameters[i].name == paramName)
            {
                controller.RemoveParameter(i);
                return;
            }
    }

    // ------------------------------------------------------------ Camera

    private static void SetUpCamera(PlayerController player)
    {
        if (Object.FindAnyObjectByType<LockOnCamera>(FindObjectsInactive.Include) != null)
            return;   // already done

        Camera mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogWarning("Set Up Lock-On: no camera tagged MainCamera in the scene.");
            return;
        }

        // ---- Main Camera: the Brain replaces ThirdPersonCamera ----
        foreach (MonoBehaviour mb in mainCamera.GetComponents<MonoBehaviour>())
            if (mb != null && mb.GetType().Name == "ThirdPersonCamera")
                Undo.DestroyObjectImmediate(mb);

        CinemachineBrain brain = mainCamera.GetComponent<CinemachineBrain>();
        if (brain == null)
            brain = Undo.AddComponent<CinemachineBrain>(mainCamera.gameObject);
        brain.DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Styles.EaseInOut, 0.35f);

        // The cameras ignore the player's own collider when avoiding walls.
        if (player.CompareTag("Untagged"))
        {
            Undo.RecordObject(player.gameObject, "Set Up Lock-On");
            player.gameObject.tag = "Player";
        }

        // ---- Rig ----
        var rig = new GameObject("Camera Rig");
        Undo.RegisterCreatedObjectUndo(rig, "Set Up Lock-On");
        var pivot = new GameObject("Camera Pivot").transform;
        pivot.SetParent(rig.transform, false);

        CinemachineCamera free = CreateCamera("Free Camera", rig.transform, pivot, mainCamera,
                                              shoulder: new Vector3(0.4f, 0f, 0f), distance: 4f, priority: 10, player.tag);
        CinemachineCamera locked = CreateCamera("Lock-On Camera", rig.transform, pivot, mainCamera,
                                                shoulder: new Vector3(0.6f, 0.2f, 0f), distance: 4.5f, priority: 0, player.tag);

        LockOnCamera lockOn = rig.AddComponent<LockOnCamera>();
        var so = new SerializedObject(lockOn);
        so.FindProperty("player").objectReferenceValue = player;
        so.FindProperty("pivot").objectReferenceValue = pivot;
        so.FindProperty("freeCamera").objectReferenceValue = free;
        so.FindProperty("lockOnCamera").objectReferenceValue = locked;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    private static CinemachineCamera CreateCamera(string cameraName, Transform parent, Transform pivot, Camera mainCamera,
                                                  Vector3 shoulder, float distance, int priority, string ignoreTag)
    {
        var go = new GameObject(cameraName);
        go.transform.SetParent(parent, false);

        var cam = go.AddComponent<CinemachineCamera>();
        cam.Target.TrackingTarget = pivot;
        cam.Priority = priority;
        cam.Lens.FieldOfView = mainCamera.fieldOfView;

        var follow = go.AddComponent<CinemachineThirdPersonFollow>();
        follow.ShoulderOffset = shoulder;
        follow.VerticalArmLength = 0f;
        follow.CameraSide = 1f;
        follow.CameraDistance = distance;
        follow.Damping = new Vector3(0.1f, 0.2f, 0.1f);
        follow.AvoidObstacles.Enabled = true;
        follow.AvoidObstacles.IgnoreTag = ignoreTag;
        follow.AvoidObstacles.CameraRadius = 0.2f;
        return cam;
    }
}
