using Fusion;
using UnityEngine;

public struct FusionPlayerInput : INetworkInput
{
    public Vector2     MoveDir;
    public Vector2     AimDir;
    public NetworkBool AutoAttack;
    public NetworkBool AimableAttack;
    public NetworkBool Domain;
}
