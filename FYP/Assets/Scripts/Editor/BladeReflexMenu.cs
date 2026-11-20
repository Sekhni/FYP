using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Adds two items to the Unity menu under "Tools > Blade and Reflex":
///
/// "Add Training Dummy" builds a dummy 3 m in front of the Player and makes
/// sure the Player has a Health component (with its bar in the top-left
/// corner). The dummy gets a HitBody so the blade can hit it.
///
/// "Set Up Hit Detection" gives the Player a HitBody (filled from its
/// humanoid rig) and a HitCalculator reading the blade from the sword bone,
/// and gives every Training Dummy without one a HitBody. Safe to run again:
/// it only adds what is missing.
///
/// This file must stay inside a folder called "Editor".
/// </summary>
public static class BladeReflexMenu
{
    private const string SwordBoneName = "Sword_joint";   // mixamorig:Sword_joint on the Paladin rig

    [MenuItem("Tools/Blade and Reflex/Add Training Dummy")]
    private static void AddTrainingDummy()
    {
        PlayerController player = Object.FindAnyObjectByType<PlayerController>();

        // ---- Player health ----
        if (player != null && player.GetComponent<Health>() == null)
        {
            Health ph = Undo.AddComponent<Health>(player.gameObject);
            SerializedObject so = new SerializedObject(ph);
            so.FindProperty("barInCorner").boolValue = true;
            so.FindProperty("barLabel").stringValue = "Player";
            so.ApplyModifiedProperties();
        }

        // ---- Dummy ----
        Vector3 position = Vector3.forward * 3f;
        Quaternion rotation = Quaternion.LookRotation(Vector3.back);
        if (player != null)
        {
            Transform p = player.transform;
            position = p.position + p.forward * 3f;
            rotation = Quaternion.LookRotation(-p.forward, Vector3.up);
        }

        GameObject root = new GameObject("Training Dummy");
        Undo.RegisterCreatedObjectUndo(root, "Add Training Dummy");
        root.transform.SetPositionAndRotation(position, rotation);

        // Body (pivot of the root stays at the feet).
        GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0f, 1f, 0f);
        body.transform.localScale = new Vector3(0.8f, 1f, 0.8f);

        // Arm pointing forward, so you can see which way it faces.
        GameObject arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
        arm.name = "Arm";
        arm.transform.SetParent(root.transform, false);
        arm.transform.localPosition = new Vector3(0.35f, 1.3f, 0.55f);
        arm.transform.localScale = new Vector3(0.15f, 0.15f, 0.9f);
        Object.DestroyImmediate(arm.GetComponent<Collider>());

        Health health = root.AddComponent<Health>();
        SerializedObject hs = new SerializedObject(health);
        hs.FindProperty("barLabel").stringValue = "Dummy";
        hs.ApplyModifiedProperties();

        root.AddComponent<TrainingDummy>();
        AddDummyHitBody(root, false);

        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(root.scene);

        Debug.Log(player != null
            ? "Training Dummy added 3 m in front of the Player. Save the scene to keep it."
            : "Training Dummy added at (0, 0, 3). No PlayerController found in the scene.");
        if (player != null && player.GetComponent<HitCalculator>() == null)
            Debug.Log("The Player can't hit the dummy yet: run Tools > Blade and Reflex > Set Up Hit Detection.");
    }

    [MenuItem("Tools/Blade and Reflex/Set Up Hit Detection")]
    private static void SetUpHitDetection()
    {
        PlayerController player = Object.FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            Debug.LogWarning("Set Up Hit Detection: no PlayerController found in the scene.");
            return;
        }

        GameObject go = player.gameObject;
        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Set Up Hit Detection");
        int undoGroup = Undo.GetCurrentGroup();

        // ---- Player body ----
        if (go.GetComponent<HitBody>() == null)
        {
            HitBody body = Undo.AddComponent<HitBody>(go);
            if (!body.FillFromHumanoid())
                Debug.LogWarning("Set Up Hit Detection: the Player has no humanoid Animator, so its HitBody has no parts.");
        }

        // ---- Player blade ----
        HitCalculator calculator = go.GetComponent<HitCalculator>();
        if (calculator == null)
            calculator = Undo.AddComponent<HitCalculator>(go);

        if (!calculator.HasBlade)
        {
            Transform sword = FindChildEndingWith(go.transform, SwordBoneName);
            if (sword != null)
            {
                SerializedObject so = new SerializedObject(calculator);
                so.FindProperty("bladeBone").objectReferenceValue = sword;
                so.ApplyModifiedProperties();
            }
            else
            {
                Debug.LogWarning($"Set Up Hit Detection: no bone named '*{SwordBoneName}' under the Player. " +
                                 "Set HitCalculator's Blade Bone by hand.");
            }
        }

        // ---- Dummies ----
        foreach (TrainingDummy dummy in Object.FindObjectsByType<TrainingDummy>(FindObjectsInactive.Include))
            if (dummy.GetComponent<HitBody>() == null)
            {
                AddDummyHitBody(dummy.gameObject, true);
                EditorSceneManager.MarkSceneDirty(dummy.gameObject.scene);
            }

        EditorSceneManager.MarkSceneDirty(go.scene);
        Undo.CollapseUndoOperations(undoGroup);
        Debug.Log("Hit detection set up. Save the scene to keep it.");
    }

    // Capsules matching the dummy's primitive body and arm (positions in the root's space).
    private static void AddDummyHitBody(GameObject root, bool undoable)
    {
        HitBody hitBody = undoable ? Undo.AddComponent<HitBody>(root) : root.AddComponent<HitBody>();
        // Body: Unity capsule (height 2, radius 0.5) scaled 0.8 wide, centred 1 m up.
        hitBody.AddPart("Body", root.transform, new Vector3(0f, 0.4f, 0f), new Vector3(0f, 1.6f, 0f), 0.4f);
        // Arm: 0.9 m long, 0.15 m thick cube centred at (0.35, 1.3, 0.55).
        hitBody.AddPart("Arm", root.transform, new Vector3(0.35f, 1.3f, 0.175f), new Vector3(0.35f, 1.3f, 0.925f), 0.075f);
        EditorUtility.SetDirty(hitBody);
    }

    private static Transform FindChildEndingWith(Transform root, string suffix)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name.EndsWith(suffix, System.StringComparison.Ordinal))
                return t;
        return null;
    }
}
