using Fusion;
using UnityEngine;

/// <summary>
/// Host mode combat. Only the server simulates: it reads the controlling peer's FusionPlayerInput,
/// fires AutoAttackController / AimableAttackController in FixedUpdateNetwork, and replicates attack
/// starts and hit reactions to the other peers via RPC. Damage is dealt server-side by Hurtbox, which
/// calls ReplicateHurt so remote peers play the matching reaction.
/// Aimable is hold-Q to aim, release-Q to fire — resolved from button edges, so a tap can't be lost.
/// </summary>
public class FusionPlayerCombat : NetworkBehaviour
{
    [Networked] public NetworkBool NetworkedOverdrive     { get; set; }
    [Networked] public NetworkBool NetworkedFieldActive  { get; set; }
    [Networked] public NetworkBool NetworkedFieldStartup { get; set; }
    // Aim mode lives on the server, but the controlling client needs it to size its own reticle.
    [Networked] public NetworkBool NetworkedAiming        { get; set; }

    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private BaseNullField             _field;
    private PlayerOverdrive           _overdrive;
    private PlayerCombatController    _combat;
    private PlayerAudio               _audio;

    // Server-side only (no client prediction, so no resimulation): last received input plus the previous
    // button state the press/release edges are taken against.
    private FusionPlayerInput _input;
    private NetworkButtons    _prevButtons;

    void Awake()
    {
        _health    = GetComponent<PlayerHealth>();
        _anim      = GetComponent<PlayerAnimationController>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();
        _field     = GetComponent<BaseNullField>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _combat    = GetComponent<PlayerCombatController>();
        _audio     = GetComponent<PlayerAudio>();
    }

    public override void Spawned()
    {
        // Replicate auto/aimable attacks to remote peers so they see the punch/skill anim too.
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

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;

        // Advance the button edges every tick, even while dead or frozen — a key held through a freeze
        // must not fire the moment control returns (this replaces the old buffered-press clearing).
        if (GetInput(out FusionPlayerInput received)) _input = received;
        NetworkButtons pressed  = _input.Buttons.GetPressed(_prevButtons);
        NetworkButtons released = _input.Buttons.GetReleased(_prevButtons);
        _prevButtons = _input.Buttons;

        // Replicate our null field's open/closed + forming state every tick so the other peer can apply
        // its cross-player effects (overdrive lock / regen freeze), the startup freeze, and the VFX/arena.
        NetworkedFieldActive  = _field != null && _field.IsActive;
        NetworkedFieldStartup = _field != null && _field.IsStartingUp;
        NetworkedAiming        = _aimable != null && _aimable.IsAiming;

        if (_health != null && _health.IsDead)
        {
            if (NetworkedOverdrive) NetworkedOverdrive = false;
            _overdrive?.SetActive(false);
            return;
        }

        // Frozen during the death → respawn round reset: kill overdrive, ignore this tick's edges.
        if (FusionPlayerSync.RoundResetting)
        {
            if (NetworkedOverdrive) NetworkedOverdrive = false;
            _overdrive?.SetActive(false);
            return;
        }

        // Frozen while a null field forms (its 0.5s startup).
        if (BaseNullField.PlayersFrozen) return;

        // Overdrive is a toggle: each Shift press flips the replicated state. While our null field is up the
        // null field controls overdrive (free) — ignore presses and just mirror the real state.
        if (_field == null || !_field.IsActive)
        {
            if (pressed.IsSet(PlayerButton.Overdrive)) NetworkedOverdrive = !NetworkedOverdrive;
            _overdrive?.SetActive(NetworkedOverdrive);
        }
        // Re-sync to the real state (SetActive can refuse when null field-locked; null field-free forces it on),
        // so the networked flag — and the remote tint — always tracks what's really happening.
        if (_overdrive != null) NetworkedOverdrive = _overdrive.IsActive;

        // Match offline behavior (PlayerCombatController early-returns on hitstun): a key pressed during
        // the hurt animation is ignored rather than queued.
        if (_anim != null && _anim.IsHitstun) return;

        Vector2 aimPoint = _input.AimPoint;

        if (pressed.IsSet(PlayerButton.AutoAttack)) _auto?.TryAttack(aimPoint);
        if (pressed.IsSet(PlayerButton.Aimable))    _aimable?.StartAiming();
        if (released.IsSet(PlayerButton.Aimable))   _aimable?.ReleaseAttack(aimPoint);

        if (pressed.IsSet(PlayerButton.NullField) && _field != null)
        {
            if (_field.IsActive) _field.Deactivate();
            else                  _field.Activate();
        }
    }

