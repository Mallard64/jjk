using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Drives the player's aimable attack as a jump move: startup → jump (i-frames, no hitbox) →
/// impact (hitbox active, vulnerable) → endlag → cooldown. Firing is hold-to-aim / release-to-fire:
/// StartAiming reserves the input window; ReleaseAttack commits with the final aim direction.
/// Cursed energy is spent at release time.
/// </summary>
public class AimableAttackController : MonoBehaviour
{
    // Phase lengths are in animation frames. At release, one frame = clipLength / (startup+jump+impact+endlag),
    // so the four phases spread across the WHOLE attack clip — the leap always plays in full. Set each phase
    // to how many of your animation's frames it should occupy; nothing else to configure.
    [Header("Timing (animation frames)")]
    [Tooltip("Wind-up frames before the leap (after release).")]
    [SerializeField] private int   aimableAttackStartupFrames  = 2;
    [Tooltip("Airborne leap frames. Player is invincible and the hitbox is off for this whole window.")]
    [SerializeField] private int   aimableAttackJumpFrames     = 4;
    [Tooltip("Impact frames. Hitbox is active and the player is vulnerable again.")]
    [SerializeField] private int   aimableAttackImpactFrames   = 2;
    [Tooltip("Recovery frames after impact. Movement stays locked through this. startup+jump+impact+endlag = your animation's frame count.")]
    [SerializeField] private int   aimableAttackEndlagFrames   = 3;
    [Tooltip("Minimum frames between attack starts (same frame unit). Measured from release.")]
    [SerializeField] private int   aimableAttackCooldownFrames = 16;
    [Tooltip("Speeds up (>1) or slows down (<1) the whole attack: the animation plays at this rate and every phase + the cooldown scale by 1/speed. 1 = authored speed.")]
    [Range(0.1f, 4f)]
    [SerializeField] private float aimableAttackPlaybackSpeed   = 1f;
    [SerializeField] private float aimableAttackEnergyCost = 20f;

    [Header("Phase frame scales")]
    [Tooltip("Per-phase multipliers on the frame counts above. Reshape how the clip is split between phases without re-authoring the ints. 1 = as authored.")]
    [Range(0.1f, 4f)] [SerializeField] private float startupFrameScale = 1f;
    [Range(0.1f, 4f)] [SerializeField] private float jumpFrameScale    = 1f;
    [Range(0.1f, 4f)] [SerializeField] private float impactFrameScale  = 1f;
    [Range(0.1f, 4f)] [SerializeField] private float endlagFrameScale  = 1f;

    [Header("Animation")]
    [Tooltip("Action name for the aimable attack state, e.g. 'skill' → 'skill-se'.")]
    [SerializeField] private string aimableAttackAnim = "skill";
    [Tooltip("If true, the attack aims by direction: plays '{anim}-{dir}' and orients the hitbox toward the aim. If false, it ignores direction and plays the bare '{anim}' clip with the hitbox at its authored placement.")]
    [SerializeField] private bool takeDirection = true;

    [Header("Throwable")]
    [Tooltip("If true, releasing leaps to the aim point (clamped to throwRadius) and the hitbox lands there, instead of an in-place directional leap. The reticle becomes a circle at that point.")]
    [SerializeField] private bool  throwable   = false;
    [Tooltip("Max distance from the player the throwable target (and its reticle) can sit.")]
    [SerializeField] private float throwRadius = 4f;

    [Header("Jump Phase")]
    [Tooltip("Layer the player is moved to for the airborne jump window so it doesn't collide with the other player; the original layer is restored on impact. Configure the Physics2D collision matrix so this layer ignores the opponent's hitbox/body.")]
    [SerializeField] private int   jumpLayer = 2;

    [Header("Overdrive Variant")]
    [Tooltip("Snapshotted at release time when PlayerOverdrive.IsActive. Releasing Shift mid-swing does not revert.")]
    [SerializeField] private float overdriveAttackDamageMultiplier     = 1.8f;
    [Tooltip("Multiplied with the Hitbox's base knockback when the attack fires in overdrive.")]
    [SerializeField] private float overdriveAttackKnockbackMultiplier  = 2.5f;
    [Tooltip("Scales the hitbox size when the attack fires in overdrive (1.5–2 = noticeably bigger).")]
    [SerializeField] private float overdriveAttackHitboxSizeMultiplier = 1.75f;
    [Tooltip("Playback rate for the overdrive aimable attack (separate from the normal aimableAttackPlaybackSpeed). Snapshotted at release time.")]
    [Range(0.1f, 4f)]
    [SerializeField] private float overdriveAttackPlaybackSpeed = 1f;

