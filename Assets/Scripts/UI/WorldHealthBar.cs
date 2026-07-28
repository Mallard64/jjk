using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows a player's HP either as a fixed top-left screen UI bar (for the local player in
/// online play) or as a world-space bar floating above the head (for the online opponent,
/// and for both players offline). Drives pre-authored prefab-child objects — no runtime build.
///
/// The view is resolved every frame, not at Awake: Fusion only assigns the NetworkObject in
/// Spawned() (after Awake), so the authority isn't known yet at startup. Both object sets start
/// hidden and the correct one is switched on once the network view resolves (matches the
/// per-frame pattern in PlayerMovement / AimingReticle).
///
/// Authoring (per player prefab):
///  - cornerRoot/cornerFill : Screen Space - Overlay group anchored top-left.
///  - worldRoot/worldFill    : world-space Canvas floating above the head (`offset`).
///  Fill images are Type = Filled (Horizontal, origin Left). A static icon sprite can sit
///  alongside the fill; it is not driven by this script.
/// </summary>
public class WorldHealthBar : MonoBehaviour
{
    [Header("Corner UI (local player, online)")]
    [SerializeField] private GameObject cornerRoot;
    [SerializeField] private Image      cornerFill;

    [Header("World Head Bar (opponent online / both offline)")]
    [SerializeField] private Transform worldRoot;
    [SerializeField] private Image     worldFill;
    [SerializeField] private Vector3   offset = new Vector3(0f, 0.7f, 0f);
    [SerializeField] private bool      hideWhenFull = false;

    [Header("Change Flash")]
    [Tooltip("The fill flashes toward this colour on a discrete HP change, fading back to its normal colour.")]
    [SerializeField] private Color flashColor    = Color.white;
    [SerializeField] private float flashDuration = 0.18f;
    [Tooltip("Minimum HP change to flash — keeps continuous drips (overdrive/domain) from flashing every frame; only real hits/spends do.")]
    [SerializeField] private float flashMinChange = 3f;

    private PlayerHealth     _health;
    private FusionPlayerSync _net;
    private Image            _fill;
    private bool             _world;
    private bool             _resolved;
    private bool             _forceWorld;
    private Color            _fillBaseColor = Color.white;
    private float            _flashTimer;
    private float            _lastValue = float.NaN;

    /// <summary>Pin to the world head bar regardless of network view (used by the training dummy so its
    /// HP never hijacks the local player's corner UI).</summary>
    public void ForceWorldView()
    {
        _forceWorld = true;
        _resolved = false;  // re-resolve to the head bar next frame
    }

    void Awake()
    {
        _health = GetComponentInParent<PlayerHealth>();
        if (_health == null)
        {
            Debug.LogWarning($"WorldHealthBar on {gameObject.name}: no PlayerHealth found in parents.");
            enabled = false;
            return;
        }

        _net = GetComponentInParent<FusionPlayerSync>();
        // Hide both until the network view resolves (online: after Spawned; offline: first frame).
        if (cornerRoot != null) cornerRoot.SetActive(false);
        if (worldRoot  != null) worldRoot.gameObject.SetActive(false);

        _health.OnHealthChanged += HandleHealthChanged;
    }

    void OnDestroy()
    {
        if (_health != null) _health.OnHealthChanged -= HandleHealthChanged;
    }

    void LateUpdate()
    {
        Resolve();
        if (_world && worldRoot != null && _health != null)
            worldRoot.position = _health.transform.position + offset;

        if (_flashTimer > 0f && _fill != null)
        {
            _flashTimer -= Time.deltaTime;
            _fill.color = _flashTimer > 0f
                ? Color.Lerp(_fillBaseColor, flashColor, _flashTimer / flashDuration)
                : _fillBaseColor;
        }
    }

    private void Resolve()
    {
        bool online = _net != null && _net.Object != null && _net.Object.IsValid;
        // Corner UI only for the fighter this peer controls (input authority, not the simulating server);
        // everyone else (and any forced) gets the world head bar.
        bool world = _forceWorld || !(online && _net.IsLocalPlayer);
        if (_resolved && world == _world) return;

        _resolved = true;
        _world = world;
        if (_world)
        {
            if (cornerRoot != null) cornerRoot.SetActive(false);
            _fill = worldFill;
            if (worldRoot != null) worldRoot.gameObject.SetActive(true);
        }
        else
        {
            if (worldRoot != null) worldRoot.gameObject.SetActive(false);
            _fill = cornerFill;
            if (cornerRoot != null) cornerRoot.SetActive(true);
        }

        if (_fill == null)
        {
            Debug.LogWarning($"WorldHealthBar on {gameObject.name}: {(_world ? "worldFill" : "cornerFill")} not assigned.");
        }
        else
        {
            _fillBaseColor = _fill.color;  // capture the authored colour to flash back to
            _flashTimer = 0f;
            HandleHealthChanged(_health.CurrentHp, _health.MaxHp);
        }
    }

    private void HandleHealthChanged(float current, float max)
    {
        if (_fill == null) return;

        // Flash on a discrete change (hit / spend), not continuous drain, and not the initial bind.
        if (!float.IsNaN(_lastValue) && Mathf.Abs(current - _lastValue) >= flashMinChange)
            _flashTimer = flashDuration;
        _lastValue = current;

        float ratio = max > 0f ? Mathf.Clamp01(current / max) : 0f;
        _fill.fillAmount = ratio;
        // Only the floating head bar auto-hides; the corner UI is permanent.
        if (_world && worldRoot != null)
            worldRoot.gameObject.SetActive(!(hideWhenFull && ratio >= 1f));
    }
}
