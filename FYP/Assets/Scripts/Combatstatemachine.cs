using System;
using System.Collections.Generic;
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
/// Hits: while the state is Active, anything with a Health component inside
/// the reach (a slice in front of you, attackRange metres long and attackArc
/// degrees wide) takes attackDamage. Each target can be hit once per swing.
/// </summary>
public class CombatStateMachine : MonoBehaviour
{
    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;

    [Header("Light attack hit")]
    [SerializeField] private float attackDamage = 20f;
    [Tooltip("How far in front of you the swing reaches, in metres.")]
    [SerializeField] private float attackRange = 2f;
    [Tooltip("How wide the swing is, in degrees.")]
    [Range(10f, 360f)]
    [SerializeField] private float attackArc = 100f;
    [Tooltip("Which layers the swing can hit.")]
    [SerializeField] private LayerMask hitLayers = ~0;

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
    private readonly HashSet<Health> hitThisSwing = new HashSet<Health>();
    private readonly Collider[] overlapBuffer = new Collider[32];

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
    }

    private void OnDestroy()
    {
        if (health != null)
            health.HitReceived -= OnHitReceived;
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
            if (State == CombatState.Active)
                SweepForHits();

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

    // ---- Hit detection ----

    private void SweepForHits()
    {
        Vector3 origin = transform.position + Vector3.up;
        int count = Physics.OverlapSphereNonAlloc(origin, attackRange, overlapBuffer,
                                                  hitLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Health target = overlapBuffer[i].GetComponentInParent<Health>();
            if (target == null || target.transform == transform || hitThisSwing.Contains(target))
                continue;

            Vector3 toTarget = target.transform.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f
                && Vector3.Angle(transform.forward, toTarget) > attackArc * 0.5f)
                continue;

            hitThisSwing.Add(target);
            target.TakeHit(attackDamage, transform);
        }
    }

    private void OnDrawGizmosSelected()
    {
        // Shows the swing's reach in the Scene view when the Player is selected.
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.8f);
        Vector3 origin = transform.position + Vector3.up;
        float half = attackArc * 0.5f;
        Vector3 left = Quaternion.Euler(0f, -half, 0f) * transform.forward * attackRange;
        Vector3 right = Quaternion.Euler(0f, half, 0f) * transform.forward * attackRange;
        Gizmos.DrawLine(origin, origin + left);
        Gizmos.DrawLine(origin, origin + right);
        Gizmos.DrawLine(origin + left, origin + transform.forward * attackRange);
        Gizmos.DrawLine(origin + right, origin + transform.forward * attackRange);
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

        if (next == CombatState.Windup)
            hitThisSwing.Clear();

        if (logStateChanges)
            Debug.Log($"[Combat] {name} -> {next}");

        StateChanged?.Invoke(next);
    }
}