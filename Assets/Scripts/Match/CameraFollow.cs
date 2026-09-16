using UnityEngine;

/// <summary>
/// Keeps the camera on the action at a fixed zoom, picking a target in priority order:
///   1. An explicit target set by SetFollowTarget — offline practice uses this to lock onto the human.
///   2. Networked: the local-authority player.
///   3. Offline fallback: the midpoint of both tagged players, which is what bot-vs-bot spectating wants.
/// Orthographic size is constant — a changing zoom scaled tiles to fractional screen sizes, which
/// produced crawling tilemap seams.
/// </summary>
public class CameraFollow : MonoBehaviour
{
    [SerializeField] private string tag1 = "Player1";
    [SerializeField] private string tag2 = "Player2";
    [SerializeField] private float smoothSpeed = 5f;
    [Tooltip("Fixed orthographic size. Constant on purpose so tiles never scale to a fractional screen size.")]
    [SerializeField] private float orthoSize = 6f;

    private Camera _cam;
    private Transform _t1;
    private Transform _t2;
    // The smoothly-following base position. We keep it separate from transform.position so
    // CameraShake's per-frame offset never gets fed back into the Lerp (which would drag the
    // camera toward the shake instead of returning to center after the shake ends).
    private Vector3 _basePosition;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _basePosition = transform.position;
        if (_cam != null && _cam.orthographic) _cam.orthographicSize = orthoSize;
    }

    private Transform _explicitTarget;

    /// <summary>Locks the camera to one fighter. Offline practice-vs-bot sets this to the human so the
    /// view stays on them instead of drifting to the midpoint. Pass null to restore midpoint framing,
    /// which is what bot-vs-bot spectating wants.</summary>
    public void SetFollowTarget(Transform target) => _explicitTarget = target;

    void LateUpdate()
    {
        if (_explicitTarget != null)
        {
            FollowTarget(_explicitTarget.position);
            ApplyShake();
            return;
        }

        // Networked path: FusionPlayerSync registers the local-authority player in Spawned().
        var local = FusionPlayerSync.LocalPlayerTransform;
        if (local != null)
        {
            FollowTarget(local.position);
            ApplyShake();
            return;
        }

        // Offline path: midpoint of two tagged players keeps both fighters framed.
        RefreshTaggedPlayers();
        if (_t1 != null && _t2 != null)
            FollowTarget((_t1.position + _t2.position) / 2f);
        else if (_t1 != null || _t2 != null)
            FollowTarget((_t1 ?? _t2).position);

        ApplyShake();
    }

    void ApplyShake()
    {
        transform.position = _basePosition + (CameraShake.Instance != null ? CameraShake.Instance.Offset : Vector3.zero);
    }

    void RefreshTaggedPlayers()
    {
        if (_t1 == null || _t1.gameObject == null)
        {
            var go = GameObject.FindWithTag(tag1);
            if (go != null) _t1 = go.transform;
        }
        if (_t2 == null || _t2.gameObject == null)
        {
            var go = GameObject.FindWithTag(tag2);
            if (go != null) _t2 = go.transform;
        }
    }

    void FollowTarget(Vector3 targetPos)
    {
        targetPos.z = _basePosition.z;
        _basePosition = Vector3.Lerp(_basePosition, targetPos, smoothSpeed * Time.deltaTime);
    }
}
