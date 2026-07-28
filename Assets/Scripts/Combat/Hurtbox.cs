using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public class Hurtbox : MonoBehaviour
{
    private PlayerHealth              _health;
    private Rigidbody2D               _rb;
    private FusionPlayerCombat        _netCombat;
    private PlayerAnimationController _anim;

    void Awake()
    {
        _health    = GetComponentInParent<PlayerHealth>();
        _rb        = GetComponentInParent<Rigidbody2D>();
        _netCombat = GetComponentInParent<FusionPlayerCombat>();
        _anim      = GetComponentInParent<PlayerAnimationController>();
    }

    /// <summary>Apply a landed hit. Only ever reached on the simulating peer — Hitbox gates its trigger on
    /// state authority, so this runs on the host in online play and locally when offline.</summary>
    public void ReceiveHit(float damage, Vector2 knockback, GameObject source, float hitstun)
    {
        if (_health == null || _health.IsDead || _health.IsInvincible) return;

        // Face away from the hit before the hurt anim so the post-hurt idle/walk orients toward the attacker.
        _anim?.SetFacingFromHit(knockback);
        _anim?.SetNextHitstun(hitstun);
        _health.TakeDamage(damage, source);
        if (_rb != null && _rb.simulated && knockback.sqrMagnitude > 0f && !_health.IsDead)
        {
            // Combo decay: TakeDamage just ran PlayHurt, so the multiplier reflects this hit's combo depth.
            float comboScale = _anim != null ? _anim.ComboKnockbackMultiplier : 1f;
            Vector2 impulse = knockback * comboScale;
            _rb.AddForce(impulse, ForceMode2D.Impulse);
            _anim?.SetLastHitKnockback(impulse.magnitude);  // a later wall collision uses this for the splat
            _anim?.CheckWallSplatOnHit();                   // …or splat now if already pinned to a wall
        }

        // Online: replay the same reaction on the other peers (HP itself replicates via FusionPlayerSync).
        // No-ops when unspawned — an offline player, or a training dummy placed in a scene.
        _netCombat?.ReplicateHurt(damage, knockback, hitstun, source);
    }
}
