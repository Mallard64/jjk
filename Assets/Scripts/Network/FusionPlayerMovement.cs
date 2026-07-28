using Fusion;
using UnityEngine;

/// <summary>
/// Host mode movement. Only the server simulates: it consumes the controlling peer's FusionPlayerInput,
/// drives the Rigidbody2D, and replicates move/aim direction so every peer can drive the animator in
/// Render(). Position itself replicates through NetworkTransform.
/// </summary>
public class FusionPlayerMovement : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;

    [Networked] public Vector2 NetworkedMoveDir { get; set; }
    [Networked] public Vector2 NetworkedAimDir  { get; set; }

    // The movement gates live in the attack/roll/animation controllers, which are non-networked local state
    // that ONLY the server runs. A predicting client can't read them, so the server publishes the resolved
    // values here and both sides drive movement off these — same inputs, same gates, same motion.
    [Networked] public NetworkBool VelocityLocked { get; set; }  // hitstun / roll own the velocity outright
    [Networked] public NetworkBool MoveBlocked    { get; set; }  // …plus the aimable jump: input is ignored
    [Networked] public float       SpeedScale     { get; set; }  // overdrive x domain x auto-attack drift

    private Rigidbody2D               _rb;
    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private PlayerRoll                _roll;
    private PlayerOverdrive           _overdrive;
    private BaseDomainExpansion       _domain;

    // Server-side only (no client prediction, so no resimulation): the last input we received, reused
    // when a packet is missing, and the previous button state the press edges are taken against.
    private FusionPlayerInput _input;
    private NetworkButtons    _prevButtons;

    void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _health    = GetComponent<PlayerHealth>();
        _anim      = GetComponent<PlayerAnimationController>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();
        _roll      = GetComponent<PlayerRoll>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _domain    = GetComponent<BaseDomainExpansion>();
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
        NetworkButtons pressed = _input.Buttons.GetPressed(_prevButtons);
        _prevButtons = _input.Buttons;

        if (_health != null && _health.IsDead) return;

        // Frozen during the death → respawn round reset, or while a domain forms (its startup).
        if (FusionPlayerSync.RoundResetting || BaseDomainExpansion.PlayersFrozen)
        {
            if (_rb != null && _rb.simulated) _rb.velocity = Vector2.zero;
            if (HasStateAuthority) NetworkedMoveDir = Vector2.zero;
            return;
        }

        // Server resolves the gates from the real controllers and publishes them. During hurt lock the
        // knockback impulse drives the rigidbody, and the roll owns velocity for its dash window — so
        // neither may have velocity overwritten. The aimable is a locked jump; the auto attack drifts
        // at a reduced speed for repositioning.
        if (HasStateAuthority)
        {
            bool inHitstun     = _anim != null && _anim.IsHitstun;
            bool autoAttacking = _auto != null && _auto.IsAttacking;
            bool aimableLocked = _aimable != null && _aimable.IsAttacking;
            bool isRolling     = _roll != null && _roll.IsRolling;

            // "Something other than input owns the body" — knockback during hitstun, the roll's dash, and
            // the attacks. Attacks are included so a roll cancelled into an attack keeps its momentum and
            // decays through drag instead of being hard-zeroed (which snapped the position back).
            VelocityLocked = inHitstun || isRolling || aimableLocked || autoAttacking;
            MoveBlocked    = VelocityLocked;

            float scale = _overdrive != null ? _overdrive.MoveSpeedMultiplier : 1f;
            if (_domain != null) scale *= _domain.MoveSpeedMultiplier;  // domain bonus (owner only)
            SpeedScale = scale;

            // The roll's dash must be re-asserted once per PHYSICS step, and physics now steps on network
            // ticks — so it belongs here, not in PlayerRoll's Unity FixedUpdate (which is out of phase).
            _roll?.SustainDash();
        }

        Vector2 move = MoveBlocked ? Vector2.zero : _input.MoveDir;
        if (!VelocityLocked && _rb != null) _rb.velocity = move * (moveSpeed * SpeedScale);

        NetworkedMoveDir = move;
        NetworkedAimDir  = AimDirection();

        // Roll runs a server-side coroutine over non-networked state, so it is never predicted — the
        // client sees it through RpcRollStarted, and the dash velocity arrives with the position sync.
        if (HasStateAuthority && pressed.IsSet(PlayerButton.Roll) && _roll != null)
        {
            // Roll along current move; fall back to aim direction if standing still.
            Vector2 rollDir = move.sqrMagnitude > 0.01f ? move : NetworkedAimDir;
            _roll.TryRoll(rollDir);
        }
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
