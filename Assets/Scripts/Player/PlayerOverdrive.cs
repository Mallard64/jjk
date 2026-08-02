using UnityEngine;

/// <summary>
/// "Overdrive" stance toggled by the local player (press to turn on, press again to turn off).
/// While active: drains cursed energy per second, then health once energy is depleted; tints the
/// sprite red as a visual marker; and signals to AutoAttackController that the heavy auto-attack
/// variant should fire on the next click.
///
/// Authority-only drain: in online play, proxies receive the activation state via FusionPlayerCombat
/// (for the visual) but the actual resource burn happens on the authority.
/// </summary>
public class PlayerOverdrive : MonoBehaviour
{
    [Header("Drain")]
    [SerializeField] private float energyDrainPerSecond = 20f;
    [Tooltip("Per-second HP drain that kicks in once cursed energy is at 0. Drain stops if the player dies.")]
    [SerializeField] private float healthDrainPerSecond = 5f;

    [Header("Movement")]
    [Tooltip("Multiplied against PlayerMovement / FusionPlayerMovement's base moveSpeed while overdrive is active. 1 = no boost.")]
    [SerializeField] private float overdriveMoveSpeedMultiplier = 1.4f;

    [Header("Visual")]
    [Tooltip("SpriteRenderer to tint while overdrive is active. Auto-finds the first child renderer if left empty.")]
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private Color overdriveTint = new Color(1f, 0.35f, 0.35f, 1f);

    public bool IsActive { get; private set; }

    /// <summary>1 when inactive; serialized multiplier when active. Movement scripts read this when computing velocity.</summary>
    public float MoveSpeedMultiplier => IsActive ? overdriveMoveSpeedMultiplier : 1f;

    private CursedEnergy              _energy;
    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private INetworkAdapter           _net;
    private PlayerAudio               _audio;
    private Color                     _originalColor = Color.white;
    private bool                      _originalColorCaptured;

    void Awake()
    {
        _energy = GetComponent<CursedEnergy>();
        _health = GetComponent<PlayerHealth>();
        _anim   = GetComponent<PlayerAnimationController>();
        _net    = GetComponent<INetworkAdapter>();
        _audio  = GetComponent<PlayerAudio>();
        if (spriteRenderer == null) spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            _originalColor = spriteRenderer.color;
            _originalColorCaptured = true;
        }
    }

    /// <summary>Set overdrive on/off. Forced off while dead. Proxies call this every frame with the
    /// replicated state; the local player flips it via Toggle().</summary>
    private bool _domainLocked;

    /// <summary>Locked out of overdrive by the domain system (an enemy domain is active, or this player is
    /// in domain burnout). Forces overdrive off and refuses re-entry while locked.</summary>
    public void SetDomainLocked(bool locked)
    {
        _domainLocked = locked;
        if (locked && IsActive) SetActive(false);
    }

    private bool _domainFree;

    /// <summary>While true, the owner's active domain grants overdrive for FREE: it's forced on and
    /// neither CE nor HP drains. Releasing it (domain ends) drops overdrive.</summary>
    public void SetDomainFree(bool free)
    {
        if (_domainFree == free) return;
        _domainFree = free;
        SetActive(free);  // domain grants overdrive on open, drops it on close
        // Domain overdrive shows the red tint but NOT the white screen glow (the domain's own collapse
        // flicker is busy enough); force the glow off in case overdrive was already on manually.
        if (free && (_net == null || _net.IsLocalPlayer))
            ScreenEffects.Instance?.SetOverdriveGlow(false);
    }

    public void SetActive(bool value)
    {
        if (_health != null && _health.IsDead) value = false;
        if (_domainLocked && value) return;  // can't enter overdrive while domain-locked
        if (IsActive == value) return;
        IsActive = value;
        ApplyTint();
        _anim?.SetOverdriveMode(value);

        // Heard on every peer: the authority calls this from FixedUpdateNetwork and the others from
        // FusionPlayerCombat.Render with the replicated flag, and the equality guard above makes the body
        // a true one-shot. Skipped for the free overdrive a domain grants — the domain has its own cue,
        // same reason that case suppresses the screen glow below.
        if (value && !_domainFree) _audio?.PlayOverdrive();

        // Glow the screen only for the local player (offline, or the online authority) — never for an
        // opponent proxy, and never for domain-granted (free) overdrive.
        if (_net == null || _net.IsLocalPlayer)
            ScreenEffects.Instance?.SetOverdriveGlow(value && !_domainFree);
    }

    /// <summary>Flip overdrive on/off — the toggle entry point for the local player's Shift press.</summary>
    public void Toggle() => SetActive(!IsActive);

    void Update()
    {
        if (_domainFree)
        {
            // Domain-powered: keep it on (manual toggle can't drop it) and drain nothing — it's free.
            if (!IsActive && !(_health != null && _health.IsDead)) SetActive(true);
            return;
        }
        if (!IsActive) return;
        // Drain runs on authority only. Proxies still get the tint via SetActive (called by
        // FusionPlayerCombat.Render based on the networked flag) but never debit resources.
        if (_net != null && !_net.IsAuthority) return;
        if (_health != null && _health.IsDead) { SetActive(false); return; }

        // One cost per frame, paid out of cursed energy first and out of HP for whatever CE couldn't
        // cover. Branching on "CE > 0" instead never bleeds: passive regen puts a sliver back every
        // frame, so the meter reads fractionally above zero here and the HP drain never starts.
        float energyCost = energyDrainPerSecond * Time.deltaTime;
        float paidFromEnergy = 0f;
        if (_energy != null)
        {
            paidFromEnergy = Mathf.Min(energyCost, _energy.CurrentEnergy);
            _energy.Drain(paidFromEnergy);
        }

        if (paidFromEnergy < energyCost && _health != null)
        {
            // Reactionless: the bleed is self-inflicted, so it drives no hurt reaction, and unlike
            // TakeDamage it ignores invincibility — roll / aimable i-frames must not pause the cost.
            // Still updates the bar and kills at 0 via PlayerHealth.Die().
            _health.TakeReactionlessDamage(healthDrainPerSecond * Time.deltaTime);
        }
    }

    private void ApplyTint()
    {
        if (spriteRenderer == null) return;
        if (!_originalColorCaptured)
        {
            _originalColor = spriteRenderer.color;
            _originalColorCaptured = true;
        }
        spriteRenderer.color = IsActive ? overdriveTint : _originalColor;
    }
}
