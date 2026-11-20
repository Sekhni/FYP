using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Third-person camera with lock-on for Blade & Reflex (GDD sections 3 and 9),
/// built on two Cinemachine cameras that both follow the same pivot point
/// above the player's head:
///   Free camera    - sits behind the player and turns with them (the player
///                    turns with the mouse or A / D). Moving the mouse up and
///                    down tilts it.
///   Lock-on camera - used while PlayerController is locked on. The pivot
///                    is aimed at the target, so the camera looks over the
///                    player's shoulder at the enemy and frames both fighters.
/// The Cinemachine Brain on the Main Camera blends between the two when the
/// lock is toggled. Both cameras pull in front of walls (Cinemachine's
/// obstacle avoidance; this is camera placement, not combat).
///
/// While locked on, a small reticle is drawn on the target.
///
/// Build the rig with Tools > Blade and Reflex > Set Up Lock-On.
/// </summary>
[DefaultExecutionOrder(-50)]   // aim the pivot before the Cinemachine Brain moves the camera
public class LockOnCamera : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PlayerController player;
    [Tooltip("The point both Cinemachine cameras follow. Moved and turned by this script every frame.")]
    [SerializeField] private Transform pivot;
    [SerializeField] private CinemachineCamera freeCamera;
    [SerializeField] private CinemachineCamera lockOnCamera;

    [Header("Pivot")]
    [Tooltip("Height of the pivot above the player's feet, in metres.")]
    [SerializeField] private float pivotHeight = 1.5f;

    [Header("Free camera tilt (mouse up/down)")]
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float stickTiltSpeed = 90f;
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 60f;
    [SerializeField] private float startPitch = 15f;
    [SerializeField] private bool invertY = false;
    [Tooltip("How quickly the camera catches up with the player's turning, in seconds. 0 = instant.")]
    [SerializeField] private float yawSmoothTime = 0.05f;

    [Header("Lock-on framing")]
    [Tooltip("Extra downward tilt while locked on, in degrees, so the player stays in the lower part of the frame.")]
    [SerializeField] private float lockPitchOffset = 8f;
    [Tooltip("How quickly the camera turns to follow the target, in seconds.")]
    [SerializeField] private float lockSmoothTime = 0.1f;

    [Header("Reticle")]
    [SerializeField] private bool showReticle = true;
    [SerializeField] private float reticleSize = 10f;
    [SerializeField] private Color reticleColour = new Color(1f, 0.85f, 0.3f, 0.9f);

    private const int ActivePriority = 20;
    private const int FreePriority = 10;
    private const int InactivePriority = 0;

    private float yaw;
    private float pitch;
    private float yawVelocity;
    private float pitchVelocity;
    private static Texture2D white;

    private void Start()
    {
        if (player == null)
        {
            Debug.LogWarning("LockOnCamera: no player set. Run Tools > Blade and Reflex > Set Up Lock-On.");
            enabled = false;
            return;
        }

        yaw = player.transform.eulerAngles.y;
        pitch = startPitch;
        UpdatePivot(snap: true);
    }

    private void LateUpdate()
    {
        UpdatePivot(snap: false);

        bool locked = player.IsLockedOn;
        if (freeCamera != null)
            freeCamera.Priority = FreePriority;
        if (lockOnCamera != null)
            lockOnCamera.Priority = locked ? ActivePriority : InactivePriority;
    }

    private void UpdatePivot(bool snap)
    {
        if (pivot == null)
            return;

        Vector3 pivotPos = player.transform.position + Vector3.up * pivotHeight;
        pivot.position = pivotPos;

        float targetYaw, targetPitch, smoothTime;
        if (player.IsLockedOn)
        {
            // Look from the pivot at the target, tilted down a little.
            Vector3 dir = player.LockPoint - pivotPos;
            float flat = new Vector2(dir.x, dir.z).magnitude;
            targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            targetPitch = Mathf.Clamp(-Mathf.Atan2(dir.y, flat) * Mathf.Rad2Deg + lockPitchOffset, minPitch, maxPitch);
            smoothTime = lockSmoothTime;
        }
        else
        {
            // Turn with the player; tilt with the mouse / right stick.
            targetYaw = player.transform.eulerAngles.y;
            targetPitch = Mathf.Clamp(pitch + ReadTilt(), minPitch, maxPitch);
            smoothTime = yawSmoothTime;
        }

        if (snap || smoothTime <= 0f)
        {
            yaw = targetYaw;
            pitch = targetPitch;
        }
        else
        {
            yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVelocity, smoothTime);
            pitch = player.IsLockedOn
                ? Mathf.SmoothDampAngle(pitch, targetPitch, ref pitchVelocity, smoothTime)
                : targetPitch;   // tilt follows the mouse directly
        }

        pivot.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    // Degrees to tilt this frame (positive = look further down).
    private float ReadTilt()
    {
        float tilt = 0f;
        Mouse mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            tilt += mouse.delta.ReadValue().y * mouseSensitivity;
        Gamepad pad = Gamepad.current;
        if (pad != null)
            tilt += pad.rightStick.ReadValue().y * stickTiltSpeed * Time.deltaTime;
        return invertY ? tilt : -tilt;
    }

    // ------------------------------------------------------------ Reticle

    private void OnGUI()
    {
        if (!showReticle || player == null || !player.IsLockedOn)
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

        float s = reticleSize;
        float x = screen.x, y = Screen.height - screen.y;
        GUI.color = reticleColour;
        // Four short ticks around the lock point.
        GUI.DrawTexture(new Rect(x - s, y - 1f, s * 0.6f, 2f), white);
        GUI.DrawTexture(new Rect(x + s * 0.4f, y - 1f, s * 0.6f, 2f), white);
        GUI.DrawTexture(new Rect(x - 1f, y - s, 2f, s * 0.6f), white);
        GUI.DrawTexture(new Rect(x - 1f, y + s * 0.4f, 2f, s * 0.6f), white);
        GUI.color = Color.white;
    }
}
