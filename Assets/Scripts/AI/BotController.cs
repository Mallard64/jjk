using UnityEngine;

/// <summary>
/// A computer-controlled fighter. It does not re-implement movement or combat: it writes a
/// PlayerInputData into PlayerInputHandler every frame and lets PlayerMovement and
/// PlayerCombatController drive the character exactly as they do for a human. That is what keeps it
/// looking human and keeps one code path per action.
///
/// Decisions are heuristic — spacing, cooldowns, threat, resource budget. BotMemory adds a slight
/// drift on top: moves that land get thrown more, ranges that hurt get avoided. BotProfile supplies
/// the personality.
///
/// Perception rule: the default fill reads ONLY what a human could see — position, velocity, the HP
/// and RES head bars, and visibly telegraphed actions. It never reads the opponent's aim point, i-frame
/// state, or cooldown timers. BotPreset.Omnipotent is the documented exception and cheats on purpose.
///
/// Runs before the default execution order so its input lands before PlayerCombatController.Update
/// consumes the one-frame edges and before PlayerMovement.FixedUpdate reads MoveDir.
/// </summary>
[DefaultExecutionOrder(-50)]
[RequireComponent(typeof(PlayerInputHandler))]
public class BotController : MonoBehaviour
{
    private enum Intent { Neutral, Engage, Attack, Evade }

    [SerializeField] private BotProfile profile = new BotProfile();

    [Header("Reach")]
    [Tooltip("Centre-to-centre distance at which the auto attack connects. The shipped hitbox is 1.5x1 at offset 0.25 against a 0.5-radius hurtbox, so ~1.4. The aimable's reach is read from its own ThrowRadius.")]
    [SerializeField] private float autoAttackRange = 1.4f;
    [Tooltip("How far ahead of a moving target the aimable aims, before the profile's lead factor. Replaced by the controller's real ImpactDelay once it has fired once.")]
    [SerializeField] private float aimableLeadSeconds = 0.3f;

    [Header("Resources")]
    [Tooltip("Resonance needed before it will enter overdrive. Exit is profile-driven (recklessness).")]
    [SerializeField] private float overdriveEntryCE = 60f;
    [Tooltip("Seconds before it reconsiders a null field after deciding against one.")]
    [SerializeField] private float fieldRetryCooldown = 6f;
    [Tooltip("Fraction of a full resonance bar required to open a null field. The 40 RES activation is only the entry fee — it then drains 8 RES/s, so opening on a part-full bar collapses almost immediately and wastes the cost. Effectively 'full bar only'.")]
    [Range(0.5f, 1f)] [SerializeField] private float fieldMinEnergyFraction = 1f;

    [Header("Movement")]
    [Tooltip("How fast the emitted heading eases toward the desired one. Higher is more responsive but reintroduces the sprite-flicker this exists to prevent.")]
    [SerializeField] private float moveSmoothing = 10f;

    [Header("Walls / Arena")]
    [Tooltip("How far ahead of itself the bot probes for solid geometry before committing to a heading.")]
    [SerializeField] private float wallProbeDistance = 1.4f;
    [Tooltip("Radius of that probe. Roughly the body's own half-width plus clearance.")]
    [SerializeField] private float wallProbeRadius = 0.7f;
    [Tooltip("Playable bounds, used for corner awareness. More stable than collider probes: a boundary test on authored numbers can't flicker on and off frame to frame.")]
    [SerializeField] private Vector2 arenaMin = new Vector2(-10.5f, -5.5f);
    [SerializeField] private Vector2 arenaMax = new Vector2(11.5f, 5.5f);
    [Tooltip("How close to an edge counts as cornered — for escaping it, and for spotting that the OPPONENT is trapped there.")]
    [SerializeField] private float cornerMargin = 2.5f;
    [Tooltip("Leash: past this separation, retreating turns back into approaching. Two bots that both back off otherwise settle in opposite corners and the match stops. Keep it above the aimable's reach so the mid-range game still works.")]
    [SerializeField] private float maxSeparation = 8f;

    [Header("Combos")]
    [Tooltip("Follow-up punches after a landed auto attack. The victim's hitstun (1s) outlasts a punch's recovery, so these are guaranteed — 2 here means a 3-hit string.")]
    [SerializeField] private int comboAfterAuto = 2;
    [Tooltip("Follow-up punches after a landed aimable attack, whose 1.5s hitstun leaves even more room.")]
    [SerializeField] private int comboAfterAimable = 2;
    [Tooltip("Seconds to land each follow-up before the bot writes the combo off and returns to neutral.")]
    [SerializeField] private float comboStepTimeout = 0.9f;

    [Header("Meter")]
    [Tooltip("Below this resonance the bot stops attacking and disengages to regenerate. Attacking suppresses regen, so without this it pokes itself into permanent meter starvation. Recovers at twice this value.")]
    [SerializeField] private float meterFloorRes = 20f;

    [Header("Roll cancel (Omnipotent)")]
    [Tooltip("Delay between the roll and the attack thrown out of it. Long enough to bank i-frames, short enough that the hitbox still lands inside the 0.3s dash.")]
    [SerializeField] private float rollCancelDelay = 0.1f;

    [Header("Debug")]
    [Tooltip("Draws the bot's intent, spacing, land rates and range pressure in the top-right corner.")]
    [SerializeField] private bool showDebug = false;

    private PlayerInputHandler        _input;
    private PlayerHealth              _health;
    private Resonance                 _energy;
    private PlayerAnimationController _anim;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;
    private PlayerRoll                _roll;
    private PlayerOverdrive           _overdrive;
    private BaseNullField             _field;

    private readonly BotMemory _memory = new BotMemory();

