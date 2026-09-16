using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Drives the player's auto attack: startup → active → endlag → cooldown.
/// Owns its own AttackHitboxController ("auto attack hitbox") and orients/toggles it during the
/// active window. Reads PlayerOverdrive.IsActive at fire time and swaps in the heavy variant.
/// Movement is locked for the whole startup+active+endlag window.
/// </summary>
public class AutoAttackController : MonoBehaviour
{
    // Phase lengths are in animation frames. At fire time, one frame = clipLength / (startup+active+endlag),
    // so startup→active→endlag spreads across the WHOLE attack clip — the swing always plays in full. Set
    // each phase to how many of your animation's frames it should occupy; nothing else to configure.
    [Header("Timing (animation frames)")]
    [Tooltip("Wind-up frames before the hitbox becomes active.")]
    [SerializeField] private int   autoAttackStartupFrames  = 2;
    [Tooltip("Frames the hitbox is active and can hit.")]
    [SerializeField] private int   autoAttackActiveFrames   = 2;
    [Tooltip("Recovery frames after the hitbox deactivates. Movement stays locked through this. startup+active+endlag = your animation's frame count.")]
    [SerializeField] private int   autoAttackEndlagFrames   = 2;
    [Tooltip("Minimum frames between attack starts (same frame unit). Measured from start; if shorter than startup+active+endlag, no extra wait after recovery.")]
    [SerializeField] private int   autoAttackCooldownFrames = 8;
    [Tooltip("Speeds up (>1) or slows down (<1) the whole attack: the animation plays at this rate and every phase + the cooldown scale by 1/speed. 1 = authored speed.")]
    [Range(0.1f, 4f)]
    [SerializeField] private float autoAttackPlaybackSpeed  = 1f;

    [Header("Phase frame scales")]
    [Tooltip("Per-phase multipliers on the frame counts above. Reshape how the clip is split between phases (e.g. a bigger active window) without re-authoring the ints. 1 = as authored.")]
    [Range(0.1f, 4f)] [SerializeField] private float startupFrameScale = 1f;
    [Range(0.1f, 4f)] [SerializeField] private float activeFrameScale  = 1f;
    [Range(0.1f, 4f)] [SerializeField] private float endlagFrameScale  = 1f;

    [Header("Animation")]
    [Tooltip("Action name for the auto attack state, e.g. 'punch' → 'punch-se'.")]
    [SerializeField] private string autoAttackAnim = "punch";
    [Tooltip("If true, the attack aims by direction: plays '{anim}-{dir}' and orients the hitbox toward the aim. If false, it ignores direction and plays the bare '{anim}' clip with the hitbox at its authored placement.")]
    [SerializeField] private bool takeDirection = true;

    [Header("Throwable")]
    [Tooltip("If true, the attack lands at the aim point (clamped to throwRadius) instead of being a directional swing — the hitbox drops on the cursor. The reticle becomes a circle at that point.")]
    [SerializeField] private bool  throwable   = false;
    [Tooltip("Max distance from the player the throwable target (and its reticle) can sit.")]
    [SerializeField] private float throwRadius = 4f;

    [Header("Movement")]
    [Tooltip("Fraction of moveSpeed the player keeps while the auto attack is in progress (0 = rooted, 1 = full speed). Lets the player drift slightly to reposition mid-swing.")]
    [Range(0f, 1f)]
    [SerializeField] private float attackMoveSpeedMultiplier = 0.35f;

    [Header("Overdrive Variant")]
    [Tooltip("Used when PlayerOverdrive.IsActive at the moment TryAttack fires. Snapshots at fire time — releasing Shift mid-swing does not revert. Frames spread across the heavy clip the same way (startup+active+endlag = its frame count).")]
    [SerializeField] private int    overdriveAttackStartupFrames    = 3;
    [SerializeField] private int    overdriveAttackActiveFrames     = 2;
    [SerializeField] private int    overdriveAttackEndlagFrames     = 4;
    [SerializeField] private int    overdriveAttackCooldownFrames   = 14;
    [Tooltip("Multiplied with the Hitbox's base damage when the attack fires in overdrive.")]
    [SerializeField] private float  overdriveAttackDamageMultiplier = 1.8f;
    [Tooltip("Multiplied with the Hitbox's base knockback when the attack fires in overdrive.")]
    [SerializeField] private float  overdriveAttackKnockbackMultiplier = 2.5f;
    [Tooltip("Scales the hitbox size when the attack fires in overdrive (1.5–2 = noticeably bigger).")]
    [SerializeField] private float  overdriveAttackHitboxSizeMultiplier = 1.75f;
    [Tooltip("Playback rate for the overdrive auto attack (separate from the normal autoAttackPlaybackSpeed). Snapshotted at fire time.")]
    [Range(0.1f, 4f)]
    [SerializeField] private float  overdriveAttackPlaybackSpeed = 1f;
    [Tooltip("Fallback animation when no per-character overdrive variant is authored. Runtime first tries 'overdrive-{autoAttackAnim}-{dir}' (e.g. 'overdrive-punch-se') and only uses this name when that state is missing.")]
    [SerializeField] private string overdriveAttackAnim = "heavy";

