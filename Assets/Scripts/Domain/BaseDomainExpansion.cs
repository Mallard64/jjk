using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Stub base class for domain expansion. Not fully implemented — structure is here for future characters.
/// TODO: Add spatial distortion VFX, domain clash resolution, guaranteed-hit rule.
/// TODO: Simple domain rule — simple domain can nullify invincibility on targets inside it.
/// </summary>
public abstract class BaseDomainExpansion : MonoBehaviour
{
    [Header("Domain Settings")]
    [SerializeField] protected float activationEnergyCost = 50f;
    [SerializeField] protected float energyDrainPerSecond = 10f;
    [SerializeField] protected float maxDuration = 10f;

    public bool IsActive { get; private set; }

    protected CursedEnergy Energy;
    protected PlayerHealth Health;

    // TODO: Add network sync hooks here for multiplayer
    public event Action OnDomainActivated;
    public event Action OnDomainDeactivated;

    protected virtual void Awake()
    {
        Energy = GetComponent<CursedEnergy>();
        Health = GetComponent<PlayerHealth>();
    }

    public virtual bool CanActivate()
    {
        return !IsActive && (Energy == null || Energy.CurrentEnergy >= activationEnergyCost);
    }

    public virtual void Activate()
    {
        if (!CanActivate()) return;

        Energy?.TrySpend(activationEnergyCost);
        IsActive = true;
        OnDomainActivated?.Invoke();
        OnActivated();
        StartCoroutine(DrainAndExpire());
    }

    public virtual void Deactivate()
    {
        if (!IsActive) return;
        IsActive = false;
        OnDomainDeactivated?.Invoke();
        OnDeactivated();
    }

    /// <summary>Override to add domain-specific activation logic (VFX, hitbox, etc.)</summary>
    protected virtual void OnActivated() { }

    /// <summary>Override to clean up domain-specific effects on deactivation.</summary>
    protected virtual void OnDeactivated() { }

    private IEnumerator DrainAndExpire()
    {
        float elapsed = 0f;
        while (IsActive && elapsed < maxDuration)
        {
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;

            if (Energy != null)
            {
                Energy.Drain(energyDrainPerSecond * 0.5f);
                if (Energy.CurrentEnergy <= 0f)
                {
                    Deactivate();
                    yield break;
                }
            }
        }

        if (IsActive) Deactivate();
    }

    // TODO: SimpleDomain override — override CanNullifyInvincibility() returning true.
    // When SimpleDomain is active and an invincible target is inside the domain, call
    // target.SetInvincible(false) to strip the buff. Implement in a SimpleDomainExpansion subclass.
}