    private PlayerHealth            _oppHealth;
    private Transform               _oppTransform;
    private Rigidbody2D             _oppBody;
    private BotController           _oppBot;
    private AutoAttackController    _oppAuto;
    private AimableAttackController _oppAimable;
    private PlayerRoll              _oppRoll;
    private Resonance               _oppEnergy;
    private PlayerInputHandler      _oppInput;
    private PlayerAnimationController _oppAnim;   // visible hurt state — drives combo continuation

    private Perception _p;

    private Intent  _intent;
    private BotMove _pickedMove;
    private bool    _hasPickedMove;

    private float _nextDecision;
    private float _lastActionTime = -99f;
    private float _nextFieldAttempt;

    // Aim hold. A human does not release the instant they press.
    private bool  _holdingAim;
    private float _releaseAimAt;
    private float _abandonAimAt;

    // Movement shape.
    private float _strafeSign = 1f;
    private float _nextStrafeFlip;
    // Both are eased toward their targets every frame rather than applied as steps — snapping the
    // heading and the stand-off distance on each decision made the bot vibrate in place instead of
    // travelling, and blurred the screen because the camera chased the oscillation.
    private float   _headingJitterRad;
    private float   _headingJitterTarget;
    private float   _rangeWobble;
    private float   _rangeWobbleTarget;
    private float   _nextWobbleRoll;
    private Vector2 _moveSmoothed;

    // Full-commitment beeline: no orbit, no flinching. Rolled when a press window opens.
    private bool _rushing;

    // Commitment window: see BotProfile.PressInterval for why holding a preferred range deadlocks.
    private float _pressUntil;
    private float _nextPress;
    private bool  Pressing => Time.time < _pressUntil;

    // Threat episode: the dice are rolled once when an attack first becomes visible, not every frame.
    private float _threatSince = -1f;
    private bool  _threatWillRoll;

    // Roll-cancel: the attack is a separate scheduled input, never the same frame as the roll.
    private float _rollCancelAt = -1f;

    // Omnipotent counter-read of another bot's weights: >0 back off, <0 get inside their leap.
    private float _counterRangeBias;

    // True while the bot is deliberately not attacking so its resonance can come back. See UpdateMeterRecovery.
    private bool _recoveringMeter;

    private bool _warnedNoOpponent;

    // Scripted follow-ups after a hit lands. See TryComboStep.
    private int     _comboRemaining;
    private float   _comboDeadline;
    private BotMove _lastFiredMove;

    private static readonly Collider2D[] _wallProbe = new Collider2D[8];

    // Damage-taken readout, mirroring TrainingDummy's so practice-vs-bot keeps a combo display.
    private int      _combo;
    private float    _lastDamage;
    private float    _totalDamage;
    private GUIStyle _style;

    /// <summary>Read by an Omnipotent opponent. Perfect information is the whole point of that tier.</summary>
    public BotProfile Profile => profile;
    public BotMemory  Memory  => _memory;

    private struct Perception
    {
        public bool    Valid;
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector2 ToOpponent;   // normalized, self → opponent
        public float Distance;
        public float HpFraction;
        public bool  AttackingAuto;
        public bool  AttackingAimable;
        public bool  Aiming;
        // Omniscient-only. Left false/zero for every other preset so nothing below can read them by accident.
        public bool    Invincible;
        public bool    CanRoll;
        public Vector2 AimPoint;
        public bool    AimPointKnown;
    }

    void Awake()
    {
        _input     = GetComponent<PlayerInputHandler>();
        _health    = GetComponent<PlayerHealth>();
        _energy    = GetComponent<Resonance>();
        _anim      = GetComponent<PlayerAnimationController>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();
        _roll      = GetComponent<PlayerRoll>();
        _overdrive = GetComponent<PlayerOverdrive>();
        _field     = GetComponent<BaseNullField>();

        profile.Resolve();

        // No reticle for a bot, and keep its bars on its head instead of hijacking the corner HUD —
        // the same two adjustments TrainingDummy makes.
        var reticle = GetComponent<AimingReticle>();
        if (reticle != null) reticle.enabled = false;
        foreach (var bar in GetComponentsInChildren<WorldHealthBar>(true)) bar.ForceWorldView();
        foreach (var bar in GetComponentsInChildren<WorldEnergyBar>(true)) bar.ForceWorldView();

        if (_auto    != null) _auto.OnAttackStarted    += OnOwnAutoStarted;
        if (_aimable != null) _aimable.OnAttackStarted += OnOwnAimableStarted;
        if (_health  != null) _health.OnDamaged        += OnOwnDamaged;

        // Desynchronise identical bots. Two copies of one prefab share a profile, so without a random
        // starting phase they orbit in the same direction, flip at the same instant and decide on the
        // same frame — two bots mirroring each other exactly, circling forever. Every periodic timer
        // starts at a random point in its own cycle.
        _strafeSign     = Random.value < 0.5f ? -1f : 1f;
        _nextStrafeFlip = Time.time + Random.Range(0f, profile.StrafeFlipInterval);
        _nextPress      = Time.time + Random.Range(0f, profile.PressInterval);
        _nextDecision   = Time.time + Random.Range(0f, profile.DecisionInterval);
    }

    void OnDestroy()
    {
        if (_auto    != null) _auto.OnAttackStarted    -= OnOwnAutoStarted;
        if (_aimable != null) _aimable.OnAttackStarted -= OnOwnAimableStarted;
        if (_health  != null) _health.OnDamaged        -= OnOwnDamaged;
        BindOpponent(null);
    }

    void OnDisable()
    {
        // MatchManager freezes fighters by disabling their control components. Hand back a neutral
        // frame so a stale MoveDir can't sit in PlayerInputHandler.Current while we're switched off.
        _holdingAim   = false;
        _rollCancelAt = -1f;
        _input?.SetExternalInput(default);
    }

