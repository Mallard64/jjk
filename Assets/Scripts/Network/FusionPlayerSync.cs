using System.Collections;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

/// <summary>
/// Host mode identity + HP/CE replication. Owns the NetworkObject's lifecycle hooks and the remote-side
/// HP/death mirroring; pairs with FusionPlayerMovement and FusionPlayerCombat on the same player prefab.
///
/// Two different questions, two different flags:
///   IsAuthority   — "do I simulate this player?" → state authority, i.e. the server, for BOTH fighters.
///   IsLocalPlayer — "is this the fighter I control?" → input authority, exactly one per peer.
/// On a client neither fighter is simulated locally: it sends input and renders what the server replicates.
///
/// Also drives the best-of-5 round/match flow, split by role: the SERVER runs the authoritative half
/// (scoring, respawns, the replicated NetworkedRoundResetting freeze), and EVERY peer runs the same
/// presentation timeline off that flag — death linger, fade to white, ROUND END, then VICTORY / DEFEAT
/// and a runner shutdown once someone reaches roundsToWin.
/// </summary>
public class FusionPlayerSync : NetworkBehaviour, INetworkAdapter
{
    // Object is null until Fusion's Spawned() runs. Reading the authority flags before that throws,
    // which breaks any caller (e.g. CameraFollow) when MatchManager Instantiates the prefab directly
    // without a NetworkRunner.
    private bool IsNetworked => Object != null && Object.IsValid;

    public bool IsLocalPlayer => IsNetworked && HasInputAuthority;
    public bool IsAuthority   => IsNetworked && HasStateAuthority;

    [Networked] public float       NetworkedHp             { get; set; }
    [Networked] public float       NetworkedEnergy         { get; set; }
    [Networked] public NetworkBool NetworkedDead           { get; set; }
    [Networked] public int         NetworkedRoundWins      { get; set; }
    // Server-written, read by every peer: freezes input handling and starts the round-end presentation.
    [Networked] public NetworkBool NetworkedRoundResetting { get; set; }

    [Header("Round / Match flow")]
    [Tooltip("Seconds the death animation is shown before the screen fades.")]
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

    // Every live player known to this peer — both fighters on every peer; only the authority flags differ.
    private static readonly HashSet<FusionPlayerSync> All = new HashSet<FusionPlayerSync>();

    /// <summary>True on this peer while a round reset is in flight. Replicated by the server, so the
    /// freeze lands on client and host together. Movement/combat read it to drop input.</summary>
    public static bool RoundResetting
    {
        get
        {
            foreach (var p in All) if (p != null && p.IsNetworked && p.NetworkedRoundResetting) return true;
            return false;
        }
    }

    // This peer's own fighter (input authority). Drives the camera and the score HUD without polling.
    private static FusionPlayerSync _local;
    public static bool MatchActive    => _local != null;
    public static int  LocalRoundWins => _local != null ? _local.NetworkedRoundWins : 0;
    public static int  OpponentRoundWins
    {
        get
        {
            foreach (var p in All) if (p != null && p != _local) return p.NetworkedRoundWins;
            return 0;
        }
    }

    // Match-level, not per-object: whichever server-side instance sees the death first claims the flow.
    // Re-arms only once every fighter is alive again, so one death can't score twice.
    private static bool _roundArmed = true;
    private static bool _presenting;

    private Vector3 _spawnPosition;

    public bool IsDead => _health != null && _health.IsDead;

    // Set in Spawned() on the peer that controls this fighter. CameraFollow reads it directly
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

        // Nothing here touches the rigidbody. NetworkRigidbody2D forces PROXIES kinematic in its own
        // Spawned(); a client's own fighter must stay dynamic so it can predict its movement locally.
        if (HasInputAuthority)
        {
            _local = this;
            LocalPlayerTransform = transform;
        }

        if (HasStateAuthority)
        {
            _spawnPosition = transform.position;  // GameLauncher placed us here — respawn returns to it
            if (_health != null) NetworkedHp     = _health.MaxHp;
            if (_energy != null) NetworkedEnergy = _energy.MaxEnergy;
        }

        // Mirror any server-side HP/death change into [Networked] state so remotes pick it up —
        // covers Heal, Respawn, and any death path that doesn't go through the damage hit.
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
        if (_local == this) _local = null;
        // Last fighter gone: the match is over, so never strand the next one frozen or mid-presentation.
        if (All.Count == 0) { _roundArmed = true; _presenting = false; }

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
        // Server half: detect the death, score it, respawn. Claimed once per round via the static flag.
        if (Runner != null && Runner.IsServer && HasStateAuthority)
        {
            bool anyDead = AnyPlayerDead();
            if (!anyDead && !RoundResetting) _roundArmed = true;
            if (_roundArmed && anyDead && !RoundResetting)
            {
                _roundArmed = false;
                StartCoroutine(ServerRoundRoutine());
            }
        }

