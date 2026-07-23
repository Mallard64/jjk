using UnityEngine;

/// <summary>
/// Destroys this GameObject after `lifetime` seconds. Put it on a one-shot VFX prefab (e.g. the domain
/// activation effect) so it plays its animation and cleans itself up. Set lifetime to the clip length.
/// </summary>
public class TimedSelfDestruct : MonoBehaviour
{
    [Tooltip("Seconds before this object destroys itself (match the animation length).")]
    [SerializeField] private float lifetime = 1f;

    void Start() => Destroy(gameObject, lifetime);
}