    /// <summary>Clears the adaptation. Called when a fresh match starts.</summary>
    public void ResetMemory()
    {
        _memory.Reset();
        _comboRemaining   = 0;
        _combo            = 0;
        _lastDamage       = 0f;
        _totalDamage      = 0f;
        _counterRangeBias = 0f;
    }

    void Update()
    {
        ResolveOpponent();
        _memory.Tick(profile.AdaptRate, Time.deltaTime);
        UpdateMeterRecovery();
        SmoothMovementNoise();

        var data = new PlayerInputData();

        // Overdrive is resolved even while stunned or frozen: the drain keeps running through hitstun,
        // and once RES hits zero it eats 20 HP/s, so the bot must be able to bail out mid-combo.
        // PlayerCombatController handles the toggle before its own hitstun return, so this still lands.
        data.Overdrive = ResolveOverdriveToggle();

        if (!CanAct())
        {
            _holdingAim   = false;
            _rollCancelAt = -1f;
            data.AimDir   = _anim != null ? _anim.CurrentFacing : Vector2.right;
            data.AimPoint = (Vector2)transform.position + data.AimDir;
            _input.SetExternalInput(data);
            return;
        }

        Sense();

        if (Time.time >= _nextDecision)
        {
            Decide();
            _nextDecision = Time.time + profile.DecisionInterval * Random.Range(0.65f, 1.35f);
        }

        Act(ref data);
        data.MoveDir = SmoothMove(data.MoveDir);
        _input.SetExternalInput(data);
    }

    private bool CanAct()
    {
        if (_health != null && _health.IsDead) return false;
        if (BaseNullField.PlayersFrozen) return false;
        if (_anim != null && _anim.IsHitstun)  return false;
        return true;
    }

    // ── Sensing ───────────────────────────────────────────────────────────

    private void Sense()
    {
        _p = default;
        if (_oppHealth == null || _oppTransform == null) return;

        _p.Valid      = true;
        _p.Position   = _oppTransform.position;
        _p.Velocity   = _oppBody != null ? _oppBody.velocity : Vector2.zero;
        _p.HpFraction = _oppHealth.MaxHp > 0f ? _oppHealth.CurrentHp / _oppHealth.MaxHp : 1f;

        Vector2 delta = _p.Position - (Vector2)transform.position;
        _p.Distance   = delta.magnitude;
        _p.ToOpponent = _p.Distance > 0.001f ? delta / _p.Distance : Vector2.right;

        // Visible telegraphs: the swing and the leap are on screen for anyone to see.
        _p.AttackingAuto    = _oppAuto    != null && _oppAuto.IsAttacking;
        _p.AttackingAimable = _oppAimable != null && (_oppAimable.IsAttacking || _oppAimable.IsAiming);
        _p.Aiming           = _oppAimable != null && _oppAimable.IsAiming;

        if (profile.Omniscient) SenseOmniscient();
    }

    // Everything a human cannot see. Gated behind the Omnipotent preset and nothing else.
    private void SenseOmniscient()
    {
        _p.Invincible = _oppHealth.IsInvincible;

        _p.CanRoll = _oppRoll != null
                     && !_oppRoll.IsRolling
                     && _oppRoll.CooldownRemaining <= 0f
                     && (_oppEnergy == null || _oppEnergy.CurrentEnergy >= _oppRoll.EnergyCost);

        // Their reticle: where the leap is about to land, before they commit to it.
        if (_oppInput != null)
        {
            _p.AimPoint      = _oppInput.Current.AimPoint;
            _p.AimPointKnown = true;
        }

        // Opponent is also a bot: read which move it actually lands and stand where that move is worst.
        if (_oppBot != null)
        {
            float theirAuto    = _oppBot.Memory.LandRate(BotMove.Auto);
            float theirAimable = _oppBot.Memory.LandRate(BotMove.Aimable);
            _counterRangeBias  = theirAuto > theirAimable ? 1.2f : -1.0f;
        }
    }

    // ── Deciding ──────────────────────────────────────────────────────────

    private void Decide()
    {
        _hasPickedMove       = false;
        _headingJitterTarget = Random.Range(-profile.HeadingJitterDegrees, profile.HeadingJitterDegrees)
                               * Mathf.Deg2Rad;

        if (Time.time >= _nextStrafeFlip)
        {
            _strafeSign     = -_strafeSign;
            _nextStrafeFlip = Time.time + profile.StrafeFlipInterval * Random.Range(0.7f, 1.3f);
        }

        // Open a commitment window. Without this the bot settles at its preferred range and stays
        // there — the one behaviour that makes two bots orbit each other and never fight.
        // A cornered opponent forces one open immediately: that opening is too good to wait out.
        if (Time.time >= _nextPress || (OpponentCornered && !Pressing))
        {
            _pressUntil = Time.time + profile.PressDuration * Random.Range(0.7f, 1.3f);
            _nextPress  = Time.time + profile.PressInterval * Random.Range(0.6f, 1.4f);
            _rushing    = OpponentCornered || Random.value < profile.RushChance;
        }

        if (!_p.Valid) { _intent = Intent.Neutral; return; }

        // No low-HP kiting. Both fighters take damage at roughly the same rate, so an HP-triggered
        // retreat fires on both at once and the match becomes two bots running from each other. Being
        // hurt is already answered by the reflex roll and the meter budget, which are situational
        // rather than a blanket mode switch.

        // An undisciplined bot burns a roll for no reason, which costs it the meter it needed for a leap.
        // Never mid-commitment: a bot that flinches out of its own approach never reaches striking range.
        if (!Pressing && Random.value < profile.PanicRollChance && CanRoll(emergency: false))
        {
            _intent = Intent.Evade;
            return;
        }

        if (Time.time - _lastActionTime < profile.MinActionGap) { _intent = Intent.Neutral; return; }

        PickMove();
        _intent = _hasPickedMove                       ? Intent.Attack
                : _p.Distance > PreferredRange + 1f    ? Intent.Engage
                                                       : Intent.Neutral;
    }

