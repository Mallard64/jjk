using UnityEngine;

/// <summary>
/// Client-side render smoothing for the OPPONENT (proxy). This Fusion build's NetworkTransform can only
/// interpolate the root, which fights the authority's physics-driven movement — so interpolation is left
/// off and proxies snap to each received tick, visibly stepping when the render rate outpaces the send
/// rate. This eases the whole proxy root toward its networked position every frame, so the entire
/// character (sprite, shadow, bars, collider) glides together — no per-part desync.
///
/// Strictly proxy-only: on the local authority and offline it does nothing, so it can never affect the
/// local player's responsiveness. It also adds no network traffic (pure local render smoothing). With
/// near-zero RTT (e.g. two windows on one PC) a tiny smoothTime looks smooth with negligible delay.
/// </summary>
public class ProxyRootSmoother : MonoBehaviour
{
    [Tooltip("Easing time, seconds. Smaller = crisper but a touch choppier; larger = smoother but slightly behind.")]
    [SerializeField] private float smoothTime   = 0.05f;
    [Tooltip("If the root jumps further than this in one frame (respawn / teleport), snap instead of sliding.")]
    [SerializeField] private float snapDistance = 2f;

    private FusionPlayerSync _net;
    private Rigidbody2D      _rb;
    private Vector3          _shown;
    private Vector3          _velocity;
    private bool             _tracking;

    void Awake()
    {
        _net = GetComponent<FusionPlayerSync>();
        _rb  = GetComponent<Rigidbody2D>();
    }

    void LateUpdate()
    {
        bool isProxy = _net != null && _net.Object != null && _net.Object.IsValid && !_net.IsAuthority;
        if (!isProxy) { _tracking = false; return; }

        // NetworkTransform rewrites the proxy's position every Render (this frame's tick target). Reading
        // it here — after that write, before the frame is drawn — gives the true target regardless of our
        // own previous-frame write.
        Vector3 target = transform.position;

        if (!_tracking)
        {
            // Drop Unity's rigidbody interpolation on the proxy so it doesn't compete with ours.
            if (_rb != null) _rb.interpolation = RigidbodyInterpolation2D.None;
            _shown    = target;
            _velocity = Vector3.zero;
            _tracking = true;
        }
        else if ((target - _shown).sqrMagnitude > snapDistance * snapDistance)
        {
            _shown    = target;   // respawn / teleport — snap, don't slide across the arena
            _velocity = Vector3.zero;
        }
        else
        {
            _shown = Vector3.SmoothDamp(_shown, target, ref _velocity, smoothTime);
        }

        transform.position = _shown;
    }
}
