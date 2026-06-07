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
    private Color                     _originalColor = Color.white;
    private bool                      _originalColorCaptured;

    void Awake()
    {
        _energy = GetComponent<CursedEnergy>();
        _health = GetComponent<PlayerHealth>();
        _anim   = GetComponent<PlayerAnimationController>();
        _net    = GetComponent<INetworkAdapter>();
        if (spriteRenderer == null) spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            _originalColor = spriteRenderer.color;
            _originalColorCaptured = true;
        }
    }

    /// <summary>Set overdrive on/off. Forced off while dead. Proxies call this every frame with the
    /// replicated state; the local player flips it via Toggle().</summary>
    public void SetActive(bool value)
    {
        if (_health != null && _health.IsDead) value = false;
        if (IsActive == value) return;
        IsActive = value;
        ApplyTint();
        _anim?.SetOverdriveMode(value);

        // Glow the screen only for the local player (offline, or the online authority) — never for an
        // opponent proxy, whose overdrive is mirrored here for the red sprite tint.
        if (_net == null || _net.IsLocalPlayer)
            ScreenEffects.Instance?.SetOverdriveGlow(value);
    }

    /// <summary>Flip overdrive on/off — the toggle entry point for the local player's Shift press.</summary>
    public void Toggle() => SetActive(!IsActive);

    void Update()
    {
        if (!IsActive) return;
        // Drain runs on authority only. Proxies still get the tint via SetActive (called by
        // FusionPlayerCombat.Render based on the networked flag) but never debit resources.
        if (_net != null && !_net.IsAuthority) return;
        if (_health != null && _health.IsDead) { SetActive(false); return; }

        if (_energy != null && _energy.CurrentEnergy > 0f)
        {
            _energy.Drain(energyDrainPerSecond * Time.deltaTime);
        }
        else if (_health != null)
        {
            // No energy left — bleed HP. TakeDamage handles death via PlayerHealth.Die().
            _health.TakeDamage(healthDrainPerSecond * Time.deltaTime, gameObject);
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