    // Weighted random over the legal moves, biased by how often each has been landing. Not an argmax —
    // a bot that always throws its single best option reads as a script.
    private void PickMove()
    {
        // Out of meter: hold off entirely. Every attack calls Resonance.SuppressRegenForAction, which
        // blocks regen for the swing PLUS postActionRegenLockout (1.5s on the prefab). A bot that pokes
        // continuously therefore never regenerates and can no longer afford a roll or an aimable — it
        // starves itself with the one move that is free. Backing off lets the lockout expire.
        // (PreferredRange also widens while recovering, so it gives ground instead of hovering in range.)
        if (_recoveringMeter) return;

        // Perfect information: never swing into i-frames. Very visible, and the honest cost is that it
        // stands there doing nothing while the opponent rolls.
        if (profile.Omniscient && _p.Invincible) return;

        float scale = profile.AttackWeightScale;
        // They cannot escape right now — press the advantage.
        if (profile.Omniscient && !_p.CanRoll) scale *= 1.5f;

        float wAuto = _auto != null && _auto.CanAttack() && _p.Distance <= autoAttackRange * 1.15f
                    ? scale * _memory.LandRate(BotMove.Auto)
                    : 0f;

        float wAimable = profile.UseAimable && _aimable != null && _aimable.CanStartAiming()
                         && _p.Distance <= AimableRange && _p.Distance > autoAttackRange * 0.6f
                       ? scale * _memory.LandRate(BotMove.Aimable)
                       : 0f;

        // Idle is proportional to the best real option, so it can never out-vote a free hit. With no
        // attack available there is nothing to weigh and the bot repositions instead. Mid-commitment
        // it does not hesitate at all — pausing is what the neutral game is for.
        float best = Mathf.Max(wAuto, wAimable);
        if (best <= 0f) return;
        float total = wAuto + wAimable + best * (Pressing ? 0f : profile.IdleFactor);

        float pick = Random.value * total;
        if (pick < wAuto)            { _pickedMove = BotMove.Auto;    _hasPickedMove = true; return; }
        if (pick < wAuto + wAimable) { _pickedMove = BotMove.Aimable; _hasPickedMove = true; }
    }

    // ── Acting ────────────────────────────────────────────────────────────

    private void Act(ref PlayerInputData data)
    {
        data.AimPoint = _p.Valid ? _p.Position : (Vector2)transform.position + Vector2.right;
        data.AimDir   = _p.Valid ? _p.ToOpponent : (_anim != null ? _anim.CurrentFacing : Vector2.right);

        // Reflex first, every frame, outside the decision tick — a same-frame reaction is not
        // expressible any other way. Pre-empts whatever the last decision committed to.
        if (TryReflex(ref data)) return;

        // A roll-cancel's attack is a separate scheduled input; issuing both in one frame would give up
        // the i-frames entirely, since PlayerCombatController handles Roll and AutoAttack together.
        if (_rollCancelAt > 0f && Time.time >= _rollCancelAt)
        {
            _rollCancelAt = -1f;
            if (_auto != null && _auto.CanAttack())
            {
                data.AutoAttack = true;
                data.MoveDir    = DesiredMove();
                _lastActionTime = Time.time;
                return;
            }
        }

        // A landed hit opens a guaranteed string. Run it to completion instead of re-deciding between
        // steps — re-rolling the weighted pick mid-combo is how a bot throws away free damage.
        if (_comboRemaining > 0 && TryComboStep(ref data)) return;

        if (ResolveFieldPress()) { data.NullField = true; _lastActionTime = Time.time; return; }

        if (_holdingAim)
        {
            data.MoveDir = DesiredMove();
            // StartAiming can refuse (overdrive drained the RES mid-hold); don't hang in this state.
            if (_aimable == null || (!_aimable.IsAiming && Time.time > _abandonAimAt))
            {
                _holdingAim = false;
                return;
            }
            if (Time.time >= _releaseAimAt)
            {
                _holdingAim          = false;
                data.AimPoint        = PredictedAimPoint();
                data.AimableAttackUp = true;
                _lastFiredMove  = BotMove.Aimable;
                _lastActionTime = Time.time;
            }
            return;
        }

        // A free hit standing right in front of it. Checked EVERY FRAME, not only on a decision tick:
        // ticks are up to 0.4s apart and CanAttack() flips ready in between, so a bot that can only
        // attack at tick boundaries walks into its opponent and stands there with a swing available.
        // Also bypasses meter recovery on purpose — the auto costs nothing, and declining a point-blank
        // free hit to protect regen is never the right trade.
        if (TryOpportunisticAttack(ref data)) return;

        switch (_intent)
        {
            case Intent.Evade:
                data.MoveDir = EscapeDirection();
                if (CanRoll(emergency: false))
                {
                    data.Roll = true;
                    ScheduleRollCancel();
                    _lastActionTime = Time.time;
                }
                return;

            case Intent.Attack:
                if (!_hasPickedMove) { data.MoveDir = DesiredMove(); return; }

                // Sloppiness: fire anyway from outside range. Most of the "human mistake" texture.
                float reach = _pickedMove == BotMove.Auto ? autoAttackRange * 1.15f : AimableRange;
                if (_p.Distance > reach && Random.value > profile.WhiffChance)
                {
                    data.MoveDir = DesiredMove();
                    return;
                }

                if (_pickedMove == BotMove.Auto)
                {
                    data.AutoAttack = true;
                    data.AimPoint   = ScatteredPoint(_p.Position);
                    _lastFiredMove  = BotMove.Auto;
                }
                else
                {
                    data.AimableAttackDown = true;
                    _holdingAim   = true;
                    _releaseAimAt = Time.time + Random.Range(0.15f, 0.5f);
                    // Grace period for IsAiming to come back true, not a full aim window: if StartAiming
                    // was refused the bot bails in 0.2s instead of standing there looking broken.
                    _abandonAimAt = Time.time + 0.2f;
                }
                _hasPickedMove  = false;
                _lastActionTime = Time.time;
                return;

            default:
                data.MoveDir = DesiredMove();
                return;
        }
    }

