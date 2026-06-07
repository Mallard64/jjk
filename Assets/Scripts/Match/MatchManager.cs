using System.Collections;
using UnityEngine;

/// <summary>
/// Manages a 1v1 match: spawns players, watches for death, runs the death → blackout → respawn
/// transition. When a player dies the fallen fighter plays its death animation, both players are
/// frozen, the screen blacks out for a few seconds, then both respawn at full HP/CE on their own
/// spawn points. Assign player prefabs and spawn points in the Inspector.
/// Tags "Player1" and "Player2" must exist in Unity's Tag Manager.
/// </summary>
public class MatchManager : MonoBehaviour
{
    [Header("Players")]
    [SerializeField] private GameObject player1Prefab;
    [SerializeField] private GameObject player2Prefab;
    [SerializeField] private Transform spawn1;
    [SerializeField] private Transform spawn2;

    [Header("Death / Respawn")]
    [Tooltip("Seconds the death animation is shown before the screen starts fading to black.")]
    [SerializeField] private float deathLinger = 1f;
    [Tooltip("Seconds to fade the screen to / from black.")]
    [SerializeField] private float fadeDuration = 0.35f;
    [Tooltip("Seconds the screen stays fully black before players respawn.")]
    [SerializeField] private float blackoutDuration = 3f;

    public bool MatchActive { get; private set; }

    private GameObject _p1Instance;
    private GameObject _p2Instance;

    public static MatchManager Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        SpawnPlayers();
        MatchActive = true;
        MatchUI.Instance?.ShowMatchStart();
    }

    private void SpawnPlayers()
    {
        _p1Instance = Instantiate(player1Prefab, spawn1.position, Quaternion.identity);
        _p2Instance = Instantiate(player2Prefab, spawn2.position, Quaternion.identity);

        _p1Instance.tag = "Player1";
        _p2Instance.tag = "Player2";

        _p1Instance.GetComponent<PlayerInputHandler>()?.SetPlayerIndex(0);
        _p2Instance.GetComponent<PlayerInputHandler>()?.SetPlayerIndex(1);

        // Instances persist across deaths, so subscribe once.
        var p1Health = _p1Instance.GetComponent<PlayerHealth>();
        var p2Health = _p2Instance.GetComponent<PlayerHealth>();
        if (p1Health != null) p1Health.OnDeath += OnPlayerDied;
        if (p2Health != null) p2Health.OnDeath += OnPlayerDied;
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

        yield return new WaitForSeconds(deathLinger);
        yield return ScreenBlackout.Instance.FadeTo(1f, fadeDuration);
        yield return new WaitForSeconds(blackoutDuration);

        RespawnPlayer(_p1Instance, spawn1);
        RespawnPlayer(_p2Instance, spawn2);

        yield return ScreenBlackout.Instance.FadeTo(0f, fadeDuration);

        MatchActive = true;
        MatchUI.Instance?.ShowMatchStart();
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
        if (spawn != null) player.transform.position = spawn.position;
        player.GetComponent<PlayerHealth>()?.Respawn();            // full HP + CE, resets anim + movement
        SetControlEnabled(player, true);
    }

    private void SetControlEnabled(GameObject player, bool enabled)
    {
        var input = player.GetComponent<PlayerInputHandler>();
        if (input != null) input.enabled = enabled;
        var combat = player.GetComponent<PlayerCombatController>();
        if (combat != null) combat.enabled = enabled;
    }
}
