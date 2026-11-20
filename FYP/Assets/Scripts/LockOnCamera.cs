using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// Third-person camera with lock-on for Blade & Reflex (GDD sections 3 and 9),
/// built on two Cinemachine cameras that both follow the same pivot point
/// above the player's head:
///   Free camera    - orbits the player: the mouse / right stick turn it
///                    left and right and tilt it up and down (GDD section 3
///                    controls). The player moves relative to it.
///   Lock-on camera - used while PlayerController is locked on. The pivot
///                    is aimed at the target, so the camera looks over the
///                    player's shoulder at the enemy, keeping both fighters
///                    in view at duelling range.
/// The Cinemachine Brain on the Main Camera blends between the two when the
/// lock is toggled. Both cameras pull in front of walls (Cinemachine's
/// obstacle avoidance; this is camera placement, not combat).
///
/// Camera turn and tilt are read here; movement and lock-on are read by
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

    [Header("Free camera (mouse / right stick)")]
    [Tooltip("Degrees of turn per unit of sideways mouse movement.")]
    [SerializeField] private float mouseYawSensitivity = 0.15f;
    [Tooltip("Degrees of tilt per unit of up/down mouse movement.")]
    [FormerlySerializedAs("mouseSensitivity")]
    [SerializeField] private float mousePitchSensitivity = 0.1f;
    [Tooltip("Degrees per second of turn with the right stick held fully left or right.")]
    [SerializeField] private float stickTurnSpeed = 180f;
    [Tooltip("Degrees per second of tilt with the right stick held fully up or down.")]
    [SerializeField] private float stickTiltSpeed = 90f;
    [Tooltip("Lowest tilt in degrees (negative = looking up).")]
    [SerializeField] private float minPitch = -20f;
    [Tooltip("Highest tilt in degrees (looking down).")]
    [SerializeField] private float maxPitch = 60f;
    [Tooltip("Tilt in degrees when the game starts.")]
    [SerializeField] private float startPitch = 15f;
    [SerializeField] private bool invertY = false;

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
        yaw = player.transform.eulerAngles.y;
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
            // Start each lock without leftover smoothing.
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

        if (player.IsLockedOn)
        {
            // Look from the pivot at the target, tilted down a little.
            Vector3 dir = player.LockPoint - pivotPos;
            float flat = new Vector2(dir.x, dir.z).magnitude;
            float targetYaw = flat > 0.05f ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg : yaw;   // directly overhead: keep heading
            float targetPitch = Mathf.Clamp(-Mathf.Atan2(dir.y, flat) * Mathf.Rad2Deg + lockPitchOffset, minPitch, maxPitch);
            if (snap || lockSmoothTime <= 0f)
            {
                yaw = targetYaw;
                pitch = targetPitch;
            }
            else
            {
                yaw = Mathf.SmoothDampAngle(yaw, targetYaw, ref yawVelocity, lockSmoothTime);
                pitch = Mathf.SmoothDampAngle(pitch, targetPitch, ref pitchVelocity, lockSmoothTime);
            }
        }
        else if (!snap)
        {
            // Free: the mouse / right stick orbit the camera directly. After a
            // lock it carries on from where the lock-on camera was looking.
            yaw += ReadTurn();
            pitch = Mathf.Clamp(pitch + ReadTilt(), minPitch, maxPitch);
        }

        pivot.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    // Degrees to turn this frame (positive = turn right).
    private float ReadTurn()
    {
        float turn = 0f;
        Mouse mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            turn += mouse.delta.ReadValue().x * mouseYawSensitivity;
        Gamepad pad = Gamepad.current;
        if (pad != null)
            turn += DeadZone(pad.rightStick.ReadValue().x) * stickTurnSpeed * Time.deltaTime;
        return turn;
    }

    // Small stick values (a worn or resting stick) are ignored so the camera doesn't creep.
    private static float DeadZone(float value)
    {
        const float deadZone = 0.15f;
        return Mathf.Abs(value) < deadZone ? 0f : value;
    }

    // Degrees to tilt this frame (positive = look further down).
    private float ReadTilt()
    {
        float tilt = 0f;
        Mouse mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            tilt += mouse.delta.ReadValue().y * mousePitchSensitivity;
        Gamepad pad = Gamepad.current;
        if (pad != null)
            tilt += DeadZone(pad.rightStick.ReadValue().y) * stickTiltSpeed * Time.deltaTime;
        return invertY ? tilt : -tilt;
    }
}
