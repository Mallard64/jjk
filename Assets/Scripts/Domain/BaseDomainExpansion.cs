using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Domain Expansion as a strategic resource-warfare system, not a sure-hit super. The base handles
/// everything mechanical — activation (40 CE + 0.5s startup that freezes both fighters while the owner
/// plays the activation animation), CE drain (8/s solo, 20/s clash; the owner's regen is frozen too so
/// the drain is net and CE cleanly determines duration), clash detection, collapse, burnout, the
/// cross-player effects (free overdrive for the owner; opponent loses overdrive + CE regen), and
/// spawning + flickering the player's own DOMAIN prefab (a customizable environment that replaces the
/// arena). It deals no damage.
///
/// Subclasses stay thin: the character bonus (MoveSpeedMultiplier / OnDomainStarted-Ended) + VFX/SFX.
/// The domain environment + activation VFX are per-player prefab fields, so each character customizes
/// their own. Cross-player / global state is resolved through a static registry of all live domains on
/// this peer; online the opponent's instance is a proxy whose active + startup flags are mirrored.
/// </summary>
public abstract class BaseDomainExpansion : MonoBehaviour
{
    [Header("Domain — resource warfare")]
    [Tooltip("CE spent immediately on activation. Also the minimum CE required to open.")]
    [SerializeField] protected float activationCost = 40f;
    [Tooltip("Seconds the domain takes to form (both fighters frozen, owner plays the activation anim) before it's active.")]
    [SerializeField] protected float startupTime = 0.5f;
    [Tooltip("CE drained per second while this is the only domain up.")]
    [SerializeField] protected float drainSolo = 8f;
    [Tooltip("CE drained per second while both players hold a domain (clash).")]
    [SerializeField] protected float drainClash = 20f;
    [Tooltip("Seconds of burnout after a domain collapses: can't domain, can't overdrive, no CE regen.")]
    [SerializeField] protected float burnoutDuration = 2f;
    [Tooltip("CE at/above which the domain is stable (no collapse flicker). Below it the flicker ramps up to CE 0.")]
    [SerializeField] protected float flickerStartCE = 40f;

    [Header("Player-spawned domain (customize per character)")]
    [Tooltip("The domain environment prefab the player spawns on activation — covers the arena and is destroyed on collapse. Each client spawns its own.")]
    [SerializeField] private GameObject domainPrefab;
    [Tooltip("One-shot activation burst spawned on activation (each client spawns its own); give it TimedSelfDestruct to clean up.")]
    [SerializeField] private GameObject activationVfxPrefab;
    [Tooltip("World position the activation burst spawns at (the domain sits at world origin, so this is usually near it — NOT the player's position).")]
    [SerializeField] private Vector3 activationVfxPosition = new Vector3(-2f, -40f, 0f);

    [Header("Fade-in")]
    [Tooltip("Seconds the domain environment fades in (alpha 0→1) when it opens — match the activation VFX duration.")]
    [SerializeField] private float domainFadeInDuration = 1f;

    [Header("Collapse flicker (of the domain prefab)")]
    [SerializeField] private float slowFlickerInterval = 1.2f;   // at low intensity
    [SerializeField] private float fastFlickerInterval = 0.07f;  // at full intensity (CE near 0)
    [SerializeField] private float flickerFlashDuration = 0.05f; // how long each flicker hides the domain

    [Header("Feedback (optional)")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip activateSfx;

    private static readonly List<BaseDomainExpansion> All = new List<BaseDomainExpansion>();

    public bool IsActive       { get; private set; }
    public bool IsStartingUp   { get; private set; }
    public bool IsInBurnout    => _burnoutTimer > 0f;
    public bool IsClashing     => CountActive() >= 2;

    /// <summary>Bonus channel read by PlayerMovement / FusionPlayerMovement. 1 = no bonus.</summary>
    public float MoveSpeedMultiplier { get; protected set; } = 1f;

    /// <summary>0 while stable, ramping to 1 as CE falls to 0 — drives the collapse flicker.</summary>
    public float CollapseIntensity
        => (IsActive && Energy != null && flickerStartCE > 0f)
            ? Mathf.Clamp01((flickerStartCE - Energy.CurrentEnergy) / flickerStartCE)
            : 0f;

    protected CursedEnergy Energy;
    protected PlayerHealth Health;
    private PlayerOverdrive _overdrive;
    private PlayerAnimationController _anim;
    private FusionPlayerSync _sync;
    private float _burnoutTimer;

    private GameObject _domainInstance;
    private float _flickerTimer;
    private float _flashTimer;
    private float _startupTimer;

    // Fade-in state: alpha ramps 0→1 over domainFadeInDuration; _fadeTimer < 0 means "not fading".
    private float _fadeTimer = -1f;
    private readonly List<SpriteRenderer> _fadeSprites      = new List<SpriteRenderer>();
    private readonly List<Color>          _fadeSpriteColors = new List<Color>();
    private readonly List<Tilemap>        _fadeTilemaps     = new List<Tilemap>();
    private readonly List<Color>          _fadeTilemapColors = new List<Color>();

    protected virtual void Awake()
    {
        Energy     = GetComponent<CursedEnergy>();
        Health     = GetComponent<PlayerHealth>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _anim      = GetComponent<PlayerAnimationController>();
        _sync      = GetComponent<FusionPlayerSync>();
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

    /// <summary>Open the domain: pay 40 CE now, freeze + play the activation anim through startup, then open.</summary>
    public void Activate()
    {
        if (!CanActivate()) return;
        Energy?.Drain(activationCost);
        IsStartingUp = true;
        _startupTimer = startupTime;
        _anim?.PlayDomain();
        // Startup is ticked in Update() (Unity loop), NOT a coroutine started here — Activate() runs
        // inside FixedUpdateNetwork online, and coroutines launched from there are unreliable, same as
        // why the spawn itself must not happen in FixedUpdateNetwork.
    }

    /// <summary>Manual cancel — collapses the domain and puts the owner into burnout.</summary>
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
            if (domainPrefab != null) _domainInstance = Instantiate(domainPrefab, Vector3.zero, Quaternion.identity);
            SpawnActivationVfx();
            SetOriginalArenaActive(false);   // hide the normal tilemap while the domain is up
            BeginDomainFade();               // ramp the environment in over domainFadeInDuration
            _flickerTimer = 0f;
            _flashTimer = 0f;
            if (activateSfx != null && audioSource != null) audioSource.PlayOneShot(activateSfx);
            OnDomainStarted();
        }
        else
        {
            if (_domainInstance != null) { Destroy(_domainInstance); _domainInstance = null; }
            EndDomainFade();
            SetOriginalArenaActive(true);    // restore the normal tilemap
            MoveSpeedMultiplier = 1f;
            OnDomainEnded();
        }
    }