    /// <summary>Same-frame response to a visible attack, delayed by the profile for every preset except
    /// Omnipotent. Returns true when it took the frame.</summary>
    private bool TryReflex(ref PlayerInputData data)
    {
        if (!_p.Valid || !(_p.AttackingAuto || _p.AttackingAimable))
        {
            _threatSince = -1f;
            return false;
        }

        // New threat episode: roll the dice once, not every frame.
        if (_threatSince < 0f)
        {
            _threatSince    = Time.time;
            _threatWillRoll = Random.value < profile.RollOnThreatChance;
        }

        // Mid-commitment it trades punches rather than backing out, but still respects the leap — that
        // is the real threat and it covers ground. Two bots that dodge every jab never land anything,
        // and each dodge costs 15 RES, so they starve each other into a stalemate.
        // A rush ignores everything: that is what makes it a rush.
        if (Pressing && (_rushing || !_p.AttackingAimable)) return false;

        if (!_threatWillRoll) return false;
        if (Time.time - _threatSince < profile.ThreatDelay) return false;

        // Only worth a roll if the attack can actually reach: the leap covers ground, the swing does not.
        float reach = _p.AttackingAimable ? AimableRange + 1.5f : autoAttackRange * 1.6f;
        if (_p.Distance > reach) return false;
        if (!CanRoll(emergency: true)) return false;

        _threatWillRoll = false;
        data.MoveDir    = EscapeDirection();
        data.Roll       = true;
        ScheduleRollCancel();
        _lastActionTime = Time.time;
        return true;
    }

    // PlayerRoll.TryRoll cancels any aimable, which clears IsAiming under us. Drop the hold here too,
    // or the bot spends the abandon timeout walking around waiting to release an aim it no longer has.
    /// <summary>Executes the next punch of a scripted string. Returns true when it owns the frame.
    /// The string is guaranteed because the victim's hitstun (1s from an auto, 1.5s from an aimable)
    /// outlasts a punch's own recovery, so the next hit connects before they can act. Continuation is
    /// judged off the victim's visible hurt animation, which any player can read — no cheating here,
    /// so every preset gets combos, not just Omnipotent.</summary>
    private bool TryComboStep(ref PlayerInputData data)
    {
        // Dropped: they recovered, they're gone, or we took too long getting there.
        if (!_p.Valid || Time.time > _comboDeadline || _oppAnim == null || !_oppAnim.IsHitstun)
        {
            _comboRemaining = 0;
            return false;
        }

        // Mid-swing or on cooldown: hold the spot rather than wandering off and losing the string.
        if (_auto == null || !_auto.CanAttack())
        {
            data.MoveDir = Vector2.zero;
            return true;
        }

        // Out of reach — they're still stunned, so close the gap rather than restarting neutral.
        if (_p.Distance > autoAttackRange * 1.15f)
        {
            data.MoveDir = AvoidWalls(_p.ToOpponent);
            return true;
        }

        data.AutoAttack = true;
        data.AimPoint   = ScatteredPoint(_p.Position);
        _lastFiredMove  = BotMove.Auto;
        _lastActionTime = Time.time;
        return true;
    }

    /// <summary>Swings whenever the opponent is simply standing in range and the attack is off cooldown.
    /// Deliberately outside the decision tick and outside the weighted pick: those decide what to
    /// *pursue*, and neither is the right place to answer "there is a target touching me right now".
    /// Runs after the reflex, combo, null field and aim-hold branches so none of them get overridden.</summary>
    private bool TryOpportunisticAttack(ref PlayerInputData data)
    {
        if (!_p.Valid || _holdingAim) return false;
        if (_auto == null || !_auto.CanAttack()) return false;
        if (_p.Distance > autoAttackRange * 1.15f) return false;
        if (Time.time - _lastActionTime < profile.MinActionGap) return false;
        if (profile.Omniscient && _p.Invincible) return false;   // never swing into i-frames

        data.AutoAttack = true;
        data.AimPoint   = ScatteredPoint(_p.Position);
        _lastFiredMove  = BotMove.Auto;
        _lastActionTime = Time.time;
        return true;
    }

    private void ScheduleRollCancel()
    {
        _holdingAim = false;
        if (profile.UseRollCancel) _rollCancelAt = Time.time + rollCancelDelay;
    }

    // ── Movement ──────────────────────────────────────────────────────────

    private float AimableRange => _aimable != null ? _aimable.ThrowRadius : autoAttackRange;

    private float PreferredRange
    {
        get
        {
            // Committing: drop the defensive spacing entirely and drive to striking distance. An
            // aggressive bot's base range plus SpacingBias can otherwise sit OUTSIDE its own attack
            // reach, so it parks there with zero radial error, never enters range, and the auto attack
            // weight stays permanently zero — the orbit-forever failure.
            if (OpponentCornered) return autoAttackRange;                 // free opening, take it
            if (Pressing && !_recoveringMeter) return autoAttackRange;

            return Mathf.Clamp(
                profile.PreferredRangeBase(autoAttackRange, AimableRange)
                + profile.SpacingBias
                + _memory.RangeDrift
                + _counterRangeBias
                + _rangeWobble
                + (_recoveringMeter ? 1.5f : 0f),   // out of meter: give ground while regen catches up
                autoAttackRange * 0.8f, AimableRange);
        }
    }

