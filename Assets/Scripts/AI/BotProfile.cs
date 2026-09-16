using System;
using UnityEngine;

/// <summary>
/// Named playstyle for a bot. Presets fill the trait vector; Custom keeps whatever is in the Inspector;
/// Random rolls a fresh opponent every run. Omnipotent is the boss tier — it cheats by design (see
/// BotProfile.Omniscient and BotController's perception layer), so it is not evidence the heuristics
/// are strong.
/// </summary>
public enum BotPreset { Random, Custom, Rushdown, Zoner, Turtle, Wildcard, Omnipotent }

/// <summary>
/// The playstyle dials for a bot, and the only place the 0–1 traits are turned into real numbers.
/// BotController reads the derived properties and never the raw traits, so retuning a personality
/// means editing the Lerp endpoints here and nothing else.
///
/// Every endpoint below is calibrated against the shipped player prefab, whose economy is the real
/// constraint: resonance regenerates at 5/s, a roll costs 15, an aimable 20, a null field 40, and
/// overdrive burns 30 RES/s before it starts eating 20 HP/s. The auto attack is the only free move.
/// Several traits are, in practice, "how does this bot spend its meter".
/// </summary>
[Serializable]
public class BotProfile
{
    [Tooltip("Random rolls every trait at Awake. Custom uses the sliders below verbatim. Any other preset overwrites them at runtime (the asset on disk is untouched).")]
    [SerializeField] private BotPreset preset = BotPreset.Random;

    [Header("Traits")]
    [Tooltip("How badly it wants to be in your face: pulls the preferred range in and raises attack frequency.")]
    [Range(0f, 1f)] [SerializeField] private float aggression   = 0.5f;
    [Tooltip("How well its attacks are placed: less aim scatter, better leading of a moving target.")]
    [Range(0f, 1f)] [SerializeField] private float accuracy     = 0.5f;
    [Tooltip("How fast it notices and responds. Decides what it can actually dodge — only a high value reacts to the auto attack's short startup.")]
    [Range(0f, 1f)] [SerializeField] private float reaction     = 0.5f;
    [Tooltip("How little it wastes. High = waits for openings and holds meter; low = mashes, whiffs from out of range, runs itself out of RES.")]
    [Range(0f, 1f)] [SerializeField] private float discipline   = 0.5f;
    [Tooltip("How readily it rolls away from a telegraphed attack. Also pushes its spacing out.")]
    [Range(0f, 1f)] [SerializeField] private float defense      = 0.5f;
    [Tooltip("How fast BotMemory shifts. Deliberately capped low — the bot is meant to drift, not to learn.")]
    [Range(0f, 1f)] [SerializeField] private float adaptability = 0.5f;
    [Tooltip("Appetite for spending HP and meter: how deep into overdrive it rides, how eagerly it opens a null field.")]
    [Range(0f, 1f)] [SerializeField] private float recklessness = 0.5f;
    [Tooltip("How unreadable its movement is: faster strafe flips and wider heading jitter.")]
    [Range(0f, 1f)] [SerializeField] private float erraticism   = 0.4f;

    [Header("Moveset")]
    [Tooltip("Auto attack and roll are always available — they are the core of the spacing loop.")]
    [SerializeField] private bool useAimable   = true;
    [SerializeField] private bool useOverdrive = true;
    [SerializeField] private bool useNullField    = true;

    // Set by the Omnipotent preset. Everything gated on it is documented as cheating.
    private bool _omniscient;

    public bool UseAimable    => useAimable;
    public bool UseOverdrive  => useOverdrive;
    public bool UseNullField     => useNullField;
    public BotPreset Preset   => preset;

    /// <summary>Perfect information: reads the opponent's aim point, i-frames, cooldowns and — if the
    /// opponent is also a bot — its trait vector and land-rate weights. Omnipotent only.</summary>
    public bool Omniscient    => _omniscient;

    /// <summary>Roll, brief pause, then attack out of the dash. Omnipotent only; see BotController.</summary>
    public bool UseRollCancel => _omniscient;

