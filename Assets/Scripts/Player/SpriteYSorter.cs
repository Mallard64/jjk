using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Dynamic top-down Y-sorting: the player lower on the screen (smaller world Y) draws in front.
/// Ranks all active players by Y each frame and assigns the main sprite's sorting order so the
/// front-most gets the highest order. Orders stay in a tight band starting at baseSortingOrder, so
/// they never collide with the head bars / aiming reticle which sit at fixed higher orders.
/// </summary>
public class SpriteYSorter : MonoBehaviour
{
    [Tooltip("The character's main SpriteRenderer whose sorting order is driven by Y.")]
    [SerializeField] private SpriteRenderer targetSprite;
    [Tooltip("Order given to the back-most (highest-Y) player; each player in front of it gets +1.")]
    [SerializeField] private int baseSortingOrder = 51;

    private static readonly List<SpriteYSorter> _all = new List<SpriteYSorter>();
    private static readonly System.Comparison<SpriteYSorter> _byDescendingY =
        (a, b) => b.transform.position.y.CompareTo(a.transform.position.y);
    private static int _lastFrame = -1;

    void Awake()
    {
        if (targetSprite == null) targetSprite = GetComponentInChildren<SpriteRenderer>();
    }

    void OnEnable()  => _all.Add(this);
    void OnDisable() => _all.Remove(this);

    void LateUpdate()
    {
        // One global re-rank per frame (the first sorter to tick owns it). Highest Y (back) first,
        // so each player further forward lands at a higher order.
        if (Time.frameCount == _lastFrame) return;
        _lastFrame = Time.frameCount;

        _all.Sort(_byDescendingY);
        for (int i = 0; i < _all.Count; i++)
        {
            var sr = _all[i].targetSprite;
            if (sr != null) sr.sortingOrder = _all[i].baseSortingOrder + i;
        }
    }
}