    private Vector2 DesiredMove()
    {
        if (!_p.Valid) return Vector2.zero;

        // Close or back off toward the preferred range, plus a tangential orbit. The soft clamp stops it
        // snapping between advance and retreat when it is already sitting near the target distance.
        Vector2 self = transform.position;

        // Free kill: they are pinned against an edge and cannot circle out. Drop everything and go
        // straight at them — no orbit, no stand-off. This is the single best opening in the game.
        // Stop closing once inside striking distance: the two fighters do not collide with each other
        // (Physics2D layer 1 vs 1 is off), so a pure beeline walks through them and out the far side.
        if (OpponentCornered)
            return _p.Distance <= autoAttackRange
                 ? Vector2.zero
                 : AvoidWalls(Rotate(_p.ToOpponent, _headingJitterRad * 0.3f));

        float error     = _p.Distance - PreferredRange;
        Vector2 radial  = _p.ToOpponent * Mathf.Clamp(error / 1.5f, -1f, 1f);
        // Cut the orbit down while committing, and remove it entirely on a rush — the sideways
        // component fights the approach and makes the bot spiral in far too slowly to ever arrive.
        float orbit     = _rushing && Pressing ? 0f : Pressing ? 0.3f : 0.75f;
        Vector2 tangent = new Vector2(-_p.ToOpponent.y, _p.ToOpponent.x) * _strafeSign * orbit;
        Vector2 dir     = radial + tangent;

        // Backed onto an edge while the opponent still has room: work back toward open ground rather
        // than letting them pin us there. Skipped when they are cornered too — then the attack wins.
        if (SelfCornered && !OpponentCornered) dir += ArenaPull(self) * 1.2f;

        // Perfect information: step out of the circle their leap is about to land in.
        if (profile.Omniscient && _p.AimPointKnown && _p.Aiming)
        {
            Vector2 fromAim = (Vector2)transform.position - _p.AimPoint;
            if (fromAim.sqrMagnitude < 4f)
                dir += (fromAim.sqrMagnitude > 0.001f ? fromAim.normalized : tangent.normalized) * 1.5f;
        }

        if (dir.sqrMagnitude < 0.0001f) return Vector2.zero;
        return AvoidWalls(Rotate(dir.normalized, _headingJitterRad));
    }

    private Vector2 EscapeDirection()
    {
        if (!_p.Valid) return Vector2.zero;

        // Leash. Every retreat path routes through here (low-HP kiting, panic evades, reflex rolls), so
        // bounding it once stops two mutually-retreating bots from parking in opposite corners and
        // ending the match. Past the leash, "escape" means re-approach.
        if (_p.Distance > maxSeparation) return AvoidWalls(_p.ToOpponent);

        // Away, but angled — a straight retreat backs into a wall and is trivial to follow.
        Vector2 away    = -_p.ToOpponent;
        Vector2 tangent = new Vector2(-_p.ToOpponent.y, _p.ToOpponent.x) * _strafeSign;
        return AvoidWalls(Rotate((away + tangent * 0.6f).normalized, _headingJitterRad * 0.5f));
    }

    // Steers a heading away from solid geometry it is about to walk into. Without this the bot orbits
    // and retreats purely relative to the opponent, presses itself into a wall and stays there while
    // being comboed — the arena's corners were doing more damage than the opponent.
    // If the push directly opposes the heading (a flat wall dead ahead), it slides along the wall
    // instead, so the bot rounds a corner rather than stalling against it.
    private Vector2 AvoidWalls(Vector2 desired)
    {
        if (desired.sqrMagnitude < 0.0001f) return desired;

        Vector2 self = transform.position;
        // Two probes: one ahead (don't walk into it) and one on the body itself, weighted higher so a
        // bot that is ALREADY against or inside a wall prioritises getting out over where it wanted to go.
        Vector2 push = ProbeWalls(self + desired * wallProbeDistance, self)
                     + ProbeWalls(self, self) * 2f;
        if (push.sqrMagnitude < 0.0001f) return desired;

        push = push.normalized;
        Vector2 steered = desired + push * 1.5f;

        // Head-on into a flat wall: desired and push cancel out. Slide along the surface instead of
        // resolving to zero and standing still.
        if (steered.sqrMagnitude < 0.04f)
            steered = new Vector2(-push.y, push.x) * _strafeSign;

        return steered.normalized;
    }

    // Corner awareness works off the authored arena bounds rather than collider probes: a boundary test
    // on fixed numbers gives the same answer every frame, where an overlap test flickers on and off
    // right at the edge — which is exactly where these decisions get made.
    private bool SelfCornered     => IsCornered(transform.position);
    private bool OpponentCornered => _p.Valid && IsCornered(_p.Position);

    private bool IsCornered(Vector2 p)
        => p.x < arenaMin.x + cornerMargin || p.x > arenaMax.x - cornerMargin
        || p.y < arenaMin.y + cornerMargin || p.y > arenaMax.y - cornerMargin;

    /// <summary>Unit-ish vector back toward open ground, one axis at a time, so a true corner produces
    /// a diagonal out.</summary>
    private Vector2 ArenaPull(Vector2 p)
    {
        Vector2 pull = Vector2.zero;
        if (p.x < arenaMin.x + cornerMargin) pull.x += 1f;
        if (p.x > arenaMax.x - cornerMargin) pull.x -= 1f;
        if (p.y < arenaMin.y + cornerMargin) pull.y += 1f;
        if (p.y > arenaMax.y - cornerMargin) pull.y -= 1f;
        return pull;
    }

