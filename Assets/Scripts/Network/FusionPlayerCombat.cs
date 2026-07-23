using Fusion;
using UnityEngine;

/// <summary>
/// Shared Mode combat. Captures attack/aimable/domain input on the authority, fires
/// AutoAttackController / AimableAttackController in FixedUpdateNetwork, and replicates attack
/// starts + hit reactions to proxies via RPC. Damage is applied via RpcTakeDamage on the victim's
/// authority. Aimable is hold-Q to aim, release-Q to fire.
/// </summary>
public class FusionPlayerCombat : NetworkBehaviour
{
    [Networked] public NetworkBool NetworkedOverdrive { get; set; }
    [Networked] public NetworkBool NetworkedDomainActive { get; set; }
    [Networked] public NetworkBool NetworkedDomainStartup { get; set; }

    private Rigidbody2D               _rb;
    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private BaseDomainExpansion       _domain;
    private PlayerOverdrive           _overdrive;

    private bool _attackPressed, _aimableDown, _aimableUp, _domainPressed, _overdrivePressed;

    void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _health    = GetComponent<PlayerHealth>();
        _anim      = GetComponent<PlayerAnimationController>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();
        _domain    = GetComponent<BaseDomainExpansion>();
        _overdrive = GetComponent<PlayerOverdrive>();
    }

    public override void Spawned()
    {
        // Replicate auto/aimable attacks to proxies so they see the punch/skill anim too.
        if (_auto != null)
        {
            _auto.OnAttackStarted += OnLocalAutoAttackStarted;
            _auto.OnAttackEnded   += OnLocalAutoAttackEnded;
        }
        if (_aimable != null)
        {
            _aimable.OnAttackStarted += OnLocalAimableAttackStarted;
            _aimable.OnAttackEnded   += OnLocalAimableAttackEnded;
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (_auto != null)
        {
            _auto.OnAttackStarted -= OnLocalAutoAttackStarted;
            _auto.OnAttackEnded   -= OnLocalAutoAttackEnded;
        }
        if (_aimable != null)
        {
            _aimable.OnAttackStarted -= OnLocalAimableAttackStarted;
            _aimable.OnAttackEnded   -= OnLocalAimableAttackEnded;
        }
    }

    void Update()
    {
        if (Object == null || !Object.IsValid || !HasStateAuthority) return;
        if (Input.GetMouseButtonDown(0))        _attackPressed    = true;
        if (Input.GetKeyDown(KeyCode.Q))        _aimableDown      = true;
        if (Input.GetKeyUp(KeyCode.Q))          _aimableUp        = true;
        if (Input.GetKeyDown(KeyCode.E))        _domainPressed    = true;
        if (Input.GetKeyDown(KeyCode.LeftShift)) _overdrivePressed = true;
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        // Replicate our domain's open/closed + forming state every tick so the opponent's peer can apply
        // its cross-player effects (overdrive lock / regen freeze), the startup freeze, and the VFX/arena.
        NetworkedDomainActive  = _domain != null && _domain.IsActive;
        NetworkedDomainStartup = _domain != null && _domain.IsStartingUp;

        if (_health != null && _health.IsDead)
        {
            _overdrivePressed = false;
            if (NetworkedOverdrive) NetworkedOverdrive = false;
            _overdrive?.SetActive(false);
            return;
        }

        // Frozen during the death → respawn round reset: drop buffered input, kill overdrive.
        if (FusionPlayerSync.RoundResetting)
        {
            _attackPressed = _aimableDown = _aimableUp = _domainPressed = _overdrivePressed = false;
            if (NetworkedOverdrive) NetworkedOverdrive = false;
            _overdrive?.SetActive(false);
            return;
        }

        // Frozen while a domain forms (its 0.5s startup): drop buffered input.
        if (BaseDomainExpansion.PlayersFrozen)
        {
            _attackPressed = _aimableDown = _aimableUp = _domainPressed = _overdrivePressed = false;
            return;
        }

        // Overdrive is a toggle: each Shift press flips the replicated state. While our domain is up the
        // domain controls overdrive (free) — ignore presses and just mirror the real state.
        if (_domain != null && _domain.IsActive)
        {
            _overdrivePressed = false;
        }
        else
        {
            if (_overdrivePressed)
            {
                NetworkedOverdrive = !NetworkedOverdrive;
                _overdrivePressed  = false;
            }
            _overdrive?.SetActive(NetworkedOverdrive);
        }
        // Re-sync to the real state (SetActive can refuse when domain-locked; domain-free forces it on),
        // so the networked flag — and the proxy tint — always tracks what's really happening.
        if (_overdrive != null) NetworkedOverdrive = _overdrive.IsActive;

        // Match offline behavior (PlayerCombatController early-returns on hitstun): drop buffered
        // presses so a key tapped during the hurt animation doesn't fire the moment hitstun ends.
        if (_anim != null && _anim.IsHitstun)
        {
            _attackPressed = _aimableDown = _aimableUp = _domainPressed = false;
            return;
        }

        Vector2 aimPoint = FusionPlayerSync.GetMouseWorldPoint(transform);

        if (_attackPressed)
        {
            _auto?.TryAttack(aimPoint);
            _attackPressed = false;
        }
        if (_aimableDown)
        {
            _aimable?.StartAiming();
            _aimableDown = false;
        }
        if (_aimableUp)
        {
            _aimable?.ReleaseAttack(aimPoint);
            _aimableUp = false;
        }
        if (_domainPressed && _domain != null)
        {
            if (_domain.IsActive) _domain.Deactivate();
            else                  _domain.Activate();
            _domainPressed = false;
        }
    }

    public override void Render()
    {
        // Proxies mirror the authority's overdrive state so the red tint matches what the
        // local player sees. Authority's call here re-asserts the same state (no-op via
        // PlayerOverdrive.SetActive's idempotent guard).
        if (!HasStateAuthority)
        {
            _overdrive?.SetActive(NetworkedOverdrive);
            _domain?.SetNetworkActive(NetworkedDomainActive);    // mirror opponent's domain (registry/VFX/arena)
            _domain?.SetNetworkStartup(NetworkedDomainStartup);  // …and its forming state (freeze on this peer)
        }
    }

    private void OnLocalAutoAttackStarted(string action, Vector2 facing)
    {
        if (HasStateAuthority) RpcAutoAttackStartedOnProxies(action, facing);
    }

    private void OnLocalAutoAttackEnded()
    {
        if (HasStateAuthority) RpcAutoAttackEndedOnProxies();
    }

    private void OnLocalAimableAttackStarted(Vector2 aimDir, string action)
    {
        if (HasStateAuthority) RpcAimableAttackStartedOnProxies(aimDir, action);
    }

    private void OnLocalAimableAttackEnded()
    {
        if (HasStateAuthority) RpcAimableAttackEndedOnProxies();
    }

    // Hitter calls this on the victim. Only the victim's StateAuthority actually applies damage.
    // FusionPlayerSync's OnLocalHealthChanged / OnLocalDeath subscriptions propagate the HP/death
    // change into [Networked] state for proxies — no manual mirror needed here.
    [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
    public void RpcTakeDamage(float amount, Vector2 knockback, float hitstun)
    {
        if (_health == null || _health.IsDead || _health.IsInvincible) return;

        // Face away from the hit BEFORE the hurt anim so the post-hurt idle uses the correct facing.
        _anim?.SetFacingFromHit(knockback);
        _anim?.SetNextHitstun(hitstun);

        _health.TakeDamage(amount);

        if (_rb != null && _rb.simulated && !_health.IsDead)
        {
            // Combo decay: TakeDamage just ran PlayHurt, so the multiplier reflects this hit's combo depth.
            float comboScale = _anim != null ? _anim.ComboKnockbackMultiplier : 1f;
            Vector2 impulse = knockback * comboScale;
            _rb.AddForce(impulse, ForceMode2D.Impulse);
            _anim?.SetLastHitKnockback(impulse.magnitude);  // a later wall collision uses this for the splat
            _anim?.CheckWallSplatOnHit();                   // …or splat now if already pinned to a wall
        }

        // Authority's own hurt animation fires via OnDamaged -> PlayerCombatController. Tell proxies separately.
        RpcPlayHurtOnProxies(knockback, hitstun);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcPlayHurtOnProxies(Vector2 knockback, float hitstun)
    {
        _anim?.SetFacingFromHit(knockback);
        _anim?.SetNextHitstun(hitstun);
        _anim?.PlayHurt();
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcAutoAttackStartedOnProxies(string action, Vector2 facing)
    {
        bool directional = _auto == null || _auto.TakeDirection;
        if (directional) _anim?.SetFacing(facing);
        _anim?.PlayAutoAttack(action, directional, _auto != null ? _auto.PlaybackSpeed : 1f);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcAutoAttackEndedOnProxies()
    {
        _anim?.RefreshMovementState();
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcAimableAttackStartedOnProxies(Vector2 aimDir, string action)
    {
        bool directional = _aimable == null || _aimable.TakeDirection;
        _anim?.PlayAimableAttack(aimDir, action, directional, _aimable != null ? _aimable.PlaybackSpeed : 1f);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcAimableAttackEndedOnProxies()
    {
        _anim?.RefreshMovementState();
    }
}
