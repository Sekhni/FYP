using System;
using UnityEngine;
using UnityEngine.InputSystem;

public enum CombatState
{
    Idle,
    Windup,     // attack started, blade not dangerous yet
    Active,     // blade can hit (hit maths runs only in this state)
    Recovery,   // swing finished, fighter is vulnerable
    Stunned     // just got hit: flinching, can't act
}

/// <summary>
/// Minimal combat state machine for Blade & Reflex.
/// Attacks (left mouse button, gamepad: West / Square / X):
///   Tap  -> light attack, decided when the button is released. Light
///           attacks chain: each one started within chainWindow seconds of
///           the previous one ending plays the next swing (1 -> 2 -> 3 -> 1),
///           otherwise it starts again at swing 1. (Later the guard direction
///           will choose the swing instead; GDD section 3.)
///   Hold -> heavy attack once held for heavyHoldTime: slower, more damage.
/// The Animator gets the swing number in "AttackIndex" and the trigger
/// "AttackLight" or "AttackHeavy".
/// Hold right mouse button (gamepad: left bumper) to block. You can't attack
/// while blocking, and rolling (PlayerController) drops the block. Three Animation Events on
/// every attack clip move the state forward:
///     ActiveStart  ->  Active
///     ActiveEnd    ->  Recovery
///     AttackEnd    ->  Idle
/// The events must be on a clip played by the Animator that sits on the same
/// GameObject as this script (the Player root).
///
/// Getting hit (needs a Health component on the same object):
///   Hit     -> Stunned for stunTime: a hit reaction plays (one of several,
///              picked at random through "HitVariant"), any attack in
///              progress is cancelled, and you slide back knockbackDistance.
///   Blocked -> a block reaction plays (random, "BlockHitVariant") and you
///              slide back a little; you keep your guard.
///   Dodged  -> nothing.
///
/// Hits: while the state is Active, HitCalculator (same object) sweeps the
/// blade against every HitBody's capsules. When it reports a target, that
/// target's Health takes attackDamage (light) or heavyDamage (heavy). Each
/// target can be hit once per swing.
/// </summary>
public class CombatStateMachine : MonoBehaviour
{
    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;

    [Header("Light attacks (tap)")]
    [Tooltip("Damage dealt when a light attack's blade touches a target (before its block or dodge is applied).")]
    [SerializeField] private float attackDamage = 20f;
    [Tooltip("Seconds after an attack ends in which the next light attack continues the chain (swing 1 -> 2 -> 3).")]
    [SerializeField] private float chainWindow = 0.6f;

    [Header("Heavy attack (hold)")]
    [Tooltip("Damage dealt when the heavy attack's blade touches a target.")]
    [SerializeField] private float heavyDamage = 35f;
    [Tooltip("Seconds the attack button must be held for a heavy attack. A shorter press is a light attack.")]
    [SerializeField] private float heavyHoldTime = 0.25f;

    [Header("Reaction variety")]
    [Tooltip("Number of hit-reaction clips in the Animator's HitReact blend tree.")]
    [SerializeField] private int hitVariants = 2;
    [Tooltip("Number of block-reaction clips in the Animator's BlockImpact blend tree.")]
    [SerializeField] private int blockHitVariants = 2;

    [Header("Getting hit")]
    [Tooltip("Seconds you can't move, attack, block or roll after being hit.")]
    [SerializeField] private float stunTime = 0.6f;
    [Tooltip("Metres pushed back by a hit that lands.")]
    [SerializeField] private float knockbackDistance = 0.6f;
    [Tooltip("Metres pushed back by a blocked hit.")]
    [SerializeField] private float blockKnockbackDistance = 0.25f;
    [Tooltip("Seconds the push-back takes.")]
    [SerializeField] private float knockbackTime = 0.15f;

    [Header("Safety")]
    [Tooltip("If an attack never reaches Idle (for example a missing Animation Event), force Idle after this many seconds.")]
    [SerializeField] private float failSafeSeconds = 2f;

    [Header("Debug")]
    [SerializeField] private bool logStateChanges = true;

    /// <summary>Light swings in the chain (Animator states Attack_Light_1..3; must match AnimationVarietySetup.LightAttacks).</summary>
    public const int LightAttackCount = 3;

