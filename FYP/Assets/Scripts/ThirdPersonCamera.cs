using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Simple third-person camera for Blade & Reflex.
/// Sits behind the target and turns with it (the player turns with the mouse
/// or A / D). Moving the mouse up/down tilts the camera.
/// PlayerController adds this to the Main Camera automatically if it is missing,
/// so you can also add it yourself to tweak the values in the Inspector.
/// Lock-on framing is added later.
/// </summary>
public class ThirdPersonCamera : MonoBehaviour
{
    [Tooltip("What the camera follows. Filled in by PlayerController if left empty.")]
    public Transform Target;

    [Header("Framing")]
    [SerializeField] private float distance = 4f;
    [Tooltip("Height of the point the camera looks at, above the target's feet.")]
    [SerializeField] private float lookHeight = 1.5f;
    [Tooltip("Shifts the camera sideways (positive = over the right shoulder).")]
    [SerializeField] private float shoulderOffset = 0.4f;

    [Header("Tilt (mouse up/down)")]
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float stickTiltSpeed = 90f;
    [SerializeField] private float minPitch = -20f;
    [SerializeField] private float maxPitch = 60f;
    [SerializeField] private float startPitch = 15f;
    [SerializeField] private bool invertY = false;

    [Header("Smoothing")]
    [Tooltip("How quickly the camera catches up with the player's turning. 0 = instant.")]
    [SerializeField] private float yawSmoothTime = 0.05f;
    [Tooltip("How quickly the camera catches up with the player's position. 0 = instant.")]
    [SerializeField] private float followSmoothTime = 0.05f;

    [Header("Collision")]
    [Tooltip("Pull the camera in when a wall is between it and the player.")]
    [SerializeField] private bool avoidWalls = true;
    [SerializeField] private float wallPadding = 0.2f;
    [SerializeField] private LayerMask wallLayers = ~0;

    private float pitch;
    private float yaw;
    private float yawVelocity;
    private Vector3 pivot;
    private Vector3 pivotVelocity;

    private void Start()
    {
        pitch = startPitch;
        if (Target != null)
        {
            yaw = Target.eulerAngles.y;
            pivot = Target.position + Vector3.up * lookHeight;
        }
    }

    // LateUpdate runs after the player has moved this frame, so there is no jitter.
    private void LateUpdate()
    {
        if (Target == null)
            return;

        // ---- Tilt ----
        float tilt = 0f;
        Mouse mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            tilt += mouse.delta.ReadValue().y * mouseSensitivity;
        Gamepad pad = Gamepad.current;
        if (pad != null)
            tilt += pad.rightStick.ReadValue().y * stickTiltSpeed * Time.deltaTime;
        pitch = Mathf.Clamp(pitch + (invertY ? tilt : -tilt), minPitch, maxPitch);

        // ---- Follow the player's facing and position ----
        yaw = yawSmoothTime > 0f
            ? Mathf.SmoothDampAngle(yaw, Target.eulerAngles.y, ref yawVelocity, yawSmoothTime)
            : Target.eulerAngles.y;

        Vector3 targetPivot = Target.position + Vector3.up * lookHeight;
        pivot = followSmoothTime > 0f
            ? Vector3.SmoothDamp(pivot, targetPivot, ref pivotVelocity, followSmoothTime)
            : targetPivot;

        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 lookPoint = pivot + rotation * Vector3.right * shoulderOffset;
        Vector3 wanted = lookPoint - rotation * Vector3.forward * distance;

        // ---- Keep walls out of the way ----
        if (avoidWalls)
        {
            Vector3 toCamera = wanted - lookPoint;
            if (Physics.SphereCast(lookPoint, wallPadding, toCamera.normalized, out RaycastHit hit,
                                   toCamera.magnitude, wallLayers, QueryTriggerInteraction.Ignore)
                && !hit.transform.IsChildOf(Target))
            {
                wanted = lookPoint + toCamera.normalized * Mathf.Max(hit.distance, 0.5f);
            }
        }

        transform.SetPositionAndRotation(wanted, rotation);
    }
}
