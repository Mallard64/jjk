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

    public void ReceiveHit(float damage, Vector2 knockback, GameObject source, float hitstun)
    {
        if (_health == null || _health.IsDead) return;

        // Online: route through victim's StateAuthority — but only when actually network-spawned.
        // An unspawned FusionPlayerCombat (offline player, or a training dummy placed in a scene) has
        // no valid NetworkObject, so fall through to the direct offline path instead of RPCing into the void.
        if (_netCombat != null && _netCombat.Object != null && _netCombat.Object.IsValid)
        {
            _netCombat.RpcTakeDamage(damage, knockback, hitstun);
            return;
        }

        // Offline: apply directly. Face away from the hit before the hurt anim so the
        // post-hurt idle/walk orients the player toward the attacker.
        _anim?.SetFacingFromHit(knockback);
        _anim?.SetNextHitstun(hitstun);
        _health.TakeDamage(damage, source);
        if (_rb != null && knockback.sqrMagnitude > 0f && !_health.IsDead)
        {
            // Combo decay: TakeDamage just ran PlayHurt, so the multiplier reflects this hit's combo depth.
            float comboScale = _anim != null ? _anim.ComboKnockbackMultiplier : 1f;
            Vector2 impulse = knockback * comboScale;
            _rb.AddForce(impulse, ForceMode2D.Impulse);
            _anim?.SetLastHitKnockback(impulse.magnitude);  // a later wall collision uses this for the splat
            _anim?.CheckWallSplatOnHit();                   // …or splat now if already pinned to a wall
        }
    }
}
