using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Null Field as a strategic resource-warfare system, not a sure-hit super. The base handles
/// everything mechanical — activation (40 RES + 0.5s startup that freezes both fighters while the owner
/// plays the activation animation), RES drain (8/s solo, 20/s clash; the owner's regen is frozen too so
/// the drain is net and RES cleanly determines duration), clash detection, collapse, burnout, the
/// cross-player effects (free overdrive for the owner; opponent loses overdrive + RES regen), and
/// spawning + flickering the player's own NULL FIELD prefab (a customizable environment that replaces the
/// arena). It deals no damage.
///
/// Subclasses stay thin: the character bonus (MoveSpeedMultiplier / OnFieldStarted-Ended) + VFX/SFX.
/// The null field environment + activation VFX are per-player prefab fields, so each character customizes
/// their own. Cross-player / global state is resolved through a static registry of all live null fields on
/// this peer; online the opponent's instance is a proxy whose active + startup flags are mirrored.
/// </summary>
public abstract class BaseNullField : MonoBehaviour
{
    [Header("Null field — resource warfare")]
    [Tooltip("RES spent immediately on activation. Also the minimum RES required to open.")]
    [SerializeField] protected float activationCost = 40f;
    [Tooltip("Seconds the null field takes to form (both fighters frozen, owner plays the activation anim) before it's active.")]
    [SerializeField] protected float startupTime = 0.5f;
    [Tooltip("RES drained per second while this is the only null field up.")]
    [SerializeField] protected float drainSolo = 8f;
    [Tooltip("RES drained per second while both players hold a null field (clash).")]
    [SerializeField] protected float drainClash = 20f;
    [Tooltip("Seconds of burnout after a null field collapses: can't open a null field, can't overdrive, no RES regen.")]
    [SerializeField] protected float burnoutDuration = 2f;
    [Tooltip("RES at/above which the null field is stable (no collapse flicker). Below it the flicker ramps up to RES 0.")]
    [SerializeField] protected float flickerStartCE = 40f;

    [Header("Player-spawned null field (customize per character)")]
    [Tooltip("The null field environment prefab the player spawns on activation — covers the arena and is destroyed on collapse. Each client spawns its own.")]
    [SerializeField] private GameObject fieldPrefab;
    [Tooltip("One-shot activation burst spawned on activation (each client spawns its own); give it TimedSelfDestruct to clean up.")]
    [SerializeField] private GameObject activationVfxPrefab;
    [Tooltip("World position the activation burst spawns at (the null field sits at world origin, so this is usually near it — NOT the player's position).")]
    [SerializeField] private Vector3 activationVfxPosition = new Vector3(-2f, -40f, 0f);

    [Header("Fade-in")]
    [Tooltip("Seconds the null field environment fades in (alpha 0→1) when it opens — match the activation VFX duration.")]
    [SerializeField] private float fieldFadeInDuration = 1f;

    [Header("Collapse flicker (of the null field prefab)")]
    [SerializeField] private float slowFlickerInterval = 1.2f;   // at low intensity
    [SerializeField] private float fastFlickerInterval = 0.07f;  // at full intensity (RES near 0)
    [SerializeField] private float flickerFlashDuration = 0.05f; // how long each flicker hides the null field

    private static readonly List<BaseNullField> All = new List<BaseNullField>();

    public bool IsActive       { get; private set; }
    public bool IsStartingUp   { get; private set; }
    public bool IsInBurnout    => _burnoutTimer > 0f;
    public bool IsClashing     => CountActive() >= 2;

    /// <summary>Bonus channel read by PlayerMovement / FusionPlayerMovement. 1 = no bonus.</summary>
    public float MoveSpeedMultiplier { get; protected set; } = 1f;

    /// <summary>0 while stable, ramping to 1 as RES falls to 0 — drives the collapse flicker.</summary>
    public float CollapseIntensity
        => (IsActive && Energy != null && flickerStartCE > 0f)
            ? Mathf.Clamp01((flickerStartCE - Energy.CurrentEnergy) / flickerStartCE)
            : 0f;

    protected Resonance Energy;
    protected PlayerHealth Health;
    private PlayerOverdrive _overdrive;
    private PlayerAnimationController _anim;
    private FusionPlayerSync _sync;
    private PlayerAudio _audio;
    private float _burnoutTimer;

    private GameObject _fieldInstance;
    private float      _flickerTimer;
    private float      _flashTimer;
    private float      _startupTimer;

    // Fade-in state: alpha ramps 0→1 over fieldFadeInDuration; _fadeTimer < 0 means "not fading".
    private float _fadeTimer = -1f;
    private readonly List<SpriteRenderer> _fadeSprites      = new List<SpriteRenderer>();
    private readonly List<Color>          _fadeSpriteColors = new List<Color>();
    private readonly List<Tilemap>        _fadeTilemaps     = new List<Tilemap>();
    private readonly List<Color>          _fadeTilemapColors = new List<Color>();

