using Fusion;
using UnityEngine;

/// <summary>
/// Host mode movement. The server simulates both fighters from the controlling peer's FusionPlayerInput and
/// replicates move/aim direction so every peer can drive the animator in Render(); the controlling client
/// predicts its own fighter from the same input. Position itself replicates through NetworkRigidbody2D.
///
/// Everything a predicted tick reads has to live in [Networked] state or in the input, because the client
/// resimulates from the last confirmed tick every frame and only those two rewind. That is why the movement
/// gate, the roll's dash and the aimable's leap are all networked here instead of being read off the
/// controllers, which are non-networked local state that only the server runs.
/// </summary>
public class FusionPlayerMovement : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;

    [Networked] public Vector2 NetworkedMoveDir { get; set; }
    [Networked] public Vector2 NetworkedAimDir  { get; set; }

    // "Something other than input owns the body" — knockback during hitstun, the roll's dash, and both
    // attacks. Attacks are included so a roll cancelled into an attack keeps its momentum and decays
    // through drag instead of being hard-zeroed (which snapped the position back). The server resolves it
    // from the real controllers; the client predicts its onset from its own button edge (see PredictGates).
    [Networked] public NetworkBool VelocityLocked { get; set; }
    [Networked] public float       SpeedScale     { get; set; }  // overdrive x null field

    // The roll's dash, as networked state. PlayerRoll runs it from a server-only coroutine, so without this
    // the client keeps walking through its own roll for a round trip and is then yanked to wherever the
    // server dashed it. DashCooldown mirrors PlayerRoll.CooldownRemaining so the client admits a roll on the
    // same tick the server does, rather than predicting dashes the server refuses.
    [Networked] public Vector2 DashVelocity  { get; set; }
    [Networked] public float   DashRemaining { get; set; }
    [Networked] public float   DashCooldown  { get; set; }

    // The aimable's throwable leap. Velocity is locked for the whole attack, so without these a predicting
    // client stands still while the server carries it a throwRadius away, and each snapshot lands as a jump.
    [Networked] public NetworkBool Leaping       { get; set; }
    [Networked] public Vector2     LeapTarget    { get; set; }
    [Networked] public float       LeapRemaining { get; set; }
    [Networked] public NetworkBool Airborne      { get; set; }  // on jumpLayer: no body collision with the opponent

    // The button state the press/release edges are taken against. NETWORKED, not a plain field: a plain one
    // does not rewind with the rest of the state, so on a resimulated tick the edges would be derived
    // against whatever the newest tick left behind — every press either lost or fired twice.
    [Networked] public NetworkButtons PrevButtons { get; set; }

    private Rigidbody2D               _rb;
    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private PlayerRoll                _roll;
    private PlayerOverdrive           _overdrive;
    private BaseNullField             _field;
    private FusionPlayerSync          _sync;

    // Last input we received, reused when a packet is missing. Not networked: Fusion replays the buffered
    // input for every resimulated tick, so this only ever fills gaps out at the prediction edge.
    private FusionPlayerInput _input;

    void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _health    = GetComponent<PlayerHealth>();
        _anim      = GetComponent<PlayerAnimationController>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();
        _roll      = GetComponent<PlayerRoll>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _field     = GetComponent<BaseNullField>();
        _sync      = GetComponent<FusionPlayerSync>();
    }

    public override void Spawned()
    {
        // Neutral until the first server write lands. 0 would read as "frozen" on a predicting client.
        if (HasStateAuthority) SpeedScale = 1f;

        // Replicate roll to remote peers so they see the dodge animation alongside the networked dash motion.
        if (_roll != null)
        {
            _roll.OnRollStarted += OnLocalRollStarted;
            _roll.OnRollEnded   += OnLocalRollEnded;
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        if (_roll != null)
        {
            _roll.OnRollStarted -= OnLocalRollStarted;
            _roll.OnRollEnded   -= OnLocalRollEnded;
        }
    }

    public override void FixedUpdateNetwork()
    {
        // The server simulates both fighters; the controlling client predicts its own from the same input.
        if (!HasStateAuthority && !HasInputAuthority) return;

        // Keep consuming input even while dead/frozen so the edges stay current — otherwise a button held
        // through the freeze would fire the instant control returns.
        if (GetInput(out FusionPlayerInput received)) _input = received;
        NetworkButtons pressed  = _input.Buttons.GetPressed(PrevButtons);
        NetworkButtons released = _input.Buttons.GetReleased(PrevButtons);
        PrevButtons = _input.Buttons;

        if (_health != null && _health.IsDead) return;

        // Frozen during the death → respawn round reset, or while a null field forms (its startup).
        if (FusionPlayerSync.RoundResetting || BaseNullField.PlayersFrozen)
        {
            if (_rb != null && _rb.simulated) _rb.velocity = Vector2.zero;
            if (HasStateAuthority) NetworkedMoveDir = Vector2.zero;
            return;
        }

        float dt = Runner.DeltaTime;

        if (HasStateAuthority) ResolveGates();
        else                   PredictGates(pressed, released, dt);

        Vector2 move = VelocityLocked ? Vector2.zero : _input.MoveDir;
        if (!VelocityLocked && _rb != null) _rb.velocity = move * (moveSpeed * SpeedScale);

        // The dash owns the body for the roll window on the server and the predicting client alike, driven
        // off networked state so both run the identical motion from the same tick.
        if (DashRemaining > 0f)
        {
            if (_rb != null && _rb.simulated) _rb.velocity = DashVelocity;
            DashRemaining -= dt;
        }

        AdvanceLeap(dt);

        NetworkedMoveDir = move;
        NetworkedAimDir  = AimDirection();

        // Roll along current move; fall back to aim direction if standing still. Both peers derive the
        // same direction from the same replicated values, so the predicted dash matches the server's.
        if (pressed.IsSet(PlayerButton.Roll))
            TryStartDash(move.sqrMagnitude > 0.01f ? move : NetworkedAimDir);
    }

    /// <summary>Server: publish the gates resolved from the real controllers. During hurt lock the knockback
    /// impulse drives the rigidbody, the roll owns velocity for its dash window, and both attacks root the
    /// player — so none of them may have velocity overwritten by input.</summary>
    private void ResolveGates()
    {
        bool inHitstun     = _anim    != null && _anim.IsHitstun;
        bool autoAttacking = _auto    != null && _auto.IsAttacking;
        bool aimableLocked = _aimable != null && _aimable.IsAttacking;
        bool isRolling     = _roll    != null && _roll.IsRolling;

        VelocityLocked = inHitstun || isRolling || aimableLocked || autoAttacking;

        float scale = _overdrive != null ? _overdrive.MoveSpeedMultiplier : 1f;
        if (_field != null) scale *= _field.MoveSpeedMultiplier;  // null field bonus (owner only)
        SpeedScale = scale;

        // Mirror rather than run a second timer: PlayerRoll owns the cooldown, this only publishes it so
        // the client can gate its predicted dash on the same value.
        if (_roll != null) DashCooldown = _roll.CooldownRemaining;
    }

    /// <summary>Client: predict the gates from our own input. The server resolves VelocityLocked from
    /// controllers we don't run, so the published value is a round trip old — without this the client walks
    /// through the first RTT of its own attack or roll and then snaps back when the locked snapshot lands.
    /// We can't know how long the lock lasts, but we know when it STARTS: it's our own button edge, on the
    /// same tick the server will act on it. Clearing it is left to the server's confirmed state. Locking a
    /// touch early is invisible and unlocking a touch late costs a couple of frames of held stillness —
    /// neither is a teleport, which is what mispredicting the onset produces.</summary>
    private void PredictGates(NetworkButtons pressed, NetworkButtons released, float dt)
    {
        // Pressing Aimable only enters aim mode (movement stays free); releasing it is what fires.
        if (pressed.IsSet(PlayerButton.AutoAttack) ||
            pressed.IsSet(PlayerButton.Roll) ||
            released.IsSet(PlayerButton.Aimable))
            VelocityLocked = true;

        if (DashCooldown > 0f) DashCooldown -= dt;
    }

    /// <summary>Commit the roll's dash to networked state: on the server when PlayerRoll actually rolled, on
    /// the predicting client behind as much of the same gate as it can reproduce.
    ///
    /// The client deliberately does NOT call PlayerRoll.CanRoll(): that reads the roll's own cooldown timer
    /// and Resonance.CurrentEnergy, neither of which rewinds with a resimulated tick (energy is applied
    /// in FusionPlayerSync.Render, once a frame). A resim would then answer differently than the forward
    /// tick did and drop a dash already in flight. Every value below rewinds, so the answer is stable:
    /// DashCooldown is the published cooldown and NetworkedEnergy is the server's own RES for that tick.</summary>
    private void TryStartDash(Vector2 dir)
    {
        if (_roll == null || DashRemaining > 0f || DashCooldown > 0f) return;

        if (HasStateAuthority)
        {
            _roll.TryRoll(dir);            // spends RES, cancels attacks, runs the i-frames and roll anim
            if (!_roll.IsRolling) return;  // refused
        }
        else if (_sync == null || _sync.NetworkedEnergy < _roll.EnergyCost) return;

        DashVelocity  = dir.normalized * _roll.DashSpeed;
        DashRemaining = _roll.DashDuration;
        DashCooldown  = _roll.RollCooldown;
    }

    /// <summary>The aimable's leap is a physics write, so it advances on the network tick — never in a Unity
    /// FixedUpdate. The server steps the controller's own leap and publishes it; a predicting client steps
    /// the NETWORKED remaining and writes it back, so a resimulated tick continues the leap instead of
    /// restarting it. (Seeding the controller from the networked value every tick, as this used to, re-read
    /// the same confirmed remaining on every resimulated tick: the lerp never converged and the body was
    /// flung at the target once per resimulated tick — the stutter and the flashes forward.)</summary>
    private void AdvanceLeap(float dt)
    {
        if (_aimable == null) return;

        if (HasStateAuthority)
        {
            _aimable.AdvanceLeap(dt);
            Leaping       = _aimable.LeapActive;
            LeapTarget    = _aimable.LeapTarget;
            LeapRemaining = _aimable.LeapRemaining;
            Airborne      = _aimable.IsAirborne;
            return;
        }

        _aimable.SetNetworkAirborne(Airborne);
        if (!Leaping) return;

        LeapRemaining = _aimable.StepLeap(LeapTarget, LeapRemaining, dt);
        if (LeapRemaining <= 0f) Leaping = false;
    }

    /// <summary>Aim resolved against the server's own position, never a client-supplied heading.</summary>
    private Vector2 AimDirection()
    {
        Vector2 dir = _input.AimPoint - (Vector2)transform.position;
        if (dir.sqrMagnitude > 0.01f) return dir.normalized;
        return NetworkedAimDir.sqrMagnitude > 0.01f ? NetworkedAimDir : Vector2.right;
    }

    public override void Render()
    {
        bool moving = NetworkedMoveDir.sqrMagnitude > 0.01f;
        // Stationary: face the aim direction (replicated from the controlling peer). Moving: face movement.
        _anim?.SetMoveDirection(moving ? NetworkedMoveDir : NetworkedAimDir);
        _anim?.SetIsMoving(moving);
    }

    private void OnLocalRollStarted(Vector2 dir, string action)
    {
        if (HasStateAuthority) RpcRollStarted(dir, action);
    }

    private void OnLocalRollEnded()
    {
        if (HasStateAuthority) RpcRollEnded();
    }

    // Targets All, not Proxies: in host mode the controlling client is this object's INPUT authority, not
    // a proxy, so a Proxies-only RPC would skip the very peer that pressed the button. InvokeLocal is off
    // because the server already played the animation through its own PlayerRoll events.
    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcRollStarted(Vector2 dir, string action)
    {
        _anim?.PlayRoll(dir, action);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.All, InvokeLocal = false)]
    public void RpcRollEnded()
    {
        _anim?.RefreshMovementState();
    }
}
