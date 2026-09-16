using UnityEngine;

/// <summary>
/// Default Null Field. The base class owns all the warfare mechanics (RES cost, drain, clash,
/// burnout, regen/overdrive locks); this subclass only supplies the character's BONUS: while the
/// null field is open the owner moves faster — a control/pressure advantage, never guaranteed damage.
/// Other characters are new subclasses that set MoveSpeedMultiplier differently or do their own thing
/// in OnFieldStarted/OnFieldEnded, plus their own VFX/SFX.
/// </summary>
public class StandardNullField : BaseNullField
{
    [Header("Null field Bonus")]
    [Tooltip("Move-speed multiplier the owner gains while the null field is open (1 = none).")]
    [SerializeField] private float bonusMoveSpeedMultiplier = 1.3f;

    protected override void OnFieldStarted()
    {
        MoveSpeedMultiplier = bonusMoveSpeedMultiplier;
    }

    protected override void OnFieldEnded()
    {
        MoveSpeedMultiplier = 1f;  // base already resets this; explicit for clarity
    }
}
