using UnityEngine;

/// <summary>
/// World-space rectangle that follows the local player's aim direction, indicating the
/// projected attack range. Self-builds its sprite at runtime — just drop on the player prefab.
/// Switches between autoSize and aimableSize based on AimableAttackController.IsAiming, so the
/// player can see the aimable's reach the moment they hold Q.
/// Hidden for proxies in online play; visible for both players in offline.
/// </summary>
public class AimingReticle : MonoBehaviour
{
    [Header("Auto Attack Reticle")]
    [Tooltip("Length (along aim) and width (perpendicular) in world units. Matches the auto attack's reach.")]
    [SerializeField] private Vector2 autoSize = new Vector2(1.2f, 0.4f);
    [Tooltip("Extra offset along the aim before the auto reticle starts.")]
    [SerializeField] private float   autoStartOffset = 0f;
    [SerializeField] private Color   autoColor = new Color(1f, 0.85f, 0.2f, 0.32f);

    [Header("Aimable Attack Reticle (while Q held)")]
    [Tooltip("Length (along aim) and width (perpendicular) in world units. Matches the aimable attack's reach.")]
    [SerializeField] private Vector2 aimableSize = new Vector2(3.0f, 0.5f);
    [Tooltip("Extra offset along the aim before the aimable reticle starts.")]
    [SerializeField] private float   aimableStartOffset = 0f;
    [SerializeField] private Color   aimableColor = new Color(0.4f, 0.8f, 1f, 0.32f);

    [Header("Throwable Marker (when the active attack is throwable)")]
    [Tooltip("Diameter in world units of the circle drawn at the throw target.")]
    [SerializeField] private float throwMarkerSize  = 1.2f;
    [SerializeField] private Color throwMarkerColor = new Color(1f, 0.4f, 0.3f, 0.4f);

    [Header("Anchor")]
    [Tooltip("World-space offset from the player's transform that the reticle rotates around (e.g. (0, 0.3) to emerge from the chest instead of the feet).")]
    [SerializeField] private Vector2 anchorOffset = Vector2.zero;

    [Header("Rendering")]
    [SerializeField] private string sortingLayer = "Default";
    [SerializeField] private int    sortingOrder = 50;

    private static Sprite _sharedSprite;
    private static Sprite _sharedCircleSprite;

    private FusionPlayerSync        _net;
    private PlayerInputHandler      _input;
    private AutoAttackController    _auto;
    private AimableAttackController _aimable;
    private Transform               _reticle;
    private SpriteRenderer          _sr;

    void Awake()
    {
        _net     = GetComponent<FusionPlayerSync>();
        _input   = GetComponent<PlayerInputHandler>();
        _auto    = GetComponent<AutoAttackController>();
        _aimable = GetComponent<AimableAttackController>();
        BuildReticle();
    }

    void OnDestroy()
    {
        if (_reticle != null) Destroy(_reticle.gameObject);
    }

    void LateUpdate()
    {
        if (_reticle == null) return;

        if (!IsLocalView())
        {
            if (_reticle.gameObject.activeSelf) _reticle.gameObject.SetActive(false);
            return;
        }

        Vector2 aim = ResolveAimDir();
        if (aim.sqrMagnitude < 0.0001f)
        {
            if (_reticle.gameObject.activeSelf) _reticle.gameObject.SetActive(false);
            return;
        }

        if (!_reticle.gameObject.activeSelf) _reticle.gameObject.SetActive(true);

        // The aimable governs the reticle while aiming (Q held); otherwise the auto attack does.
        bool aiming = _aimable != null && _aimable.IsAiming;
        if (aiming ? (_aimable != null && _aimable.Throwable) : (_auto != null && _auto.Throwable))
            DrawThrowMarker(aiming);
        else
            DrawDirectionalReticle(aim, aiming);
    }

    private void DrawDirectionalReticle(Vector2 aim, bool aiming)
    {
        if (_sr != null && _sr.sprite != _sharedSprite) _sr.sprite = _sharedSprite;

        Vector2 size      = aiming ? aimableSize        : autoSize;
        float   offset    = aiming ? aimableStartOffset : autoStartOffset;
        Color   color     = aiming ? aimableColor       : autoColor;
        if (_sr != null && _sr.color != color) _sr.color = color;

        Vector2 n = aim.normalized;
        float angle = Mathf.Atan2(n.y, n.x) * Mathf.Rad2Deg;
        float centerDist = offset + size.x * 0.5f;
        Vector3 anchor = transform.position + (Vector3)anchorOffset;
        _reticle.position = anchor + (Vector3)(n * centerDist);
        _reticle.rotation = Quaternion.Euler(0f, 0f, angle);
        _reticle.localScale = new Vector3(size.x, size.y, 1f);
    }

