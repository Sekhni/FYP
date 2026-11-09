using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player movement for Blade & Reflex.
/// - Turn with the mouse OR with A / D (gamepad: right stick or left stick sideways).
///   The camera (ThirdPersonCamera) sits behind the player and follows.
/// - W walks forward, S walks backward (the walk clip plays in reverse).
///   The gamepad left stick gives analogue speed, as in the GDD.
/// - Hold Left Shift (or click the left stick) to run forward.
/// - Space rolls in the direction you are pressing (forward if nothing is
///   pressed). The roll's own movement comes from the dive animation.
/// - While blocking (right mouse, handled by CombatStateMachine) you can turn
///   but not walk. You can roll out of a block.
/// - The mouse cursor is locked to the game window. Esc frees it, click to lock again.
/// - Speed builds up and falls off over a short time, so movement feels weighty
///   instead of snapping on and off.
/// - The Animator's "Speed" float is the real ground speed in metres per second.
///   The Locomotion blend tree thresholds (0 / 1.43 / 3.95) are the speeds the
///   Idle / Walk / Run clips actually travel at, so the feet don't slide.
/// - While an attack plays, normal movement stops and the attack clip's own
///   forward step (root motion) moves the player instead, scaled by
///   attackStepScale.
/// Lock-on strafing is added later.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement (match the clip speeds to avoid foot sliding)")]
    [Tooltip("Metres per second. The Walk clip travels at about 1.43 m/s.")]
    [SerializeField] private float walkSpeed = 1.43f;
    [Tooltip("Metres per second. The Run clip travels at about 3.95 m/s.")]
    [SerializeField] private float runSpeed = 3.95f;
    [Tooltip("How quickly the player reaches full speed, in seconds.")]
    [SerializeField] private float accelerationTime = 0.15f;
    [Tooltip("How quickly the player stops, in seconds.")]
    [SerializeField] private float decelerationTime = 0.1f;
    [SerializeField] private float gravity = -20f;

    [Header("Attacks")]
    [Tooltip("How much of the attack clip's forward step is applied. 0 = attack in place, 1 = full step (about 2 m on the light attack).")]
    [Range(0f, 1f)]
    [SerializeField] private float attackStepScale = 0.5f;

    [Header("Roll (Space)")]
    [Tooltip("Seconds before you can move again after starting a roll.")]
    [SerializeField] private float rollDuration = 1.05f;
    [Tooltip("How much of the dive animation's travel is used. 1 = full distance (about 3.6 m).")]
    [Range(0.2f, 1.5f)]
    [SerializeField] private float rollDistanceScale = 1f;

    [Header("Turning")]
    [Tooltip("Degrees turned per pixel of mouse movement.")]
    [SerializeField] private float mouseSensitivity = 0.15f;
    [Tooltip("Degrees per second when turning with A / D or a stick.")]
    [SerializeField] private float keyTurnSpeed = 180f;
    [Tooltip("Backward walking speed as a fraction of walk speed. No running backward.")]
    [Range(0.3f, 1f)]
    [SerializeField] private float backwardSpeedFactor = 0.8f;
    [Tooltip("Lock and hide the mouse cursor while playing.")]
    [SerializeField] private bool lockCursor = true;

    [Header("Input")]
    [Tooltip("Gamepad stick values below this are ignored.")]
    [SerializeField] private float stickDeadZone = 0.15f;

    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;
    [SerializeField] private Transform cameraTransform;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int MoveDirectionHash = Animator.StringToHash("MoveDirection");
    private static readonly int DodgeHash = Animator.StringToHash("Dodge");

    private CharacterController controller;
    private CombatStateMachine combat;
    private Vector3 horizontalVelocity;
    private Vector3 velocitySmoothing;
    private float verticalVelocity;
    private float yaw;
    private float rollTimer;

    /// <summary>Current horizontal speed in metres per second.</summary>
    public float CurrentSpeed => horizontalVelocity.magnitude;

    /// <summary>True while a roll is playing.</summary>
    public bool IsDodging => rollTimer > 0f;

    private bool IsAttacking => combat != null && !combat.CanMove;
    private bool IsBlocking => combat != null && combat.IsBlocking;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        combat = GetComponent<CombatStateMachine>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Make sure the camera follows the player.
        if (cameraTransform != null)
        {
            ThirdPersonCamera follow = cameraTransform.GetComponent<ThirdPersonCamera>();
            if (follow == null)
                follow = cameraTransform.gameObject.AddComponent<ThirdPersonCamera>();
            if (follow.Target == null)
                follow.Target = transform;
        }

        yaw = transform.eulerAngles.y;

        // Movement comes from this script, not from the clips (except the
        // attack step, handled in OnAnimatorMove).
        if (animator != null)
            animator.applyRootMotion = false;
    }

    private void Start()
    {
        if (lockCursor)
            SetCursorLocked(true);
    }

    private void Update()
    {
        UpdateCursorLock();

        Vector2 input = ReadMoveInput();   // x = turn (A/D), y = forward/back (W/S)

        // ---- Roll ----
        if (rollTimer > 0f)
            rollTimer -= Time.deltaTime;
        else if (IsRollPressed() && !IsAttacking && controller.isGrounded)
            StartRoll(input);

        // ---- Turn with mouse and A/D (not mid-attack or mid-roll: you commit to it) ----
        if (!IsAttacking && !IsDodging)
        {
            yaw += ReadMouseTurn() + input.x * keyTurnSpeed * Time.deltaTime;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        bool backward = input.y < 0f;
        bool running = IsRunHeld() && !backward;
        float speed = backward ? walkSpeed * backwardSpeedFactor
                    : running  ? runSpeed
                    :            walkSpeed;
        Vector3 moveDir = transform.forward * input.y;   // magnitude 0..1, sign = direction

        // ---- Horizontal movement ----
        Vector3 targetVelocity = moveDir * speed;

        if (IsAttacking || IsDodging || IsBlocking)
        {
            // Stop at once: attacks and rolls move by their animation instead,
            // and blocking holds you in place.
            targetVelocity = Vector3.zero;
            horizontalVelocity = Vector3.zero;
            velocitySmoothing = Vector3.zero;
        }
        else
        {
            bool speedingUp = targetVelocity.sqrMagnitude > horizontalVelocity.sqrMagnitude;
            float smoothTime = speedingUp ? accelerationTime : decelerationTime;
            horizontalVelocity = Vector3.SmoothDamp(
                horizontalVelocity, targetVelocity, ref velocitySmoothing, smoothTime);
        }

        // ---- Gravity ----
        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = horizontalVelocity;
        velocity.y = verticalVelocity;
        controller.Move(velocity * Time.deltaTime);

        // ---- Drive the animator with the real ground speed ----
        if (animator != null)
        {
            float animSpeed = horizontalVelocity.magnitude;
            animator.SetFloat(SpeedHash, animSpeed, 0.05f, Time.deltaTime);

            // 1 = clips play forward, -1 = play in reverse (walking backward)
            bool movingBack = Vector3.Dot(horizontalVelocity, transform.forward) < -0.05f;
            animator.SetFloat(MoveDirectionHash, movingBack ? -1f : 1f);
        }
    }

    /// <summary>
    /// Called by Unity after the Animator updates, when the Animator is on this
    /// GameObject. Defining it means Unity hands root motion to this script.
    /// Only the attack's forward step is used; locomotion clips stay in place.
    /// </summary>
    private void OnAnimatorMove()
    {
        if (animator == null)
            return;

        float scale = IsDodging ? rollDistanceScale
                    : IsAttacking ? attackStepScale
                    : 0f;
        if (scale <= 0f)
            return;

        Vector3 step = animator.deltaPosition;
        step.y = 0f;
        controller.Move(step * scale);
    }

    private void StartRoll(Vector2 input)
    {
        // Face the pressed direction (W/S = forward/back, A/D = sideways), then roll forward.
        if (input.sqrMagnitude > 0.01f)
        {
            yaw += Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        rollTimer = rollDuration;
        if (animator != null)
            animator.SetTrigger(DodgeHash);
    }

    // ---------------------------------------------------------------- Input

    private Vector2 ReadMoveInput()
    {
        Vector2 input = Vector2.zero;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            input.x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            input.y = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        }

        Gamepad pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 stick = pad.leftStick.ReadValue();
            if (stick.magnitude > stickDeadZone && stick.sqrMagnitude > input.sqrMagnitude)
                input = stick;

            float rightX = pad.rightStick.ReadValue().x;
            if (Mathf.Abs(rightX) > stickDeadZone && Mathf.Abs(rightX) > Mathf.Abs(input.x))
                input.x = rightX;
        }

        input.x = Mathf.Clamp(input.x, -1f, 1f);
        input.y = Mathf.Clamp(input.y, -1f, 1f);
        return input;
    }

    private static bool IsRollPressed()
    {
        Keyboard kb = Keyboard.current;
        Gamepad pad = Gamepad.current;
        return (kb != null && kb.spaceKey.wasPressedThisFrame)
            || (pad != null && pad.buttonEast.wasPressedThisFrame);
    }

    private static bool IsRunHeld()
    {
        Keyboard kb = Keyboard.current;
        Gamepad pad = Gamepad.current;
        return (kb != null && kb.leftShiftKey.isPressed)
            || (pad != null && pad.leftStickButton.isPressed);
    }

    private float ReadMouseTurn()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || Cursor.lockState != CursorLockMode.Locked)
            return 0f;
        return mouse.delta.ReadValue().x * mouseSensitivity;
    }

    private void UpdateCursorLock()
    {
        if (!lockCursor)
            return;

        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;

        if (kb != null && kb.escapeKey.wasPressedThisFrame)
            SetCursorLocked(false);
        else if (mouse != null && mouse.leftButton.wasPressedThisFrame
                 && Cursor.lockState != CursorLockMode.Locked)
            SetCursorLocked(true);
    }

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
