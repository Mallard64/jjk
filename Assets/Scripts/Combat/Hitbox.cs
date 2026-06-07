using UnityEngine;

/// <summary>
/// Attach to a trigger collider child object. Enable/disable the GameObject to activate the hitbox window.
/// Calls TakeDamage on any Hurtbox it overlaps.
/// </summary>
public class Hitbox : MonoBehaviour
{
    [SerializeField] private float damage = 15f;
    [SerializeField] private float knockbackForce = 3f;
    [Tooltip("Seconds the victim is locked in hitstun by this hit. Floored to the hurt clip length on the victim.")]
    [SerializeField] private float hitstun = 0.35f;

    private GameObject _owner;
    private float _damageMultiplier = 1f;
    private float _knockbackMultiplier = 1f;

    public void Init(GameObject owner, float damageMultiplier = 1f, float knockbackMultiplier = 1f)
    {
        _owner = owner;
        _damageMultiplier = damageMultiplier > 0f ? damageMultiplier : 1f;
        _knockbackMultiplier = knockbackMultiplier > 0f ? knockbackMultiplier : 1f;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        // In online play, only the state authority applies damage to prevent double hits
        if (_owner != null)
        {
            var adapter = _owner.GetComponent<INetworkAdapter>();
            if (adapter != null && !adapter.IsAuthority) return;
        }

        if (_owner != null && other.transform.IsChildOf(_owner.transform)) return;

        var victimRoot = other.transform.root;
        var hurtbox = victimRoot.GetComponentInChildren<Hurtbox>(true);
        if (hurtbox == null)
        {
            Debug.Log($"[Hitbox] No Hurtbox found on {victimRoot.name}");
            return;
        }

        // Capture the victim's combo-relevant state BEFORE the hit lands (offline applies hurt
        // synchronously inside ReceiveHit, which would otherwise flip IsHitstun for this very hit).
        var victimAnim     = victimRoot.GetComponentInChildren<PlayerAnimationController>(true);
        var victimHealth   = victimRoot.GetComponentInChildren<PlayerHealth>(true);
        bool victimStunned = victimAnim != null && victimAnim.IsHitstun;
        bool connects      = victimHealth == null || (!victimHealth.IsDead && !victimHealth.IsInvincible);
        // A training dummy tracks its own combo + damage in its detailed readout; keep its hits out
        // of the global corner combo counter.
        bool isDummy       = victimRoot.GetComponentInChildren<TrainingDummy>(true) != null;

        float dealt = damage * _damageMultiplier;
        Vector2 knockbackDir = (other.transform.position - transform.position).normalized;
        hurtbox.ReceiveHit(dealt, knockbackDir * knockbackForce * _knockbackMultiplier, _owner, hitstun);

        if (connects && !isDummy) ComboCounter.Instance.Register(_owner, victimAnim, victimStunned);
    }
}