    /// <summary>Mirror the owner's active state on a proxy (registry / spawn the domain prefab / effects).</summary>
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
        // Spawn at a fixed world position (near the origin-spawned domain), not the player's position.
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
        UpdateDomainFade();       // runs on every client showing the domain (owner + proxy)
        UpdateDomainFlicker();

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

    // Flicker the spawned domain prefab off briefly (revealing the normal arena behind it) more and more
    // often as the owner's CE falls, to telegraph collapse. Driven off CollapseIntensity, which works on
    // proxies too (their CE is the mirrored owner CE).
    private void UpdateDomainFlicker()
    {
        if (_domainInstance == null) return;

        if (_flashTimer > 0f)
        {
            _flashTimer -= Time.deltaTime;
            if (_flashTimer <= 0f) ShowDomainVisual(true);   // flash over → domain back, normal hidden
            return;
        }

        float intensity = CollapseIntensity;
        if (intensity <= 0f)
        {
            ShowDomainVisual(true);
            return;
        }

        _flickerTimer -= Time.deltaTime;
        if (_flickerTimer <= 0f)
        {
            ShowDomainVisual(false);                          // flicker → hide domain, reveal normal tilemap
            _flashTimer = flickerFlashDuration;
            _flickerTimer = Mathf.Lerp(slowFlickerInterval, fastFlickerInterval, intensity) * Random.Range(0.5f, 1.3f);
        }
    }

    // Domain visible ⇒ normal tilemap hidden, and vice versa.
    private void ShowDomainVisual(bool domainVisible)
    {
        if (_domainInstance != null && _domainInstance.activeSelf != domainVisible)
            _domainInstance.SetActive(domainVisible);
        SetOriginalArenaActive(!domainVisible);
    }

    // Capture the spawned environment's renderers and fade their alpha up from 0.
    private void BeginDomainFade()
    {
        _fadeSprites.Clear(); _fadeSpriteColors.Clear();
        _fadeTilemaps.Clear(); _fadeTilemapColors.Clear();
        if (_domainInstance == null || domainFadeInDuration <= 0f) { _fadeTimer = -1f; return; }

        foreach (var sr in _domainInstance.GetComponentsInChildren<SpriteRenderer>(true))
        { _fadeSprites.Add(sr); _fadeSpriteColors.Add(sr.color); }
        foreach (var tm in _domainInstance.GetComponentsInChildren<Tilemap>(true))
        { _fadeTilemaps.Add(tm); _fadeTilemapColors.Add(tm.color); }

        _fadeTimer = 0f;
        ApplyDomainFade(0f);
    }

    private void EndDomainFade()
    {
        _fadeTimer = -1f;
        _fadeSprites.Clear(); _fadeSpriteColors.Clear();
        _fadeTilemaps.Clear(); _fadeTilemapColors.Clear();
    }

    private void UpdateDomainFade()
    {
        if (_fadeTimer < 0f) return;
        _fadeTimer += Time.deltaTime;
        float t = Mathf.Clamp01(_fadeTimer / domainFadeInDuration);
        ApplyDomainFade(t);
        if (t >= 1f) _fadeTimer = -1f;   // done — colours left at full alpha
    }

    // Scale each captured renderer's alpha by t (multiplies the authored alpha, so it ends restored).
    private void ApplyDomainFade(float t)
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
        bool enemyDomain = AnyActiveExcept(this);
        // ANY domain (mine included) freezes my CE regen, so the drain is net — CE cleanly determines
        // duration and the domain collapses right when the bar empties. Burnout freezes it too.
        if (Energy != null) Energy.RegenBlocked = AnyActive() || IsInBurnout;
        if (_overdrive != null)
        {
            _overdrive.SetDomainLocked(!IsActive && (enemyDomain || IsInBurnout));
            _overdrive.SetDomainFree(IsActive);
        }
    }

    protected virtual void OnDomainStarted() { }
    protected virtual void OnDomainEnded() { }

    // ── Global state read by movement/combat (freeze) ─────────────────────────────────────────────
    /// <summary>True while any domain is in its activation startup — both fighters freeze.</summary>
    public static bool PlayersFrozen
    {
        get { foreach (var d in All) if (d != null && d.IsStartingUp) return true; return false; }
    }
    public static bool AnyActive()
    {
        foreach (var d in All) if (d != null && d.IsActive) return true;
        return false;
    }

    private static bool AnyActiveExcept(BaseDomainExpansion self)
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
