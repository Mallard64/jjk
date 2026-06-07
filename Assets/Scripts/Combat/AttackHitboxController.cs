using UnityEngine;

/// <summary>
/// Toggles a Hitbox's collider on/off. Held by AutoAttackController (the "auto attack hitbox")
/// and AimableAttackController (the "aimable attack hitbox"); the active window is owned by the
/// attack-phase coroutine — this controller is a dumb enable/disable wrapper.
/// When isProjectile is on, Enable instead spawns a traveling Projectile and the owned Hitbox is unused.
/// </summary>
public class AttackHitboxController : MonoBehaviour
{
    [SerializeField] private Hitbox hitbox;
    [Tooltip("Distance from the player's center the hitbox sits when oriented.")]
    [SerializeField] private float hitboxOffsetDistance = 0.6f;

    [Header("Projectile")]
    [Tooltip("If true, activating spawns a traveling projectile instead of enabling this hitbox in place. The owned Hitbox is unused in this mode.")]
    [SerializeField] private bool isProjectile = false;
    [Tooltip("Projectile prefab spawned when isProjectile is on. Must have a Projectile component.")]
    [SerializeField] private Projectile projectilePrefab;
    [Tooltip("Travel speed (units/sec) of the spawned projectile. Ignored when the attack is throwable — it just appears at the target with no velocity.")]
    [SerializeField] private float projectileSpeed = 10f;
    [Tooltip("Seconds the spawned projectile lives before it despawns.")]
    [SerializeField] private float projectileDuration = 2f;

    private Collider2D _col;
    private Vector3    _baseScale = Vector3.one;

    // Aim/placement recorded by the most recent Orient/PlaceAt and consumed when a projectile spawns.
    // The attack controllers call one of them once per fire before Enable, so these are current at spawn.
    private Vector2 _aimDir;
    private Vector2 _targetPoint;
    private bool    _placeAtTarget;

    public float OffsetDistance => hitboxOffsetDistance;

    void Awake()
    {
        if (hitbox != null)
        {
            _baseScale = hitbox.transform.localScale;
            _col = hitbox.GetComponent<Collider2D>();
            if (_col != null) _col.enabled = false;
        }
        else if (!isProjectile)
        {
            Debug.LogError($"AttackHitboxController on {gameObject.name}: Hitbox not assigned.");
        }
    }

    /// <summary>Position and rotate the hitbox so its forward axis points along `direction`, offset from the player by hitboxOffsetDistance.</summary>
    public void Orient(Vector2 direction)
    {
        if (direction.sqrMagnitude < 0.0001f) return;
        _aimDir = direction.normalized;
        _placeAtTarget = false;
        if (isProjectile || hitbox == null) return;
        hitbox.transform.localPosition = (Vector3)(_aimDir * hitboxOffsetDistance);
        float angle = Mathf.Atan2(_aimDir.y, _aimDir.x) * Mathf.Rad2Deg;
        hitbox.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
    }

    /// <summary>Drop the hitbox at a world point (throwable attacks), rotated to face `direction`.
    /// Unlike Orient, the hitbox lands on the target instead of a fixed offset from the player.</summary>
    public void PlaceAt(Vector2 worldPoint, Vector2 direction)
    {
        _targetPoint = worldPoint;
        _placeAtTarget = true;
        if (direction.sqrMagnitude >= 0.0001f) _aimDir = direction.normalized;
        if (isProjectile || hitbox == null) return;
        hitbox.transform.position = (Vector3)worldPoint;
        if (direction.sqrMagnitude < 0.0001f) return;
        float angle = Mathf.Atan2(_aimDir.y, _aimDir.x) * Mathf.Rad2Deg;
        hitbox.transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    public void Enable(GameObject owner, float damageMultiplier = 1f, float knockbackMultiplier = 1f, float sizeMultiplier = 1f)
    {
        if (isProjectile)
        {
            SpawnProjectile(owner, damageMultiplier, knockbackMultiplier, sizeMultiplier);
            return;
        }
        if (hitbox == null || _col == null) return;
        hitbox.transform.localScale = _baseScale * (sizeMultiplier > 0f ? sizeMultiplier : 1f);
        hitbox.Init(owner, damageMultiplier, knockbackMultiplier);
        _col.enabled = true;
    }

    public void Disable()
    {
        if (isProjectile) return;  // spawned projectiles self-manage their lifetime
        if (_col != null) _col.enabled = false;
        if (hitbox != null) hitbox.transform.localScale = _baseScale;
    }

    private void SpawnProjectile(GameObject owner, float damageMultiplier, float knockbackMultiplier, float sizeMultiplier)
    {
        if (projectilePrefab == null)
        {
            Debug.LogWarning($"AttackHitboxController on {gameObject.name}: isProjectile is on but no projectilePrefab assigned.");
            return;
        }

        // No aim recorded (a bare-clip attack that calls neither Orient nor PlaceAt): fall back to facing.
        Vector2 dir = _aimDir;
        if (dir.sqrMagnitude < 0.0001f)
        {
            var anim = owner.GetComponent<PlayerAnimationController>();
            dir = anim != null ? anim.CurrentFacing : Vector2.right;
        }

        Vector2 spawnPos = _placeAtTarget ? _targetPoint : (Vector2)owner.transform.position + dir * hitboxOffsetDistance;
        float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        Projectile p = Instantiate(projectilePrefab, (Vector3)spawnPos, Quaternion.Euler(0f, 0f, angle));

        // Throwable just appears at the drop point; a directional shot travels along the aim.
        Vector2 velocity = _placeAtTarget ? Vector2.zero : dir * projectileSpeed;
        p.Launch(owner, velocity, projectileDuration, damageMultiplier, knockbackMultiplier, sizeMultiplier);
    }
}
