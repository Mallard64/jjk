using UnityEngine;

/// <summary>
/// Online-only match presentation: the running best-of-5 round score plus the "round end", "victory"
/// and "defeat" result screens. Drop one on a scene object in the Arena; FusionPlayerSync drives it
/// through the singleton.
///
/// Each result screen is a GameObject you wire in and it's shown/hidden via SetActive — give it its own
/// Animator (or sprites/particles) and it plays when shown. If a screen's GameObject is left unassigned,
/// a placeholder text banner is drawn in its place (OnGUI), so the flow is visible before art is wired.
/// The running round score is drawn with OnGUI too, matching ComboCounter / GameLauncher's debug UI.
/// </summary>
public class NetworkMatchUI : MonoBehaviour
{
    public static NetworkMatchUI Instance { get; private set; }

    [Header("Result screens (your art — each shown via SetActive; leave empty for placeholder text)")]
    [Tooltip("Shown at the end of every round on both peers.")]
    [SerializeField] private GameObject roundEndScreen;
    [Tooltip("Shown to the peer that won the match.")]
    [SerializeField] private GameObject victoryScreen;
    [Tooltip("Shown to the peer that lost the match.")]
    [SerializeField] private GameObject defeatScreen;

    [Header("Score HUD")]
    [SerializeField] private bool showScore = true;

    private enum Banner { None, RoundEnd, Victory, Defeat }
    private Banner _banner = Banner.None;

    private GUIStyle _scoreStyle;
    private GUIStyle _bannerStyle;

    void Awake()
    {
        Instance = this;
        HideAll();
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    public void ShowRoundEnd() { _banner = Banner.RoundEnd; Apply(); }
    public void ShowVictory()  { _banner = Banner.Victory;  Apply(); }
    public void ShowDefeat()   { _banner = Banner.Defeat;   Apply(); }
    public void HideRoundEnd() { if (_banner == Banner.RoundEnd) { _banner = Banner.None; Apply(); } }
    public void HideAll()      { _banner = Banner.None; Apply(); }

    private void Apply()
    {
        if (roundEndScreen != null) roundEndScreen.SetActive(_banner == Banner.RoundEnd);
        if (victoryScreen  != null) victoryScreen.SetActive(_banner == Banner.Victory);
        if (defeatScreen   != null) defeatScreen.SetActive(_banner == Banner.Defeat);
    }

    void OnGUI()
    {
        if (showScore && FusionPlayerSync.MatchActive)
        {
            if (_scoreStyle == null)
            {
                _scoreStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperCenter
                };
                _scoreStyle.normal.textColor = Color.white;
            }
            string score = $"{FusionPlayerSync.LocalRoundWins}  -  {FusionPlayerSync.OpponentRoundWins}";
            GUI.Label(new Rect(Screen.width / 2f - 150, 16, 300, 48), score, _scoreStyle);
        }

        // Placeholder banner — only when the matching art GameObject wasn't wired.
        string text = null; GameObject art = null;
        switch (_banner)
        {
            case Banner.RoundEnd: text = "ROUND END"; art = roundEndScreen; break;
            case Banner.Victory:  text = "VICTORY";   art = victoryScreen;  break;
            case Banner.Defeat:   text = "DEFEAT";    art = defeatScreen;   break;
        }
        if (text != null && art == null)
        {
            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 72, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter
                };
                _bannerStyle.normal.textColor = Color.black;
            }
            GUI.Label(new Rect(0, Screen.height / 2f - 60, Screen.width, 120), text, _bannerStyle);
        }
    }
}
