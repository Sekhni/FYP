using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player movement for Blade & Reflex.
/// - Turn with the mouse OR with A / D (gamepad: right stick or left stick sideways).
///   The camera (ThirdPersonCamera) sits behind the player and follows.
/// - W walks forward, S walks backward (the walk clip plays in reverse).
///   The gamepad left stick gives analogue speed.
/// - Hold Left Shift (or click the left stick) to run forward.
/// - Space rolls in the direction you are pressing (forward if nothing is
///   pressed).
/// - While blocking (right mouse, handled by CombatStateMachine) you can turn
///   but not walk. You can roll out of a block.
/// - The mouse cursor is locked to the game window. Esc frees it, click to lock again.
/// - Speed builds up and falls off over a short time, so movement feels weighty
///   instead of snapping on and off.
/// - The Animator's "Speed" float is the real ground speed in metres per second.
///   The Locomotion blend tree thresholds (0 / 1.43 / 3.95) are the speeds the
///   Idle / Walk / Run clips actually travel at, so the feet don't slide.
/// - No root motion is used anywhere. Rolls and attacks are moved by this
///   script: each frame it reads how far through its animation the player is
///   (Animator normalizedTime, which already includes the state's playback
///   speed) and moves the matching share of rollDistance / attackDistance.
///   So the movement always lasts exactly as long as the animation plays,
///   even if you change the clip or its speed. The progress curves describe
///   how the distance is spread over the animation (measured from the clips).
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
    [Tooltip("Metres the light attack steps forward over the whole swing. 0 = attack in place.")]
    [SerializeField] private float attackDistance = 1f;
    [Tooltip("How the attack distance is spread over the swing (x = animation progress 0-1, y = share of distance 0-1). Default is measured from the Attack_Light clip.")]
    [SerializeField] private AnimationCurve attackProgress = new AnimationCurve(
        new Keyframe(0f, 0f), new Keyframe(0.23f, 0.17f), new Keyframe(0.40f, 0.51f),
        new Keyframe(0.52f, 0.68f), new Keyframe(0.72f, 0.94f), new Keyframe(1f, 1f));

    [Header("Roll (Space)")]
    [Tooltip("Metres travelled by one roll.")]
    [SerializeField] private float rollDistance = 3.6f;
    [Tooltip("How the roll distance is spread over the roll (x = animation progress 0-1, y = share of distance 0-1). Default is measured from the dive clip.")]
    [SerializeField] private AnimationCurve rollProgress = new AnimationCurve(
        new Keyframe(0f, 0f), new Keyframe(0.25f, 0.27f), new Keyframe(0.5f, 0.68f),
        new Keyframe(0.75f, 0.9f), new Keyframe(1f, 1f));
    [Tooltip("Safety: end the roll after this many seconds even if the animation never reported finishing.")]
    [SerializeField] private float rollFailSafeSeconds = 3f;

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
    private bool rolling;
    private bool rollStarted;     // the Dodge animation has begun
    private float rollElapsed;
    private float rollLastProgress;
    private float attackLastTime;
    private float attackLastProgress;

    private const string RollTag = "Dodge";     // tag on the Dodge state in PlayerAnimator
    private const string AttackTag = "Attack";  // tag on the Attack_Light state

    /// <summary>Current horizontal speed in metres per second.</summary>
    public float CurrentSpeed => horizontalVelocity.magnitude;

    /// <summary>True while a roll is playing.</summary>
    public bool IsDodging => rolling;

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

        // All movement comes from this script, never from the clips.
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
        if (rolling)
            UpdateRoll();
        else if (IsRollPressed() && !IsAttacking && controller.isGrounded)
            StartRoll(input);

        // ---- Attack step ----
        UpdateAttackStep();

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
            // Stop at once: rolls and attacks have their own timed movement,
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

    // ------------------------------------------------- Timed roll / attack

    private void StartRoll(Vector2 input)
    {
        // Face the pressed direction (W/S = forward/back, A/D = sideways), then roll forward.
        if (input.sqrMagnitude > 0.01f)
        {
            yaw += Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        rolling = true;
        rollStarted = false;
        rollElapsed = 0f;
        rollLastProgress = 0f;

        if (animator != null)
            animator.SetTrigger(DodgeHash);
    }

    private void UpdateRoll()
    {
        rollElapsed += Time.deltaTime;

        if (TryGetAnimProgress(RollTag, out float t, out bool leaving))
        {
            rollStarted = true;
            MoveAlongCurve(rollProgress, rollDistance, t, ref rollLastProgress);

            // Hand control back as soon as the roll starts blending out.
            if (leaving || t >= 1f)
                EndRoll();
        }
        else if (rollStarted || rollElapsed > rollFailSafeSeconds || animator == null)
        {
            EndRoll();
        }
    }

    private void EndRoll()
    {
        rolling = false;
        if (animator != null)
            animator.ResetTrigger(DodgeHash);
    }

    private void UpdateAttackStep()
    {
        if (!IsAttacking || attackDistance <= 0f
            || !TryGetAnimProgress(AttackTag, out float t, out _))
        {
            attackLastTime = 0f;
            attackLastProgress = 0f;
            return;
        }

        // A new swing started (chained attack): measure from zero again.
        if (t < attackLastTime)
            attackLastProgress = 0f;
        attackLastTime = t;

        MoveAlongCurve(attackProgress, attackDistance, t, ref attackLastProgress);
    }

    /// <summary>
    /// Moves forward by the part of 'distance' covered since last frame,
    /// according to how far through the animation we are.
    /// </summary>
    private void MoveAlongCurve(AnimationCurve curve, float distance, float t, ref float lastProgress)
    {
        float progress = curve.Evaluate(Mathf.Clamp01(t));
        float step = (progress - lastProgress) * distance;
        lastProgress = progress;

        if (Mathf.Abs(step) > 0f)
            controller.Move(transform.forward * step);
    }

    /// <summary>
    /// How far (0-1) the state with this tag has played. Uses the Animator's own
    /// clock, so it already accounts for clip length and playback speed.
    /// 'leaving' is true while that state is blending out to another state.
    /// </summary>
    private bool TryGetAnimProgress(string tag, out float t, out bool leaving)
    {
        t = 0f;
        leaving = false;
        if (animator == null)
            return false;

        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
        bool inTransition = animator.IsInTransition(0);

        if (inTransition)
        {
            AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
            if (next.IsTag(tag))
            {
                t = next.normalizedTime;      // blending in
                return true;
            }
        }

        if (current.IsTag(tag))
        {
            t = current.normalizedTime;
            leaving = inTransition;           // blending out
            return true;
        }

        return false;
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
