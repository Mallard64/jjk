using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Full-screen black overlay used for the death → respawn transition. Builds its own top-most
/// Screen Space - Overlay canvas on first access, so nothing needs to be wired in the scene.
/// Driven by MatchManager: fade to black, hold while the arena resets, fade back in.
/// </summary>
public class ScreenBlackout : MonoBehaviour
{
    private static ScreenBlackout _instance;
    public static ScreenBlackout Instance => _instance != null ? _instance : Build();

    private CanvasGroup _group;
    private Image       _image;

    private static ScreenBlackout Build()
    {
        var go = new GameObject("ScreenBlackout");
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue; // above every other UI
        go.AddComponent<CanvasScaler>();

        var group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable   = false;

        var imageGo = new GameObject("Black");
        imageGo.transform.SetParent(go.transform, false);
        var image = imageGo.AddComponent<Image>();
        image.color = Color.black;
        image.raycastTarget = false;
        var rt = image.rectTransform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        _instance = go.AddComponent<ScreenBlackout>();
        _instance._group = group;
        _instance._image = image;
        return _instance;
    }

    /// <summary>Set the overlay colour (RGB only; alpha stays driven by FadeTo), then fade as usual.
    /// Used for the online round transition, which fades to white instead of black.</summary>
    public IEnumerator FadeTo(float target, float duration, Color color)
    {
        if (_image != null)
        {
            var c = _image.color;
            c.r = color.r; c.g = color.g; c.b = color.b;
            _image.color = c;
        }
        yield return FadeTo(target, duration);
    }

    /// <summary>Snap the overlay alpha instantly (0 = clear). Used to clear a leftover fade between matches.</summary>
    public void SetAlpha(float alpha) { if (_group != null) _group.alpha = alpha; }

    /// <summary>Lerp the overlay alpha to `target` (0 = clear, 1 = black) over `duration` seconds.</summary>
    public IEnumerator FadeTo(float target, float duration)
    {
        if (_group == null) yield break;
        float start = _group.alpha;
        if (duration <= 0f) { _group.alpha = target; yield break; }

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            _group.alpha = Mathf.Lerp(start, target, t / duration);
            yield return null;
        }
        _group.alpha = target;
    }
}
