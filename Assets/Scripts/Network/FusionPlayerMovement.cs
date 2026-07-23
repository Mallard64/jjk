using Fusion;
using UnityEngine;

/// <summary>
/// Shared Mode movement. Reads local WASD, applies velocity on authority, replicates
/// move/aim direction to proxies so they can drive the animator in Render().
/// </summary>
public class FusionPlayerMovement : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 5f;

    [Networked] public Vector2 NetworkedMoveDir { get; set; }
    [Networked] public Vector2 NetworkedAimDir  { get; set; }

    private Rigidbody2D               _rb;
    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private PlayerRoll                _roll;
    private PlayerOverdrive           _overdrive;
    private BaseDomainExpansion       _domain;

    private bool _rollPressed;

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
        // Replicate roll to proxies so they see the dodge animation alongside the networked dash motion.
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

    void Update()
    {
        if (Object == null || !Object.IsValid || !HasStateAuthority) return;
        // Edge-buffered: GetMouseButtonDown only returns true the frame the click happens,
        // so we capture it here in Update and consume in FixedUpdateNetwork.
        if (Input.GetMouseButtonDown(1)) _rollPressed = true;
    }

    public override void FixedUpdateNetwork()
    {
        if (!HasStateAuthority) return;
        if (_health != null && _health.IsDead) return;

        // Frozen during the death → respawn round reset (blackout window), or while a domain forms (startup).
        if (FusionPlayerSync.RoundResetting || BaseDomainExpansion.PlayersFrozen)
        {
            if (_rb != null && _rb.simulated) _rb.velocity = Vector2.zero;
            NetworkedMoveDir = Vector2.zero;
            return;
        }

        // During hurt lock, let the knockback impulse drive the rigidbody — don't override velocity.
        // Roll owns the rigidbody velocity for the dash window; same treatment. The aimable attack is
        // a locked jump move; the auto attack instead drifts at a reduced speed for repositioning.
        bool inHitstun     = _anim != null && _anim.IsHitstun;
        bool autoAttacking = _auto != null && _auto.IsAttacking;
        bool aimableLocked = _aimable != null && _aimable.IsAttacking;
        bool isRolling     = _roll != null && _roll.IsRolling;

        Vector2 move = (inHitstun || aimableLocked || isRolling) ? Vector2.zero : new Vector2(
            (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f),
            (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f)
        ).normalized;

        if (!inHitstun && !isRolling && _rb != null)
        {
            float speed = moveSpeed * (_overdrive != null ? _overdrive.MoveSpeedMultiplier : 1f);
            if (_domain != null) speed *= _domain.MoveSpeedMultiplier;  // domain bonus (owner only)
            if (autoAttacking) speed *= _auto.AttackMoveSpeedMultiplier;
            _rb.velocity = move * speed;
        }
        NetworkedMoveDir = move;
        NetworkedAimDir  = FusionPlayerSync.GetMouseAim(transform);

        if (_rollPressed && _roll != null)
        {
            // Roll along current move; fall back to aim direction if standing still.
            Vector2 rollDir = move.sqrMagnitude > 0.01f ? move : NetworkedAimDir;
            _roll.TryRoll(rollDir);
            _rollPressed = false;
        }
    }

    public override void Render()
    {
        bool moving = NetworkedMoveDir.sqrMagnitude > 0.01f;
        // Stationary: face the aim direction (mouse on authority, networked to proxies). Moving: face movement.
        _anim?.SetMoveDirection(moving ? NetworkedMoveDir : NetworkedAimDir);
        _anim?.SetIsMoving(moving);
    }

    private void OnLocalRollStarted(Vector2 dir, string action)
    {
        if (HasStateAuthority) RpcRollStartedOnProxies(dir, action);
    }

    private void OnLocalRollEnded()
    {
        if (HasStateAuthority) RpcRollEndedOnProxies();
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcRollStartedOnProxies(Vector2 dir, string action)
    {
        _anim?.PlayRoll(dir, action);
    }

    [Rpc(RpcSources.StateAuthority, RpcTargets.Proxies)]
    public void RpcRollEndedOnProxies()
    {
        _anim?.RefreshMovementState();
    }
}
