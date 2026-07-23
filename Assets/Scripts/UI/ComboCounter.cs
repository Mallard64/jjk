using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// On-screen true-combo readout for reference. Each connecting hit is registered against its attacker
/// (from Hitbox, which runs on the attacker's authority — so online a peer only tallies its own local
/// player's hits, and both players' offline).
///
/// A TRUE combo only grows while the victim is kept in hitstun: a hit that lands while the victim is
/// still staggered from the previous one extends the combo; a hit on a recovered (actable) victim
/// starts a fresh combo at 1. The combo ends — and the number clears — once the victim recovers (with
/// a brief display hold so the final count lingers and any networked hitstun update can settle).
///
/// Self-building (like ScreenBlackout): the singleton spawns itself on the first registered hit, so
/// nothing needs to be placed in the scene. Drawn with OnGUI, matching GameLauncher's debug UI.
/// </summary>
public class ComboCounter : MonoBehaviour
{
    private const float DisplayHold = 0.6f;  // keeps the final count visible briefly after a combo ends

    private static ComboCounter _instance;
    public static ComboCounter Instance => _instance != null ? _instance : Build();

    private class Combo { public int count; public float hold; public PlayerAnimationController victim; public int lastFrame = -1; }

    private readonly Dictionary<GameObject, Combo> _combos  = new Dictionary<GameObject, Combo>();
    private readonly List<GameObject>              _scratch = new List<GameObject>();
    private GUIStyle _style;

    private static ComboCounter Build()
    {
        var go = new GameObject("ComboCounter");
        _instance = go.AddComponent<ComboCounter>();
        return _instance;
    }

    /// <summary>Record a connecting hit by `attacker` on `victim`. `victimWasStunned` is the victim's
    /// hitstun state captured BEFORE this hit landed: true means the combo continues, false restarts it.</summary>
    public void Register(GameObject attacker, PlayerAnimationController victim, bool victimWasStunned)
    {
        if (attacker == null) return;
        if (!_combos.TryGetValue(attacker, out var combo))
        {
            combo = new Combo();
            _combos[attacker] = combo;
        }

        // A victim has two overlapping colliders (body + hurtbox), so one swing can raise two hits in the
        // same frame. Damage is deduped victim-side by i-frames, but this attacker-side tally isn't — so
        // count only the first registration of a given victim per frame.
        if (combo.lastFrame == Time.frameCount && combo.victim == victim) return;

        bool continued = victimWasStunned && combo.victim == victim;
        combo.count     = continued ? combo.count + 1 : 1;
        combo.victim    = victim;
        combo.hold      = DisplayHold;
        combo.lastFrame = Time.frameCount;
    }

    /// <summary>Wipe all live combos (e.g. at an online round reset) without building the singleton.</summary>
    public static void ClearAll() { if (_instance != null) _instance._combos.Clear(); }

    void Update()
    {
        _scratch.Clear();
        foreach (var key in _combos.Keys) _scratch.Add(key);

        foreach (var attacker in _scratch)
        {
            if (attacker == null) { _combos.Remove(attacker); continue; }
            var combo = _combos[attacker];
            if (combo.hold > 0f) combo.hold -= Time.deltaTime;

            // The combo is live while the victim is still staggered; once they recover (and the brief
            // display hold has elapsed) it's over, so the number clears.
            bool victimStunned = combo.victim != null && combo.victim.IsHitstun;
            if (combo.hold <= 0f && !victimStunned) combo.count = 0;
        }
    }

    void OnGUI()
    {
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold };
            _style.normal.textColor = Color.yellow;
        }

        int line = 0;
        foreach (var kv in _combos)
        {
            if (kv.Value.count < 2) continue;  // 1 hit isn't a combo
            GUI.Label(new Rect(24, 24 + line * 38, 400, 44), $"{kv.Value.count}  HIT COMBO", _style);
            line++;
        }
    }
}
