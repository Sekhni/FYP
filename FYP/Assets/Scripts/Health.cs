using System;
using UnityEngine;

public enum HitResult
{
    Ignored,   // already dead
    Hit,       // took full damage
    Blocked,   // guard was up and facing the attacker
    Dodged     // rolling: the hit passed through
}

/// <summary>
/// Health for anything that can be hit (the player and the training dummy).
/// Call TakeHit to deal damage. It checks, in this order:
///   1. Rolling (PlayerController.IsDodging)  -> Dodged, no damage
///   2. Blocking (CombatStateMachine.IsBlocking) while the attacker is in
///      front of you                          -> Blocked, chip damage only
///   3. Otherwise                             -> Hit, full damage
/// Also draws a simple health bar (top-left of the screen, or above the head).
/// </summary>
public class Health : MonoBehaviour
{
    [Header("Health")]
    [SerializeField] private float maxHealth = 100f;
    [Tooltip("Seconds after reaching 0 before health refills. 0 = never (stays dead).")]
    [SerializeField] private float refillAfterDeath = 2f;

    [Header("Blocking")]
    [Tooltip("Share of damage that still gets through a block. 0 = blocks stop everything.")]
    [Range(0f, 1f)]
    [SerializeField] private float blockedDamageShare = 0f;
    [Tooltip("How wide the guard covers, in degrees. 120 = attacks from the front and front-sides are blocked, attacks from behind are not.")]
    [Range(0f, 360f)]
    [SerializeField] private float blockArc = 120f;

    [Header("Health bar")]
    [SerializeField] private bool showBar = true;
    [Tooltip("On = bar in the top-left corner (use for the player). Off = bar floats above the head.")]
    [SerializeField] private bool barInCorner = false;
    [SerializeField] private string barLabel = "";
    [SerializeField] private float barHeightAboveFeet = 2.3f;

    private CombatStateMachine combat;
    private PlayerController mover;
    private float deadTimer;
    private float flashTimer;
    private HitResult lastResult;

    public float Current { get; private set; }
    public float Max => maxHealth;
    public bool IsDead => Current <= 0f;

    /// <summary>Raised on every hit attempt that isn't ignored.</summary>
    public event Action<HitResult, float> Damaged;
    /// <summary>Like Damaged, but also says who attacked (for knockback and reactions).</summary>
    public event Action<HitResult, Transform> HitReceived;
    public event Action Died;
    public event Action Refilled;

    private void Awake()
    {
        Current = maxHealth;
        combat = GetComponent<CombatStateMachine>();
        mover = GetComponent<PlayerController>();
        if (string.IsNullOrEmpty(barLabel))
            barLabel = name;
    }

    private void Update()
    {
        if (flashTimer > 0f)
            flashTimer -= Time.deltaTime;

        if (IsDead && refillAfterDeath > 0f)
        {
            deadTimer += Time.deltaTime;
            if (deadTimer >= refillAfterDeath)
                Refill();
        }
    }

    /// <summary>Try to deal damage. 'attacker' is used to check block direction.</summary>
    public HitResult TakeHit(float damage, Transform attacker)
    {
        if (IsDead)
            return HitResult.Ignored;

        HitResult result;
        float dealt;

        if (mover != null && mover.IsDodging)
        {
            result = HitResult.Dodged;
            dealt = 0f;
        }
        else if (combat != null && combat.IsBlocking && IsInFront(attacker))
        {
            result = HitResult.Blocked;
            dealt = damage * blockedDamageShare;
        }
        else
        {
            result = HitResult.Hit;
            dealt = damage;
        }

        Current = Mathf.Max(0f, Current - dealt);
        lastResult = result;
        flashTimer = 0.25f;

        Debug.Log($"[Health] {name}: {result} ({dealt:0} damage) -> {Current:0}/{maxHealth:0}");
        Damaged?.Invoke(result, dealt);
        HitReceived?.Invoke(result, attacker);

        if (IsDead)
        {
            deadTimer = 0f;
            Debug.Log($"[Health] {name} died");
            Died?.Invoke();
        }

        return result;
    }

    public void Refill()
    {
        Current = maxHealth;
        deadTimer = 0f;
        Refilled?.Invoke();
    }

    private bool IsInFront(Transform attacker)
    {
        if (attacker == null)
            return true;

        Vector3 toAttacker = attacker.position - transform.position;
        toAttacker.y = 0f;
        if (toAttacker.sqrMagnitude < 0.0001f)
            return true;

        return Vector3.Angle(transform.forward, toAttacker) <= blockArc * 0.5f;
    }

    // ------------------------------------------------------------ Health bar

    private static Texture2D white;

    private void OnGUI()
    {
        if (!showBar)
            return;
        if (white == null)
        {
            white = new Texture2D(1, 1);
            white.SetPixel(0, 0, Color.white);
            white.Apply();
        }

        const float width = 220f, height = 18f;
        Rect rect;

        if (barInCorner)
        {
            rect = new Rect(20f, 20f, width, height);
        }
        else
        {
            Camera cam = Camera.main;
            if (cam == null)
                return;
            Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * barHeightAboveFeet);
            if (screen.z <= 0f)
                return; // behind the camera
            rect = new Rect(screen.x - width * 0.5f, Screen.height - screen.y - height, width, height);
        }

        float fill = maxHealth > 0f ? Current / maxHealth : 0f;
        Color barColor = flashTimer > 0f
            ? (lastResult == HitResult.Blocked ? new Color(0.4f, 0.7f, 1f)
             : lastResult == HitResult.Dodged  ? Color.white
             :                                   new Color(1f, 0.85f, 0.2f))
            : new Color(0.85f, 0.15f, 0.15f);

        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(rect, white);
        GUI.color = barColor;
        GUI.DrawTexture(new Rect(rect.x + 2f, rect.y + 2f, (rect.width - 4f) * fill, rect.height - 4f), white);
        GUI.color = Color.white;

        string text = IsDead ? $"{barLabel}  DOWN" : $"{barLabel}  {Current:0}/{maxHealth:0}";
        GUI.Label(new Rect(rect.x + 6f, rect.y, rect.width, rect.height), text);
    }
}