    protected virtual void Awake()
    {
        Energy     = GetComponent<Resonance>();
        Health     = GetComponent<PlayerHealth>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _anim      = GetComponent<PlayerAnimationController>();
        _sync      = GetComponent<FusionPlayerSync>();
        _audio     = GetComponent<PlayerAudio>();
    }

    void OnEnable() { if (!All.Contains(this)) All.Add(this); }
    void OnDisable() { All.Remove(this); if (IsActive) SetActiveState(false); IsStartingUp = false; }

    private bool Simulates
    {
        get
        {
            bool online = _sync != null && _sync.Object != null && _sync.Object.IsValid;
            return !online || _sync.IsAuthority;
        }
    }

    public bool CanActivate()
        => Simulates && !IsActive && !IsStartingUp && !IsInBurnout
           && (Energy == null || Energy.CurrentEnergy >= activationCost);

    /// <summary>Open the null field: pay 40 RES now, freeze + play the activation anim through startup, then open.</summary>
    public void Activate()
    {
        if (!CanActivate()) return;
        Energy?.Drain(activationCost);
        IsStartingUp = true;
        _startupTimer = startupTime;
        _anim?.PlayNullField();
        // Startup is ticked in Update() (Unity loop), NOT a coroutine started here — Activate() runs
        // inside FixedUpdateNetwork online, and coroutines launched from there are unreliable, same as
        // why the spawn itself must not happen in FixedUpdateNetwork.
    }

    /// <summary>Manual cancel — collapses the null field and puts the owner into burnout.</summary>
    public void Deactivate()
    {
        if (!IsActive) return;
        Collapse();
    }

    private void Collapse()
    {
        SetActiveState(false);
        _burnoutTimer = burnoutDuration;
    }

    private void SetActiveState(bool value)
    {
        if (IsActive == value) return;
        IsActive = value;
        if (value)
        {
            // Spawn at world origin (the arena centre), NOT in Activate/FixedUpdateNetwork where a plain
            // Instantiate doesn't take — this runs from the Update startup tick / proxy mirror (Unity loop).
            if (fieldPrefab != null) _fieldInstance = Instantiate(fieldPrefab, Vector3.zero, Quaternion.identity);
            SpawnActivationVfx();
            SetOriginalArenaActive(false);   // hide the normal tilemap while the null field is up
            BeginFieldFade();               // ramp the environment in over fieldFadeInDuration
            _flickerTimer = 0f;
            _flashTimer   = 0f;
            // Runs on the owner and on every proxy (SetActiveState is the mirrored path), so the null field is
            // heard on both peers for the same reason the environment appears on both.
            _audio?.PlayNullField();
            OnFieldStarted();
        }
        else
        {
            if (_fieldInstance != null) { Destroy(_fieldInstance); _fieldInstance = null; }
            EndFieldFade();
            SetOriginalArenaActive(true);    // restore the normal tilemap
            MoveSpeedMultiplier = 1f;
            OnFieldEnded();
        }
    }

    /// <summary>Mirror the owner's active state on a proxy (registry / spawn the null field prefab / effects).</summary>
    public void SetNetworkActive(bool active)
    {
        if (Simulates) return;
        SetActiveState(active);
    }

    /// <summary>Mirror the owner's startup state on a proxy (so the freeze fires on this peer too).</summary>
    public void SetNetworkStartup(bool startingUp)
    {
        if (Simulates) return;
        IsStartingUp = startingUp;
    }

    private void SpawnActivationVfx()
    {
        // Spawn at a fixed world position (near the origin-spawned null field), not the player's position.
        if (activationVfxPrefab != null)
            Instantiate(activationVfxPrefab, activationVfxPosition, Quaternion.identity);
    }

    private static void SetOriginalArenaActive(bool active)
    {
        if (OriginalArena.Instance != null && OriginalArena.Instance.activeSelf != active)
            OriginalArena.Instance.SetActive(active);
    }

    void Update()
    {
        if (_burnoutTimer > 0f) _burnoutTimer = Mathf.Max(0f, _burnoutTimer - Time.deltaTime);
        UpdateFieldFade();       // runs on every client showing the null field (owner + proxy)
        UpdateFieldFlicker();

        if (!Simulates) return;   // proxies: state mirrored via SetNetwork*, no drain/effects here

        if (IsStartingUp)
        {
            _startupTimer -= Time.deltaTime;
            if (_startupTimer <= 0f)
            {
                IsStartingUp = false;
                if (!(Health != null && Health.IsDead))
                {
                    _anim?.RefreshMovementState();
                    SetActiveState(true);
                }
            }
        }

        if (Health != null && Health.IsDead)
        {
            if (IsActive) SetActiveState(false);
            IsStartingUp = false;
            ApplyCrossPlayerEffects();
            return;
        }

        if (IsActive && Energy != null)
        {
            Energy.Drain((IsClashing ? drainClash : drainSolo) * Time.deltaTime);
            if (Energy.CurrentEnergy <= 0f) Collapse();
        }

        ApplyCrossPlayerEffects();
    }

