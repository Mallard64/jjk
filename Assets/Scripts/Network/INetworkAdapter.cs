using UnityEngine;

/// <summary>
/// Abstraction layer between gameplay code and the networking backend.
/// Implement LocalNetworkAdapter for offline play. FusionPlayerSync implements this for Shared Mode online play.
/// </summary>
public interface INetworkAdapter
{
    bool IsLocalPlayer { get; }
    bool IsAuthority { get; }

    /// <summary>
    /// Send this player's input to the authority (server or host).
    /// In local play, this is a no-op — input is already local.
    /// </summary>
    void SendInput(PlayerInputData input);

    // TODO: Add Photon Fusion INetworkRunner hooks here when integrating Fusion.
    // Replace [Command]/[ClientRpc] with Fusion's [Rpc] and Networked properties.
    // See DEV_NOTES.md → "Adding Photon Fusion" for setup steps.
}
