using UnityEngine;

/// <summary>
/// Per-camera shake offset that CameraFollow reads when computing target position.
/// Trigger via CameraShake.Instance?.Shake() from the victim's local client so only the
/// hit player's view shakes.
/// </summary>
public class CameraShake : MonoBehaviour
{
    public static CameraShake Instance { get; private set; }

    [SerializeField] private float defaultDuration  = 0.18f;
    [SerializeField] private float defaultMagnitude = 0.25f;

    private float _timer;
    private float _magnitude;

    public Vector3 Offset { get; private set; }

    void Awake() { Instance = this; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    public void Shake() => Shake(defaultDuration, defaultMagnitude);

    public void Shake(float duration, float magnitude)
    {
        _timer     = Mathf.Max(_timer,     duration);
        _magnitude = Mathf.Max(_magnitude, magnitude);
    }

    // Use Update so the offset is computed before CameraFollow's LateUpdate reads it.
    void Update()
    {
        if (_timer > 0f)
        {
            _timer -= Time.deltaTime;
            Vector2 r = Random.insideUnitCircle * _magnitude;
            Offset = new Vector3(r.x, r.y, 0f);
        }
        else
        {
            Offset = Vector3.zero;
            _magnitude = 0f;
        }
    }
}
