using UnityEngine;

/// <summary>
/// Reads Unity input and produces a PlayerInputData struct.
/// playerIndex 0 = WASD + mouse aim. playerIndex 1 = Arrow keys + auto-aim toward Player1.
/// Aimable attack is hold-to-aim / release-to-fire (Q for P1, Numpad2 for P2).
/// Null field expansion is E for P1, Numpad3 for P2.
/// </summary>
public class PlayerInputHandler : MonoBehaviour
{
    [SerializeField] private int playerIndex = 0;
    public void SetPlayerIndex(int index) => playerIndex = index;

    public PlayerInputData Current { get; private set; }

    public bool AutoAttackDown    { get; private set; }
    public bool AimableAttackDown { get; private set; }
    public bool AimableAttackUp   { get; private set; }
    public bool NullFieldDown        { get; private set; }
    public bool RollDown          { get; private set; }
    public bool OverdriveDown     { get; private set; }

    // Cached for P2 auto-aim (avoids FindWithTag every frame)
    private Transform _p1Transform;

    // Latched by SetExternalInput. Once a non-human source owns this player the keyboard poll stops
    // for good — a bot writes every frame, so falling back to hardware input on a frame it happened
    // to skip would let whoever is at the keyboard drive the bot.
    private bool _external;

    /// <summary>Feeds input from a non-human source (BotController) instead of the keyboard. The caller
    /// owns the input from here on and must write every frame: the *Down/*Up fields are one-frame edges.</summary>
    public void SetExternalInput(PlayerInputData data)
    {
        _external = true;
        Current   = data;

        AutoAttackDown    = data.AutoAttack;
        AimableAttackDown = data.AimableAttackDown;
        AimableAttackUp   = data.AimableAttackUp;
        NullFieldDown        = data.NullField;
        RollDown          = data.Roll;
        OverdriveDown     = data.Overdrive;
    }

    void Update()
    {
        if (_external) return;

        if (playerIndex == 0)
            Current = ReadPlayer1();
        else
            Current = ReadPlayer2();
    }

    PlayerInputData ReadPlayer1()
    {
        var data = new PlayerInputData();

        data.MoveDir = new Vector2(
            (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0),
            (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0)
        ).normalized;

        data.AimPoint = transform.position;
        if (Camera.main != null)
        {
            Vector3 mouseWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            mouseWorld.z = 0f;
            data.AimPoint = mouseWorld;
            Vector2 toMouse = mouseWorld - transform.position;
            data.AimDir = toMouse.sqrMagnitude > 0.01f ? toMouse.normalized : Vector2.right;
        }

        AutoAttackDown    = Input.GetMouseButtonDown(0);
        AimableAttackDown = Input.GetKeyDown(KeyCode.Q);
        AimableAttackUp   = Input.GetKeyUp(KeyCode.Q);
        NullFieldDown        = Input.GetKeyDown(KeyCode.E);
        RollDown          = Input.GetMouseButtonDown(1);
        OverdriveDown     = Input.GetKeyDown(KeyCode.LeftShift);

        data.AutoAttack        = AutoAttackDown;
        data.AimableAttackDown = AimableAttackDown;
        data.AimableAttackUp   = AimableAttackUp;
        data.NullField            = NullFieldDown;
        data.Roll              = RollDown;
        data.Overdrive         = OverdriveDown;

        return data;
    }

    PlayerInputData ReadPlayer2()
    {
        var data = new PlayerInputData();

        data.MoveDir = new Vector2(
            (Input.GetKey(KeyCode.RightArrow) ? 1 : 0) - (Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
            (Input.GetKey(KeyCode.UpArrow)    ? 1 : 0) - (Input.GetKey(KeyCode.DownArrow)  ? 1 : 0)
        ).normalized;

        // Cache P1 transform; re-find if the reference goes null (respawn)
        if (_p1Transform == null)
        {
            var p1 = GameObject.FindWithTag("Player1");
            if (p1 != null) _p1Transform = p1.transform;
        }

        if (_p1Transform != null)
        {
            data.AimPoint = _p1Transform.position;
            Vector2 toP1 = _p1Transform.position - transform.position;
            data.AimDir = toP1.sqrMagnitude > 0.01f ? toP1.normalized : Vector2.left;
        }
        else
        {
            data.AimPoint = (Vector2)transform.position + Vector2.left;
            data.AimDir = Vector2.left;
        }

        AutoAttackDown    = Input.GetKeyDown(KeyCode.Keypad1);
        AimableAttackDown = Input.GetKeyDown(KeyCode.Keypad2);
        AimableAttackUp   = Input.GetKeyUp(KeyCode.Keypad2);
        NullFieldDown        = Input.GetKeyDown(KeyCode.Keypad3);
        RollDown          = Input.GetKeyDown(KeyCode.Keypad0);
        OverdriveDown     = Input.GetKeyDown(KeyCode.RightShift);

        data.AutoAttack        = AutoAttackDown;
        data.AimableAttackDown = AimableAttackDown;
        data.AimableAttackUp   = AimableAttackUp;
        data.NullField            = NullFieldDown;
        data.Roll              = RollDown;
        data.Overdrive         = OverdriveDown;

        return data;
    }

    // Called after respawn to clear stale cached reference
    public void ResetCachedTarget() => _p1Transform = null;
}
