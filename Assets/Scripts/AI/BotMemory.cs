using UnityEngine;

/// <summary>Which of the bot's own attacks a memory entry refers to.</summary>
public enum BotMove { Auto, Aimable }

/// <summary>
/// The bot's adaptation, and deliberately nothing more than two exponential moving averages:
///
///   Offense — "is this move landing?" Each attempt resolves to hit or whiff and nudges that move's
///             land rate, which scales its weight in BotController's move pick. Moves that connect
///             get thrown more.
///   Defense — "where am I getting hurt?" Damage taken is bucketed by how far away the opponent was,
///             and the bot's preferred range drifts toward the band that is hurting it least. Melted
///             in melee, it starts playing the mid-range game; caught by leaps, it closes the gap.
///
/// This is not learning. It does not persist across sessions and it must not be described as ML.
/// Rates are clamped away from zero so a move that has been failing is de-prioritised but still
/// re-tested occasionally — a bot that permanently abandons a move stops looking like a player.
/// </summary>
public class BotMemory
{
    // Range bands, in world units, matched to the prefab's reach: melee lands under ~1.5, the aimable
    // leap covers out to ~4 plus its hitbox half-extent.
    public const float CloseMax = 2f;
    public const float MidMax   = 4.5f;

    private const float MinLandRate = 0.15f;  // never fully abandon a move
    private const float SeedRate    = 0.5f;   // no opinion until it has swung a few times
    // Old reads fade on their own, so a range the bot got punished in early stops dominating a long set.
    private const float PressureDecayPerSecond = 0.05f;

    private readonly float[] _landRate = { SeedRate, SeedRate };
    private readonly float[] _pressure = new float[3];   // indexed by Band()

    // Attempt bookkeeping: one attack is in flight at a time, so a single pending slot is enough.
    private bool    _attemptOpen;
    private BotMove _attemptMove;
    private float   _attemptDeadline;
    private bool    _attemptLanded;

    // A victim carries overlapping body + hurtbox colliders, so one swing can raise two triggers in a
    // frame. ComboCounter dedupes for the same reason; without this a single hit would count twice.
    private int _lastHitFrame = -1;

    // Mirrors the profile's AdaptRate, refreshed every Tick so retuning adaptability takes effect live.
    private float _rate = 0.1f;

    public float LandRate(BotMove move) => _landRate[(int)move];

    /// <summary>Signed adjustment to the bot's preferred range, from which band is hurting it least.
    /// Zero until it has actually taken damage, so a fresh bot uses its profile's spacing unmodified.</summary>
    public float RangeDrift { get; private set; }

    /// <summary>Opens a hit window for an attack that just started. `window` should cover the move's
    /// active frames — ActiveDelay for the auto, ImpactDelay for the aimable, plus slack.</summary>
    public void NoteAttackStarted(BotMove move, float window)
    {
        // An attack starting while one is pending means the last one was interrupted (a hit, a roll
        // cancel). Resolve it as a whiff rather than letting it leak into this attempt's result.
        if (_attemptOpen) CloseAttempt();

        _attemptOpen     = true;
        _attemptMove     = move;
        _attemptLanded   = false;
        _attemptDeadline = Time.time + window;
    }

    /// <summary>The opponent took damage from this bot. Only counts toward the open attempt.</summary>
    public void NoteHitLanded()
    {
        if (!_attemptOpen || Time.frameCount == _lastHitFrame) return;
        _lastHitFrame  = Time.frameCount;
        _attemptLanded = true;
    }

    /// <summary>The bot took damage; `distance` is how far the opponent was at that instant.</summary>
    public void NoteDamageTaken(float amount, float maxHp, float distance)
    {
        if (maxHp <= 0f) return;
        int band = Band(distance);
        _pressure[band] += _rate * (amount / maxHp - _pressure[band]);
        RecomputeDrift();
    }

    /// <summary>Per-frame upkeep: closes an expired hit window and decays stale pressure.</summary>
    public void Tick(float adaptRate, float deltaTime)
    {
        _rate = adaptRate;

        if (_attemptOpen && Time.time >= _attemptDeadline) CloseAttempt();

        float decay = PressureDecayPerSecond * deltaTime;
        for (int i = 0; i < _pressure.Length; i++)
            _pressure[i] = Mathf.MoveTowards(_pressure[i], 0f, decay);
        RecomputeDrift();
    }

    public void Reset()
    {
        _landRate[0] = _landRate[1] = SeedRate;
        for (int i = 0; i < _pressure.Length; i++) _pressure[i] = 0f;
        RangeDrift   = 0f;
        _attemptOpen = false;
    }

    private void CloseAttempt()
    {
        int i = (int)_attemptMove;
        _landRate[i] = Mathf.Clamp(_landRate[i] + _rate * ((_attemptLanded ? 1f : 0f) - _landRate[i]),
                                   MinLandRate, 1f);
        _attemptOpen = false;
    }

    private static int Band(float distance)
        => distance < CloseMax ? 0 : distance < MidMax ? 1 : 2;

    // Drift toward the centre of the least-punished band, relative to the mid band (the neutral
    // spacing the profile already aims for). BotController clamps the result to its real reach.
    private void RecomputeDrift()
    {
        int safest = 0;
        for (int i = 1; i < _pressure.Length; i++)
            if (_pressure[i] < _pressure[safest]) safest = i;

        // Only move if some band is actually hurting — otherwise the profile's spacing stands.
        float worst = 0f;
        for (int i = 0; i < _pressure.Length; i++) worst = Mathf.Max(worst, _pressure[i]);
        if (worst <= 0.001f) { RangeDrift = 0f; return; }

        float target = safest == 0 ? CloseMax * 0.5f
                     : safest == 1 ? (CloseMax + MidMax) * 0.5f
                                   : MidMax + 1f;
        float neutral = (CloseMax + MidMax) * 0.5f;
        // Scaled by how badly the worst band hurts, so a light tap doesn't reposition the whole game.
        RangeDrift = (target - neutral) * Mathf.Clamp01(worst * 4f);
    }

    /// <summary>Compact dump for the debug overlay.</summary>
    public string Describe()
        => $"land  auto {_landRate[0]:0.00}  aimable {_landRate[1]:0.00}\n"
         + $"hurt  near {_pressure[0]:0.00}  mid {_pressure[1]:0.00}  far {_pressure[2]:0.00}\n"
         + $"drift {RangeDrift:+0.00;-0.00; 0.00}";
}
