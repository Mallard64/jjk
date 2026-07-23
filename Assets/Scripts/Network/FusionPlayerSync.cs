using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// Shared Mode identity + HP replication. Owns the NetworkObject's lifecycle hooks and
/// proxy-side HP/death mirroring; pairs with FusionPlayerMovement and FusionPlayerCombat
/// on the same player prefab.
///
/// Also drives the best-of-5 round/match flow. Each peer watches every player it knows about (its own
/// authority player plus opponent proxies) and, when ANY of them dies, runs a local reset — freeze (via
/// the static RoundResetting flag, read by FusionPlayerMovement / FusionPlayerCombat), award the round to
/// the survivor (each winner's authority tallies its own replicated RoundWins), replay the last few
/// seconds, fade to white, and show the ROUND END screen. If neither fighter has reached roundsToWin it
/// respawns and the next round starts; once someone hits roundsToWin it shows VICTORY / DEFEAT and shuts
/// the runner down (both peers disconnect — the arena closes). Both peers observe the same death, so both
/// run the flow together.
/// </summary>
public class FusionPlayerSync : NetworkBehaviour, INetworkAdapter
{
    // Object is null until Fusion's Spawned() runs. Reading HasStateAuthority before that throws,
    // which breaks any caller (e.g. CameraFollow) when MatchManager Instantiates the prefab directly
    // without a NetworkRunner.
    public bool IsLocalPlayer => Object != null && Object.IsValid && HasStateAuthority;
    public bool IsAuthority   => Object != null && Object.IsValid && HasStateAuthority;

    [Networked] public float       NetworkedHp        { get; set; }
    [Networked] public float       NetworkedEnergy    { get; set; }
    [Networked] public NetworkBool NetworkedDead      { get; set; }
    [Networked] public int         NetworkedRoundWins { get; set; }

    [Header("Round / Match flow")]
    [Tooltip("Seconds the death animation is shown before the instant replay.")]
    [SerializeField] private float deathLinger = 1f;
    [Tooltip("Seconds to fade the screen to / from white.")]
    [SerializeField] private float fadeDuration = 0.35f;
    [Tooltip("Seconds the ROUND END screen is held before the next round / result.")]
    [SerializeField] private float roundEndHold = 2f;
    [Tooltip("Seconds the VICTORY / DEFEAT screen is held before disconnecting.")]
    [SerializeField] private float matchEndHold = 3.5f;
    [Tooltip("Round wins needed to take the match (best-of-5 = first to 3).")]
    [SerializeField] private int   roundsToWin = 3;

    private Rigidbody2D     _rb;
    private PlayerHealth    _health;
    private CursedEnergy    _energy;
    private PlayerOverdrive _overdrive;

    // Every live player known to this peer (own authority player + opponent proxies).
    private static readonly HashSet<FusionPlayerSync> All = new HashSet<FusionPlayerSync>();
    // True on a peer while it is mid round-reset; movement/combat freeze the local player off this.
    public static bool RoundResetting { get; private set; }

    // This peer's own authority player. Drives the score HUD without polling.
    private static FusionPlayerSync _local;
    public static bool MatchActive        => _local != null;
    public static int  LocalRoundWins     => _local != null ? _local.NetworkedRoundWins : 0;
    public static int  OpponentRoundWins
    {
        get
        {
            foreach (var p in All) if (p != null && p != _local) return p.NetworkedRoundWins;
            return 0;
        }
    }

    private Vector3 _spawnPosition;
    // Edge-detect so one death triggers exactly one round reset. Re-arms only once every player is
    // alive again — otherwise the winner (who respawns first) would re-fire off the opponent proxy
    // that still reads dead, double-counting the round.
    private bool _roundArmed = true;

    public bool IsDead => _health != null && _health.IsDead;

    // Set in Spawned() on the peer that has state authority. CameraFollow reads this directly
    // instead of polling FindObjectsOfType every frame.
    public static Transform LocalPlayerTransform { get; private set; }