    [Header("Hitbox")]
    [SerializeField] private AttackHitboxController autoAttackHitbox;

    private Resonance                 _energy;
    private PlayerAnimationController _anim;
    private PlayerOverdrive           _overdrive;
    private PlayerAudio               _audio;

    private float     _cooldownTimer;
    private Coroutine _routine;

    private int     _startupFrames, _activeFrames, _endlagFrames, _cooldownFrames;
    private string  _currentAnim;
    private Vector2 _currentAimDir;
    private Vector2 _currentTarget;
    private float   _currentDamageMultiplier = 1f;
    private float   _currentKnockbackMultiplier = 1f;
    private float   _currentSizeMultiplier = 1f;
    private float   _currentPlaybackSpeed = 1f;
    private bool    _currentOverdrive;

    public bool  IsAttacking       => _routine != null;
    public float CooldownRemaining => Mathf.Max(0f, _cooldownTimer);
    /// <summary>Seconds from the start of the swing to the active window, resolved at fire time (so it
    /// accounts for the clip that actually played, the overdrive variant and the playback speed). Read by
    /// FusionPlayerCombat when it replicates the attack, so remote peers — which never run this routine —
    /// can time the swing SFX to the same frame the hitbox comes out on.</summary>
    public float ActiveDelay       { get; private set; }
    public bool  TakeDirection     => takeDirection;
    public bool  Throwable         => throwable;
    public float ThrowRadius       => throwRadius;
    /// <summary>Fraction of base move speed allowed while attacking; read by the movement scripts.</summary>
    public float AttackMoveSpeedMultiplier => attackMoveSpeedMultiplier;
    /// <summary>Whether the swing in flight was fired in overdrive — the fire-time snapshot, not the live
    /// stance. FusionPlayerCombat sends it with the attack so a remote peer resolves the playback speed and
    /// the SFX cue from the very value the timing was computed from.</summary>
    public bool FiredInOverdrive => _currentOverdrive;

    /// <summary>Playback rate for an attack fired in (or out of) overdrive. Takes the flag explicitly rather
    /// than reading the live stance: on a remote peer the mirrored stance is applied in Render() and can lag
    /// the attack RPC, which would play the clip at one rate while the SFX delay was computed for the other —
    /// landing the swing sound off the active frames.</summary>
    public float PlaybackSpeedFor(bool overdrive)
        => overdrive ? overdriveAttackPlaybackSpeed : autoAttackPlaybackSpeed;

    // Seconds per frame = clipLength / spanFrames, so the spanning phases fill the whole attack clip.
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

    // Local-side events the network adapter listens to so it can replicate the attack to proxies.
    public event Action<string, Vector2> OnAttackStarted;
    public event Action                  OnAttackEnded;

    void Awake()
    {
        _energy    = GetComponent<Resonance>();
        _anim      = GetComponent<PlayerAnimationController>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _audio     = GetComponent<PlayerAudio>();
    }

    void Update()
    {
        if (_cooldownTimer > 0f) _cooldownTimer -= Time.deltaTime;
    }

    public bool CanAttack() => _cooldownTimer <= 0f && !IsAttacking;