    private Vector2 ProbeWalls(Vector2 at, Vector2 self)
    {
        int n = Physics2D.OverlapCircleNonAlloc(at, wallProbeRadius, _wallProbe);
        Vector2 push = Vector2.zero;
        for (int i = 0; i < n; i++)
        {
            var col = _wallProbe[i];
            if (col == null || col.isTrigger) continue;                            // hurt/hitboxes, null fields
            if (col.transform.root.GetComponent<PlayerHealth>() != null) continue; // self + the opponent

            Vector2 away = self - col.ClosestPoint(self);
            // ClosestPoint returns the QUERY POINT ITSELF when it is inside the collider, so a bot that
            // has been knocked into a wall gets a zero push — no avoidance in the one case that matters.
            // Fall back to the collider's centre, which always points back out.
            if (away.sqrMagnitude < 0.0001f) away = self - (Vector2)col.bounds.center;
            if (away.sqrMagnitude > 0.0001f) push += away.normalized;
        }
        return push;
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
        return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
    }

    // ── Aim ───────────────────────────────────────────────────────────────

    private Vector2 PredictedAimPoint()
    {
        if (!_p.Valid) return transform.position;
        // ImpactDelay only resolves once the attack has fired, so the serialized estimate covers the
        // first leap and the real value takes over afterwards.
        float lead = _aimable != null && _aimable.ImpactDelay > 0f ? _aimable.ImpactDelay : aimableLeadSeconds;
        return ScatteredPoint(_p.Position + _p.Velocity * lead * profile.LeadFactor);
    }

    // Scatter grows with how fast the target is moving: tracking a sprinting opponent is harder.
    private Vector2 ScatteredPoint(Vector2 point)
        => point + Random.insideUnitCircle * profile.AimScatter * (1f + _p.Velocity.magnitude * 0.15f);

    // ── Resources ─────────────────────────────────────────────────────────

    // PlayerRoll.CanRoll is private, so mirror its predicate. Non-emergency rolls also respect the
    // profile's RES reserve, which is what stops a cautious bot rolling away its next aimable.
    private bool CanRoll(bool emergency)
    {
        if (_roll == null || _roll.IsRolling || _roll.CooldownRemaining > 0f) return false;
        if (_health != null && _health.IsDead) return false;
        if (_anim != null && _anim.IsHitstun)  return false;
        if (_energy == null) return true;

        float after = _energy.CurrentEnergy - _roll.EnergyCost;
        if (after < 0f) return false;
        if (emergency) return true;

        // Recovering: the entire point is to stop spending. A discretionary roll here costs 15 RES and
        // re-arms the regen lockout, undoing exactly what the bot backed off to earn — it would sit at
        // low meter indefinitely, rolling away the energy it was waiting on.
        if (_recoveringMeter) return false;

        return after >= profile.RollCeReserve;
    }

    /// <summary>Decides whether the bot should stop attacking and let its meter come back. Hysteresis
    /// is deliberate — dropping out at the same value it recovers at would flicker every frame and
    /// produce a bot that neither commits nor disengages.</summary>
    /// <summary>Eases the two movement-noise values toward their targets. Applied as steps they made the
    /// bot oscillate in place: a fresh stand-off offset every decision flips the radial error's sign
    /// several times a second, so the fighter walks in, out, in, out and never gets anywhere. The
    /// stand-off target itself re-rolls on a slow timer, not per decision, for the same reason.</summary>
    /// <summary>Eases the emitted heading. This is the one that matters visually:
    /// PlayerAnimationController picks a directional clip by SECTORING the move vector into 8 compass
    /// directions, so a heading sitting near a sector boundary flips the sprite between two clips every
    /// frame — the fighter vibrates in place and the camera chasing it blurs the screen. Smoothing the
    /// inputs to the heading is not enough; the output is what gets sectored.
    /// Magnitude is preserved (not renormalised) so the bot accelerates and stops smoothly instead of
    /// snapping between full speed and standstill.</summary>
    private Vector2 SmoothMove(Vector2 desired)
    {
        _moveSmoothed = Vector2.Lerp(_moveSmoothed, desired, moveSmoothing * Time.deltaTime);
        return _moveSmoothed.sqrMagnitude < 0.02f ? Vector2.zero : _moveSmoothed;
    }

    private void SmoothMovementNoise()
    {
        if (Time.time >= _nextWobbleRoll)
        {
            _rangeWobbleTarget = Random.Range(-0.4f, 0.4f);
            _nextWobbleRoll    = Time.time + Random.Range(2.5f, 5f);
        }

        _headingJitterRad = Mathf.Lerp(_headingJitterRad, _headingJitterTarget, 5f * Time.deltaTime);
        _rangeWobble      = Mathf.Lerp(_rangeWobble,      _rangeWobbleTarget,   1.5f * Time.deltaTime);
    }

    private void UpdateMeterRecovery()
    {
        if (_energy == null) { _recoveringMeter = false; return; }
        float res = _energy.CurrentEnergy;
        _recoveringMeter = _recoveringMeter ? res < meterFloorRes * 2f : res < meterFloorRes;
    }

    private bool ResolveOverdriveToggle()
    {
        if (!profile.UseOverdrive || _overdrive == null || _energy == null) return false;
        // While a null field is up, overdrive is free and forced; PlayerCombatController ignores presses.
        if (_field != null && _field.IsActive) return false;
        if (_health != null && _health.IsDead) return false;

        if (_overdrive.IsActive) return _energy.CurrentEnergy <= profile.OverdriveExitCE;
        return _energy.CurrentEnergy >= overdriveEntryCE && _p.Valid && _p.Distance <= AimableRange;
    }

