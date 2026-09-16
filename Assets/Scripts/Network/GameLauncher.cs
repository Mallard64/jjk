using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Addons.Physics;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Drop on a scene GameObject. Disable MatchManager when using this.
///
/// Host mode: one peer hosts (server + its own player), everyone else joins as a client. The server owns
/// state authority over every player, so it is the only peer that simulates movement, attacks and damage.
/// Clients own input authority over their own player and do nothing but send input and render replicated
/// state — no extrapolation, so remote motion is smooth instead of rubber-banding.
///
/// This is also the single input poll for the peer: OnInput fills FusionPlayerInput for whichever player
/// this peer controls, and FusionPlayerMovement / FusionPlayerCombat read it back on the server.
/// </summary>
public class GameLauncher : MonoBehaviour, INetworkRunnerCallbacks
{
    [Header("Scene Refs")]
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private Transform  spawn1;
    [SerializeField] private Transform  spawn2;

    [Header("Offline practice")]
    [Tooltip("MatchManager that spawns the local practice line-ups. Leave empty to hide the practice buttons.")]
    [SerializeField] private MatchManager practiceMatch;

    private NetworkRunner _runner;
    private bool          _started;
    private string _room   = "ResonanceArena";
    private string _status = "";

    // Server-side: which object belongs to which player, so a leaver's fighter is despawned.
    private readonly Dictionary<PlayerRef, NetworkObject> _spawned = new Dictionary<PlayerRef, NetworkObject>();

    void OnGUI()
    {
        if (_started) return;

        // Taller and higher than the original two-button menu so the practice buttons stay on screen.
        GUILayout.BeginArea(new Rect(Screen.width / 2f - 150, Screen.height / 2f - 130, 300, 280));
        _room = GUILayout.TextField(_room, GUILayout.Height(30));
        if (GUILayout.Button("Host Match", GUILayout.Height(40))) _ = Launch(GameMode.Host);
        if (GUILayout.Button("Join Match", GUILayout.Height(40))) _ = Launch(GameMode.Client);

        if (practiceMatch != null)
        {
            GUILayout.Space(8);
            if (GUILayout.Button("Practice vs Bot", GUILayout.Height(34)))
                StartLocal(MatchManager.PracticeMode.PlayerVsBot);
            if (GUILayout.Button("Bot vs Bot", GUILayout.Height(34)))
                StartLocal(MatchManager.PracticeMode.BotVsBot);
        }

        GUILayout.Label(_status);
        GUILayout.EndArea();
    }

    /// <summary>Offline practice: no runner is created, so every gameplay script stays on its local
    /// path. Lives here because this is the only menu in the game — a second competing OnGUI would
    /// overlap it.</summary>
    private void StartLocal(MatchManager.PracticeMode mode)
    {
        _started = true;

        // The scene's passive TrainingDummy is tagged Player1, which would hijack CameraFollow's
        // midpoint framing and PlayerInputHandler's P2 auto-aim once the real fighters spawn.
        foreach (var dummy in FindObjectsByType<TrainingDummy>(FindObjectsSortMode.None))
            dummy.gameObject.SetActive(false);

        practiceMatch.StartMatch(mode);
    }

    async System.Threading.Tasks.Task Launch(GameMode mode)
    {
        _started = true;
        _status  = mode == GameMode.Host ? "Hosting…" : "Connecting…";
        NetworkMatchUI.Instance?.HideAll();  // clear any result screen left from a previous match
        ScreenBlackout.Instance.SetAlpha(0f);  // …and the white fade the previous match ended on

        _runner = gameObject.AddComponent<NetworkRunner>();
        _runner.ProvideInput = true;  // host and client both send input for the player they control
        _runner.AddCallbacks(this);

        // Steps Physics2D per network tick. Added here rather than left to NetworkRigidbody2D's
        // auto-add (which warns and uses defaults) so the client prediction mode is ours to set:
        // SimulateAlways runs physics on forward AND resimulation ticks, which is what lets a client
        // predict its own fighter from local input and reconcile against the server's snapshots.
        var physics = gameObject.AddComponent<RunnerSimulatePhysics2D>();
        physics.ClientPhysicsSimulation = ClientPhysicsSimulation.SimulateAlways;

        var result = await _runner.StartGame(new StartGameArgs
        {
            GameMode     = mode,
            SessionName  = _room,
            Scene        = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex),
            SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>(),
        });

        if (!result.Ok)
        {
            _status  = $"Failed: {result.ShutdownReason}";
            _started = false;
        }
    }

    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        // Only the server spawns, and it spawns for everyone — clients receive the objects replicated.
        if (!runner.IsServer) return;
        if (_spawned.ContainsKey(player)) return;

        Transform spawn = _spawned.Count == 0 ? spawn1 : spawn2;
        // Input authority goes to the joining player: they drive this fighter, the server simulates it.
        var obj = runner.Spawn(playerPrefab, spawn.position, Quaternion.identity, player);
        _spawned[player] = obj;
    }

    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (!runner.IsServer) return;
        if (!_spawned.TryGetValue(player, out var obj)) return;
        _spawned.Remove(player);
        if (obj != null) runner.Despawn(obj);
    }

    // The one input poll for this peer. Fusion delivers it to whichever player object we have input
    // authority over; the server reads it back through GetInput in FixedUpdateNetwork.
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        var data = new FusionPlayerInput
        {
            MoveDir = new Vector2(
                (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f),
                (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f)
            ).normalized,
            AimPoint = MouseWorldPoint(),
        };

        data.Buttons.Set(PlayerButton.AutoAttack, Input.GetMouseButton(0));
        data.Buttons.Set(PlayerButton.Roll,       Input.GetMouseButton(1));
        data.Buttons.Set(PlayerButton.Aimable,    Input.GetKey(KeyCode.Q));
        data.Buttons.Set(PlayerButton.NullField,     Input.GetKey(KeyCode.E));
        data.Buttons.Set(PlayerButton.Overdrive,  Input.GetKey(KeyCode.LeftShift));

        input.Set(data);
    }

    private static Vector2 MouseWorldPoint()
    {
        if (Camera.main == null) return Vector2.zero;
        Vector3 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        mouse.z = 0f;
        return mouse;
    }

    public void OnShutdown(NetworkRunner r, ShutdownReason reason)
    {
        _spawned.Clear();
        _started = false;
    }

    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason)
    {
        _spawned.Clear();
        _started = false;
    }

    // ── Stub callbacks ────────────────────────────────────────────────────
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject o, PlayerRef p)                                                    { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject o, PlayerRef p)                                                   { }
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput i)                                                      { }
    public void OnConnectedToServer(NetworkRunner r)                                                                               { }
    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest req, byte[] token)                     { }
    public void OnConnectFailed(NetworkRunner r, NetAddress addr, NetConnectFailedReason reason)                                  { }
    public void OnUserSimulationMessage(NetworkRunner r, SimulationMessagePtr msg)                                                { }
    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> list)                                                     { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> d)                                     { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken token)                                                        { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef p, ReliableKey key, ArraySegment<byte> data)                   { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef p, ReliableKey key, float progress)                            { }
    public void OnSceneLoadDone(NetworkRunner r)                                                                                   { }
    public void OnSceneLoadStart(NetworkRunner r)                                                                                  { }
}
