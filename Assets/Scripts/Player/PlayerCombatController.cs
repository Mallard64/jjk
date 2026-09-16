using UnityEngine;

/// <summary>
/// Bridges input → auto attack / aimable attack / null field.
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
    private BaseNullField             _field;
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
        _field     = GetComponent<BaseNullField>();
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
        // Only bail when a runner is actually driving this fighter — then FusionPlayerCombat owns
        // attacks. A dormant sync component (offline Instantiate) must still let the local path run,
        // which is the same test OnDamaged below and PlayerMovement/PlayerRoll already use.
        if (_net != null && _net.Object != null && _net.Object.IsValid) return;
        if (_health != null && _health.IsDead) return;
        if (_input == null) return;
        if (BaseNullField.PlayersFrozen) return;  // frozen while a null field forms

        // Overdrive is a toggle: each Shift press flips it on/off. Suppressed while a null field is up — the
        // null field controls overdrive (free) — so a stray press can't drop it.
        if (_overdrive != null && _input.OverdriveDown && (_field == null || !_field.IsActive))
            _overdrive.Toggle();

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

        if (_input.NullFieldDown && _field != null)
        {
            if (_field.IsActive) _field.Deactivate();
            else                  _field.Activate();
        }
    }

    private void OnDamaged(float amount, GameObject source)
    {
        // Cancel in-progress attacks so their coroutines don't fight the hurt-lock for movement control.
        _auto?.Cancel();
        _aimable?.Cancel();
        _anim?.PlayHurt();

        // Online this fires on the SERVER for both fighters, so the feedback is limited to the one this
        // peer controls — the hit client gets its own via FusionPlayerCombat.RpcHurt. Offline, _net is
        // dormant and both players fire locally as before.
        if (_net == null || _net.Object == null || !_net.Object.IsValid || _net.IsLocalPlayer)
            PlayDamageFeedback(amount);
    }

    /// <summary>Screen feedback for a hit taken by the fighter this peer is watching, scaled by how hard
    /// it landed. Also called by FusionPlayerCombat.RpcHurt on the client whose fighter was hit.</summary>
    public void PlayDamageFeedback(float amount)
    {
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
