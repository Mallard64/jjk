using UnityEngine;

/// <summary>
/// Offline adapter — both players are local, no networking needed.
/// This is the default until Photon Fusion is integrated.
/// </summary>
public class LocalNetworkAdapter : MonoBehaviour, INetworkAdapter
{
    public bool IsLocalPlayer => true;
    public bool IsAuthority   => true;

    public void SendInput(PlayerInputData input)
    {
        // No-op in local play. Input is already applied directly.
    }
}