    private static readonly int AttackLightHash = Animator.StringToHash("AttackLight");
    private static readonly int AttackHeavyHash = Animator.StringToHash("AttackHeavy");
    private static readonly int AttackIndexHash = Animator.StringToHash("AttackIndex");
    private static readonly int BlockingHash = Animator.StringToHash("Blocking");
    private static readonly int HitHash = Animator.StringToHash("Hit");
    private static readonly int BlockHitHash = Animator.StringToHash("BlockHit");
    private static readonly int HitVariantHash = Animator.StringToHash("HitVariant");
    private static readonly int BlockHitVariantHash = Animator.StringToHash("BlockHitVariant");

    private int nextLightIndex;
    private float lastAttackEndTime = float.NegativeInfinity;
    private bool attackPressPending;
    private float attackPressStart;

    private Health health;
    private CharacterController characterController;
    private Vector3 knockbackVelocity;
    private float knockbackTimer;

    private PlayerController mover;
    private HitCalculator hitCalculator;

    private float timeInState;

    public CombatState State { get; private set; } = CombatState.Idle;

    /// <summary>The player may only walk while idle.</summary>
    public bool CanMove => State == CombatState.Idle;

    /// <summary>True only while the blade is allowed to hit something.</summary>
    public bool IsAttackActive => State == CombatState.Active;

    /// <summary>True while the guard is up.</summary>
    public bool IsBlocking { get; private set; }

    /// <summary>True if the current (or last) attack is the heavy one.</summary>
    public bool IsHeavyAttack { get; private set; }

    /// <summary>Which light swing (0-2) the current (or last) light attack is.</summary>
    public int CurrentLightIndex { get; private set; }

    public event Action<CombatState> StateChanged;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        mover = GetComponent<PlayerController>();
        characterController = GetComponent<CharacterController>();

        health = GetComponent<Health>();
        if (health != null)
            health.HitReceived += OnHitReceived;

