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
/// Left mouse button starts a light attack.
/// Hold right mouse button (gamepad: left bumper) to block. You can't attack
/// while blocking, and rolling (PlayerController) drops the block. Three Animation Events on the
/// attack clip move the state forward:
///     ActiveStart  ->  Active
///     ActiveEnd    ->  Recovery
///     AttackEnd    ->  Idle
/// The events must be on a clip played by the Animator that sits on the same
/// GameObject as this script (the Player root).
///
/// Getting hit (needs a Health component on the same object):
///   Hit     -> Stunned for stunTime: the HitReact animation plays, any attack
///              in progress is cancelled, and you slide back knockbackDistance.
///   Blocked -> the BlockImpact animation plays and you slide back a little;
///              you keep your guard.
///   Dodged  -> nothing.
///
/// Hits: while the state is Active, HitCalculator (same object) sweeps the
/// blade against every HitBody's capsules. When it reports a target, that
/// target's Health takes attackDamage. Each target can be hit once per swing.
/// </summary>
public class CombatStateMachine : MonoBehaviour
{
    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;

    [Header("Light attack hit")]
    [Tooltip("Damage dealt when the blade touches a target (before its block or dodge is applied).")]
    [SerializeField] private float attackDamage = 20f;

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

    private static readonly int AttackLightHash = Animator.StringToHash("AttackLight");
    private static readonly int BlockingHash = Animator.StringToHash("Blocking");
    private static readonly int HitHash = Animator.StringToHash("Hit");
    private static readonly int BlockHitHash = Animator.StringToHash("BlockHit");

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
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                TryLightAttack();
        }
        else if (State != CombatState.Idle)
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

    public bool TryLightAttack()
    {
        if (State != CombatState.Idle || IsBlocking)
            return false;

        SetState(CombatState.Windup);
        animator.SetTrigger(AttackLightHash);
        return true;
    }

    // ---- Getting hit ----

    private void OnHitReceived(HitResult result, Transform attacker)
    {
        if (result == HitResult.Hit)
        {
            // Cancel whatever we were doing and flinch.
            animator.ResetTrigger(AttackLightHash);
            animator.SetTrigger(HitHash);
            SetState(CombatState.Stunned);
            StartKnockback(attacker, knockbackDistance);
        }
        else if (result == HitResult.Blocked)
        {
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
            target.Health.TakeHit(attackDamage, transform);
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
            SetState(CombatState.Idle);
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