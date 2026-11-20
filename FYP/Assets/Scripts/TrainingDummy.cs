using UnityEngine;

/// <summary>
/// A practice target that also attacks you, so you can test hitting,
/// blocking and rolling.
///
/// Loop: wait -> wind up (turns yellow, telegraphing the swing) -> strike.
/// The strike hits the player if they are within attackRange and in front of
/// the dummy. Its colour after the strike tells you what happened:
///   red  = you got hit,   blue = you blocked,   white = you rolled through.
/// When its health hits 0 it tips over, then stands back up when Health refills.
///
/// The dummy's own strike is a simple range-and-arc check, not a blade
/// sweep: it is a test target, not a fighter. Hitting the dummy does use
/// the blade (it has a HitBody).
///
/// Add it with the menu: Tools > Blade and Reflex > Add Training Dummy.
/// </summary>
[RequireComponent(typeof(Health))]
public class TrainingDummy : MonoBehaviour
{
    [Header("Attacking")]
    [SerializeField] private bool attacks = true;
    [Tooltip("Seconds between swings (from the end of one swing to the start of the next wind-up).")]
    [SerializeField] private float timeBetweenAttacks = 2.5f;
    [Tooltip("How long it glows yellow before striking. This is your reaction window.")]
    [SerializeField] private float windUpTime = 0.7f;
    [SerializeField] private float attackDamage = 15f;
    [Tooltip("How far the strike reaches, in metres.")]
    [SerializeField] private float attackRange = 2.5f;
    [Tooltip("How wide the strike is, in degrees.")]
    [SerializeField] private float attackArc = 120f;
    [Tooltip("Only start attacking when the player is closer than this.")]
    [SerializeField] private float noticeRange = 6f;

    [Header("Facing")]
    [SerializeField] private bool facePlayer = true;
    [SerializeField] private float turnSpeed = 180f;

    [Header("Colours")]
    [SerializeField] private Color idleColour = new Color(0.55f, 0.45f, 0.35f);
    [SerializeField] private Color windUpColour = new Color(1f, 0.8f, 0.1f);
    [SerializeField] private Color hitPlayerColour = new Color(0.9f, 0.15f, 0.15f);
    [SerializeField] private Color blockedColour = new Color(0.3f, 0.6f, 1f);
    [SerializeField] private Color dodgedColour = Color.white;
    [SerializeField] private Color hurtColour = new Color(1f, 0.5f, 0.5f);

    private enum Phase { Waiting, WindUp, ShowResult }

    private Health health;
    private Health player;
    private Renderer[] renderers;
    private MaterialPropertyBlock block;
    private Phase phase = Phase.Waiting;
    private float timer;
    private Color currentColour;
    private float hurtFlash;
    private Quaternion uprightRotation;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");

    private void Awake()
    {
        health = GetComponent<Health>();
        renderers = GetComponentsInChildren<Renderer>();
        block = new MaterialPropertyBlock();
        uprightRotation = transform.rotation;
        currentColour = idleColour;
        timer = timeBetweenAttacks;

        health.Damaged += OnDamaged;
        health.Died += OnDied;
        health.Refilled += OnRefilled;
    }

    private void Start()
    {
        PlayerController pc = FindAnyObjectByType<PlayerController>();
        if (pc == null)
        {
            Debug.LogWarning("TrainingDummy: no PlayerController found in the scene, so the dummy has nothing to attack.");
            return;
        }

        player = pc.GetComponent<Health>();
        if (player == null)
            Debug.LogWarning("TrainingDummy: the Player has no Health component, so the dummy's swings can't hurt it.");
    }

    private void OnDestroy()
    {
        if (health == null)
            return;
        health.Damaged -= OnDamaged;
        health.Died -= OnDied;
        health.Refilled -= OnRefilled;
    }

    private void Update()
    {
        if (hurtFlash > 0f)
            hurtFlash -= Time.deltaTime;

        if (health.IsDead)
        {
            ApplyColour(Color.grey);
            return;
        }

        if (facePlayer && player != null)
            TurnTowards(player.transform.position);

        if (attacks && player != null)
            UpdateAttack();

        ApplyColour(hurtFlash > 0f ? hurtColour : currentColour);
    }

    private void UpdateAttack()
    {
        timer -= Time.deltaTime;

        switch (phase)
        {
            case Phase.Waiting:
                if (timer <= 0f)
                {
                    if (Flat(player.transform.position - transform.position).magnitude > noticeRange)
                    {
                        timer = 0.5f;   // player too far: check again shortly
                        break;
                    }
                    phase = Phase.WindUp;
                    timer = windUpTime;
                    currentColour = windUpColour;
                }
                break;

            case Phase.WindUp:
                if (timer <= 0f)
                {
                    currentColour = Strike();
                    phase = Phase.ShowResult;
                    timer = 0.4f;
                }
                break;

            case Phase.ShowResult:
                if (timer <= 0f)
                {
                    currentColour = idleColour;
                    phase = Phase.Waiting;
                    timer = timeBetweenAttacks;
                }
                break;
        }
    }

    private Color Strike()
    {
        Vector3 toPlayer = Flat(player.transform.position - transform.position);
        bool inReach = toPlayer.magnitude <= attackRange
                    && Vector3.Angle(transform.forward, toPlayer) <= attackArc * 0.5f;
        if (!inReach)
            return idleColour;   // whiffed

        switch (player.TakeHit(attackDamage, transform))
        {
            case HitResult.Blocked: return blockedColour;
            case HitResult.Dodged:  return dodgedColour;
            case HitResult.Hit:     return hitPlayerColour;
            default:                return idleColour;
        }
    }

    // ------------------------------------------------------------ Reactions

    private void OnDamaged(HitResult result, float amount)
    {
        if (result == HitResult.Hit)
            hurtFlash = 0.15f;
    }

    private void OnDied()
    {
        // Tip over backwards.
        transform.rotation = uprightRotation * Quaternion.Euler(-80f, 0f, 0f);
        phase = Phase.Waiting;
        timer = timeBetweenAttacks;
        currentColour = idleColour;
    }

    private void OnRefilled()
    {
        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }

    // ------------------------------------------------------------ Helpers

    private void TurnTowards(Vector3 point)
    {
        Vector3 dir = Flat(point - transform.position);
        if (dir.sqrMagnitude < 0.0001f)
            return;
        Quaternion target = Quaternion.LookRotation(dir, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
        uprightRotation = transform.rotation;
    }

    private void ApplyColour(Color c)
    {
        foreach (Renderer r in renderers)
        {
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);   // URP Lit
            block.SetColor(ColorId, c);       // Built-in / other shaders
            r.SetPropertyBlock(block);
        }
    }

    private static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