    [Header("Hitbox")]
    [SerializeField] private AttackHitboxController aimableAttackHitbox;

    private CursedEnergy              _energy;
    private PlayerAnimationController _anim;
    private PlayerMovement            _movement;
    private PlayerOverdrive           _overdrive;
    private PlayerHealth              _health;
    private Rigidbody2D               _rb;
    private FusionPlayerSync          _net;
    private PlayerAudio               _audio;

    private float     _cooldownTimer;
    private Coroutine _routine;
    private bool      _aiming;
    private int       _preJumpLayer;
    private bool      _onJumpLayer;

    // Throwable leap state. Advanced once per PHYSICS step (see AdvanceLeap), not per coroutine yield.
    private bool      _leaping;
    private Vector2   _leapTarget;
    private float     _leapRemaining;

    private float _currentDamageMultiplier    = 1f;
    private float _currentKnockbackMultiplier = 1f;
    private float _currentSizeMultiplier      = 1f;
    private float _currentPlaybackSpeed       = 1f;
    private bool  _currentOverdrive;

    public bool  IsAiming           => _aiming;
    public bool  IsAttacking        => _routine != null;
    public float CooldownRemaining  => Mathf.Max(0f, _cooldownTimer);
    public float EnergyCost         => aimableAttackEnergyCost;
    /// <summary>Whether the attack in flight was released in overdrive — the release-time snapshot, not the
    /// live stance. Sent with the attack RPC so a remote peer resolves the playback speed and the SFX cue
    /// from the same value the timing was computed from (see PlaybackSpeedFor).</summary>
    public bool  FiredInOverdrive   => _currentOverdrive;

    /// <summary>Playback rate for an attack released in (or out of) overdrive. Takes the flag explicitly
    /// rather than reading the live stance, which on a remote peer is applied in Render() and can lag the
    /// attack RPC — playing the clip at one rate while the SFX delay was computed for the other.</summary>
    public float PlaybackSpeedFor(bool overdrive)
        => overdrive ? overdriveAttackPlaybackSpeed : aimableAttackPlaybackSpeed;
    public bool  TakeDirection      => takeDirection;
    public bool  Throwable          => throwable;
    public float ThrowRadius        => throwRadius;
    /// <summary>Seconds from the start of the attack to the impact window (startup + the leap), resolved at
    /// release time. Read by FusionPlayerCombat when it replicates the attack, so remote peers — which never
    /// run this routine — can time the impact SFX to the same frame the hitbox lands on.</summary>
    public float ImpactDelay        { get; private set; }

    // Read by FusionPlayerMovement, which publishes them so a predicting client reproduces the leap and
    // the jump-layer window instead of taking a correction on every server snapshot.
    public bool    LeapActive    => _leaping;
    public Vector2 LeapTarget    => _leapTarget;
    public float   LeapRemaining => _leapRemaining;
    public bool    IsAirborne    => _onJumpLayer;

    // Seconds per frame = clipLength / spanFrames, so the four phases fill the whole attack clip.
    // FallbackSecondsPerFrame (1/60s) covers the degenerate case where no clip is readable yet (e.g. the
    // state isn't authored), so frame counts still produce sane durations.
    private const float FallbackSecondsPerFrame = 1f / 60f;
    private float SecondsPerFrame(float spanFrames)
    {
        float clipLen = _anim != null ? _anim.CurrentClipLength() : 0f;
        return (clipLen > 0f && spanFrames > 0f) ? clipLen / spanFrames : FallbackSecondsPerFrame;
    }

    /// <summary>Clamp a world-space aim point to within throwRadius of the player.</summary>
    public Vector2 ClampToThrowRadius(Vector2 point)
    {
        Vector2 from = transform.position;
        Vector2 d = point - from;
        if (d.sqrMagnitude > throwRadius * throwRadius) d = d.normalized * throwRadius;
        return from + d;
    }

    public event Action<Vector2, string> OnAttackStarted;
    public event Action                  OnAttackEnded;

    void Awake()
    {
        _energy    = GetComponent<CursedEnergy>();
        _anim      = GetComponent<PlayerAnimationController>();
        _movement  = GetComponent<PlayerMovement>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _health    = GetComponent<PlayerHealth>();
        _rb        = GetComponent<Rigidbody2D>();
        _net       = GetComponent<FusionPlayerSync>();
        _audio     = GetComponent<PlayerAudio>();
    }

    void Update()
    {
        if (_cooldownTimer > 0f) _cooldownTimer -= Time.deltaTime;
    }