    // Flicker the spawned null field prefab off briefly (revealing the normal arena behind it) more and more
    // often as the owner's RES falls, to telegraph collapse. Driven off CollapseIntensity, which works on
    // proxies too (their RES is the mirrored owner RES).
    private void UpdateFieldFlicker()
    {
        if (_fieldInstance == null) return;

        if (_flashTimer > 0f)
        {
            _flashTimer -= Time.deltaTime;
            if (_flashTimer <= 0f) ShowFieldVisual(true);   // flash over → null field back, normal hidden
            return;
        }

        float intensity = CollapseIntensity;
        if (intensity <= 0f)
        {
            ShowFieldVisual(true);
            return;
        }

        _flickerTimer -= Time.deltaTime;
        if (_flickerTimer <= 0f)
        {
            ShowFieldVisual(false);                          // flicker → hide null field, reveal normal tilemap
            _flashTimer   = flickerFlashDuration;
            _flickerTimer = Mathf.Lerp(slowFlickerInterval, fastFlickerInterval, intensity) * Random.Range(0.5f, 1.3f);
        }
    }

    // Null field visible ⇒ normal tilemap hidden, and vice versa.
    private void ShowFieldVisual(bool fieldVisible)
    {
        if (_fieldInstance != null && _fieldInstance.activeSelf != fieldVisible)
            _fieldInstance.SetActive(fieldVisible);
        SetOriginalArenaActive(!fieldVisible);
    }

    // Capture the spawned environment's renderers and fade their alpha up from 0.
    private void BeginFieldFade()
    {
        _fadeSprites.Clear(); _fadeSpriteColors.Clear();
        _fadeTilemaps.Clear(); _fadeTilemapColors.Clear();
        if (_fieldInstance == null || fieldFadeInDuration <= 0f) { _fadeTimer = -1f; return; }

        foreach (var sr in _fieldInstance.GetComponentsInChildren<SpriteRenderer>(true))
        { _fadeSprites.Add(sr); _fadeSpriteColors.Add(sr.color); }
        foreach (var tm in _fieldInstance.GetComponentsInChildren<Tilemap>(true))
        { _fadeTilemaps.Add(tm); _fadeTilemapColors.Add(tm.color); }

        _fadeTimer = 0f;
        ApplyFieldFade(0f);
    }

    private void EndFieldFade()
    {
        _fadeTimer = -1f;
        _fadeSprites.Clear(); _fadeSpriteColors.Clear();
        _fadeTilemaps.Clear(); _fadeTilemapColors.Clear();
    }

    private void UpdateFieldFade()
    {
        if (_fadeTimer < 0f) return;
        _fadeTimer += Time.deltaTime;
        float t = Mathf.Clamp01(_fadeTimer / fieldFadeInDuration);
        ApplyFieldFade(t);
        if (t >= 1f) _fadeTimer = -1f;   // done — colours left at full alpha
    }

    // Scale each captured renderer's alpha by t (multiplies the authored alpha, so it ends restored).
    private void ApplyFieldFade(float t)
    {
        for (int i = 0; i < _fadeSprites.Count; i++)
        {
            if (_fadeSprites[i] == null) continue;
            Color c = _fadeSpriteColors[i]; c.a = _fadeSpriteColors[i].a * t;
            _fadeSprites[i].color = c;
        }
        for (int i = 0; i < _fadeTilemaps.Count; i++)
        {
            if (_fadeTilemaps[i] == null) continue;
            Color c = _fadeTilemapColors[i]; c.a = _fadeTilemapColors[i].a * t;
            _fadeTilemaps[i].color = c;
        }
    }

    private void ApplyCrossPlayerEffects()
    {
        bool enemyField = AnyActiveExcept(this);
        // ANY null field (mine included) freezes my RES regen, so the drain is net — RES cleanly determines
        // duration and the null field collapses right when the bar empties. Burnout freezes it too.
        if (Energy != null) Energy.RegenBlocked = AnyActive() || IsInBurnout;
        if (_overdrive != null)
        {
            _overdrive.SetFieldLocked(!IsActive && (enemyField || IsInBurnout));
            _overdrive.SetFieldFree(IsActive);
        }
    }

    protected virtual void OnFieldStarted() { }
    protected virtual void OnFieldEnded() { }

    // ── Global state read by movement/combat (freeze) ─────────────────────────────────────────────
    /// <summary>True while any null field is in its activation startup — both fighters freeze.</summary>
    public static bool PlayersFrozen
    {
        get { foreach (var d in All) if (d != null && d.IsStartingUp) return true; return false; }
    }
    public static bool AnyActive()
    {
        foreach (var d in All) if (d != null && d.IsActive) return true;
        return false;
    }

    private static bool AnyActiveExcept(BaseNullField self)
    {
        foreach (var d in All) if (d != null && d != self && d.IsActive) return true;
        return false;
    }
    private static int CountActive()
    {
        int n = 0;
        foreach (var d in All) if (d != null && d.IsActive) n++;
        return n;
    }
}
