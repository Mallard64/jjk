using UnityEngine;

/// <summary>
/// Snapshot of player input for one frame. Network-safe: can be sent over the wire or filled locally.
/// </summary>
public struct PlayerInputData
{
    public Vector2 MoveDir;
    public Vector2 AimDir;
    // World-space target the player is aiming at (mouse for P1, opponent for P2). Carries the
    // distance AimDir drops by normalizing — throwable attacks need the actual point, not just a heading.
    public Vector2 AimPoint;
    public bool AutoAttack;
    public bool AimableAttackDown;
    public bool AimableAttackUp;
    public bool Domain;
    public bool Roll;
    public bool Overdrive;
}
