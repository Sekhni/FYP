using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First pass at player movement for Blade & Reflex.
/// WASD moves the player relative to the camera, the character turns to face
/// its movement direction, and the Animator's "Speed" float is fed so the
/// Idle/Run transitions work. Lock-on strafing is added later.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 2f;      // metres per second
    [SerializeField] private float runSpeed = 4.5f;     // while Left Shift is held
    [SerializeField] private float turnSpeed = 720f;    // degrees per second
    [SerializeField] private float gravity = -20f;

    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;
    [SerializeField] private Transform cameraTransform;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");

    private CharacterController controller;
    private CombatStateMachine combat;
    private float verticalVelocity;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        combat = GetComponent<CombatStateMachine>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    private void Update()
    {
        Vector2 input = ReadMoveInput();
        Vector3 moveDir = CameraRelative(input);

        // No walking while an attack is in progress
        if (combat != null && !combat.CanMove)
            moveDir = Vector3.zero;

        // Walk by default, run while Left Shift is held
        bool running = IsRunHeld();

        // Horizontal movement
        Vector3 velocity = moveDir * (running ? runSpeed : walkSpeed);

        // Gravity
        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;
        velocity.y = verticalVelocity;

        controller.Move(velocity * Time.deltaTime);

        // Face the direction of travel
        if (moveDir.sqrMagnitude > 0.001f)
        {
            Quaternion target = Quaternion.LookRotation(moveDir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, target, turnSpeed * Time.deltaTime);
        }

        // Drive the animator: 0 = idle, 0.5 = walk, 1 = run (blend tree thresholds)
        if (animator != null)
        {
            float animSpeed = moveDir.magnitude * (running ? 1f : 0.5f);
            animator.SetFloat(SpeedHash, animSpeed, 0.1f, Time.deltaTime);
        }
    }

    private static Vector2 ReadMoveInput()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null)
            return Vector2.zero;

        float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        return new Vector2(x, y);
    }

    private static bool IsRunHeld()
    {
        Keyboard kb = Keyboard.current;
        return kb != null && kb.leftShiftKey.isPressed;
    }

    private Vector3 CameraRelative(Vector2 input)
    {
        Vector3 forward = Vector3.forward;
        Vector3 right = Vector3.right;

        if (cameraTransform != null)
        {
            forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
            right = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
        }

        Vector3 dir = forward * input.y + right * input.x;
        return dir.sqrMagnitude > 1f ? dir.normalized : dir;
    }
}