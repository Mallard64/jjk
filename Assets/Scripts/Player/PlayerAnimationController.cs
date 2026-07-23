using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Drives animator state names directly (no animator parameters — see project memory).
/// Default state naming: "{action}-{dir}", e.g. "idle-s", "punch-se", "walk-ne".
/// If characterPrefix is set, names become "{prefix}-{action}-{dir}", e.g. "sukuna-idle-s".
/// 8 input directions collapse onto 4 unique clips: -s, -se, -ne, -n.
/// East/West share the southeast clip (W is just SE flipped); SW also reuses SE flipped.
/// </summary>
public class PlayerAnimationController : MonoBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private SpriteRenderer spriteRenderer;
    [SerializeField] private float hurtLockDuration = 0.35f;
    [Tooltip("Seconds of damage-invincibility granted on being hit. Kept short so follow-up hits can combo while the player is still staggered (the movement/anim hitstun lasts the full hurtLockDuration / hit's hitstun). Set to 0 for no i-frames on hit.")]
    [SerializeField] private float hurtInvincibilityDuration = 0.1f;
    [Tooltip("Seconds of the 'hurt' clip played as the intro before holding on 'hurtf'. In seconds (not frames) so it's independent of the clip's sample rate.")]
    [SerializeField] private float hurtIntroSeconds = 0.15f;
    [Tooltip("Seconds of the 'hurt' clip played as the outro after the 'hurtf' hold (the clip's last stretch).")]
    [SerializeField] private float hurtOutroSeconds = 0.3f;

    [Tooltip("Combo decay: each hit after the first in a combo multiplies the victim's knockback by an extra this much, so combos push the victim out of range instead of running forever. Resets when the combo ends.")]
    [SerializeField] private float comboKnockbackGrowth = 0.5f;
    [Tooltip("Cap on the combo knockback multiplier.")]
    [SerializeField] private float comboKnockbackMax = 3f;

    [Tooltip("Applied-knockback magnitude at/above which being slammed into a wall plays the wall_hurt/wall_hurtf splat instead of the normal hurt recovery. Default 7 ≈ an overdrive hit (normal hits sit at 3-5, overdrive 7.5-12.5).")]
    [SerializeField] private float wallHurtKnockbackThreshold = 7f;
    [Tooltip("Fixed chip damage dealt to the player on a wall hit (it slammed into a wall).")]
    [SerializeField] private float wallSplatDamage = 8f;
    [Tooltip("Fraction of the wall hitstun kept on each consecutive wall hit (0.5 = each wall hit lasts 50% as long as the previous), so repeated wall hits can't lock forever. 1 = no decay.")]
    [Range(0f, 1f)]
    [SerializeField] private float wallHitstunDecay = 0.5f;

    [Tooltip("Optional character clip prefix. Leave empty for default names like 'idle-s'. If set (e.g. 'sukuna'), names become 'sukuna-idle-s'.")]
    [SerializeField] private string characterPrefix = "";

    private Animator _animator;
    private string _currentAnim = "";
    private string _facingSuffix = "-s";
    private bool _facingFlipX = false;
    private bool _isMoving = false;
    private bool _locked;
    private bool _attackLocked;
    // True when the most recent ApplyFacing input was east or west (sector 0 / ±4). Lets attack
    // states swap the folded "-se" clip for a dedicated "-e" if the controller defines one.
    private bool _facingIsEastOrWest;
    private bool _overdriveMode;
    private float _pendingHitstun = -1f;
    private int _comboHits;  // consecutive hits taken while staying in hitstun; drives knockback decay
    private float _lastHitKnockback;   // magnitude of the most recent hit's applied knockback
    private float _currentHitstun;     // base hitstun of the current hit (the wall_hurtf hold)
    private bool _wallReactionDone;    // wall splat already triggered for the current hit
    private int _wallHitCount;         // consecutive wall hits this combo; halves wall hitstun each time
    private Coroutine _hurtRoutine;    // the active HurtLock/WallHurtLock so a wall hit can swap it
    private readonly HashSet<string> _missingStateWarned = new HashSet<string>();

    public bool IsHitstun => _locked;

    /// <summary>Knockback multiplier for the current incoming hit: 1 for the first hit of a combo,
    /// growing each consecutive in-stun hit (capped) so combos self-terminate by pushing the victim away.
    /// Read by the damage path AFTER PlayHurt has registered this hit.</summary>
    public float ComboKnockbackMultiplier =>
        Mathf.Min(comboKnockbackMax, 1f + Mathf.Max(0, _comboHits - 1) * comboKnockbackGrowth);

    void Awake()
    {
        _animator = animator != null ? animator : GetComponentInChildren<Animator>();
        if (_animator == null)
            Debug.LogError($"PlayerAnimationController on {gameObject.name}: no Animator found.");
        else if (!_animator.enabled)
            _animator.enabled = true;

        if (spriteRenderer == null)
            spriteRenderer = GetComponentInChildren<SpriteRenderer>();
    }

    public void SetMoveDirection(Vector2 dir)
    {
        if (_locked || _attackLocked) return;
        if (dir.sqrMagnitude >= 0.01f) ApplyFacing(dir);
        RefreshMoveAnim();
    }

    public void SetIsMoving(bool value)
    {
        if (_locked || _attackLocked) return;
        _isMoving = value;
        RefreshMoveAnim();
    }

    public void PlayAutoAttack(string action, bool directional = true, float playbackSpeed = 1f)
    {
        _attackLocked = true;
        SetAnimatorSpeed(playbackSpeed);
        TryPlay(directional ? PickAttackState(action) : BuildState(action, ""));
    }

    public void PlayAimableAttack(Vector2 aimDir, string action, bool directional = true, float playbackSpeed = 1f)
    {
        _attackLocked = true;
        SetAnimatorSpeed(playbackSpeed);
        if (directional)
        {
            if (aimDir.sqrMagnitude >= 0.01f) ApplyFacing(aimDir);
            TryPlay(PickAttackState(action));
        }
        else
        {
            // Direction-less: play the action clip by name only (like roll/hurt), no facing applied.
            TryPlay(BuildState(action, ""));
        }
    }

    public void PlayRoll(Vector2 dir, string action)
    {
        _attackLocked = true;
        SetAnimatorSpeed(1f);  // attack playback speed doesn't carry into the roll
        // ApplyFacing still runs so post-roll idle/walk face the dash direction, but the roll clip
        // itself is direction-less (single state) — like hurt/death.
        if (dir.sqrMagnitude >= 0.01f) ApplyFacing(dir);
        TryPlay(BuildState(action, ""));
    }

    // Attacks may scale the animator's playback (see AutoAttackController/AimableAttackController
    // attackPlaybackSpeed). Reset to 1 on every exit from an attack so hurt/idle/walk play normally.
    private void SetAnimatorSpeed(float speed)
    {
        if (_animator != null) _animator.speed = Mathf.Max(0.01f, speed);
    }

    // Picks "{action}-e" when the input was pure east/west and the controller defines that clip;
    // falls back to the regular folded "-se" / direction suffix otherwise. flipX (already set by
    // ApplyFacing) handles mirroring "-e" for west.
    private string PickAttackState(string action)
    {
        if (_facingIsEastOrWest)
        {
            string eastState = BuildState(action, "-e");
            if (_animator != null && _animator.HasState(0, Animator.StringToHash(eastState)))
                return eastState;
        }
        return BuildState(action, _facingSuffix);
    }

    /// <summary>Play the domain activation pose (direction-less single clip "domain", like roll). Locked
    /// like an attack so movement doesn't override it during the startup freeze; RefreshMovementState
    /// (called when the domain opens) clears it.</summary>
    public void PlayDomain()
    {
        _attackLocked = true;
        SetAnimatorSpeed(1f);
        TryPlay(BuildState("domain", ""));
    }

    /// <summary>Set the hitstun (seconds) the NEXT PlayHurt should use. Consumed once; falls back to
    /// hurtLockDuration when unset. The hit's Hitbox supplies this just before damage is applied.</summary>
    public void SetNextHitstun(float seconds)
    {
        _pendingHitstun = seconds;
    }

    /// <summary>Records the magnitude of the knockback actually applied for the current hit, so a later
    /// wall collision can decide whether it was hard enough to trigger the wall splat. Set by the damage
    /// path right after AddForce.</summary>
    public void SetLastHitKnockback(float magnitude) => _lastHitKnockback = magnitude;

    /// <summary>Called by the movement layer when this player slams into a wall while in hitstun. If the
    /// hit's knockback met the wall-splat threshold, swaps the running hurt recovery for the
    /// wall_hurt/wall_hurtf sequence (once per hit). No-ops if those states aren't authored.</summary>
    public void NotifyWallHit()
    {
        if (!_locked || _wallReactionDone || _animator == null) return;
        if (_lastHitKnockback < wallHurtKnockbackThreshold) return;
        if (!_animator.HasState(0, Animator.StringToHash(BuildState("wall_hurt", "")))) return;

        _wallReactionDone = true;
        _wallHitCount++;
        // Wall hitstun decays by wallHitstunDecay per consecutive wall hit (resets when the player
        // recovers), so repeated wall hits can't lock someone forever.
        float hold = _currentHitstun * Mathf.Pow(wallHitstunDecay, _wallHitCount - 1);

        if (_hurtRoutine != null) StopCoroutine(_hurtRoutine);
        _hurtRoutine = StartCoroutine(WallHurtLock(hold));

        // Hitting a wall hurts: fixed chip damage, applied without re-triggering the hurt reaction.
        if (wallSplatDamage > 0f) GetComponent<PlayerHealth>()?.TakeReactionlessDamage(wallSplatDamage);
    }

    /// <summary>Called by the damage path right after a hit lands: if the victim is up against a wall,
    /// trigger the wall splat immediately (being adjacent counts — no need to be knocked into it).</summary>
    public void CheckWallSplatOnHit()
    {
        var movement = GetComponent<PlayerMovement>();
        if (movement != null && movement.IsAdjacentToWall()) NotifyWallHit();
    }

    public void PlayHurt()
    {
        // _locked is still true here if we're already in hitstun from a prior hit — i.e. this hit
        // continues a combo. Count it before HurtLock re-arms so ComboKnockbackMultiplier can grow.
        _comboHits = _locked ? _comboHits + 1 : 1;
        _attackLocked = false;
        SetAnimatorSpeed(1f);  // a hit can interrupt a sped-up attack mid-swing
        GetComponent<PlayerHealth>()?.SetInvincible(true);
        TryPlay(BuildState("hurt", ""));
        float lockDuration = _pendingHitstun >= 0f ? _pendingHitstun : hurtLockDuration;
        _pendingHitstun = -1f;
        _currentHitstun = lockDuration;
        _wallReactionDone = false;  // this hit hasn't splatted into a wall yet
        StopAllCoroutines();
        StartCoroutine(HurtInvincibility());
        _hurtRoutine = StartCoroutine(HurtLock(lockDuration));
    }

    // I-frames are intentionally shorter than the hitstun lock so the player can be hit again
    // (combos) while still staggered. Owns invincibility's lifetime end-to-end; HurtLock no longer
    // clears it. A duration of 0 clears it the same frame, i.e. no i-frames on hit.
    private IEnumerator HurtInvincibility()
    {
        if (hurtInvincibilityDuration > 0f)
            yield return new WaitForSeconds(hurtInvincibilityDuration);
        GetComponent<PlayerHealth>()?.SetInvincible(false);
    }

    public void PlayDeath()
    {
        StopAllCoroutines();
        _locked = true;
        _attackLocked = false;
        _comboHits = 0; _wallHitCount = 0;
        SetAnimatorSpeed(1f);
        TryPlay(BuildState("death", ""));
    }

    public void ResetState()
    {
        StopAllCoroutines();
        _locked = false;
        _attackLocked = false;
        _isMoving = false;
        _comboHits = 0; _wallHitCount = 0;
        SetAnimatorSpeed(1f);
        RefreshMoveAnim();
    }

    public void PlayIdle() => SetIsMoving(false);

    /// <summary>Clears the attack lock and resets the animator to idle/walk. Call at the end of an attack routine.</summary>
    public void RefreshMovementState()
    {
        _attackLocked = false;
        SetAnimatorSpeed(1f);  // attack finished — back to normal-speed idle/walk
        RefreshMoveAnim();
    }

    /// <summary>Override facing without triggering an animation. Used by auto-aim before PlayAutoAttack.</summary>
    public void SetFacing(Vector2 dir)
    {
        if (dir.sqrMagnitude < 0.01f) return;
        ApplyFacing(dir);
    }

    /// <summary>Face the player AWAY from the source of a hit (opposite of knockback direction).</summary>
    public void SetFacingFromHit(Vector2 knockback)
    {
        if (knockback.sqrMagnitude < 0.01f) return;
        ApplyFacing(-knockback);
    }

    /// <summary>Current facing as a normalized world-space vector (8-way, mirrored via flipX).</summary>
    public Vector2 CurrentFacing
    {
        get
        {
            Vector2 v = _facingSuffix switch
            {
                "-s"  => new Vector2(0f, -1f),
                "-se" => new Vector2(1f, -1f).normalized,
                "-ne" => new Vector2(1f,  1f).normalized,
                "-n"  => new Vector2(0f,  1f),
                _     => new Vector2(0f, -1f),
            };
            if (_facingFlipX) v.x = -v.x;
            return v;
        }
    }

    private void ApplyFacing(Vector2 dir)
    {
        (_facingSuffix, _facingFlipX) = ComputeFacing(dir);
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        int sector = Mathf.RoundToInt(angle / 45f);
        if (sector == -4) sector = 4;
        _facingIsEastOrWest = (sector == 0 || sector == 4);
        if (spriteRenderer != null) spriteRenderer.flipX = _facingFlipX;
    }

    private void RefreshMoveAnim()
    {
        string action = _isMoving ? "walk" : "idle";
        if (_overdriveMode)
        {
            // Prefer 'overdrive-walk-{dir}' / 'overdrive-idle-{dir}' if authored; silently fall
            // back to the base clip so a partial overdrive clip set is safe while authoring art.
            string overdriveState = BuildState("overdrive-" + action, _facingSuffix);
            if (_animator != null && _animator.HasState(0, Animator.StringToHash(overdriveState)))
            {
                TryPlay(overdriveState);
                return;
            }
        }
        TryPlay(BuildState(action, _facingSuffix));
    }

    /// <summary>Toggle the overdrive-prefixed idle/walk lookup. Called by PlayerOverdrive when its state flips.</summary>
    public void SetOverdriveMode(bool value)
    {
        if (_overdriveMode == value) return;
        _overdriveMode = value;
        // Only swap immediately if we're not in the middle of an action — otherwise the
        // overdrive idle/walk will pick up on the next RefreshMovementState() at action end.
        if (!_locked && !_attackLocked) RefreshMoveAnim();
    }

    private string BuildState(string action, string dirSuffix)
    {
        return string.IsNullOrEmpty(characterPrefix)
            ? action + dirSuffix
            : characterPrefix + "-" + action + dirSuffix;
    }

    /// <summary>True if the animator defines the canonical south clip for `action` (e.g. 'overdrive-punch-s').
    /// Used by techniques to decide whether a per-character overdrive variant is authored before falling back.</summary>
    public bool HasActionState(string action)
    {
        if (_animator == null || string.IsNullOrEmpty(action)) return false;
        return _animator.HasState(0, Animator.StringToHash(BuildState(action, "-s")));
    }

    // 8-way sectoring. East folds onto Southeast (no -e clip); West/SW also reuse SE flipped.
    // Net: 4 unique directional clips (-s, -se, -ne, -n).
    private static (string suffix, bool flipX) ComputeFacing(Vector2 dir)
    {
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg; // -180..180, 0 = east
        int sector = Mathf.RoundToInt(angle / 45f);              // -4..4 (where -4 and 4 both = west)
        if (sector == -4) sector = 4;
        return sector switch
        {
            0  => ("-se", false),  // east  → reuse southeast
            1  => ("-ne", false),  // northeast
            2  => ("-n",  false),  // north
            3  => ("-ne", true),   // northwest = mirrored northeast
            4  => ("-se", true),   // west       → mirrored southeast
            -1 => ("-se", false),  // southeast
            -2 => ("-s",  false),  // south
            -3 => ("-se", true),   // southwest  = mirrored southeast
            _  => ("-s",  false),
        };
    }

    private void TryPlay(string stateName)
    {
        if (_animator == null) return;
        if (_currentAnim == stateName) return;
        if (!_animator.HasState(0, Animator.StringToHash(stateName)))
        {
            if (_missingStateWarned.Add(stateName))
                Debug.LogWarning($"PlayerAnimationController on {gameObject.name}: animator state '{stateName}' not found — add the clip or this transition will silently no-op.");
            return;
        }
        _animator.Play(stateName);
        // Force an immediate animator tick. Without this, Play() silently no-ops when
        // we're transitioning out of a non-looping state whose clip has already finished
        // (e.g. punch-* with m_LoopTime=0), leaving the sprite stuck on the last attack
        // frame and blocking every subsequent state change until the animator unsticks.
        _animator.Update(0f);
        _currentAnim = stateName;
    }

    private IEnumerator HurtLock(float lockDuration)
    {
        _locked = true;
        GetComponent<PlayerMovement>()?.SetCanMove(false);

        // "hurt" is already playing from frame 0 (started in PlayHurt). Split the hitstun as: the first
        // hurtIntroSeconds of "hurt" → hold on "hurtf" for as long as the lock needs → the last
        // hurtOutroSeconds of "hurt". Bookends are in seconds (not clip frames) so a high clip sample
        // rate can't shrink them to nothing. The lock is floored to the clip so hitstun never ends mid-anim.
        float clipLength = CurrentClipLength();
        float introDuration = hurtIntroSeconds;
        float outroDuration = hurtOutroSeconds;

        float effectiveLock = Mathf.Max(lockDuration, clipLength);
        float holdDuration  = effectiveLock - introDuration - outroDuration;

        bool canSplit = clipLength > 0f
                        && holdDuration > 0f
                        && (introDuration + outroDuration) <= clipLength
                        && _animator != null
                        && _animator.HasState(0, Animator.StringToHash(BuildState("hurtf", "")));

        if (canSplit)
        {
            yield return new WaitForSeconds(introDuration);
            TryPlay(BuildState("hurtf", ""));
            yield return new WaitForSeconds(holdDuration);
            PlayAt(BuildState("hurt", ""), (clipLength - outroDuration) / clipLength);
            yield return new WaitForSeconds(outroDuration);
        }
        else
        {
            // No "hurtf" authored, or the clip/lock is too short to split: hold "hurt" for the window.
            yield return new WaitForSeconds(effectiveLock);
        }

        _locked = false;
        _comboHits = 0; _wallHitCount = 0;  // recovered without another hit — combo over, knockback decay resets
        GetComponent<PlayerMovement>()?.SetCanMove(true);
        RefreshMoveAnim();
    }

    // Wall splat: intro frames of "wall_hurt" → hold on "wall_hurtf" for the base hitstun → the last
    // frames of "wall_hurt". Same split shape as HurtLock; swapped in by NotifyWallHit mid-stun.
    private IEnumerator WallHurtLock(float holdDuration)
    {
        _locked = true;
        GetComponent<PlayerMovement>()?.SetCanMove(false);

        TryPlay(BuildState("wall_hurt", ""));
        float clipLength = CurrentClipLength();
        // Bookends in seconds; clamped so they never exceed the wall_hurt clip (which may differ from hurt).
        float introDuration = Mathf.Min(hurtIntroSeconds, clipLength);
        float outroDuration = Mathf.Min(hurtOutroSeconds, Mathf.Max(0f, clipLength - introDuration));
        bool hasWallHurtf = _animator != null && _animator.HasState(0, Animator.StringToHash(BuildState("wall_hurtf", "")));

        if (introDuration > 0f) yield return new WaitForSeconds(introDuration);
        if (hasWallHurtf) TryPlay(BuildState("wall_hurtf", ""));
        if (holdDuration > 0f) yield return new WaitForSeconds(holdDuration);
        if (outroDuration > 0f && clipLength > 0f)
        {
            PlayAt(BuildState("wall_hurt", ""), (clipLength - outroDuration) / clipLength);
            yield return new WaitForSeconds(outroDuration);
        }

        _locked = false;
        _comboHits = 0; _wallHitCount = 0;
        GetComponent<PlayerMovement>()?.SetCanMove(true);
        RefreshMoveAnim();
    }

    /// <summary>Length in seconds of the clip currently playing, or 0 if none is readable. Attack
    /// controllers read this right after playing their clip to spread phase frames across the animation.</summary>
    public float CurrentClipLength()
    {
        if (_animator == null) return 0f;
        var infos = _animator.GetCurrentAnimatorClipInfo(0);
        return (infos != null && infos.Length > 0 && infos[0].clip != null) ? infos[0].clip.length : 0f;
    }

    // Like TryPlay but starts the state at a normalized time and bypasses the same-state guard, so it
    // can re-enter "hurt" partway through (used to play the clip's tail for the hurt outro).
    private void PlayAt(string stateName, float normalizedTime)
    {
        if (_animator == null) return;
        if (!_animator.HasState(0, Animator.StringToHash(stateName))) return;
        _animator.Play(stateName, 0, normalizedTime);
        _animator.Update(0f);
        _currentAnim = stateName;
    }
}
