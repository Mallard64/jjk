using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Displays match-level status: start message, winner announcement, controls hint.
/// </summary>
public class MatchUI : MonoBehaviour
{
    public static MatchUI Instance { get; private set; }

    [SerializeField] private TextMeshProUGUI statusText;
    [SerializeField] private TextMeshProUGUI controlsHint;

    void Awake()
    {
        Instance = this;
    }

    void Start()
    {
        if (controlsHint != null)
            controlsHint.text = "P1: WASD + Space(auto) + E(skill) + Q(domain)\nP2: Arrows + Num1(auto) + Num2(skill) + Num3(domain)";

        if (statusText != null) statusText.text = "";
    }

    public void ShowMatchStart()
    {
        StartCoroutine(ShowTemporary("FIGHT!", 1.5f));
    }

    private IEnumerator ShowTemporary(string message, float duration)
    {
        if (statusText != null) statusText.text = message;
        yield return new WaitForSeconds(duration);
        if (statusText != null) statusText.text = "";
    }
}
