using System.Collections;
using UnityEngine;

/// <summary>
/// Manages an offline 1v1 match: spawns two fighters, watches for death, runs the death → respawn
/// transition. Assign prefabs and spawn points in the Inspector.
/// Tags "Player1" and "Player2" must exist in Unity's Tag Manager.
///
/// Three line-ups, chosen by PracticeMode: human vs human, human vs bot, or bot vs bot for spectating.
/// The transition is either the full death → blackout → respawn presentation, or an immediate rematch
/// (instantRematch) which is what the practice modes use — there is no round score to present.
/// </summary>
public class MatchManager : MonoBehaviour
{
    /// <summary>Who fills each side. PlayerVsPlayer is the original local-2P line-up.</summary>
    public enum PracticeMode { PlayerVsPlayer, PlayerVsBot, BotVsBot }

    [Header("Players")]
    [SerializeField] private GameObject player1Prefab;
    [SerializeField] private GameObject player2Prefab;
    [Tooltip("Player prefab variant carrying a BotController. Fills the bot side(s) of a practice match.")]
    [SerializeField] private GameObject botPrefab;
    [SerializeField] private Transform spawn1;
    [SerializeField] private Transform spawn2;

    [Header("Startup")]
    [Tooltip("Spawn a PlayerVsPlayer match on Start. Leave off when a launcher calls StartMatch instead.")]
    [SerializeField] private bool autoStart = false;

    [Header("Death / Respawn")]
    [Tooltip("Skip the linger, fade and blackout and rematch on the next frame. What the practice modes want — there is no score to show.")]
    [SerializeField] private bool instantRematch = true;
    [Tooltip("Seconds the death animation is shown before the screen starts fading to black.")]
    [SerializeField] private float deathLinger = 1f;
    [Tooltip("Seconds to fade the screen to / from black.")]
    [SerializeField] private float fadeDuration = 0.35f;
    [Tooltip("Seconds the screen stays fully black before players respawn.")]
    [SerializeField] private float blackoutDuration = 3f;

    public bool MatchActive { get; private set; }

    private GameObject _p1Instance;
    private GameObject _p2Instance;
    private bool       _spawned;

    public static MatchManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        if (autoStart) StartMatch(PracticeMode.PlayerVsPlayer);
    }

    /// <summary>Spawns the line-up and starts the match. A second call only resets the existing
    /// fighters rather than spawning duplicates.</summary>
    public void StartMatch(PracticeMode mode)
    {
        if (!_spawned)
        {
            if (!SpawnPlayers(mode)) return;   // missing prefab; stay unspawned so a retry can work
            _spawned = true;
        }

        foreach (var bot in FindObjectsByType<BotController>(FindObjectsSortMode.None)) bot.ResetMemory();

        // Follow the human when there is one; fall back to midpoint framing for bot-vs-bot so both
        // fighters stay in shot.
        var camera = FindFirstObjectByType<CameraFollow>();
        if (camera != null)
            camera.SetFollowTarget(mode == PracticeMode.PlayerVsBot && _p1Instance != null
                                   ? _p1Instance.transform
                                   : null);

        MatchActive = true;
    }

    private bool SpawnPlayers(PracticeMode mode)
    {
        // botPrefab is the intended slot, but player2Prefab is the obvious place to drop a bot and it
        // is a common mis-wiring — fall back to it rather than silently spawning nothing.
        GameObject bot = botPrefab != null ? botPrefab : player2Prefab;

        GameObject side1 = mode == PracticeMode.BotVsBot       ? bot          : player1Prefab;
        GameObject side2 = mode == PracticeMode.PlayerVsPlayer ? player2Prefab : bot;

        if (side1 == null || side2 == null)
        {
            Debug.LogWarning($"[MatchManager] {mode} can't spawn: assign botPrefab (or player2Prefab) " +
                             "and player1Prefab in the Inspector.", this);
            return false;
        }
        if (mode != PracticeMode.PlayerVsPlayer && bot.GetComponent<BotController>() == null)
            Debug.LogWarning($"[MatchManager] '{bot.name}' has no BotController, so it will never act. " +
                             "Add the component to the bot prefab.", this);

        _p1Instance = Instantiate(side1, spawn1.position, Quaternion.identity);
        _p2Instance = Instantiate(side2, spawn2.position, Quaternion.identity);

        _p1Instance.tag = "Player1";
        _p2Instance.tag = "Player2";

        _p1Instance.GetComponent<PlayerInputHandler>()?.SetPlayerIndex(0);
        _p2Instance.GetComponent<PlayerInputHandler>()?.SetPlayerIndex(1);

        // Instances persist across deaths, so subscribe once.
        var p1Health = _p1Instance.GetComponent<PlayerHealth>();
        var p2Health = _p2Instance.GetComponent<PlayerHealth>();
        if (p1Health != null) p1Health.OnDeath += OnPlayerDied;
        if (p2Health != null) p2Health.OnDeath += OnPlayerDied;
        return true;
    }

    private void OnPlayerDied()
    {
        if (!MatchActive) return;
        MatchActive = false;
        StartCoroutine(DeathRespawnRoutine());
    }

    private IEnumerator DeathRespawnRoutine()
    {
        // The fallen player's death animation is already playing (PlayerCombatController.OnDeath).
        // Freeze both fighters so neither can act during the transition.
        FreezePlayer(_p1Instance);
        FreezePlayer(_p2Instance);

        if (instantRematch)
        {
            // One frame so the freeze and the death state settle before the respawn resets them.
            // Deliberately never touches ScreenBlackout — practice restarts with no interruption.
            yield return null;
        }
        else
        {
            yield return new WaitForSeconds(deathLinger);
            yield return ScreenBlackout.Instance.FadeTo(1f, fadeDuration);
            yield return new WaitForSeconds(blackoutDuration);
        }

        RespawnPlayer(_p1Instance, spawn1);
        RespawnPlayer(_p2Instance, spawn2);

        if (!instantRematch) yield return ScreenBlackout.Instance.FadeTo(0f, fadeDuration);

        MatchActive = true;
    }

    private void FreezePlayer(GameObject player)
    {
        if (player == null) return;
        SetControlEnabled(player, false);
        player.GetComponent<PlayerOverdrive>()?.SetActive(false);
        var movement = player.GetComponent<PlayerMovement>();
        if (movement != null)
        {
            movement.CanMove = false;
            movement.Freeze();
        }
    }

    private void RespawnPlayer(GameObject player, Transform spawn)
    {
        if (player == null) return;

        // Respawn first: it unfreezes the body, so the teleport lands on a live rigidbody. A plain
        // transform write here does NOT stick — auto-sync-transforms is off project-wide and the body's
        // own pose would drag the fighter back to where it died. See PlayerMovement.Teleport.
        player.GetComponent<PlayerHealth>()?.Respawn();            // full HP + RES, resets anim + movement
        if (spawn != null) player.GetComponent<PlayerMovement>()?.Teleport(spawn.position);
        SetControlEnabled(player, true);
    }

    private void SetControlEnabled(GameObject player, bool enabled)
    {
        var input = player.GetComponent<PlayerInputHandler>();
        if (input != null) input.enabled = enabled;
        var combat = player.GetComponent<PlayerCombatController>();
        if (combat != null) combat.enabled = enabled;
        // A bot's brain is its input source, so it has to be frozen alongside the handler — otherwise
        // it keeps writing a move direction into an input component that is no longer polling.
        var bot = player.GetComponent<BotController>();
        if (bot != null) bot.enabled = enabled;
    }
}