    void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _health    = GetComponent<PlayerHealth>();
        _energy    = GetComponent<CursedEnergy>();
        _overdrive = GetComponent<PlayerOverdrive>();
    }

    public override void Spawned()
    {
        All.Add(this);

        // Remote players: make them kinematic so NetworkTransform drives position, but triggers still work.
        // PlayerMovement/PlayerCombatController auto-bail when they detect this component, no need to disable.
        if (_rb != null && !HasStateAuthority)
        {
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.velocity = Vector2.zero;
        }

        if (HasStateAuthority)
        {
            _local = this;
            LocalPlayerTransform = transform;
            _spawnPosition = transform.position;  // GameLauncher placed us here — respawn returns to it
            if (_health != null) NetworkedHp     = _health.MaxHp;
            if (_energy != null) NetworkedEnergy = _energy.MaxEnergy;
        }

        // Mirror any authority-side HP/death change into [Networked] state so proxies pick it
        // up — covers Heal, Respawn, and any death path that doesn't go through RpcTakeDamage.
        if (_health != null)
        {
            _health.OnHealthChanged += OnLocalHealthChanged;
            _health.OnDeath         += OnLocalDeath;
        }
        if (_energy != null)
        {
            _energy.OnEnergyChanged += OnLocalEnergyChanged;
        }
    }

    public override void Despawned(NetworkRunner runner, bool hasState)
    {
        All.Remove(this);
        RoundResetting = false;  // never strand the peer frozen if a player leaves mid-reset
        if (_local == this) _local = null;

        if (_health != null)
        {
            _health.OnHealthChanged -= OnLocalHealthChanged;
            _health.OnDeath         -= OnLocalDeath;
        }
        if (_energy != null)
        {
            _energy.OnEnergyChanged -= OnLocalEnergyChanged;
        }
        if (LocalPlayerTransform == transform) LocalPlayerTransform = null;
    }

    void Update()
    {
        // The local authority player drives this peer's round reset when anyone dies.
        if (!IsAuthority) return;

        bool anyDead = AnyPlayerDead();
        if (!anyDead) _roundArmed = true;  // everyone alive again → ready to catch the next death

        if (RoundResetting) return;
        if (_roundArmed && anyDead)
        {
            _roundArmed = false;
            StartCoroutine(RoundResetRoutine());
        }
    }

    private static bool AnyPlayerDead()
    {
        foreach (var p in All)
            if (p != null && p.IsDead) return true;
        return false;
    }

    private bool OpponentIsDead()
    {
        foreach (var p in All)
            if (p != null && p != this && p.IsDead) return true;
        return false;
    }

    private IEnumerator RoundResetRoutine()
    {
        RoundResetting = true;
        _overdrive?.SetActive(false);
        ComboCounter.ClearAll();  // don't carry the killing-blow combo into the replay / next round

        // Award the round to the survivor: each winner's authority tallies its own replicated score.
        // The loser's routine (its own player is dead) skips this, so exactly one side scores per round.
        if (IsAuthority && !IsDead && OpponentIsDead())
            NetworkedRoundWins += 1;

        // Hold on the death pose before fading out.
        yield return new WaitForSeconds(deathLinger);

        // Fade to white and show the ROUND END screen on both peers.
        yield return ScreenBlackout.Instance.FadeTo(1f, fadeDuration, Color.white);
        NetworkMatchUI.Instance?.ShowRoundEnd();
        yield return new WaitForSeconds(roundEndHold);

        // Match point? First to roundsToWin ends it (best-of-5 = first to 3).
        if (NetworkedRoundWins >= roundsToWin || OpponentRoundWins >= roundsToWin)
        {
            if (NetworkedRoundWins >= roundsToWin) NetworkMatchUI.Instance?.ShowVictory();
            else                                   NetworkMatchUI.Instance?.ShowDefeat();
            yield return new WaitForSeconds(matchEndHold);

            // Arena closes: this peer disconnects (both peers reach here independently). Stay frozen on
            // the result screen — Despawned clears RoundResetting once the runner tears us down.
            Runner?.Shutdown();
            yield break;
        }

        // Next round: respawn this peer's player on its own spawn at full HP/CE, clear the screen.
        transform.position = _spawnPosition;
        if (_rb != null) _rb.position = _spawnPosition;
        _health?.Respawn();
        NetworkMatchUI.Instance?.HideRoundEnd();
        yield return ScreenBlackout.Instance.FadeTo(0f, fadeDuration, Color.white);
        RoundResetting = false;
    }

    private void OnLocalHealthChanged(float current, float max)
    {
        if (!HasStateAuthority) return;
        NetworkedHp = current;
        if (current > 0f) NetworkedDead = false;  // clears on respawn / heal-back
    }

    private void OnLocalDeath()
    {
        if (HasStateAuthority) NetworkedDead = true;
    }

    private void OnLocalEnergyChanged(float current, float max)
    {
        if (HasStateAuthority) NetworkedEnergy = current;
    }

    public override void Render()
    {
        // Proxies: mirror authoritative HP/energy/death state so HUDs and animation react locally.
        if (HasStateAuthority) return;

        if (_health != null)
        {
            if (Mathf.Abs(_health.CurrentHp - NetworkedHp) > 0.01f)
                _health.ForceSetHp(NetworkedHp);

            if (NetworkedDead && !_health.IsDead)
            {
                _health.Die();  // → death animation via this proxy's PlayerCombatController.OnDeath
            }
            else if (!NetworkedDead && _health.IsDead)
            {
                // Authority respawned: clear the proxy's death state + animation. Re-assert kinematic
                // afterwards — Respawn() unfreezes the rigidbody, which a proxy must never be.
                _health.Respawn();
                if (_rb != null)
                {
                    _rb.bodyType = RigidbodyType2D.Kinematic;
                    _rb.velocity = Vector2.zero;
                }
            }
        }
        if (_energy != null && Mathf.Abs(_energy.CurrentEnergy - NetworkedEnergy) > 0.01f)
            _energy.SetEnergy(NetworkedEnergy);
    }

    public void SendInput(PlayerInputData input) { /* unused in shared mode */ }

    // Shared aim helper — both movement (for NetworkedAimDir) and combat (for technique fire dir)
    // resolve aim the same way. Static so callers don't need a reference to the Sync instance.
    public static Vector2 GetMouseAim(Transform from)
    {
        if (Camera.main == null) return Vector2.right;
        Vector3 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        mouse.z = 0f;
        Vector2 dir = (Vector2)(mouse - from.position);
        return dir.sqrMagnitude > 0.01f ? dir.normalized : Vector2.right;
    }

    // World-space cursor point (throwable attacks need the actual point, not a normalized heading).
    public static Vector2 GetMouseWorldPoint(Transform fallback)
    {
        if (Camera.main == null) return fallback.position;
        Vector3 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        mouse.z = 0f;
        return mouse;
    }
}