        hitCalculator = GetComponent<HitCalculator>();
        if (hitCalculator != null)
            hitCalculator.TargetHit += OnBladeHit;
        else
            Debug.LogWarning($"CombatStateMachine on {name}: no HitCalculator, so attacks can't hit anything.");
    }

    private void OnDestroy()
    {
        if (health != null)
            health.HitReceived -= OnHitReceived;
        if (hitCalculator != null)
            hitCalculator.TargetHit -= OnBladeHit;
    }

    private void Update()
    {
        UpdateKnockback();

        bool dodging = mover != null && mover.IsDodging;
        UpdateBlock(dodging);

        if (State == CombatState.Idle && !IsBlocking && !dodging)
        {
            UpdateAttackInput();
            return;
        }

        // A press made while blocking, rolling, swinging or stunned doesn't carry over.
        attackPressPending = false;

        if (State != CombatState.Idle)
        {
            timeInState += Time.deltaTime;

            if (State == CombatState.Stunned)
            {
                if (timeInState >= stunTime)
                {
                    animator.ResetTrigger(HitHash);
                    SetState(CombatState.Idle);
                }
                return;   // the fail-safe below is only for attacks
            }

            if (timeInState > failSafeSeconds)
            {
                Debug.LogWarning($"CombatStateMachine: stuck in {State} for {failSafeSeconds}s. " +
                                 "Check the Animation Events on the attack clip. Forcing Idle.");
                nextLightIndex = 0;   // a broken swing restarts the chain
                SetState(CombatState.Idle);
            }
        }
    }

    private void UpdateBlock(bool dodging)
    {
        Mouse mouse = Mouse.current;
        Gamepad pad = Gamepad.current;
        bool held = (mouse != null && mouse.rightButton.isPressed)
                 || (pad != null && pad.leftShoulder.isPressed);

        bool blocking = held && State == CombatState.Idle && !dodging;
        if (blocking == IsBlocking)
            return;

        IsBlocking = blocking;
        if (animator != null)
            animator.SetBool(BlockingHash, blocking);
        if (logStateChanges)
            Debug.Log($"[Combat] {name} block {(blocking ? "up" : "down")}");
    }

    // Tap = light attack (on release), hold = heavy attack (once held long enough).
    private void UpdateAttackInput()
    {
        Mouse mouse = Mouse.current;
        Gamepad pad = Gamepad.current;
        // A click that only takes back the mouse cursor is not an attack.
        bool clickForCursor = mover != null && mover.IsClickForCursor;
        bool pressed = (mouse != null && mouse.leftButton.wasPressedThisFrame && !clickForCursor)
                    || (pad != null && pad.buttonWest.wasPressedThisFrame);
        bool held = (mouse != null && mouse.leftButton.isPressed)
                 || (pad != null && pad.buttonWest.isPressed);

        if (pressed)
        {
            attackPressPending = true;
            attackPressStart = Time.time;
        }
        if (!attackPressPending)
            return;

        if (!held)
        {
            attackPressPending = false;
            TryLightAttack();
        }
        else if (Time.time - attackPressStart >= heavyHoldTime)
        {
            attackPressPending = false;
            TryHeavyAttack();
        }
    }

    /// <summary>
    /// Start the next light swing in the chain (or swing 1 if the chain has
    /// lapsed). Returns false if the fighter can't attack right now.
    /// </summary>
    public bool TryLightAttack()
    {
        if (State != CombatState.Idle || IsBlocking)
            return false;

        int index = Time.time - lastAttackEndTime <= chainWindow ? nextLightIndex : 0;
        nextLightIndex = (index + 1) % LightAttackCount;
        CurrentLightIndex = index;
        IsHeavyAttack = false;

        SetState(CombatState.Windup);
        animator.SetInteger(AttackIndexHash, index);
        animator.SetTrigger(AttackLightHash);
        return true;
    }

    /// <summary>Start a heavy attack. Returns false if the fighter can't attack right now.</summary>
    public bool TryHeavyAttack()
    {
        if (State != CombatState.Idle || IsBlocking)
            return false;

        nextLightIndex = 0;   // a heavy attack ends the light chain
        IsHeavyAttack = true;

        SetState(CombatState.Windup);
        animator.SetTrigger(AttackHeavyHash);
        return true;
    }

    // ---- Getting hit ----

    private void OnHitReceived(HitResult result, Transform attacker)
    {
        if (result == HitResult.Hit)
        {
            // Cancel whatever we were doing and flinch (one of the hit reactions).
            animator.ResetTrigger(AttackLightHash);
            animator.ResetTrigger(AttackHeavyHash);
            nextLightIndex = 0;   // being hit breaks the light chain
            animator.SetFloat(HitVariantHash, UnityEngine.Random.Range(0, Mathf.Max(1, hitVariants)));
            animator.SetTrigger(HitHash);
            SetState(CombatState.Stunned);
            StartKnockback(attacker, knockbackDistance);
        }
        else if (result == HitResult.Blocked)
        {
            animator.SetFloat(BlockHitVariantHash, UnityEngine.Random.Range(0, Mathf.Max(1, blockHitVariants)));
            animator.SetTrigger(BlockHitHash);
            StartKnockback(attacker, blockKnockbackDistance);
        }
    }

    private void StartKnockback(Transform attacker, float distance)
    {
        if (attacker == null || distance <= 0f || knockbackTime <= 0f)
            return;

        Vector3 away = transform.position - attacker.position;
        away.y = 0f;
        if (away.sqrMagnitude < 0.0001f)
            away = -transform.forward;

        knockbackVelocity = away.normalized * (distance / knockbackTime);
        knockbackTimer = knockbackTime;
    }

    private void UpdateKnockback()
    {
        if (knockbackTimer <= 0f || characterController == null)
            return;

        float dt = Mathf.Min(Time.deltaTime, knockbackTimer);
        knockbackTimer -= dt;
        characterController.Move(knockbackVelocity * dt);
    }

    // ---- Dealing hits ----

    // HitCalculator found a target this swing (once per target per swing).
    // Every attack rule goes through here, so this is where directional
    // blocks, parries, poise damage and body-part multipliers will read
    // 'hit' (which part, where on the blade, when in the frame).
    private void OnBladeHit(HitBody target, BladeHit hit)
    {
        if (target.Health != null)
            target.Health.TakeHit(IsHeavyAttack ? heavyDamage : attackDamage, transform);
    }

    // ---- Animation Event receivers (names must match the events exactly) ----

    public void ActiveStart()
    {
        if (State == CombatState.Windup)
            SetState(CombatState.Active);
    }

    public void ActiveEnd()
    {
        if (State == CombatState.Active)
            SetState(CombatState.Recovery);
    }

    public void AttackEnd()
    {
        // Ignore it while stunned: an attack that was interrupted by a hit can
        // still fire its last event as it blends out.
        if (State == CombatState.Windup || State == CombatState.Active || State == CombatState.Recovery)
        {
            lastAttackEndTime = Time.time;   // the next light attack may continue the chain
            SetState(CombatState.Idle);
        }
    }

    private void SetState(CombatState next)
    {
        State = next;
        timeInState = 0f;

        if (logStateChanges)
            Debug.Log($"[Combat] {name} -> {next}");

        StateChanged?.Invoke(next);
    }
}