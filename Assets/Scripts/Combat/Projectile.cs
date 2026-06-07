using UnityEngine;

/// <summary>
/// A traveling hitbox spawned by AttackHitboxController when its attack is set to fire a projectile.
/// Flies at a fixed velocity for `duration` seconds, then despawns. Throwable attacks launch it with
/// zero velocity, so it simply sits at the drop point for its lifetime. Damage is dealt by the child
/// Hitbox on overlap, identical to a melee swing.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class Projectile : MonoBehaviour
{
    [SerializeField] private Hitbox hitbox;

    private Rigidbody2D _rb;
    private Collider2D  _hitboxCollider;
    private Vector3     _baseScale = Vector3.one;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 0f;
        if (hitbox == null) hitbox = GetComponentInChildren<Hitbox>(true);
        if (hitbox == null) { Debug.LogError($"Projectile on {name}: no Hitbox found."); return; }
        _baseScale = hitbox.transform.localScale;
        _hitboxCollider = hitbox.GetComponent<Collider2D>();
    }

    /// <summary>Initialize and release the projectile. A zero velocity is a stationary drop (throwable).</summary>
    public void Launch(GameObject owner, Vector2 velocity, float duration, float damageMultiplier, float knockbackMultiplier, float sizeMultiplier)
    {
        if (hitbox != null)
        {
            hitbox.transform.localScale = _baseScale * (sizeMultiplier > 0f ? sizeMultiplier : 1f);
            hitbox.Init(owner, damageMultiplier, knockbackMultiplier);
        }
        if (_hitboxCollider != null) _hitboxCollider.enabled = true;
        if (_rb != null) _rb.velocity = velocity;
        Destroy(gameObject, duration > 0f ? duration : 0.01f);
    }
}
