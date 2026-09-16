using UnityEngine;

/// <summary>
/// Top-down WASD movement. Reads from PlayerInputHandler.
/// Animator parameters match the Cainos Pixel Art Top Down asset pack:
///   Direction (int): 0=down, 1=up, 2=right, 3=left
///   IsMoving (bool)
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class PlayerMovement : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 5f;
    [Tooltip("How much knockback velocity is reflected when slamming into a wall (0 = stick, 1 = full bounce).")]
    [SerializeField] private float wallBounciness = 0.6f;
    [Tooltip("How close a solid collider must be to count as the player being 'adjacent to a wall' for the wall-splat check.")]
    [SerializeField] private float wallCheckRadius = 0.6f;

    private static readonly Collider2D[] _wallProbe = new Collider2D[8];

    /// <summary>True when a solid (non-trigger, non-player) collider sits within wallCheckRadius — i.e. the
    /// player is up against a wall. The damage path uses this to splat a hard hit even if the player is
    /// already pinned to the wall (no fresh collision would fire).</summary>
    public bool IsAdjacentToWall()
    {
        int n = Physics2D.OverlapCircleNonAlloc(transform.position, wallCheckRadius, _wallProbe);
        for (int i = 0; i < n; i++)
        {
            var col = _wallProbe[i];
            if (col == null || col.isTrigger) continue;                            // hurt/hitboxes, null fields, etc.
            if (col.transform.root.GetComponent<PlayerHealth>() != null) continue; // self + the other fighter
            return true;
        }
        return false;
    }

    private bool _canMove = true;
    public bool CanMove
    {
        get => _canMove;
        set
        {
            if (_canMove == value) return;
            _canMove = value;
            // Stop motion when locked, but leave the body dynamic so knockback impulses
            // (e.g. during HurtLock) still take effect. Use Freeze() for the dead state.
            if (!value && _rb != null) _rb.velocity = Vector2.zero;
        }
    }
    public void SetCanMove(bool value) => CanMove = value;

    public void Freeze()
    {
        if (_rb == null) return;
        _rb.velocity = Vector2.zero;
        _rb.isKinematic = true;
    }

    public void Unfreeze()
    {
        if (_rb != null) _rb.isKinematic = false;
    }

    /// <summary>Moves the fighter to a world point. Assigning transform.position is NOT enough here:
    ///   * The project has Physics2D auto-sync-transforms OFF, so a transform write stays invisible to
    ///     physics until a sync and the body's own pose stomps it at the next step.
    ///   * If the body is set to Interpolate, Unity rewrites the transform every frame by lerping from
    ///     its PREVIOUS physics pose, dragging a teleport back toward where it came from. Dropping
    ///     interpolation for the write clears that history.
    /// Velocity and spin are cleared first so leftover knockback can't carry it straight back off the
    /// spot. Use this for every reposition — respawns, round resets, dummy resets.</summary>
    public void Teleport(Vector3 position)
    {
        transform.position = position;

        // Lazily resolved: a disabled PlayerMovement still gets Awake, but a caller can reach this
        // before it in the same frame, and a training dummy disables the component outright.
        if (_rb == null) _rb = GetComponent<Rigidbody2D>();
        if (_rb == null) return;

        RigidbodyInterpolation2D previous = _rb.interpolation;
        _rb.interpolation   = RigidbodyInterpolation2D.None;
        _rb.velocity        = Vector2.zero;
        _rb.angularVelocity = 0f;
        _rb.position        = position;
        Physics2D.SyncTransforms();
        _rb.interpolation   = previous;
    }

    private Rigidbody2D               _rb;
    private PlayerInputHandler        _input;
    private PlayerAnimationController _anim;
    private PlayerRoll                _roll;
    private PlayerOverdrive           _overdrive;
    private AutoAttackController      _auto;
    private BaseNullField             _field;
    private FusionPlayerSync          _net;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _input = GetComponent<PlayerInputHandler>();
        _anim = GetComponent<PlayerAnimationController>();
        _roll = GetComponent<PlayerRoll>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _auto      = GetComponent<AutoAttackController>();
        _field     = GetComponent<BaseNullField>();
        _net       = GetComponent<FusionPlayerSync>();
    }

    void FixedUpdate()
    {
        // Only bail when the adapter is actually networked (spawned by a Runner).
        // If MatchManager Instantiates the prefab offline, the adapter component is dormant
        // and PlayerMovement should keep driving movement here.
        if (_net != null && _net.Object != null && _net.Object.IsValid) return;

        // Both fighters freeze while a null field is forming (its 0.5s startup).
        if (BaseNullField.PlayersFrozen) { _rb.velocity = Vector2.zero; return; }

        // While locked (attack lock, hurt lock, death), don't override velocity — that
        // lets knockback impulses survive HurtLock. Linear drag on the rigidbody decays them.
        if (!CanMove) return;
        // Roll owns the rigidbody velocity for the dash window; don't overwrite it.
        if (_roll != null && _roll.IsRolling) return;

        var input = _input != null ? _input.Current : default;
        Vector2 move = input.MoveDir;
        float speed = moveSpeed * (_overdrive != null ? _overdrive.MoveSpeedMultiplier : 1f);
        if (_field != null) speed *= _field.MoveSpeedMultiplier;  // null field bonus (owner only)
        // Mid auto attack the player drifts at a reduced speed for repositioning (the swing anim is
        // frozen by the animator's attack lock, so this drift won't break it).
        if (_auto != null && _auto.IsAttacking) speed *= _auto.AttackMoveSpeedMultiplier;
        _rb.velocity = move * speed;

        if (_anim != null)
        {
            bool isMoving = move.sqrMagnitude > 0.01f;
            // Stationary: face the aim direction (mouse for P1, opponent for P2). Moving: face movement.
            _anim.SetMoveDirection(isMoving ? move : input.AimDir);
            _anim.SetIsMoving(isMoving);
        }
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        // Reflect knockback off walls instead of sticking. Gated on IsHitstun (the window
        // where both the offline path and FusionPlayerMovement leave velocity to the knockback
        // impulse) so it works online and off. Skip kinematic bodies — network proxies driven
        // by NetworkTransform and the frozen dead state — and the roll's own dash velocity.
        if (_rb == null || _rb.isKinematic) return;
        if (_anim == null || !_anim.IsHitstun) return;
        if (_roll != null && _roll.IsRolling) return;
        if (_rb.velocity.sqrMagnitude < 0.01f) return;

        Vector2 normal = collision.GetContact(0).normal;
        _rb.velocity = Vector2.Reflect(_rb.velocity, normal) * wallBounciness;

        // Slammed into a wall (not the other fighter) while stunned — let the anim splat if the hit was hard enough.
        if (collision.transform.root.GetComponent<PlayerHealth>() == null) _anim.NotifyWallHit();
    }
}
