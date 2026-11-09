using System;
using UnityEngine;
using UnityEngine.InputSystem;

public enum CombatState
{
    Idle,
    Windup,     // attack started, blade not dangerous yet
    Active,     // blade can hit (hit maths runs only in this state)
    Recovery    // swing finished, fighter is vulnerable
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
/// </summary>
public class CombatStateMachine : MonoBehaviour
{
    [Header("References (auto-filled if left empty)")]
    [SerializeField] private Animator animator;

    [Header("Safety")]
    [Tooltip("If an attack never reaches Idle (for example a missing Animation Event), force Idle after this many seconds.")]
    [SerializeField] private float failSafeSeconds = 2f;

    [Header("Debug")]
    [SerializeField] private bool logStateChanges = true;

    private static readonly int AttackLightHash = Animator.StringToHash("AttackLight");
    private static readonly int BlockingHash = Animator.StringToHash("Blocking");

    private PlayerController mover;

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
    }

    private void Update()
    {
        bool dodging = mover != null && mover.IsDodging;
        UpdateBlock(dodging);

        if (State == CombatState.Idle && !IsBlocking && !dodging)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
                TryLightAttack();
        }
        else
        {
            timeInState += Time.deltaTime;
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
        if (State != CombatState.Idle)
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