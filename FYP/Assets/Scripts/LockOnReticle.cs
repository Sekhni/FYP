using UnityEngine;

/// <summary>
/// Draws a small reticle on the lock-on target (GDD section 7). A placeholder
/// HUD element: the guard direction indicator will later sit around it.
/// Reads everything it needs from PlayerController.
/// </summary>
public class LockOnReticle : MonoBehaviour
{
    [SerializeField] private PlayerController player;
    [Tooltip("Half the reticle's width, in pixels.")]
    [SerializeField] private float size = 10f;
    [SerializeField] private Color colour = new Color(1f, 0.85f, 0.3f, 0.9f);

    private static Texture2D white;

    private void Awake()
    {
        if (player == null)
            player = FindAnyObjectByType<PlayerController>();
    }

    private void OnGUI()
    {
        if (player == null || !player.IsLockedOn)
            return;

        Camera cam = Camera.main;
        if (cam == null)
            return;
        Vector3 screen = cam.WorldToScreenPoint(player.LockPoint);
        if (screen.z <= 0f)
            return;   // behind the camera

        if (white == null)
        {
            white = new Texture2D(1, 1);
            white.SetPixel(0, 0, Color.white);
            white.Apply();
        }

        float s = size;
        float x = screen.x, y = Screen.height - screen.y;
        GUI.color = colour;
        // Four short ticks around the lock point.
        GUI.DrawTexture(new Rect(x - s, y - 1f, s * 0.6f, 2f), white);
        GUI.DrawTexture(new Rect(x + s * 0.4f, y - 1f, s * 0.6f, 2f), white);
        GUI.DrawTexture(new Rect(x - 1f, y - s, 2f, s * 0.6f), white);
        GUI.DrawTexture(new Rect(x - 1f, y + s * 0.4f, 2f, s * 0.6f), white);
        GUI.color = Color.white;
    }
}
