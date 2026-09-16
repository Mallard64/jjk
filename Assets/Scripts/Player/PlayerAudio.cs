using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-player sound effects. Every cue is a LIST of clips: one is picked at random per play so a repeated
/// action doesn't sound identical twice, and an empty (or all-null) list is silently skipped — a character
/// can ship with only part of its sound set authored.
///
/// Each cue is deliberately fired from the one code path that already runs on EVERY peer, so host and
/// client both hear it with no extra replication: the animation hooks (roll / hit / death), the null field's
/// SetActiveState (owner + proxy), and a local poll for footsteps. The two attack cues are the exception —
/// combat only simulates on the server — so the simulating peer plays them from its attack routine and the
/// other peers are handed the same delay through FusionPlayerCombat's attack RPCs.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class PlayerAudio : MonoBehaviour
{
    [Header("Movement")]
    [Tooltip("Footsteps, played on a repeat while the walk animation is showing.")]
    [SerializeField] private List<AudioClip> footstepSfx = new List<AudioClip>();
    [Tooltip("Seconds between footsteps while walking.")]
    [SerializeField] private float footstepInterval = 0.35f;
    [SerializeField] private List<AudioClip> rollSfx = new List<AudioClip>();

    [Header("Combat")]
    [Tooltip("Auto attack swing — played on the active frames, not at the start of the wind-up.")]
    [SerializeField] private List<AudioClip> autoAttackSfx = new List<AudioClip>();
    [Tooltip("Heavy auto attack, used when the swing fires in overdrive. Leave empty to reuse autoAttackSfx.")]
    [SerializeField] private List<AudioClip> overdriveAutoAttackSfx = new List<AudioClip>();
    [Tooltip("Aimable attack — played on the impact frames, after the leap.")]
    [SerializeField] private List<AudioClip> aimableAttackSfx = new List<AudioClip>();
    [Tooltip("Heavy aimable attack, used when it fires in overdrive. Leave empty to reuse aimableAttackSfx.")]
    [SerializeField] private List<AudioClip> overdriveAimableAttackSfx = new List<AudioClip>();
    [Tooltip("Played on this player when a hit lands on them.")]
    [SerializeField] private List<AudioClip> hitSfx = new List<AudioClip>();
    [Tooltip("Played on this player's KO.")]
    [SerializeField] private List<AudioClip> deathSfx = new List<AudioClip>();

    [Header("Stance")]
    [Tooltip("Played when this player enters overdrive. Not played for the free overdrive a null field grants — the null field has its own cue.")]
    [SerializeField] private List<AudioClip> overdriveSfx = new List<AudioClip>();
    [Tooltip("Played when this player's null field opens.")]
    [SerializeField] private List<AudioClip> nullFieldSfx = new List<AudioClip>();

    [Header("Mix")]
    [Tooltip("Volume scale for the hit cue, so a landed hit reads louder than the swing that caused it. Applied per play (PlayOneShot's own scale), so nothing has to be restored afterwards.")]
    [Range(0f, 2f)] [SerializeField] private float hitVolume = 1.4f;
    [Tooltip("Volume scale for the death cue.")]
    [Range(0f, 2f)] [SerializeField] private float deathVolume = 1.6f;

    private AudioSource _source;
    private PlayerAnimationController _anim;
    private float _footstepTimer;
    private Coroutine _pendingAttack;

    // Every live player's audio on this peer, so a KO can silence the others (see PlayDeath).
    private static readonly List<PlayerAudio> All = new List<PlayerAudio>();
    // While Time.time is under this, every cue except the death cue is suppressed on every player.
    private static float _deathPriorityUntil;

    void Awake()
    {
        _source = GetComponent<AudioSource>();
        _anim   = GetComponent<PlayerAnimationController>();
    }

    void OnEnable()  { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); }

    void Update()
    {
        // Footsteps are polled, not event-driven: walking is a sustained state, and the same poll works on
        // every peer (offline PlayerMovement and online FusionPlayerMovement.Render both feed the animator's
        // moving flag, so a remote fighter's steps are heard too).
        if (_anim == null || !_anim.IsWalking || BaseNullField.PlayersFrozen)
        {
            _footstepTimer = 0f;
            return;
        }

        _footstepTimer -= Time.deltaTime;
        if (_footstepTimer > 0f) return;
        _footstepTimer = footstepInterval;
        Play(footstepSfx);
    }

    // Hit / death / roll are the three things that interrupt an attack, and each of them runs on every peer,
    // so clearing the pending swing here covers the server, the proxies and offline in one place — no
    // scheduled attack sound survives the attack being cancelled.
    public void PlayHit()  { CancelPendingAttack(); Play(hitSfx, hitVolume); }
    public void PlayRoll() { CancelPendingAttack(); Play(rollSfx); }

    /// <summary>The KO takes the stage: cuts every player's currently-playing sound, drops their scheduled
    /// swings, and suppresses all non-death cues for as long as the death clip runs — so the KO isn't buried
    /// under footsteps, a swing that was already in flight, or the other fighter still moving.</summary>
    public void PlayDeath()
    {
        AudioClip clip = Pick(deathSfx);

        foreach (PlayerAudio audio in All)
        {
            if (audio == null) continue;
            audio.CancelPendingAttack();
            audio._source.Stop();     // also stops sounds started with PlayOneShot
            audio._footstepTimer = 0f;
        }

        if (clip == null) return;
        // The window is the clip's own length rather than a tuned constant, so the priority lasts exactly
        // as long as there is a death sound to protect.
        _deathPriorityUntil = Time.time + clip.length;
        _source.PlayOneShot(clip, deathVolume);
    }

    public void PlayNullField()    => Play(nullFieldSfx);
    public void PlayOverdrive() => Play(overdriveSfx);

    /// <summary>Swing sound for the auto attack. `overdrive` picks the heavy variant. `delay` is the seconds
    /// from the start of the clip to the active window, so the sound lands on the hitbox: 0 when the caller
    /// is already at that point (the attack routine), the full startup when a remote peer schedules it.</summary>
    public void PlayAutoAttack(bool overdrive, float delay = 0f)
        => PlayAttack(overdrive ? Resolve(overdriveAutoAttackSfx, autoAttackSfx) : autoAttackSfx, delay);

    /// <summary>As PlayAutoAttack, timed to the aimable's impact phase (after startup + the leap).</summary>
    public void PlayAimableAttack(bool overdrive, float delay = 0f)
        => PlayAttack(overdrive ? Resolve(overdriveAimableAttackSfx, aimableAttackSfx) : aimableAttackSfx, delay);

    // An unauthored overdrive list falls back to the base cue rather than going silent, mirroring how the
    // overdrive ANIMATION states fall back to the base idle/walk/attack clips — a partial overdrive sound
    // set leaves the heavy attack sounding like the normal one, which is the useful default while authoring.
    private static List<AudioClip> Resolve(List<AudioClip> preferred, List<AudioClip> fallback)
        => HasClip(preferred) ? preferred : fallback;

    private void PlayAttack(List<AudioClip> clips, float delay)
    {
        CancelPendingAttack();
        if (delay <= 0f) { Play(clips); return; }

        AudioClip clip = Pick(clips);
        if (clip == null) return;
        _pendingAttack = StartCoroutine(PlayAfter(clip, delay));
    }

    private IEnumerator PlayAfter(AudioClip clip, float delay)
    {
        yield return new WaitForSeconds(delay);
        _pendingAttack = null;
        // Re-checked on arrival, not just when scheduled: a KO can land during the wind-up.
        if (Time.time < _deathPriorityUntil) yield break;
        _source.PlayOneShot(clip);
    }

    private void CancelPendingAttack()
    {
        if (_pendingAttack == null) return;
        StopCoroutine(_pendingAttack);
        _pendingAttack = null;
    }

    // The single exit point for every cue except death, so the KO's priority window can't be bypassed.
    // Volume is PlayOneShot's per-play scale, so it never touches the AudioSource's own level — there is
    // no global volume to restore afterwards.
    private void Play(List<AudioClip> clips, float volume = 1f)
    {
        if (Time.time < _deathPriorityUntil) return;
        AudioClip clip = Pick(clips);
        if (clip != null) _source.PlayOneShot(clip, volume);
    }

    private static bool HasClip(List<AudioClip> clips)
    {
        if (clips == null) return false;
        foreach (AudioClip clip in clips) if (clip != null) return true;
        return false;
    }

    // Starts at a random entry and takes the first non-null from there, so a list that's empty or still has
    // blank inspector slots simply produces no sound instead of a silent play or a null reference.
    private static AudioClip Pick(List<AudioClip> clips)
    {
        if (clips == null || clips.Count == 0) return null;
        int start = Random.Range(0, clips.Count);
        for (int i = 0; i < clips.Count; i++)
        {
            AudioClip clip = clips[(start + i) % clips.Count];
            if (clip != null) return clip;
        }
        return null;
    }
}
