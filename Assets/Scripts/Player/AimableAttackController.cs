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

    private float     _cooldownTimer;
    private Coroutine _routine;
    private bool      _aiming;
    private int       _preJumpLayer;
    private bool      _onJumpLayer;

    private float _currentDamageMultiplier    = 1f;
    private float _currentKnockbackMultiplier = 1f;
    private float _currentSizeMultiplier      = 1f;
    private float _currentPlaybackSpeed       = 1f;

    public bool  IsAiming           => _aiming;
    public bool  IsAttacking        => _routine != null;
    public float CooldownRemaining  => Mathf.Max(0f, _cooldownTimer);
    public float EnergyCost         => aimableAttackEnergyCost;
    public float PlaybackSpeed      => (_overdrive != null && _overdrive.IsActive) ? overdriveAttackPlaybackSpeed : aimableAttackPlaybackSpeed;
    public bool  TakeDirection      => takeDirection;
    public bool  Throwable          => throwable;
    public float ThrowRadius        => throwRadius;

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
    }

    void Update()
    {
        if (_cooldownTimer > 0f) _cooldownTimer -= Time.deltaTime;
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
        // Drop the jump's i-frames and the jump layer in case the cancel landed mid-leap; otherwise both stick.
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

        OnAttackStarted?.Invoke(aimDir, aimableAttackAnim);

        if (startup > 0f) yield return new WaitForSeconds(startup);

        // Jump: airborne and untouchable, no hitbox. Throwable leaps to the clamped target. The player is
        // parked on jumpLayer for the whole leap so it doesn't collide with the other player, restored on impact.
        _health?.SetInvincible(true);
        EnterJumpLayer();
        if (throwable && _rb != null && jump > 0f)
            yield return LeapTo(target, jump);
        else if (jump > 0f)
            yield return new WaitForSeconds(jump);
        RestoreLayer();
        _health?.SetInvincible(false);

        // Impact: vulnerable again, hitbox lands.
        if (aimableAttackHitbox != null)
        {
            if (throwable)          aimableAttackHitbox.PlaceAt(target, aimDir);
            else if (takeDirection) aimableAttackHitbox.Orient(aimDir);
            aimableAttackHitbox.Enable(gameObject, _currentDamageMultiplier, _currentKnockbackMultiplier, _currentSizeMultiplier);
        }

        if (impact > 0f) yield return new WaitForSeconds(impact);

        if (aimableAttackHitbox != null) aimableAttackHitbox.Disable();

        if (endlag > 0f) yield return new WaitForSeconds(endlag);

        _movement?.SetCanMove(true);
        _anim?.RefreshMovementState();
        _routine = null;
        OnAttackEnded?.Invoke();
    }

    // Carries the rigidbody from its current spot to `target` over `duration`, stepping in
    // FixedUpdate. MovePosition (not velocity) so rigidbody drag can't shorten the leap and the
    // player lands precisely on the throw point. Movement is locked, so PlayerMovement won't fight us.
    private IEnumerator LeapTo(Vector2 target, float duration)
    {
        Vector2 start = _rb.position;
        float elapsed = 0f;
        var wait = new WaitForFixedUpdate();
        while (elapsed < duration)
        {
            elapsed += Time.fixedDeltaTime;
            _rb.MovePosition(Vector2.Lerp(start, target, Mathf.Clamp01(elapsed / duration)));
            yield return wait;
        }
        _rb.MovePosition(target);
    }
}
