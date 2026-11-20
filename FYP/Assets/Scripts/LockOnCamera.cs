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
///                    player's shoulder at the enemy, keeping both fighters
///                    in view at duelling range.
/// The Cinemachine Brain on the Main Camera blends between the two when the
/// lock is toggled. Both cameras pull in front of walls (Cinemachine's
/// obstacle avoidance; this is camera placement, not combat).
///
/// Camera tilt is read here; everything else (turning, lock-on) is read by
/// PlayerController. The lock-on reticle is drawn by LockOnReticle.
///
/// Build the rig with Tools > Blade and Reflex > Set Up Lock-On.
/// </summary>
[DefaultExecutionOrder(-50)]   // aim the pivot before the Cinemachine Brain moves the camera
public class LockOnCamera : MonoBehaviour
{
    // Cinemachine uses the live camera with the highest priority.
    public const int FreePriority = 10;
    public const int LockedPriority = 20;
    public const int InactivePriority = 0;

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
    [Tooltip("Degrees of tilt per unit of mouse movement.")]
    [SerializeField] private float mouseSensitivity = 0.1f;
    [Tooltip("Degrees per second of tilt with the right stick held fully up or down.")]
    [SerializeField] private float stickTiltSpeed = 90f;
    [Tooltip("Lowest tilt in degrees (negative = looking up).")]
    [SerializeField] private float minPitch = -20f;
    [Tooltip("Highest tilt in degrees (looking down).")]
    [SerializeField] private float maxPitch = 60f;
    [Tooltip("Tilt in degrees when the game starts.")]
    [SerializeField] private float startPitch = 15f;
    [SerializeField] private bool invertY = false;
    [Tooltip("How quickly the camera catches up with the player's turning, in seconds. 0 = instant.")]
    [SerializeField] private float yawSmoothTime = 0.05f;

    [Header("Lock-on framing")]
    [Tooltip("Extra downward tilt while locked on, in degrees, so the player stays in the lower part of the frame.")]
    [SerializeField] private float lockPitchOffset = 8f;
    [Tooltip("How quickly the camera turns to follow the target, in seconds.")]
    [SerializeField] private float lockSmoothTime = 0.1f;

    private float yaw;
    private float pitch;
    private float yawVelocity;
    private float pitchVelocity;
    private bool wasLocked;

    private void Start()
    {
        if (player == null)
        {
            Debug.LogWarning("LockOnCamera: no player set. Run Tools > Blade and Reflex > Set Up Lock-On.");
            enabled = false;
            return;
        }

        if (freeCamera != null)
            freeCamera.Priority = FreePriority;
        if (lockOnCamera != null)
            lockOnCamera.Priority = InactivePriority;
        yaw =player.transform.eulerAngles.y;
        pitch = startPitch;
        UpdatePivot(snap: true);
    }

    private void LateUpdate()
    {
        if (player == null)
            return;   // player destroyed

        bool locked = player.IsLockedOn;
        if (locked != wasLocked)
        {
            // Start each lock (and each return to free) without leftover smoothing.
            yawVelocity = pitchVelocity = 0f;
            wasLocked = locked;
            if (lockOnCamera != null)
                lockOnCamera.Priority = locked ? LockedPriority : InactivePriority;
        }

        UpdatePivot(snap: false);
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
            targetYaw = flat > 0.05f ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg : yaw;   // directly overhead: keep heading
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
}
