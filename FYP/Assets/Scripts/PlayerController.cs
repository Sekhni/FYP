using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player movement and lock-on for Blade & Reflex.
/// - Turn with the mouse OR with A / D (gamepad: right stick or left stick sideways).
///   The camera (LockOnCamera) sits behind the player and follows.
/// - W walks forward, S walks backward.
///   The gamepad left stick gives analogue speed.
/// - Hold Left Shift (or click the left stick) to run forward.
/// - Tab (gamepad: click the right stick) locks on to the enemy nearest the
///   middle of the view, and again to let go. While locked on the player
///   always faces the target: W / S walk towards and away from it, A / D
///   strafe around it, and there is no running. Mouse and right-stick
///   sideways movement don't turn the player while locked on; they are kept
///   free for choosing the guard direction (GDD section 3). The lock breaks if the target
///   dies, is disabled, or gets further than lockOnBreakRange away.
/// - Space rolls in the direction you are pressing (forward if nothing is
///   pressed).
/// - While blocking (right mouse, handled by CombatStateMachine) you can turn
///   but not walk. You can roll out of a block.
/// - The mouse cursor is locked to the game window. Esc frees it, click to lock again.
/// - Speed builds up and falls off over a short time, so movement feels weighty
///   instead of snapping on and off.
/// - The Animator's "MoveX" / "MoveZ" floats are the real ground velocity in
///   metres per second, sideways and forwards relative to the player. The
///   Locomotion 2D blend tree places each clip at the speed it actually
///   travels (walk 1.43 forward, run 3.95, back 1.09, strafe 1.19 right /
///   1.05 left), so the feet don't slide. "Speed" (overall ground speed) is
///   also set, but no Animator state uses it at the moment.
/// - No root motion is used anywhere. Rolls and attacks are moved by this
///   script: each frame it reads how far through its animation the player is
///   (Animator normalizedTime, which already includes the state's playback
///   speed) and moves the matching share of rollDistance / attackDistance.
///   So the movement always lasts exactly as long as the animation plays,
///   even if you change the clip or its speed. The progress curves describe
///   how the distance is spread over the animation (measured from the clips).
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
    [Tooltip("Backward walking speed as a fraction of walk speed. No running backward. " +
             "The walk-back clip travels at about 1.09 m/s (0.76 of walk speed).")]
    [Range(0.3f, 1f)]
    [SerializeField] private float backwardSpeedFactor = 0.8f;
    [Tooltip("Lock and hide the mouse cursor while playing.")]
    [SerializeField] private bool lockCursor = true;

    [Header("Lock-on (Tab / right stick click)")]
    [Tooltip("Only targets this close can be locked on to, in metres.")]
    [SerializeField] private float lockOnRange = 15f;
    [Tooltip("The lock breaks when the target gets further away than this, in metres.")]
    [SerializeField] private float lockOnBreakRange = 20f;
    [Tooltip("Only targets within this many degrees of the camera's view direction can be picked.")]
    [Range(10f, 180f)]
    [SerializeField] private float lockOnMaxAngle = 70f;
    [Tooltip("Height above the target's feet that the camera and reticle aim at, in metres.")]
    [SerializeField] private float lockPointHeight = 1.2f;
    [Tooltip("Sideways speed while locked on, in metres per second. The strafe clips travel at 1.05-1.19 m/s.")]
    [SerializeField] private float strafeSpeed = 1.1f;
    [Tooltip("Degrees per second the player turns to keep facing the target.")]
    [SerializeField] private float lockTurnSpeed = 540f;

    [Header("Input")]
    [Tooltip("Gamepad stick values below this are ignored.")]
    [SerializeField] private float stickDeadZone = 0.15f;

    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int MoveXHash = Animator.StringToHash("MoveX");
    private static readonly int MoveZHash = Animator.StringToHash("MoveZ");
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

    // Target choice: one metre of distance counts as much as this many degrees off the view centre.
    private const float DegreesPerMetre = 2f;

    private const string RollTag = "Dodge";     // tag on the Dodge state in PlayerAnimator
    private const string AttackTag = "Attack";  // tag on the Attack_Light state

    /// <summary>Current horizontal speed in metres per second.</summary>
    public float CurrentSpeed => horizontalVelocity.magnitude;

    /// <summary>True while a roll is playing.</summary>
    public bool IsDodging => rolling;

    /// <summary>The body the player is locked on to, or null.</summary>
    public HitBody LockTarget { get; private set; }

    /// <summary>True while locked on to a live target.</summary>
    public bool IsLockedOn => LockTarget != null;

    /// <summary>The point on the target the camera and reticle aim at.</summary>
    public Vector3 LockPoint => LockTarget != null
        ? LockTarget.transform.position + Vector3.up * lockPointHeight
        : transform.position;

    /// <summary>Raised when the lock-on target changes (null = lock released).</summary>
    public event Action<HitBody> LockTargetChanged;

    private bool IsAttacking => combat != null && !combat.CanMove;
    private bool IsBlocking => combat != null && combat.IsBlocking;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        combat = GetComponent<CombatStateMachine>();

        if (animator == null)
            animator = GetComponentInChildren<Animator>();

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

    private void OnValidate()
    {
        // A break range shorter than the pick range would drop every new lock at once.
        lockOnBreakRange = Mathf.Max(lockOnBreakRange, lockOnRange);
    }

    private void Update()
    {
        UpdateCursorLock();
        UpdateLockOn();

        Vector2 input = ReadMoveInput();   // x = turn or strafe (A/D), y = forward/back (W/S)

        // ---- Roll ----
        if (rolling)
            UpdateRoll();
        else if (IsRollPressed() && !IsAttacking && controller.isGrounded)
            StartRoll(input);

        // ---- Attack step ----
        UpdateAttackStep();

        // ---- Turn (not mid-attack or mid-roll: you commit to it) ----
        // Free: mouse and A/D turn the player. Locked on: face the target.
        if (!IsAttacking && !IsDodging)
        {
            if (IsLockedOn)
            {
                Vector3 toTarget = LockTarget.transform.position - transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.0001f)
                {
                    float targetYaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
                    yaw = Mathf.MoveTowardsAngle(yaw, targetYaw, lockTurnSpeed * Time.deltaTime);
                }
            }
            else
            {
                yaw += ReadMouseTurn() + input.x * keyTurnSpeed * Time.deltaTime;
            }
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // ---- Horizontal movement ----
        bool backward = input.y < 0f;
        float forwardSpeed = backward ? walkSpeed * backwardSpeedFactor
                           : IsRunHeld() && !IsLockedOn ? runSpeed
                           : walkSpeed;
        Vector3 targetVelocity = transform.forward * (input.y * forwardSpeed);
        if (IsLockedOn)
        {
            // A/D strafe around the target. Keep diagonals (W+D) at walking
            // pace, or the blend tree would mix in the run clip.
            Vector2 move = input.sqrMagnitude > 1f ? input.normalized : input;
            targetVelocity = transform.forward * (move.y * forwardSpeed) + transform.right * (move.x * strafeSpeed);
        }

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

        // ---- Drive the animator with the real ground velocity ----
        if (animator != null)
        {
            Vector3 local = transform.InverseTransformDirection(horizontalVelocity);
            animator.SetFloat(SpeedHash, horizontalVelocity.magnitude, 0.05f, Time.deltaTime);
            animator.SetFloat(MoveXHash, local.x, 0.05f, Time.deltaTime);
            animator.SetFloat(MoveZHash, local.z, 0.05f, Time.deltaTime);
        }
    }

    // ---------------------------------------------------------------- Lock-on

    private void UpdateLockOn()
    {
        // Let go of a target that has died, been disabled or destroyed, or
        // moved out of range. (ReferenceEquals: a destroyed target compares
        // equal to null, but the lock still has to be released and announced.)
        if (!ReferenceEquals(LockTarget, null) && !IsValidTarget(LockTarget, lockOnBreakRange))
            SetLockTarget(null);

        if (IsLockOnPressed())
            ToggleLockOn();
    }

    /// <summary>
    /// Lock on to the best target in view, or let go if already locked on.
    /// Returns whether the player is locked on afterwards.
    /// </summary>
    public bool ToggleLockOn()
    {
        SetLockTarget(IsLockedOn ? null : FindLockTarget());
        return IsLockedOn;
    }

    private void SetLockTarget(HitBody target)
    {
        if (ReferenceEquals(target, LockTarget))
            return;
        LockTarget = target;
        LockTargetChanged?.Invoke(target);
    }

    /// <summary>
    /// The valid target closest to the middle of the camera's view, with
    /// distance as a tie-breaker (one metre counts like two degrees).
    /// </summary>
    private HitBody FindLockTarget()
    {
        Camera cam = Camera.main;
        Vector3 viewDir = cam != null ? cam.transform.forward : transform.forward;
        viewDir.y = 0f;
        if (viewDir.sqrMagnitude < 0.0001f)
            viewDir = transform.forward;

        HitBody best = null;
        float bestScore = float.MaxValue;
        foreach (HitBody body in HitBody.All)
        {
            if (!IsValidTarget(body, lockOnRange))
                continue;

            Vector3 to = body.transform.position - transform.position;
            to.y = 0f;
            float angle = Vector3.Angle(viewDir, to);
            if (angle > lockOnMaxAngle)
                continue;

            float score = angle + to.magnitude * DegreesPerMetre;
            if (score < bestScore)
            {
                bestScore = score;
                best = body;
            }
        }
        return best;
    }

    private bool IsValidTarget(HitBody body, float range)
    {
        if (body == null || !body.isActiveAndEnabled || body.transform.IsChildOf(transform))
            return false;
        if (body.Health != null && body.Health.IsDead)
            return false;

        Vector3 to = body.transform.position - transform.position;
        to.y = 0f;
        return to.sqrMagnitude <= range * range;
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

            // The right stick turns the player only when not locked on.
            // Locked on, it is kept free for the guard direction (GDD section 3).
            float rightX = pad.rightStick.ReadValue().x;
            if (!IsLockedOn && Mathf.Abs(rightX) > stickDeadZone && Mathf.Abs(rightX) > Mathf.Abs(input.x))
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

    private static bool IsLockOnPressed()
    {
        Keyboard kb = Keyboard.current;
        Gamepad pad = Gamepad.current;
        return (kb != null && kb.tabKey.wasPressedThisFrame)
            || (pad != null && pad.rightStickButton.wasPressedThisFrame);
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