    void FixedUpdate()
    {
        // Offline only. Online, physics steps on network ticks rather than Unity's fixed timestep, so
        // advancing the leap here lands out of phase with the step and the jump stutters — the online
        // layer advances it from FixedUpdateNetwork instead (same reason as PlayerRoll's dash).
        if (_net != null && _net.Object != null && _net.Object.IsValid) return;
        AdvanceLeap(Time.fixedDeltaTime);
    }

    public bool CanStartAiming()
        => !_aiming && !IsAttacking && _cooldownTimer <= 0f
           && (_energy == null || _energy.CurrentEnergy >= aimableAttackEnergyCost);

    /// <summary>Enter aim mode. Reticle and other systems can poll IsAiming to react.</summary>
    public void StartAiming()
    {
        if (!CanStartAiming()) return;
        _aiming = true;
    }

    /// <summary>Online mirror for a peer that doesn't simulate this fighter: the server owns the real aim
    /// state, but the controlling client still needs IsAiming so its reticle expands while the key is held.
    /// Mirrors BaseDomainExpansion.SetNetworkActive — callers gate on not being the state authority.</summary>
    public void SetNetworkAiming(bool aiming) => _aiming = aiming;

    /// <summary>Online mirror of the jump-layer window. Without it a predicting client keeps colliding
    /// with the opponent while the server's body does not — and the leap targets the aim point, which is
    /// usually the opponent, so the mismatch shows up on almost every jump.</summary>
    public void SetNetworkAirborne(bool airborne)
    {
        if (airborne) EnterJumpLayer();
        else          RestoreLayer();
    }

    /// <summary>Advance this controller's own leap by one physics step. Must be called once per physics
    /// step: Unity's FixedUpdate offline, FixedUpdateNetwork on the simulating peer online.</summary>
    public void AdvanceLeap(float deltaTime)
    {
        if (!_leaping) return;
        _leapRemaining = StepLeap(_leapTarget, _leapRemaining, deltaTime);
        if (_leapRemaining <= 0f) _leaping = false;
    }

    /// <summary>One physics step of a leap toward `target` with `remaining` seconds left on it; returns the
    /// new remaining. Converges on the target from the body's CURRENT position rather than lerping from a
    /// captured start, so a peer that learns about the leap a round trip late still lands on the point and
    /// its error shrinks each step instead of accumulating. Split out from AdvanceLeap so a predicting
    /// client can step the leap straight off its networked state — which rewinds with a resimulated tick,
    /// where this controller's own fields do not.</summary>
    public float StepLeap(Vector2 target, float remaining, float deltaTime)
    {
        if (_rb == null || deltaTime <= 0f) return remaining;

        // MovePosition (not velocity) so rigidbody drag can't shorten the leap.
        float span = Mathf.Max(deltaTime, remaining);
        _rb.MovePosition(Vector2.Lerp(_rb.position, target, Mathf.Clamp01(deltaTime / span)));

        return remaining - deltaTime;
    }

    /// <summary>Release-to-fire. No-op if not currently aiming, on cooldown, or without enough cursed energy
    /// (CE can drain below the cost mid-aim under overdrive, so re-check here).</summary>
    public void ReleaseAttack(Vector2 aimPoint)
    {
        if (!_aiming) return;
        _aiming = false;
        if (IsAttacking || _cooldownTimer > 0f) return;
        if (_energy != null && _energy.CurrentEnergy < aimableAttackEnergyCost) return;

        bool overdrive = _overdrive != null && _overdrive.IsActive;
        _currentDamageMultiplier    = overdrive ? overdriveAttackDamageMultiplier     : 1f;
        _currentKnockbackMultiplier = overdrive ? overdriveAttackKnockbackMultiplier  : 1f;
        _currentSizeMultiplier      = overdrive ? overdriveAttackHitboxSizeMultiplier : 1f;
        _currentPlaybackSpeed       = overdrive ? overdriveAttackPlaybackSpeed        : aimableAttackPlaybackSpeed;
        _currentOverdrive           = overdrive;

        _energy?.Drain(aimableAttackEnergyCost);  // guarded above — there's enough

        Vector2 toPoint = aimPoint - (Vector2)transform.position;
        Vector2 fireDir = toPoint.sqrMagnitude > 0.0001f
            ? toPoint.normalized
            : (_anim != null ? _anim.CurrentFacing : Vector2.right);
        Vector2 target = throwable ? ClampToThrowRadius(aimPoint) : (Vector2)transform.position;

        _routine = StartCoroutine(AttackRoutine(fireDir, target));
    }