    public override void Render()
    {
        // Every peer that doesn't simulate this fighter mirrors the server's overdrive/null field state so the
        // red tint, the null field arena and the freeze all match what the server is actually running.
        if (!HasStateAuthority)
        {
            _overdrive?.SetActive(NetworkedOverdrive);
            _field?.SetNetworkActive(NetworkedFieldActive);    // mirror the null field (registry/VFX/arena)
            _field?.SetNetworkStartup(NetworkedFieldStartup);  // …and its forming state (freeze on this peer)
            _aimable?.SetNetworkAiming(NetworkedAiming);         // …so the controlling client's reticle expands
        }
    }

    /// <summary>Server-side: tell the other peers to play this fighter's hurt reaction. HP itself
    /// replicates through FusionPlayerSync — this is the animation, screen feedback and combo tally.</summary>
    public void ReplicateHurt(float amount, Vector2 knockback, float hitstun, GameObject attacker)
    {
        if (Object == null || !Object.IsValid || !HasStateAuthority) return;
        var attackerObject = attacker != null ? attacker.GetComponentInParent<NetworkObject>() : null;
        RpcHurt(amount, knockback, hitstun, attackerObject != null ? attackerObject.Id : default);
    }

    // Targets All, not Proxies: the hit player's own client is this object's INPUT authority, not a proxy,
    // so a Proxies-only RPC would skip the peer that most needs to see the hit. InvokeLocal is off because
    // the server already reacted locally through PlayerHealth.OnDamaged.
    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcHurt(float amount, Vector2 knockback, float hitstun, NetworkId attackerId)
    {
        // Capture before PlayHurt flips it — the combo tally needs the pre-hit stun state.
        bool wasStunned = _anim != null && _anim.IsHitstun;

        _anim?.SetFacingFromHit(knockback);
        _anim?.SetNextHitstun(hitstun);
        _anim?.PlayHurt();

        // Screen shake / flash belong to the peer whose fighter got hit, not to whoever simulated it.
        if (HasInputAuthority) _combat?.PlayDamageFeedback(amount);

        // The combo readout is per-peer and shows only your own offense, matching the offline behaviour.
        if (!attackerId.IsValid) return;
        var attacker = Runner.FindObject(attackerId);
        if (attacker != null && attacker.HasInputAuthority)
            ComboCounter.Instance.Register(attacker.gameObject, _anim, wasStunned);
    }

    // The SFX delay AND the fire-time overdrive flag ride along with the attack: only the server runs the
    // attack routine, so remote peers can't know when the active window falls, and resolving overdrive from
    // the mirrored stance instead would let the clip play at one rate while the delay was computed for the
    // other. Sending both from the same server snapshot keeps every peer's sound on the active frames.
    private void OnLocalAutoAttackStarted(string action, Vector2 facing)
    {
        if (!HasStateAuthority) return;
        RpcAutoAttackStarted(action, facing,
                             _auto != null ? _auto.ActiveDelay : 0f,
                             _auto != null && _auto.FiredInOverdrive);
    }

    private void OnLocalAutoAttackEnded()
    {
        if (HasStateAuthority) RpcAutoAttackEnded();
    }

    private void OnLocalAimableAttackStarted(Vector2 aimDir, string action)
    {
        if (!HasStateAuthority) return;
        RpcAimableAttackStarted(aimDir, action,
                                _aimable != null ? _aimable.ImpactDelay : 0f,
                                _aimable != null && _aimable.FiredInOverdrive);
    }

    private void OnLocalAimableAttackEnded()
    {
        if (HasStateAuthority) RpcAimableAttackEnded();
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcAutoAttackStarted(string action, Vector2 facing, float sfxDelay, bool overdrive)
    {
        bool directional = _auto == null || _auto.TakeDirection;
        if (directional) _anim?.SetFacing(facing);
        _anim?.PlayAutoAttack(action, directional, _auto != null ? _auto.PlaybackSpeedFor(overdrive) : 1f);
        _audio?.PlayAutoAttack(overdrive, sfxDelay);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcAutoAttackEnded()
    {
        _anim?.RefreshMovementState();
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcAimableAttackStarted(Vector2 aimDir, string action, float sfxDelay, bool overdrive)
    {
        bool directional = _aimable == null || _aimable.TakeDirection;
        _anim?.PlayAimableAttack(aimDir, action, directional, _aimable != null ? _aimable.PlaybackSpeedFor(overdrive) : 1f);
        _audio?.PlayAimableAttack(overdrive, sfxDelay);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcAimableAttackEnded()
    {
        _anim?.RefreshMovementState();
    }
}