    private void DrawThrowMarker(bool aiming)
    {
        if (_sr != null && _sr.sprite != GetCircleSprite()) _sr.sprite = GetCircleSprite();
        if (_sr != null && _sr.color != throwMarkerColor) _sr.color = throwMarkerColor;

        float radius = aiming ? _aimable.ThrowRadius : _auto.ThrowRadius;
        Vector2 from = transform.position;
        Vector2 d = ResolveAimPoint() - from;
        if (d.sqrMagnitude > radius * radius) d = d.normalized * radius;

        _reticle.position = from + d;
        _reticle.rotation = Quaternion.identity;
        _reticle.localScale = new Vector3(throwMarkerSize, throwMarkerSize, 1f);
    }

    private bool IsLocalView()
    {
        // Online: only show on the authority's machine for their own player.
        if (_net != null && _net.Object != null && _net.Object.IsValid)
            return _net.IsAuthority;
        // Offline: PlayerInputHandler exists per-player, both are on the same machine.
        return _input != null;
    }

    private Vector2 ResolveAimDir()
    {
        // Online local: derive from mouse, anchored at the offset point so the reticle
        // rotates exactly around its visible base instead of the player's feet.
        if (_net != null && _net.Object != null && _net.Object.IsValid && _net.IsAuthority)
        {
            if (Camera.main == null) return Vector2.right;
            Vector3 mouse = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            mouse.z = 0f;
            Vector3 anchor = transform.position + (Vector3)anchorOffset;
            return ((Vector2)(mouse - anchor));
        }
        // Offline: use whatever the PlayerInputHandler computed (mouse for P1, toward P1 for P2).
        if (_input != null) return _input.Current.AimDir;
        return Vector2.right;
    }

    private Vector2 ResolveAimPoint()
    {
        if (_net != null && _net.Object != null && _net.Object.IsValid && _net.IsAuthority)
            return FusionPlayerSync.GetMouseWorldPoint(transform);
        if (_input != null) return _input.Current.AimPoint;
        return transform.position;
    }

    private void BuildReticle()
    {
        var sprite = GetSharedSprite();
        var go = new GameObject($"{gameObject.name}_AimReticle");
        _reticle = go.transform;
        _sr = go.AddComponent<SpriteRenderer>();
        _sr.sprite = sprite;
        _sr.color = autoColor;
        _sr.sortingLayerName = sortingLayer;
        _sr.sortingOrder = sortingOrder;
        _reticle.gameObject.SetActive(false);
    }

    private static Sprite GetSharedSprite()
    {
        if (_sharedSprite != null) return _sharedSprite;
        // Point-filtered 1x1 white texture with PPU=1 so the natural sprite is 1 world unit and
        // the reticle's actual size is driven by the serialized size fields via localScale.
        // Pivot at (0.5, 0.5) so scale = (length, width) draws a centered rectangle that we offset
        // along the aim axis by length/2. Point filter keeps edges crisp at the pixel-art scale.
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, Color.white);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        _sharedSprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        return _sharedSprite;
    }

    // Soft filled disc on a transparent square, PPU = resolution so the natural sprite is 1 world unit;
    // the marker's actual size is driven by throwMarkerSize via localScale (matches the rect sprite's pattern).
    private static Sprite GetCircleSprite()
    {
        if (_sharedCircleSprite != null) return _sharedCircleSprite;
        const int res = 64;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
        float half = res / 2f;
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(half, half)) / half;
            // Opaque core, feathered edge, fully transparent outside the disc.
            float a = dist >= 1f ? 0f : Mathf.SmoothStep(1f, 0f, Mathf.InverseLerp(0.8f, 1f, dist));
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        _sharedCircleSprite = Sprite.Create(tex, new Rect(0f, 0f, res, res), new Vector2(0.5f, 0.5f), res);
        return _sharedCircleSprite;
    }
}