    private bool ResolveFieldPress()
    {
        if (!profile.UseNullField || _field == null || !_p.Valid) return false;
        if (_field.IsActive || _field.IsStartingUp || _field.IsInBurnout) return false;
        if (Time.time < _nextFieldAttempt || !_field.CanActivate()) return false;

        // Full bar only. CanActivate just checks the 40 RES entry fee, but the null field then drains
        // 8 RES/s with regen frozen for everyone, so opening on a half bar buys a couple of seconds
        // and a 2s burnout for the same price as a full-length one.
        if (_energy != null &&
            _energy.CurrentEnergy < _energy.MaxEnergy * fieldMinEnergyFraction - 0.01f) return false;

        bool finisher   = _p.HpFraction <= profile.FieldOpponentHpTrigger;
        bool lastResort = _health != null && _health.CurrentHp <= _health.MaxHp * 0.3f;

        _nextFieldAttempt = Time.time + fieldRetryCooldown;
        return finisher || lastResort;
    }

    // ── Opponent binding ──────────────────────────────────────────────────

    private void ResolveOpponent()
    {
        if (_oppHealth != null) return;

        PlayerHealth best = null;
        float bestSqr = float.MaxValue;
        foreach (var candidate in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (candidate == _health) continue;
            if (candidate.GetComponent<TrainingDummy>() != null) continue;  // the passive lab target
            float sqr = ((Vector2)candidate.transform.position - (Vector2)transform.position).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; best = candidate; }
        }

        if (best != null) { BindOpponent(best); return; }

        // No target means the bot has nothing to sense and will stand still forever, which is
        // indistinguishable from a broken brain. Say so once rather than failing silently.
        if (!_warnedNoOpponent)
        {
            _warnedNoOpponent = true;
            Debug.LogWarning($"[BotController] '{name}' found no opponent — it will not act. Expected " +
                             "another fighter with a PlayerHealth in the scene.", this);
        }
    }

    private void BindOpponent(PlayerHealth opponent)
    {
        if (_oppHealth != null) _oppHealth.OnDamaged -= OnOpponentDamaged;

        _oppHealth = opponent;
        if (opponent == null)
        {
            _oppTransform = null; _oppBody    = null; _oppBot    = null; _oppInput  = null;
            _oppAuto      = null; _oppAimable = null; _oppRoll   = null; _oppEnergy = null;
            _oppAnim      = null;
            return;
        }

        _oppTransform = opponent.transform;
        _oppBody      = opponent.GetComponent<Rigidbody2D>();
        _oppBot       = opponent.GetComponent<BotController>();
        _oppInput     = opponent.GetComponent<PlayerInputHandler>();
        _oppAuto      = opponent.GetComponent<AutoAttackController>();
        _oppAimable   = opponent.GetComponent<AimableAttackController>();
        _oppRoll      = opponent.GetComponent<PlayerRoll>();
        _oppEnergy    = opponent.GetComponent<Resonance>();
        _oppAnim      = opponent.GetComponent<PlayerAnimationController>();
        _oppHealth.OnDamaged += OnOpponentDamaged;
    }

    // ── Memory feeds ──────────────────────────────────────────────────────

    // ActiveDelay / ImpactDelay are assigned immediately before these events fire, so the window is
    // already accurate here. The slack covers the active frames themselves.
    private void OnOwnAutoStarted(string action, Vector2 facing)
        => _memory.NoteAttackStarted(BotMove.Auto, _auto.ActiveDelay + 0.3f);

    private void OnOwnAimableStarted(Vector2 aimDir, string action)
        => _memory.NoteAttackStarted(BotMove.Aimable, _aimable.ImpactDelay + 0.4f);

    // PlayerHealth.OnDamaged only fires on hits that actually connect (it guards on IsDead and
    // IsInvincible first), so this is a clean landed / whiffed signal.
    private void OnOpponentDamaged(float amount, GameObject source)
    {
        if (source != gameObject) return;
        _memory.NoteHitLanded();

        // Opening hit seeds the string; a hit that was already part of one spends a step.
        if (_comboRemaining <= 0)
            _comboRemaining = _lastFiredMove == BotMove.Aimable ? comboAfterAimable : comboAfterAuto;
        else
            _comboRemaining--;

        _comboDeadline = Time.time + comboStepTimeout;
    }

    private void OnOwnDamaged(float amount, GameObject source)
    {
        float distance = _oppTransform != null
            ? Vector2.Distance(transform.position, _oppTransform.position)
            : 0f;
        _memory.NoteDamageTaken(amount, _health != null ? _health.MaxHp : 100f, distance);
        _comboRemaining = 0;   // interrupted — the string is gone, go back to neutral

        // True-combo readout: this hit continues the combo only if it was already staggered.
        bool continued = _anim != null && _anim.IsHitstun;
        _combo       = continued ? _combo + 1 : 1;
        _lastDamage  = amount;
        _totalDamage = continued ? _totalDamage + amount : amount;
    }

    void OnGUI()
    {
        if (!showDebug) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.UpperRight };
            _style.normal.textColor = Color.white;
        }

        const float w = 330f, h = 190f, margin = 16f;
        var rect = new Rect(Screen.width - w - margin, margin, w, h);
        GUI.Label(rect,
            $"BOT  {profile.Describe()}\n" +
            $"intent {_intent}{(Pressing ? (_rushing ? " RUSH" : " PRESS") : "")}{(OpponentCornered ? " CORNERED!" : "")}" +
            $"{(_recoveringMeter ? " (regen)" : "")}{(_comboRemaining > 0 ? $" combo+{_comboRemaining}" : "")}" +
            $"   range {PreferredRange:0.0} / dist {(_p.Valid ? _p.Distance : 0f):0.0}\n" +
            $"{_memory.Describe()}\n" +
            $"taken  combo {_combo}  last {_lastDamage:0}  total {_totalDamage:0}",
            _style);
    }
}
