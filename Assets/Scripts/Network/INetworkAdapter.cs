using UnityEngine;

/// <summary>
/// Abstraction layer between gameplay code and the networking backend.
/// LocalNetworkAdapter covers offline play; FusionPlayerSync implements this for Host mode online play,
/// where the two flags below stop being the same thing.
/// </summary>
public interface INetworkAdapter
{
    /// <summary>Is this the fighter this peer controls? (Fusion: input authority.) Drives local-view
    /// concerns — the aiming reticle, the corner HUD, screen shake, the combo readout.</summary>
    bool IsLocalPlayer { get; }

    /// <summary>Does this peer simulate this fighter? (Fusion: state authority — in Host mode the server,
    /// for BOTH fighters.) Gates anything that must happen exactly once: damage, resource drain, scoring.</summary>
    bool IsAuthority { get; }

    /// <summary>Offline no-op — input is already local. Online, GameLauncher.OnInput is the network
    /// input path, so nothing routes through here.</summary>
    void SendInput(PlayerInputData input);
}
