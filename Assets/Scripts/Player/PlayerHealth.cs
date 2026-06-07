using System;
using UnityEngine;

/// <summary>
/// Adapted from Hittable.cs (BS project). Adds invincibility, events, and death state.
/// </summary>
public class PlayerHealth : MonoBehaviour
{
    [SerializeField] private float maxHp = 100f;

    public float MaxHp => maxHp;
    public float CurrentHp { get; private set; }
    public bool IsDead { get; private set; }
    public bool IsInvincible { get; private set; }

    public event Action<float, float> OnHealthChanged;  // (current, max)
    public event Action<float, GameObject> OnDamaged;   // (amount, source)
    public event Action OnDeath;

    void Awake()
    {
        CurrentHp = maxHp;
    }

    public void TakeDamage(float amount, GameObject source = null)
    {
        if (IsDead || IsInvincible || amount <= 0f) return;

        CurrentHp = Mathf.Max(0f, CurrentHp - amount);
        OnDamaged?.Invoke(amount, source);
        OnHealthChanged?.Invoke(CurrentHp, maxHp);

        if (CurrentHp <= 0f)
            Die();
    }

    /// <summary>Reduce HP without firing OnDamaged — so it triggers no hurt reaction (the caller, e.g.
    /// the wall splat, drives its own animation). Still fires OnHealthChanged (bar) and can kill.
    /// Ignores invincibility since it's a deterministic consequence of a hit that already landed.</summary>
    public void TakeReactionlessDamage(float amount)
    {
        if (IsDead || amount <= 0f) return;
        CurrentHp = Mathf.Max(0f, CurrentHp - amount);
        OnHealthChanged?.Invoke(CurrentHp, maxHp);
        if (CurrentHp <= 0f) Die();
    }

    public void Heal(float amount)
    {
        if (IsDead || amount <= 0f) return;

        CurrentHp = Mathf.Min(maxHp, CurrentHp + amount);
        OnHealthChanged?.Invoke(CurrentHp, maxHp);
    }

    public void SetInvincible(bool value)
    {
        IsInvincible = value;
    }

    public void Die()
    {
        if (IsDead) return;
        IsDead = true;
        CurrentHp = 0f;
        OnDeath?.Invoke();
    }

    // Used by FusionPlayerSync to push networked HP to local component so UI events fire
    public void ForceSetHp(float value)
    {
        CurrentHp = Mathf.Clamp(value, 0f, maxHp);
        OnHealthChanged?.Invoke(CurrentHp, maxHp);
    }

    public void Respawn()
    {
        IsDead = false;
        CurrentHp = maxHp;
        IsInvincible = false;
        GetComponent<PlayerAnimationController>()?.ResetState();
        var mv = GetComponent<PlayerMovement>();
        if (mv != null)
        {
            mv.Unfreeze();
            mv.SetCanMove(true);
        }
        var energy = GetComponent<CursedEnergy>();
        if (energy != null) energy.SetEnergy(energy.MaxEnergy);  // respawn at full CE too
        OnHealthChanged?.Invoke(CurrentHp, maxHp);
    }
}
