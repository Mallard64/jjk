using UnityEngine;

/// <summary>
/// Default Domain Expansion. The base class owns all the warfare mechanics (CE cost, drain, clash,
/// burnout, regen/overdrive locks); this subclass only supplies the character's BONUS: while the
/// domain is open the owner moves faster — a control/pressure advantage, never guaranteed damage.
/// Other characters are new subclasses that set MoveSpeedMultiplier differently or do their own thing
/// in OnDomainStarted/OnDomainEnded, plus their own VFX/SFX.
/// </summary>
public class StandardDomainExpansion : BaseDomainExpansion
{
    [Header("Domain Bonus")]
    [Tooltip("Move-speed multiplier the owner gains while the domain is open (1 = none).")]
    [SerializeField] private float bonusMoveSpeedMultiplier = 1.3f;

    protected override void OnDomainStarted()
    {
        MoveSpeedMultiplier = bonusMoveSpeedMultiplier;
    }

    protected override void OnDomainEnded()
    {
        MoveSpeedMultiplier = 1f;  // base already resets this; explicit for clarity
    }
}
