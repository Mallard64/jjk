using System;
using UnityEngine;

/// <summary>
/// Cursed energy resource. Cursed techniques call TrySpend/Drain/Restore; they do NOT own this
/// directly. Passive regen can be suppressed for a window via SuppressRegenForAction so actively
/// committing to an attack/roll pauses the meter from ticking back up. Actions that cost CE check
/// CurrentEnergy and refuse to fire when it's short, so the meter never overdraws.
/// </summary>
public class CursedEnergy : MonoBehaviour
{
    [SerializeField] private float maxEnergy = 100f;
    [SerializeField] private float regenPerSecond = 3f;
    [Tooltip("Extra seconds of passive-regen lockout added on top of an action's own duration in SuppressRegenForAction.")]
    [SerializeField] private float postActionRegenLockout = 1.5f;

    public float MaxEnergy => maxEnergy;
    public float CurrentEnergy { get; private set; }

    public event Action<float, float> OnEnergyChanged; // (current, max)

    private float _regenSuppressTimer;

    void Awake()
    {
        CurrentEnergy = maxEnergy;
    }

    void Update()
    {
        if (_regenSuppressTimer > 0f)
        {
            _regenSuppressTimer -= Time.deltaTime;
            return;
        }
        if (CurrentEnergy < maxEnergy)
        {
            Restore(regenPerSecond * Time.deltaTime);
        }
    }

    /// <summary>Returns true and spends energy if available.</summary>
    public bool TrySpend(float amount)
    {
        if (CurrentEnergy < amount) return false;
        Drain(amount);
        return true;
    }

    public void Drain(float amount)
    {
        if (amount <= 0f) return;
        CurrentEnergy = Mathf.Max(0f, CurrentEnergy - amount);
        OnEnergyChanged?.Invoke(CurrentEnergy, maxEnergy);
    }

    public void Restore(float amount)
    {
        if (amount <= 0f) return;
        CurrentEnergy = Mathf.Min(maxEnergy, CurrentEnergy + amount);
        OnEnergyChanged?.Invoke(CurrentEnergy, maxEnergy);
    }

    public void SetEnergy(float value)
    {
        CurrentEnergy = Mathf.Clamp(value, 0f, maxEnergy);
        OnEnergyChanged?.Invoke(CurrentEnergy, maxEnergy);
    }

    /// <summary>Suppress passive regen for `actionDuration + postActionRegenLockout`. Stacks by max remaining.</summary>
    public void SuppressRegenForAction(float actionDuration)
    {
        float total = Mathf.Max(0f, actionDuration) + postActionRegenLockout;
        if (total > _regenSuppressTimer) _regenSuppressTimer = total;
    }
}
