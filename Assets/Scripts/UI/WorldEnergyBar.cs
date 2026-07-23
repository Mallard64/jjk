using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shows a player's cursed energy. For the local player online it's a fixed top-left screen
/// UI bar; offline (both players local) it's a world-space bar above the head. For an online
/// opponent it is hidden entirely — you never see the enemy's cursed energy. Drives
/// pre-authored prefab-child objects; see WorldHealthBar for the authoring contract and why
/// the view is resolved every frame rather than at Awake (Fusion's Spawned() timing).
///
/// A static icon sprite can sit alongside the fill; it is not driven by this script.
/// </summary>
public class WorldEnergyBar : MonoBehaviour
{
    private enum View { Unresolved, Corner, World, Hidden }

    [Header("Corner UI (local player, online)")]
    [SerializeField] private GameObject cornerRoot;
    [SerializeField] private Image      cornerFill;

    [Header("World Head Bar (offline)")]
    [SerializeField] private Transform worldRoot;
    [SerializeField] private Image     worldFill;
    [SerializeField] private Vector3   offset = new Vector3(0f, 0.88f, 0f);
    [SerializeField] private bool      hideWhenFull = false;

    [Header("Change Flash")]
    [Tooltip("The fill flashes toward this colour on a discrete CE change, fading back to its normal colour.")]
    [SerializeField] private Color flashColor    = Color.white;
    [SerializeField] private float flashDuration = 0.18f;
    [Tooltip("Minimum CE change to flash — keeps continuous drain (domain/overdrive) and regen from flashing every frame; only real spends do.")]
    [SerializeField] private float flashMinChange = 3f;

    private CursedEnergy     _energy;
    private FusionPlayerSync _net;
    private Image            _fill;
    private View             _view = View.Unresolved;
    private bool             _forceWorld;
    private Color            _fillBaseColor = Color.white;
    private float            _flashTimer;
    private float            _lastValue = float.NaN;

    /// <summary>Pin to the world head bar regardless of network view (used by the training dummy).</summary>
    public void ForceWorldView()
    {
        _forceWorld = true;
        _view = View.Unresolved;  // re-resolve to the head bar next frame
    }

    void Awake()
    {
        _energy = GetComponentInParent<CursedEnergy>();
        if (_energy == null)
        {
            Debug.LogWarning($"WorldEnergyBar on {gameObject.name}: no CursedEnergy found in parents.");
            enabled = false;
            return;
        }

        _net = GetComponentInParent<FusionPlayerSync>();
        // Hide both until the network view resolves (online: after Spawned; offline: first frame).
        if (cornerRoot != null) cornerRoot.SetActive(false);
        if (worldRoot  != null) worldRoot.gameObject.SetActive(false);

        _energy.OnEnergyChanged += HandleEnergyChanged;
    }

    void OnDestroy()
    {
        if (_energy != null) _energy.OnEnergyChanged -= HandleEnergyChanged;
    }

    void LateUpdate()
    {
        Resolve();
        if (_view == View.World && worldRoot != null && _energy != null)
            worldRoot.position = _energy.transform.position + offset;

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
        View desired;
        if (_forceWorld) desired = View.World;                              // training dummy: always head bar
        else if (online) desired = _net.IsAuthority ? View.Corner : View.Hidden; // opponent: never reveal CE
        else             desired = View.World;                             // offline: both players local
        if (desired == _view) return;

        _view = desired;
        if (cornerRoot != null) cornerRoot.SetActive(_view == View.Corner);
        if (worldRoot  != null) worldRoot.gameObject.SetActive(_view == View.World);
        _fill = _view == View.Corner ? cornerFill : _view == View.World ? worldFill : null;

        if (_view != View.Hidden)
        {
            if (_fill == null)
            {
                Debug.LogWarning($"WorldEnergyBar on {gameObject.name}: {(_view == View.Corner ? "cornerFill" : "worldFill")} not assigned.");
            }
            else
            {
                _fillBaseColor = _fill.color;  // capture the authored colour to flash back to
                _flashTimer = 0f;
                HandleEnergyChanged(_energy.CurrentEnergy, _energy.MaxEnergy);
            }
        }
    }

    private void HandleEnergyChanged(float current, float max)
    {
        if (_fill == null) return;

        // Flash on a discrete change (spend), not continuous drain/regen, and not the initial bind.
        if (!float.IsNaN(_lastValue) && Mathf.Abs(current - _lastValue) >= flashMinChange)
            _flashTimer = flashDuration;
        _lastValue = current;

        float ratio = max > 0f ? Mathf.Clamp01(current / max) : 0f;
        _fill.fillAmount = ratio;
        if (_view == View.World && worldRoot != null)
            worldRoot.gameObject.SetActive(!(hideWhenFull && ratio >= 1f));
    }
}