    public void TryAttack(Vector2 aimPoint)
    {
        if (!CanAttack()) return;

        bool overdrive = _overdrive != null && _overdrive.IsActive;
        _startupFrames  = overdrive ? overdriveAttackStartupFrames  : autoAttackStartupFrames;
        _activeFrames   = overdrive ? overdriveAttackActiveFrames   : autoAttackActiveFrames;
        _endlagFrames   = overdrive ? overdriveAttackEndlagFrames   : autoAttackEndlagFrames;
        _cooldownFrames = overdrive ? overdriveAttackCooldownFrames : autoAttackCooldownFrames;
        _currentAnim                = overdrive ? ResolveOverdriveAnim()  : autoAttackAnim;
        _currentDamageMultiplier    = overdrive ? overdriveAttackDamageMultiplier    : 1f;
        _currentKnockbackMultiplier = overdrive ? overdriveAttackKnockbackMultiplier : 1f;
        _currentSizeMultiplier      = overdrive ? overdriveAttackHitboxSizeMultiplier : 1f;
        _currentPlaybackSpeed       = overdrive ? overdriveAttackPlaybackSpeed        : autoAttackPlaybackSpeed;
        _currentOverdrive           = overdrive;

        Vector2 toPoint = aimPoint - (Vector2)transform.position;
        _currentAimDir = toPoint.sqrMagnitude > 0.0001f
            ? toPoint.normalized
            : (_anim != null ? _anim.CurrentFacing : Vector2.right);
        _currentTarget = throwable ? ClampToThrowRadius(aimPoint) : aimPoint;

        _routine = StartCoroutine(AttackRoutine());
    }

    /// <summary>Aborts an in-progress attack and tears the hitbox down. Caller owns the resulting movement-lock state.</summary>
    public void Cancel()
    {
        if (_routine == null) return;
        StopCoroutine(_routine);
        _routine = null;
        if (autoAttackHitbox != null) autoAttackHitbox.Disable();
        OnAttackEnded?.Invoke();
    }

    private IEnumerator AttackRoutine()
    {
        // No hard movement lock — the player keeps a fraction of move speed for repositioning
        // (AttackMoveSpeedMultiplier, applied by the movement scripts). The animator's _attackLocked
        // still freezes the swing clip so the drift doesn't override it.
        if (takeDirection) _anim?.SetFacing(_currentAimDir);
        _anim?.PlayAutoAttack(_currentAnim, takeDirection, _currentPlaybackSpeed);

        // Clip is now current — spread the spanning phases (startup+active+endlag) across its full length,
        // per-phase scales reshape the split, then playback speed scales the whole thing.
        // Set before the first yield so the cooldown guard is in place synchronously, same as the old fire-time set.
        float fStartup = _startupFrames * startupFrameScale;
        float fActive  = _activeFrames  * activeFrameScale;
        float fEndlag  = _endlagFrames  * endlagFrameScale;
        float perFrame = SecondsPerFrame(fStartup + fActive + fEndlag) / Mathf.Max(0.1f, _currentPlaybackSpeed);
        float startup = fStartup * perFrame;
        float active  = fActive  * perFrame;
        float endlag  = fEndlag  * perFrame;
        _cooldownTimer = _cooldownFrames * perFrame;
        _energy?.SuppressRegenForAction(startup + active + endlag);

        // Published before the event so the network layer can hand this delay to the peers that only
        // receive the attack as an RPC (see ActiveDelay).
        ActiveDelay = startup;
        OnAttackStarted?.Invoke(_currentAnim, _currentAimDir);

        if (startup > 0f) yield return new WaitForSeconds(startup);

        if (autoAttackHitbox != null)
        {
            if (throwable)          autoAttackHitbox.PlaceAt(_currentTarget, _currentAimDir);
            else if (takeDirection) autoAttackHitbox.Orient(_currentAimDir);
            autoAttackHitbox.Enable(gameObject, _currentDamageMultiplier, _currentKnockbackMultiplier, _currentSizeMultiplier);
        }

        // Already at the active window, so no delay — the remote peers schedule theirs off ActiveDelay.
        // The overdrive flag is the fire-time snapshot, so releasing Shift mid-swing can't switch the cue.
        _audio?.PlayAutoAttack(_currentOverdrive);

        if (active > 0f) yield return new WaitForSeconds(active);

        if (autoAttackHitbox != null) autoAttackHitbox.Disable();

        if (endlag > 0f) yield return new WaitForSeconds(endlag);

        // Force an immediate switch out of the attack state. Without this, exiting would have to
        // wait until PlayerMovement's next FixedUpdate (~20ms) to drive the animator.
        _anim?.RefreshMovementState();
        _routine = null;
        OnAttackEnded?.Invoke();
    }

    // Prefer "overdrive-{autoAttackAnim}" if the animator has it; otherwise fall back to overdriveAttackAnim.
    private string ResolveOverdriveAnim()
    {
        string convention = "overdrive-" + autoAttackAnim;
        return (_anim != null && _anim.HasActionState(convention)) ? convention : overdriveAttackAnim;
    }
}
