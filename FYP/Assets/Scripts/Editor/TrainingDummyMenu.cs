using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Adds "Tools > Blade and Reflex > Add Training Dummy" to the Unity menu.
/// It builds a dummy 3 m in front of the Player and makes sure the Player has
/// a Health component (with its bar in the top-left corner).
/// This file must stay inside a folder called "Editor".
/// </summary>
public static class TrainingDummyMenu
{
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

        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(root.scene);

        Debug.Log(player != null
            ? "Training Dummy added 3 m in front of the Player. Save the scene to keep it."
            : "Training Dummy added at (0, 0, 3). No PlayerController found in the scene.");
    }
}
