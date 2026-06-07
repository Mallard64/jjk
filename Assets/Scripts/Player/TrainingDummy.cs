using UnityEngine;

/// <summary>
/// Turns a copy of the player prefab into a passive practice target. Disables all control, aiming, and
/// networking so it never takes input, moves on its own, aims, or fights back; pins its HP/CE bars to
/// the head (never the local-player corner UI); plays the hurt reaction when struck; never dies (a
/// would-be killing blow tops it back up); and after a no-damage window resets to full HP at its start
/// position. Tracks the current combo + damage and draws a detailed readout in the top-right corner.
/// Offline use — place it in a non-networked scene.
/// </summary>
public class TrainingDummy : MonoBehaviour
{
    [Tooltip("Seconds without taking damage before the dummy resets to full HP at its start position.")]
    [SerializeField] private float resetDelay = 3f;

    private PlayerHealth              _health;
    private PlayerAnimationController _anim;
    private Rigidbody2D               _rb;

    private Vector3 _startPosition;
    private float   _idleTimer;
    private bool    _needsReset;

    // Readout state — current combo's hit count, the last hit's damage, and the combo's total.
    private int   _combo;
    private float _lastDamage;
    private float _totalDamage;
    private GUIStyle _style;

    void Awake()
    {
        _health = GetComponent<PlayerHealth>();
        _anim   = GetComponent<PlayerAnimationController>();
        _rb     = GetComponent<Rigidbody2D>();
        _startPosition = transform.position;

        // Disabled before their Start runs, so they never subscribe to input/events or tick.
        Disable<PlayerInputHandler>();
        Disable<PlayerMovement>();
        Disable<PlayerCombatController>();
        Disable<AutoAttackController>();
        Disable<AimableAttackController>();
        Disable<PlayerRoll>();
        Disable<PlayerOverdrive>();
        Disable<AimingReticle>();
        Disable<FusionPlayerSync>();
        Disable<FusionPlayerMovement>();
        Disable<FusionPlayerCombat>();

        // Keep the HP/CE bars on the dummy's head instead of hijacking the local-player corner UI.
        foreach (var bar in GetComponentsInChildren<WorldHealthBar>(true)) bar.ForceWorldView();
        foreach (var bar in GetComponentsInChildren<WorldEnergyBar>(true)) bar.ForceWorldView();
    }

    void OnEnable()
    {
        if (_health == null) return;
        _health.OnDamaged      += OnDamaged;
        _health.OnHealthChanged += KeepAlive;
    }

    void OnDisable()
    {
        if (_health == null) return;
        _health.OnDamaged      -= OnDamaged;
        _health.OnHealthChanged -= KeepAlive;
    }

    // Never dies — catches every HP drop (normal hits AND the wall-splat chip damage, which bypasses
    // OnDamaged) and tops the HP back up before it can reach a death.
    private void KeepAlive(float current, float max)
    {
        if (current <= 0f && _health != null) _health.Heal(max);
    }

    void Update()
    {
        // Enter resets the dummy on demand (clears the readout, snaps it home, tops players up).
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            ResetDummy();
            return;
        }
        if (!_needsReset) return;
        _idleTimer += Time.deltaTime;
        if (_idleTimer >= resetDelay) ResetDummy();
    }

    private void OnDamaged(float amount, GameObject source)
    {
        // True-combo: the hit continues the combo only if the dummy was already staggered (read before
        // this hit's PlayHurt). A hit on a recovered dummy starts a fresh combo.
        bool continued = _anim != null && _anim.IsHitstun;
        _combo       = continued ? _combo + 1 : 1;
        _lastDamage  = amount;
        _totalDamage = continued ? _totalDamage + amount : amount;

        _idleTimer  = 0f;
        _needsReset = true;
        _anim?.PlayHurt();  // Hurtbox already queued facing + hitstun before TakeDamage fired
        // Never-die is handled by KeepAlive (OnHealthChanged) so it also covers wall-splat chip damage.
    }

    void OnCollisionEnter2D(Collision2D collision)
    {
        // PlayerMovement (which normally reports wall hits) is disabled on the dummy, so do it here:
        // if a hard hit knocked the dummy into a wall (not the player) while stunned, let it splat.
        if (_anim == null || !_anim.IsHitstun) return;
        if (collision.transform.root.GetComponent<PlayerHealth>() == null) _anim.NotifyWallHit();
    }

    private void ResetDummy()
    {
        _needsReset  = false;
        _idleTimer   = 0f;
        _combo       = 0;
        _lastDamage  = 0f;
        _totalDamage = 0f;

        transform.position = _startPosition;
        if (_rb != null)
        {
            _rb.velocity = Vector2.zero;
            _rb.position = _startPosition;
        }
        _health?.Respawn();  // dummy: full HP/CE, anim → idle

        RestorePlayers();
    }

    // Tops every real player's HP + CE back to full — but nothing else (no reposition / respawn), so a
    // reset never disturbs where the player is or what they're doing. Skips dummies and online proxies.
    private void RestorePlayers()
    {
        foreach (var health in FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None))
        {
            if (health.GetComponent<TrainingDummy>() != null) continue;  // not a dummy
            var net = health.GetComponent<FusionPlayerSync>();
            if (net != null && net.Object != null && net.Object.IsValid && !net.IsAuthority) continue; // proxy
            health.Heal(health.MaxHp);
            var energy = health.GetComponent<CursedEnergy>();
            if (energy != null) energy.SetEnergy(energy.MaxEnergy);
        }
    }

    void OnGUI()
    {
        if (_totalDamage <= 0f) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperRight };
            _style.normal.textColor = Color.white;
        }

        const float w = 260f, h = 110f, margin = 16f;
        var rect = new Rect(Screen.width - w - margin, margin, w, h);
        GUI.Label(rect, $"TRAINING DUMMY\nCombo: {_combo}\nLast: {_lastDamage:0}\nTotal: {_totalDamage:0}", _style);
    }

    private void Disable<T>() where T : Behaviour
    {
        var component = GetComponent<T>();
        if (component != null) component.enabled = false;
    }
}