    /// <summary>Cancels aim mode and any in-progress attack (tears the hitbox down). Caller owns the resulting movement-lock state.</summary>
    public void Cancel()
    {
        _aiming = false;
        if (_routine == null) return;
        StopCoroutine(_routine);
        _routine = null;
        // Drop the jump's i-frames, the leap and the jump layer in case the cancel landed mid-leap;
        // otherwise all three stick. No snap to the target — a cancelled leap stops where it is.
        _leaping = false;
        _health?.SetInvincible(false);
        RestoreLayer();
        if (aimableAttackHitbox != null) aimableAttackHitbox.Disable();
        OnAttackEnded?.Invoke();
    }

    private void EnterJumpLayer()
    {
        if (_onJumpLayer) return;
        _preJumpLayer = gameObject.layer;
        gameObject.layer = jumpLayer;
        _onJumpLayer = true;
    }

    private void RestoreLayer()
    {
        if (!_onJumpLayer) return;
        gameObject.layer = _preJumpLayer;
        _onJumpLayer = false;
    }

    private IEnumerator AttackRoutine(Vector2 aimDir, Vector2 target)
    {
        _movement?.SetCanMove(false);
        if (takeDirection) _anim?.SetFacing(aimDir);
        _anim?.PlayAimableAttack(aimDir, aimableAttackAnim, takeDirection, _currentPlaybackSpeed);

        // Clip is now current — spread the four phases across its full length; per-phase scales reshape
        // the split, then playback speed scales the whole thing. Cooldown/regen are set before the first
        // yield so the guards are in place synchronously, same as the old release-time set.
        float fStartup = aimableAttackStartupFrames * startupFrameScale;
        float fJump    = aimableAttackJumpFrames    * jumpFrameScale;
        float fImpact  = aimableAttackImpactFrames  * impactFrameScale;
        float fEndlag  = aimableAttackEndlagFrames  * endlagFrameScale;
        float perFrame = SecondsPerFrame(fStartup + fJump + fImpact + fEndlag) / Mathf.Max(0.1f, _currentPlaybackSpeed);
        float startup = fStartup * perFrame;
        float jump    = fJump    * perFrame;
        float impact  = fImpact  * perFrame;
        float endlag  = fEndlag  * perFrame;
        _cooldownTimer = aimableAttackCooldownFrames * perFrame;
        _energy?.SuppressRegenForAction(startup + jump + impact + endlag);

        // Published before the event so the network layer can hand this delay to the peers that only
        // receive the attack as an RPC (see ImpactDelay).
        ImpactDelay = startup + jump;
        OnAttackStarted?.Invoke(aimDir, aimableAttackAnim);

        if (startup > 0f) yield return new WaitForSeconds(startup);

        // Jump: airborne and untouchable, no hitbox. Throwable leaps to the clamped target. The player is
        // parked on jumpLayer for the whole leap so it doesn't collide with the other player, restored on impact.
        _health?.SetInvincible(true);
        EnterJumpLayer();
        if (throwable && _rb != null && jump > 0f)
        {
            _leaping       = true;
            _leapTarget    = target;
            _leapRemaining = jump;
        }
        if (jump > 0f) yield return new WaitForSeconds(jump);
        // The phase timer runs on Unity's Update clock and the leap on the physics clock, so land the
        // player exactly on the point if the step count came up a fraction short.
        if (_leaping)
        {
            _rb.MovePosition(_leapTarget);
            _leaping = false;
        }
        RestoreLayer();
        _health?.SetInvincible(false);

        // Impact: vulnerable again, hitbox lands.
        if (aimableAttackHitbox != null)
        {
            if (throwable)          aimableAttackHitbox.PlaceAt(target, aimDir);
            else if (takeDirection) aimableAttackHitbox.Orient(aimDir);
            aimableAttackHitbox.Enable(gameObject, _currentDamageMultiplier, _currentKnockbackMultiplier, _currentSizeMultiplier);
        }

        // Already at the impact window, so no delay — the remote peers schedule theirs off ImpactDelay.
        // The overdrive flag is the release-time snapshot, so dropping Shift mid-leap can't switch the cue.
        _audio?.PlayAimableAttack(_currentOverdrive);

        if (impact > 0f) yield return new WaitForSeconds(impact);

        if (aimableAttackHitbox != null) aimableAttackHitbox.Disable();

        if (endlag > 0f) yield return new WaitForSeconds(endlag);

        _movement?.SetCanMove(true);
        _anim?.RefreshMovementState();
        _routine = null;
        OnAttackEnded?.Invoke();
    }
}
