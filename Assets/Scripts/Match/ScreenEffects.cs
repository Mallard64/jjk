using UnityEngine;

/// <summary>
/// Camera post-process for damage feedback: a brief full-screen colour flash (tunable colour) and a
/// full-screen colour invert, each fading out over its own duration. Trigger from the victim's local
/// client (see PlayerCombatController) so only the hit player's view reacts. Positional shake lives in
/// CameraShake — this owns colour only. Requires the built-in render pipeline (OnRenderImage).
/// </summary>
[RequireComponent(typeof(Camera))]
public class ScreenEffects : MonoBehaviour
{
    public static ScreenEffects Instance { get; private set; }

    [Tooltip("The Hidden/JJK/ColorDamageEffect shader. Wired in the scene so it survives a build; falls back to Shader.Find if left empty.")]
    [SerializeField] private Shader effectShader;
    [SerializeField] private float flashDuration = 0.25f;
    [Tooltip("Peak tint strength at the start of a flash (0 = none, 1 = fully the flash colour).")]
    [SerializeField, Range(0f, 1f)] private float flashStrength = 0.45f;
    [SerializeField] private float invertDuration = 0.12f;

    [Header("Overdrive Glow")]
    [Tooltip("Peak white wash while overdrive is active — the glow pulses between 0 and this (flashy, not a steady haze).")]
    [SerializeField, Range(0f, 1f)] private float overdriveGlowStrength = 0.4f;
    [Tooltip("Pulse speed of the overdrive glow (higher = faster flashing).")]
    [SerializeField] private float overdriveGlowPulseSpeed = 12f;
    [Tooltip("How fast the glow eases out when overdrive turns off (glow units per second).")]
    [SerializeField] private float overdriveGlowFade = 2.5f;

    private Material _material;
    private Color _flashColor = Color.red;
    private float _flashTimer;
    private float _invertTimer;
    private bool  _glowActive;
    private float _glowAmount;

    private static readonly int FlashColorId  = Shader.PropertyToID("_FlashColor");
    private static readonly int FlashAmountId = Shader.PropertyToID("_FlashAmount");
    private static readonly int InvertId      = Shader.PropertyToID("_Invert");
    private static readonly int GlowId        = Shader.PropertyToID("_Glow");

    void Awake()
    {
        Instance = this;
        var shader = effectShader != null ? effectShader : Shader.Find("Hidden/JJK/ColorDamageEffect");
        if (shader != null)
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        else
            Debug.LogWarning("ScreenEffects: shader 'Hidden/JJK/ColorDamageEffect' not found — screen damage flashes disabled.");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (_material != null) Destroy(_material);
    }

    /// <summary>Flash the screen toward `color`, fading out over flashDuration. Latest call wins the colour.</summary>
    public void Flash(Color color)
    {
        _flashColor = color;
        _flashTimer = Mathf.Max(_flashTimer, flashDuration);
    }

    public void InvertFlash() => _invertTimer = Mathf.Max(_invertTimer, invertDuration);

    /// <summary>Sustained white screen glow while the local player's overdrive is active (eases in/out).</summary>
    public void SetOverdriveGlow(bool active) => _glowActive = active;

    void Update()
    {
        if (_flashTimer  > 0f) _flashTimer  = Mathf.Max(0f, _flashTimer  - Time.deltaTime);
        if (_invertTimer > 0f) _invertTimer = Mathf.Max(0f, _invertTimer - Time.deltaTime);

        // Overdrive: pulse the glow between 0 and peak (flashy) while active; ease out when it stops.
        if (_glowActive)
            _glowAmount = overdriveGlowStrength * (0.5f + 0.5f * Mathf.Sin(Time.time * overdriveGlowPulseSpeed));
        else
            _glowAmount = Mathf.MoveTowards(_glowAmount, 0f, overdriveGlowFade * Time.deltaTime);
    }

    void OnRenderImage(RenderTexture src, RenderTexture dst)
    {
        if (_material == null || (_flashTimer <= 0f && _invertTimer <= 0f && _glowAmount <= 0f))
        {
            Graphics.Blit(src, dst);
            return;
        }

        float flash  = flashDuration  > 0f ? (_flashTimer  / flashDuration) * flashStrength : 0f;
        float invert = invertDuration > 0f ?  _invertTimer / invertDuration                 : 0f;
        _material.SetColor(FlashColorId, _flashColor);
        _material.SetFloat(FlashAmountId, flash);
        _material.SetFloat(InvertId, invert);
        _material.SetFloat(GlowId, _glowAmount);
        Graphics.Blit(src, dst, _material);
    }
}
