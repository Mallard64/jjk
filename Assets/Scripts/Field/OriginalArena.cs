using UnityEngine;

/// <summary>
/// Marks the scene's normal arena/tilemap so a Null Field can hide it while a null field is open
/// (and reveal it during the collapse flicker). Add this to the normal tilemap's GameObject (or its
/// Grid). It only registers a static reference — the null field toggles the GameObject's active state.
/// </summary>
public class OriginalArena : MonoBehaviour
{
    public static GameObject Instance { get; private set; }

    void Awake() => Instance = gameObject;          // set once; survives SetActive(false)
    void OnDestroy() { if (Instance == gameObject) Instance = null; }
}
