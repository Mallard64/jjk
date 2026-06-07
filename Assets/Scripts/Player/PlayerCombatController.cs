using UnityEngine;

/// <summary>
/// Bridges input → auto attack / aimable attack / domain expansion.
/// Also wires health events to animation hooks (hurt, death).
/// Aimable attack is hold-Q to aim, release-Q to fire.
/// </summary>
public class PlayerCombatController : MonoBehaviour
{
    private PlayerInputHandler        _input;
    private PlayerAnimationController _anim;
    private PlayerHealth              _health;
    private PlayerMovement            _movement;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private BaseDomainExpansion       _domain;
    private PlayerRoll                _roll;
    private PlayerOverdrive           _overdrive;
    private FusionPlayerSync          _net;

    [Header("Damage Feedback")]
    [Tooltip("Damage at/above this adds screen shake to the red flash (mid tier).")]
    [SerializeField] private float midDamageThreshold  = 20f;
    [Tooltip("Damage at/above this is treated as a heavy/overdrive hit: the flash turns white and the screen also inverts (high tier).")]
    [SerializeField] private float highDamageThreshold = 25f;

    void Awake()
    {
        _input     = GetComponent<PlayerInputHandler>();
        _anim      = GetComponent<PlayerAnimationController>();
        _health    = GetComponent<PlayerHealth>();
        _movement  = GetComponent<PlayerMovement>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();
        _domain    = GetComponent<BaseDomainExpansion>();
        _roll      = GetComponent<PlayerRoll>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _net       = GetComponent<FusionPlayerSync>();
    }

    void Start()
    {
        if (_health != null)
        {
            _health.OnDamaged += OnDamaged;
            _health.OnDeath   += OnDeath;
        }
    }

    void OnDestroy()
    {
        if (_health != null)
        {
            _health.OnDamaged -= OnDamaged;
            _health.OnDeath   -= OnDeath;
        }
    }

    void Update()
    {
        if (_net != null) return; // online: FusionPlayerCombat drives attacks
        if (_health != null && _health.IsDead) return;
        if (_input == null) return;

        // Overdrive is a toggle: each Shift press flips it on/off. Done outside the hitstun gate so
        // you can toggle while staggered; PlayerOverdrive forces itself off on death.
        if (_overdrive != null && _input.OverdriveDown) _overdrive.Toggle();

        if (_anim != null && _anim.IsHitstun) return;

        // Roll: prefer the current move direction; fall back to aim if standing still.
        if (_input.RollDown && _roll != null)
        {
            var inp = _input.Current;
            Vector2 dir = inp.MoveDir.sqrMagnitude > 0.01f ? inp.MoveDir : inp.AimDir;
            _roll.TryRoll(dir);
        }

        if (_input.AutoAttackDown && _auto != null)
            _auto.TryAttack(_input.Current.AimPoint);

        if (_aimable != null)
        {
            if (_input.AimableAttackDown) _aimable.StartAiming();
            if (_input.AimableAttackUp)   _aimable.ReleaseAttack(_input.Current.AimPoint);
        }

        if (_input.DomainDown && _domain != null)
        {
            if (_domain.IsActive) _domain.Deactivate();
            else                  _domain.Activate();
        }
    }

    private void OnDamaged(float amount, GameObject source)
    {
        // Self-inflicted damage (overdrive HP bleed) shouldn't flinch the player or fire hit feedback.
        if (source == gameObject) return;

        // Cancel in-progress attacks so their coroutines don't fight the hurt-lock for movement control.
        _auto?.Cancel();
        _aimable?.Cancel();
        _anim?.PlayHurt();

        // Cumulative damage feedback, scaled by how hard the hit landed. OnDamaged only fires on the
        // victim's authority in online play (proxies use ForceSetHp, which fires OnHealthChanged not
        // OnDamaged) — so these effects only affect the hit player's own view.
        bool high = amount >= highDamageThreshold;
        ScreenEffects.Instance?.Flash(high ? Color.white : Color.red);
        if (amount >= midDamageThreshold) CameraShake.Instance?.Shake();
        if (high)                         ScreenEffects.Instance?.InvertFlash();
    }

    private void OnDeath()
    {
        _anim?.PlayDeath();
        if (_movement != null)
        {
            _movement.CanMove = false;
            _movement.Freeze();
        }
    }
}
