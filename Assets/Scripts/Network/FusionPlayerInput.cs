using Fusion;
using UnityEngine;

/// <summary>Button indices carried in FusionPlayerInput.Buttons. The order is part of the wire format —
/// append new entries at the end, never reorder.</summary>
public enum PlayerButton
{
    AutoAttack = 0,
    Aimable    = 1,
    NullField  = 2,
    Roll       = 3,
    Overdrive  = 4,
}

/// <summary>
/// Host mode input: every peer polls this for the player it controls (GameLauncher.OnInput) and the
/// server consumes it in FixedUpdateNetwork. Buttons carry the raw held state — press/release edges are
/// derived server-side via NetworkButtons.GetPressed/GetReleased, so a tap can't be lost or double-fired
/// when a packet drops.
/// </summary>
public struct FusionPlayerInput : INetworkInput
{
    public Vector2        MoveDir;
    // World-space cursor. The aim heading is derived from this against the server's authoritative player
    // position, so a client never dictates where it is standing. Throwable attacks need the point itself.
    public Vector2        AimPoint;
    public NetworkButtons Buttons;
}
