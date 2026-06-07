using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Universal movement skill: a brief dash with i-frames, gated by cursed energy + cooldown.
/// Triggered by the offline (PlayerCombatController) or online (FusionPlayerMovement) layer.
/// </summary>
public class PlayerRoll : MonoBehaviour
{
    [Header("Animation")]
    [Tooltip("Action name passed to the animator, e.g. 'roll' → 'roll-se'.")]
    [SerializeField] private string rollAnim = "roll";

    [Header("Tuning")]
    [Tooltip("How long the dash lasts. Movement is locked and i-frames are active for this whole window.")]
    [SerializeField] private float rollDuration  = 0.35f;
    [Tooltip("Constant velocity along the roll direction for the whole duration. Re-asserted every FixedUpdate so rigidbody drag can't slow the dash mid-roll.")]
    [SerializeField] private float rollDashSpeed = 18f;
    [SerializeField] private float rollEnergyCost = 15f;
    [Tooltip("Minimum time between rolls. Measured from roll start.")]
    [SerializeField] private float rollCooldown   = 0.8f;

    private Rigidbody2D               _rb;
    private CursedEnergy              _energy;
    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private PlayerMovement            _movement;
    private AutoAttackController      _auto;
    private AimableAttackController   _aimable;

    private float _cooldownTimer;
    private Coroutine _rollRoutine;
    private Vector2 _rollDir;

    public bool IsRolling => _rollRoutine != null;
    public float CooldownRemaining => Mathf.Max(0f, _cooldownTimer);

    // Local events the network layer listens to so it can replicate the roll anim to proxies.
    public event Action<Vector2, string> OnRollStarted;
    public event Action                  OnRollEnded;

    void Awake()
    {
        _rb        = GetComponent<Rigidbody2D>();
        _energy    = GetComponent<CursedEnergy>();
        _health    = GetComponent<PlayerHealth>();
        _anim      = GetComponent<PlayerAnimationController>();
        _movement  = GetComponent<PlayerMovement>();
        _auto      = GetComponent<AutoAttackController>();
        _aimable   = GetComponent<AimableAttackController>();

        // Attacking during a roll trades the roll's i-frames for offensive commitment.
        if (_auto    != null) _auto.OnAttackStarted    += OnAutoAttackStartedDuringRoll;
        if (_aimable != null) _aimable.OnAttackStarted += OnAimableAttackStartedDuringRoll;
    }

    void OnDestroy()
    {
        if (_auto    != null) _auto.OnAttackStarted    -= OnAutoAttackStartedDuringRoll;
        if (_aimable != null) _aimable.OnAttackStarted -= OnAimableAttackStartedDuringRoll;
    }

    private void OnAutoAttackStartedDuringRoll(string action, Vector2 facing)
    {
        if (IsRolling) _health?.SetInvincible(false);
    }

    private void OnAimableAttackStartedDuringRoll(Vector2 aimDir, string action)
    {
        if (IsRolling) _health?.SetInvincible(false);
    }

    void Update()
    {
        if (_cooldownTimer > 0f) _cooldownTimer -= Time.deltaTime;
    }

    void FixedUpdate()
    {
        // Sustain dash velocity for the whole roll window. Otherwise rigidbody drag decays the
        // impulse over the duration, cutting effective distance roughly in half.
        if (!IsRolling || _rb == null || !_rb.simulated) return;
        _rb.velocity = _rollDir * rollDashSpeed;
    }

    public bool CanRoll()
    {
        if (IsRolling || _cooldownTimer > 0f) return false;
        if (_health != null && _health.IsDead)  return false;
        if (_anim != null && _anim.IsHitstun)   return false;
        if (_energy != null && _energy.CurrentEnergy < rollEnergyCost) return false;
        return true;
    }

    /// <summary>Triggers a roll along `direction`. Falls back to current facing if direction is near-zero.</summary>
    public void TryRoll(Vector2 direction)
    {
        if (!CanRoll()) return;
        if (_energy != null)
        {
            _energy.Drain(rollEnergyCost);  // CanRoll already verified there's enough
            _energy.SuppressRegenForAction(rollDuration);
        }
        _cooldownTimer = rollCooldown;

        // Cancel any in-progress attack so movement lock and anim transitions don't conflict.
        _auto?.Cancel();
        _aimable?.Cancel();

        _rollDir = direction.sqrMagnitude > 0.0001f
            ? direction.normalized
            : (_anim != null ? _anim.CurrentFacing : Vector2.right);

        _rollRoutine = StartCoroutine(RollRoutine());
        OnRollStarted?.Invoke(_rollDir, rollAnim);
    }

    private IEnumerator RollRoutine()
    {
        _movement?.SetCanMove(false);
        _health?.SetInvincible(true);
        _anim?.PlayRoll(_rollDir, rollAnim);
        // FixedUpdate keeps re-asserting velocity for the whole window so the dash maintains
        // its committed direction and speed — input can't steer the roll.

        if (rollDuration > 0f) yield return new WaitForSeconds(rollDuration);

        _health?.SetInvincible(false);
        // If an attack started during the roll, leave movement-lock and anim state for the
        // attack's own routine to clear — otherwise the roll truncates the attack mid-swing.
        bool attackInProgress = (_auto != null && _auto.IsAttacking) || (_aimable != null && _aimable.IsAttacking);
        if (!attackInProgress)
        {
            _movement?.SetCanMove(true);
            _anim?.RefreshMovementState();
        }
        _rollRoutine = null;
        OnRollEnded?.Invoke();
    }
}
