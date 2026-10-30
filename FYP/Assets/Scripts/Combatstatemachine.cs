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
/// Minimal combat state machine for Blade & Reflex (Month 1).
/// Left mouse button starts a light attack. Three Animation Events on the
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

    private float timeInState;

    public CombatState State { get; private set; } = CombatState.Idle;

    /// <summary>The player may only walk while idle.</summary>
    public bool CanMove => State == CombatState.Idle;

    /// <summary>True only while the blade is allowed to hit something.</summary>
    public bool IsAttackActive => State == CombatState.Active;

    public event Action<CombatState> StateChanged;

    private void Awake()
    {
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        if (State == CombatState.Idle)
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

    public bool TryLightAttack()
    {
        if (State != CombatState.Idle)
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