    /// <summary>Applies the preset. Call once, from Awake.</summary>
    public void Resolve()
    {
        _omniscient = preset == BotPreset.Omnipotent;

        switch (preset)
        {
            case BotPreset.Custom:                                                                  return;
            case BotPreset.Random:     RollRandom();                                                return;
            //                             aggr   accu   reac   disc   def    adapt  reck   erra
            case BotPreset.Rushdown:   Set(0.90f, 0.55f, 0.75f, 0.35f, 0.20f, 0.50f, 0.80f, 0.35f); return;
            case BotPreset.Zoner:      Set(0.15f, 0.85f, 0.60f, 0.75f, 0.65f, 0.60f, 0.30f, 0.30f); return;
            case BotPreset.Turtle:     Set(0.25f, 0.60f, 0.55f, 0.90f, 0.90f, 0.40f, 0.10f, 0.20f); return;
            case BotPreset.Wildcard:   Set(0.70f, 0.35f, 0.45f, 0.10f, 0.30f, 0.30f, 0.95f, 0.75f); return;
            case BotPreset.Omnipotent: Set(0.85f, 1.00f, 1.00f, 0.95f, 0.75f, 0.85f, 0.55f, 1.00f); return;
        }
    }

    // Never rolls the extremes: a 0.0 bot is inert and a 1.0 bot is the boss tier by accident.
    private void RollRandom()
    {
        aggression   = UnityEngine.Random.Range(0.25f, 0.90f);
        accuracy     = UnityEngine.Random.Range(0.25f, 0.90f);
        reaction     = UnityEngine.Random.Range(0.25f, 0.90f);
        discipline   = UnityEngine.Random.Range(0.25f, 0.90f);
        defense      = UnityEngine.Random.Range(0.25f, 0.90f);
        adaptability = UnityEngine.Random.Range(0.25f, 0.90f);
        recklessness = UnityEngine.Random.Range(0.25f, 0.90f);
        // Narrower than the rest: high erraticism is the one trait that reads as a bug rather than a
        // personality, so a randomly-rolled opponent never lands at the top of its range.
        erraticism   = UnityEngine.Random.Range(0.15f, 0.60f);
    }

    private void Set(float aggr, float accu, float reac, float disc, float def, float adapt, float reck, float erra)
    {
        aggression   = aggr;
        accuracy     = accu;
        reaction     = reac;
        discipline   = disc;
        defense      = def;
        adaptability = adapt;
        recklessness = reck;
        erraticism   = erra;
    }

    // ── Derived values ────────────────────────────────────────────────────
    // Spacing. At aggression 0 it sits just inside its own leap range and outside the opponent's
    // melee reach; at 1 it parks at melee range. Defense then pushes that back out.
    public float PreferredRangeBase(float autoRange, float aimableRange)
        => Mathf.Lerp(aimableRange * 0.9f, autoRange, aggression);
    public float SpacingBias        => Mathf.Lerp(0f, 0.8f, defense);
    public float AttackWeightScale  => Mathf.Lerp(0.6f, 1.6f, aggression);
    public bool  ReengageAfterWhiff => aggression > 0.6f;

    /// <summary>Seconds between commitment windows, and how long each lasts. Holding a preferred range
    /// is a STABLE equilibrium — radial error settles at zero and only the orbit survives, so two bots
    /// circle each other indefinitely and never enter attack range. The press cycle is what breaks it:
    /// periodically the bot abandons its spacing and drives in. Both are jittered per bot so two copies
    /// of the same profile don't press in lockstep.</summary>
    public float PressInterval => Mathf.Lerp(2.2f, 0.5f, aggression);
    public float PressDuration => Mathf.Lerp(1.0f, 2.5f, aggression);

    /// <summary>Chance a commitment window becomes a full rush: straight at them, no orbit, no flinching,
    /// stand-off distance abandoned entirely. The difference between a bot that circles and one that
    /// actually fights, so it is high by design even for cautious profiles.</summary>
    public float RushChance => Mathf.Lerp(0.35f, 0.85f, aggression);

