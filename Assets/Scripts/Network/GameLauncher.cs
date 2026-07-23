using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Drop on a scene GameObject. Disable MatchManager when using this.
/// Each peer spawns their own player on join. Shared Mode: every peer is authoritative over themselves.
/// </summary>
public class GameLauncher : MonoBehaviour, INetworkRunnerCallbacks
{
    [Header("Scene Refs")]
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private Transform  spawn1;
    [SerializeField] private Transform  spawn2;

    private NetworkRunner _runner;
    private bool   _started;
    private string _room   = "JJKArena";
    private string _status = "";

    void OnGUI()
    {
        if (_started) return;

        GUILayout.BeginArea(new Rect(Screen.width / 2f - 150, Screen.height / 2f - 60, 300, 120));
        _room = GUILayout.TextField(_room, GUILayout.Height(30));
        if (GUILayout.Button("Join Match", GUILayout.Height(40))) _ = Launch();
        GUILayout.Label(_status);
        GUILayout.EndArea();
    }

    async System.Threading.Tasks.Task Launch()
    {
        _started = true;
        _status  = "Connecting…";
        NetworkMatchUI.Instance?.HideAll();  // clear any result screen left from a previous match
        ScreenBlackout.Instance.SetAlpha(0f);  // …and the white fade the previous match ended on

        _runner = gameObject.AddComponent<NetworkRunner>();
        _runner.ProvideInput = true;
        _runner.AddCallbacks(this);

        var result = await _runner.StartGame(new StartGameArgs
        {
            GameMode     = GameMode.Shared,
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
        // Each peer spawns ONLY their own player
        if (player != runner.LocalPlayer) return;

        int existing = runner.SessionInfo.PlayerCount - 1; // excludes me
        Vector3 pos = existing == 0 ? spawn1.position : spawn2.position;
        runner.Spawn(playerPrefab, pos, Quaternion.identity, runner.LocalPlayer);
    }

    // ── Stub callbacks ────────────────────────────────────────────────────
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)                                                              { }
    public void OnInput(NetworkRunner runner, NetworkInput input)                                                                  { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject o, PlayerRef p)                                                    { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject o, PlayerRef p)                                                   { }
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput i)                                                      { }
    public void OnShutdown(NetworkRunner r, ShutdownReason reason)                                                                { _started = false; }
    public void OnConnectedToServer(NetworkRunner r)                                                                               { }
    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason)                                             { }
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