        // Presentation half: every peer plays it once, off its own fighter, driven by the replicated flag.
        if (!_presenting && RoundResetting && IsLocalPlayer)
            StartCoroutine(PresentRoundEnd());
    }

    private static bool AnyPlayerDead()
    {
        foreach (var p in All)
            if (p != null && p.IsDead) return true;
        return false;
    }

    /// <summary>Server: freeze both peers, award the round, then respawn — or leave the flag up on match
    /// point and let each peer's presentation close the match out.</summary>
    private IEnumerator ServerRoundRoutine()
    {
        SetRoundResettingOnAll(true);
        AwardRoundToSurvivor();

        // Match the presentation timeline, so the respawn happens under the white screen.
        yield return new WaitForSeconds(deathLinger + fadeDuration + roundEndHold);

        if (MatchOver()) yield break;  // PresentRoundEnd shows VICTORY / DEFEAT and shuts each peer down

        foreach (var p in All)
        {
            if (p == null || !p.HasStateAuthority) continue;
            p.transform.position = p._spawnPosition;
            if (p._rb != null) p._rb.position = p._spawnPosition;
            p._health?.Respawn();
        }
        SetRoundResettingOnAll(false);
    }

    private static void SetRoundResettingOnAll(bool value)
    {
        foreach (var p in All)
            if (p != null && p.HasStateAuthority) p.NetworkedRoundResetting = value;
    }

    // Exactly one survivor scores. A double KO awards nothing.
    private static void AwardRoundToSurvivor()
    {
        FusionPlayerSync survivor = null;
        int alive = 0;
        foreach (var p in All)
        {
            if (p == null || p.IsDead) continue;
            survivor = p;
            alive++;
        }
        if (alive == 1 && survivor != null && survivor.HasStateAuthority) survivor.NetworkedRoundWins += 1;
    }

    private bool MatchOver()
    {
        foreach (var p in All) if (p != null && p.NetworkedRoundWins >= roundsToWin) return true;
        return false;
    }

    /// <summary>Every peer, once per round: the shared visual timeline. Reads only replicated state, so
    /// host and client show the same thing (a client starts ~one RTT later and re-syncs at the wait).</summary>
    private IEnumerator PresentRoundEnd()
    {
        _presenting = true;
        _overdrive?.SetActive(false);
        ComboCounter.ClearAll();  // don't carry the killing-blow combo into the next round

        yield return new WaitForSeconds(deathLinger);
        yield return ScreenBlackout.Instance.FadeTo(1f, fadeDuration, Color.white);
        NetworkMatchUI.Instance?.ShowRoundEnd();
        yield return new WaitForSeconds(roundEndHold);

        if (MatchOver())
        {
            if (LocalRoundWins >= roundsToWin) NetworkMatchUI.Instance?.ShowVictory();
            else                               NetworkMatchUI.Instance?.ShowDefeat();
            yield return new WaitForSeconds(matchEndHold);

            // Arena closes on every peer. Stay frozen on the result screen — Despawned clears the statics.
            Runner?.Shutdown();
            yield break;
        }

        // Wait out the server's respawn, then clear the screen for the next round.
        while (RoundResetting) yield return null;
        NetworkMatchUI.Instance?.HideRoundEnd();
        yield return ScreenBlackout.Instance.FadeTo(0f, fadeDuration, Color.white);
        _presenting = false;
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
        // Every fighter the server doesn't simulate (both of them, on a client) mirrors authoritative
        // HP/energy/death state, so HUDs and animation react locally without simulating anything.
        if (HasStateAuthority) return;

        if (_health != null)
        {
            if (Mathf.Abs(_health.CurrentHp - NetworkedHp) > 0.01f)
                _health.ForceSetHp(NetworkedHp);

            if (NetworkedDead && !_health.IsDead)
            {
                _health.Die();  // → death animation via this peer's PlayerCombatController.OnDeath
            }
            else if (!NetworkedDead && _health.IsDead)
            {
                // Server respawned: clear the local death state + animation. NetworkRigidbody2D restores
                // the body's kinematic state itself, so don't re-assert it here.
                _health.Respawn();
            }
        }
        if (_energy != null && Mathf.Abs(_energy.CurrentEnergy - NetworkedEnergy) > 0.01f)
            _energy.SetEnergy(NetworkedEnergy);
    }

    public void SendInput(PlayerInputData input) { /* GameLauncher.OnInput is the network input path */ }

    // World-space cursor point for whichever peer is drawing a local view (the aiming reticle).
    public static Vector2 GetMouseWorldPoint(Transform fallback)
    {
        if (Camera.main == null) return fallback.position;
        Vector3 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        mouse.z = 0f;
        return mouse;
    }
}