    // Aim. The scatter ceiling is set against the aimable hitbox's ~1.125 half-extent, so a sloppy
    // bot visibly lands beside the target rather than merely clipping it.
    public float AimScatter => Mathf.Lerp(1.6f, 0.15f, accuracy);
    public float LeadFactor => Mathf.Lerp(0.3f, 1.0f, accuracy);

    // Clock. Omnipotent overrides both: reaction 1.0 alone only reaches 0.10s, which is still a
    // visible beat behind an attack's startup.
    public float DecisionInterval => _omniscient ? 0.04f : Mathf.Lerp(0.42f, 0.10f, reaction);
    public float ThreatDelay      => _omniscient ? 0f    : Mathf.Lerp(0.35f, 0.06f, reaction);

    // Sloppiness. High discipline means a BIGGER gap between actions and more deliberate standing
    // still — an undisciplined bot always has something queued.
    public float WhiffChance  => Mathf.Lerp(0.30f, 0.02f, discipline);
    public float MinActionGap => Mathf.Lerp(0.10f, 0.30f, discipline);
    /// <summary>The "human pause", as a FRACTION of the best available attack weight — not an absolute
    /// weight. Absolute idling let a disciplined bot out-vote a free hit roughly half the time, which
    /// read as the bot standing there doing nothing. Proportional idling can never dominate a real
    /// opening, and collapses to nothing when no attack is available anyway.</summary>
    public float IdleFactor   => Mathf.Lerp(0.10f, 0.80f, discipline);
    /// <summary>Chance per decision of burning a roll with nothing threatening it — which costs the
    /// 15 RES it then does not have for an aimable. Flavour only, and kept low on purpose: this fires
    /// per DECISION, so at ~5 decisions/sec even a small value becomes several rolls a second, and two
    /// bots spent the whole match dodging nothing and starving themselves of meter.</summary>
    public float PanicRollChance => Mathf.Lerp(0.04f, 0f, discipline);

    // Defence. Omnipotent forces certainty: its whole point is rolling out on the startup frame every
    // time, and the trait vector alone tops out around 0.66.
    // There is deliberately no low-HP retreat: both fighters get hurt at a similar rate, so an
    // HP-triggered kite fires on both at once and the match becomes two bots running from each other.
    public float RollOnThreatChance => _omniscient ? 1f : Mathf.Lerp(0.10f, 0.85f, defense);

    /// <summary>EMA step for BotMemory. Capped at 0.20 (~3 attempts to move a rate halfway) because
    /// the brief was to adapt *slightly* — a higher rate reads as the bot hard-switching tactics.</summary>
    public float AdaptRate => Mathf.Lerp(0.03f, 0.20f, adaptability);

    // Resource appetite. At recklessness 0 it exits overdrive with meter to spare and never bleeds
    // HP; at 1 it rides the bar to empty and pays 20 HP/s for the privilege.
    public float OverdriveExitCE         => Mathf.Lerp(25f, 0f, recklessness);
    public float FieldOpponentHpTrigger => Mathf.Lerp(0.25f, 0.60f, recklessness);
    /// <summary>RES it refuses to drop below on a non-emergency roll, so a cautious bot keeps enough
    /// banked for the 20-RES aimable instead of rolling its offense away.</summary>
    public float RollCeReserve => Mathf.Lerp(35f, 0f, recklessness);

    // Movement readability. These are deliberately mild: erraticism is meant to make the bot hard to
    // LEAD, not hard to look at. Flipping the orbit ~8x/sec and snapping the heading through 75 degrees
    // produced a fighter that vibrated in place and blurred the screen, and it never travelled anywhere
    // because each reversal cancelled the last. BotController also smooths both values rather than
    // applying them as steps.
    public float StrafeFlipInterval   => Mathf.Lerp(2.5f, 0.8f, erraticism);
    public float HeadingJitterDegrees => Mathf.Lerp(4f, 30f, erraticism);

    /// <summary>Compact trait dump for the debug overlay.</summary>
    public string Describe()
        => $"{preset}  agg {aggression:0.00} acc {accuracy:0.00} rea {reaction:0.00} dis {discipline:0.00}\n"
         + $"def {defense:0.00} adp {adaptability:0.00} rck {recklessness:0.00} err {erraticism:0.00}";
}
