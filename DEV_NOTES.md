# DEV_NOTES — JJK Prototype

## Scene Setup (ArenaTest)

**Required scene objects:**

### 1. Match Manager (Empty GameObject)
Components: `MatchManager`
- Assign Player1Prefab, Player2Prefab
- Assign Spawn1, Spawn2 (empty GameObjects as spawn point markers)

### 2. Match UI (Canvas → TextMeshPro GameObjects)
Components on root Canvas child: `MatchUI`
- StatusText (TMP) — centered, displays FIGHT! / PLAYER X WINS!
- ControlsHint (TMP) — bottom of screen, shows key bindings

### 3. Player HP / Energy bars
No separate scene HUD object — each player prefab carries `WorldHealthBar` + `WorldEnergyBar`,
which choose corner UI vs. world head bar by viewer (see "Player Prefab Setup" below).

### 3b. Network Match UI (online / Arena scene only)
Empty GameObject with `NetworkMatchUI`. Drives the best-of-5 presentation; wire your own art:
- `roundEndScreen` — GameObject shown at the end of every round ("ROUND END!"). Give it its own Animator/sprites; it plays when SetActive'd.
- `victoryScreen` / `defeatScreen` — shown to the match winner / loser before the arena disconnects.
- Leave the fields empty and the flow still runs (just the OnGUI score + white fade, no art). The running round score is drawn by this component (OnGUI), no wiring needed.
Round count / hold times are tuned on the player prefab's `FusionPlayerSync` (`roundsToWin`, `deathLinger`, `roundEndHold`, `matchEndHold`).

### 4. Arena Walls
Use Box Collider 2D on four wall GameObjects (or a tilemap with composite collider).
Layer: Default (or create a "Wall" layer).

### 5. Camera
Orthographic, top-down. Position above the arena center.
Optional: CameraFollow script to track average position of both players.
Damage feedback components on the camera (built-in render pipeline only):
- `CameraShake` — positional shake (mid+ damage).
- `ScreenEffects` — full-screen colour flash + invert (post-process via OnRenderImage). Wire its `effectShader` to `Assets/VFX/ScreenEffects/ColorDamageEffect.shader`. Low damage = red flash; high/overdrive damage = white flash + colour invert.

---

## Player Prefab Setup

Hierarchy:
```
Player (root)
  ├── [Components on root]
  │   ├── Rigidbody2D           (Freeze Z rotation, Linear drag ~2)
  │   ├── CircleCollider2D      (physics body)
  │   ├── PlayerInputHandler    (playerIndex: 0 or 1)
  │   ├── PlayerMovement        (moveSpeed: 5)
  │   ├── PlayerHealth          (maxHp: 100)
  │   ├── CursedEnergy          (maxEnergy: 100, regenPerSecond: 5)
  │   ├── PlayerAnimationController
  │   ├── PlayerCombatController
  │   ├── LocalNetworkAdapter
  │   └── [Technique] BasicBrawlerTechnique or ProjectileTechnique
  │
  ├── Sprite (child)
  │   ├── SpriteRenderer
  │   └── Animator              (assign controller from BS project assets)
  │
  ├── Hurtbox (child)
  │   ├── CircleCollider2D (IsTrigger = true)
  │   └── Hurtbox script
  │
  ├── MeleeHitbox (child)       (for melee attack window)
  │   ├── BoxCollider2D (IsTrigger = true)
  │   ├── Hitbox script
  │   └── MeleeHitboxController (on parent or here)
  │
  ├── HealthBar_Corner (child)  Screen Space - Overlay Canvas, anchored top-left
  │   └── BG Image + Fill Image (Type=Filled, Horizontal, origin Left) + Heart Image (Animator: default/hit)
  ├── HealthBar_World (child)   World Space Canvas (above head)
  │   └── BG Image + Fill Image (filled) + optional Heart Image (Animator)
  ├── EnergyBar_Corner (child)  Screen Space - Overlay Canvas, top-left (below the HP corner bar)
  │   └── BG Image + Fill Image (filled) + Icon Image (Animator: default/used)
  └── EnergyBar_World (child)   World Space Canvas (above the HP head bar)
      └── BG Image + Fill Image (filled) + optional Icon Image (Animator)
```
Wire `WorldHealthBar` (on the root): cornerRoot=HealthBar_Corner, cornerFill=its Fill, cornerIcon=its Heart Animator, worldRoot/worldFill/worldIcon=the world set. Same for `WorldEnergyBar` with the Energy bar objects (usedState icon). Icons are optional — leave an Animator slot empty to skip that view's reactive sprite. The script disables whichever set the current view doesn't use, and plays `hit`/`used` on the active icon whenever HP/CE drops (else `default`).

**Rigidbody2D settings:**
- Body Type: Dynamic
- Gravity Scale: 0
- Freeze Rotation Z: true
- Collision Detection: Continuous

**Tags:**
- Player 1 root: tag = "Player1"
- Player 2 root: tag = "Player2"

---

## Assets in jjk (already copied)

### Bot character — `CharacterAnims/Bot/`
- `rr.png` / `rr-Sheet.png` — sprite sheet
- `bot_idle_down/right/up.anim`, `bot_walk_down/right/up.anim`, `bot_hurt.anim` — directional anims
- `Bot Player.controller` — animator controller (no parameters; wire Direction + IsMoving if needed)

**Note:** `Bot Player.controller` has no animator parameters by default. Either:
- Add `Direction` (int) and `IsMoving` (bool) parameters in the Animator window and set up transitions, or
- Trigger states directly from `PlayerAnimationController` using `animator.Play("bot_idle_down")` etc.

### VFX — `VFX/`
- `MeleeSmear/` — Smear 01 Horizontal/Vertical PNGs + prefab (auto attack flash)
- `HitFX/` — `sniper_hit.anim`, `slice.anim`, `HitFX.controller`, hit sprites
- `WaveSword/` — `wavesword.prefab` + sheet (use as aimable attack projectile VFX)
- `Projectiles/` — `SniperBullet.prefab`, `Fireball.prefab`, `needle.png`
- `Dimensional_Portal.png` — domain expansion placeholder visual

---

## Adding Photon Fusion (When Ready)

### Step 1: Install Photon Fusion
1. Create an account at photonengine.com.
2. Create an app, get an App ID.
3. In Unity Package Manager → Add package from URL: `https://downloads.photonengine.com/sdk/fusion/Fusion2.zip` (or use UPM feed).
4. Add the App ID to `Fusion/Resources/PhotonAppSettings.asset`.

### Step 2: Create FusionNetworkAdapter
Create `Scripts/Network/FusionNetworkAdapter.cs`:
```csharp
using Fusion;
public class FusionNetworkAdapter : NetworkBehaviour, INetworkAdapter
{
    public bool IsLocalPlayer => Object.HasInputAuthority;
    public bool IsAuthority   => Object.HasStateAuthority;

    [Networked] public float NetworkedHp    { get; set; }
    [Networked] public float NetworkedEnergy { get; set; }

    // Sync HP and energy on State Authority (server/host)
    // Use ChangeDetector or [Networked(OnChanged = ...)] to update local UI

    public void SendInput(PlayerInputData input)
    {
        // Fusion input is sent via INetworkInput struct in SimulationBehaviour
        // Define a FusionPlayerInput struct with the same fields as PlayerInputData
    }
}
```

### Step 3: Define Fusion Input Struct
```csharp
using Fusion;
public struct FusionPlayerInput : INetworkInput
{
    public Vector2 MoveDir;
    public Vector2 AimDir;
    public NetworkBool AutoAttack;
    public NetworkBool AimableAttack;
    public NetworkBool Domain;
}
```

### Step 4: Replace LocalNetworkAdapter
On player prefabs, swap `LocalNetworkAdapter` for `FusionNetworkAdapter`.
Add `NetworkObject`, `NetworkTransform` (position sync), `NetworkRigidbody2D` (if physics).

### Step 5: Authority-Validated Hits
In `Hitbox.cs`, gate damage calls behind `IsAuthority`:
```csharp
if (!_networkAdapter.IsAuthority) return; // Only server applies damage
```

### Step 6: Match Manager → Fusion Room
Replace `MatchManager.StartMatch()` with Fusion's `NetworkRunner.StartGame()`.
Room creation is minimal: host creates, client joins via JoinOrCreate or specific room code.

### Fusion Sync Summary
| Data          | Sync method                     |
|---------------|---------------------------------|
| Position      | NetworkTransform                |
| HP            | [Networked] on FusionNetworkAdapter |
| Energy        | [Networked] on FusionNetworkAdapter |
| IsDead        | [Networked] bool                |
| Input         | INetworkInput struct            |
| Attack events | [Rpc] (fire-and-forget)         |

---

## Current Status

### Works Now
- Local 2-player on same machine
- WASD + Space/E/Q for P1, Arrows + Numpad for P2
- HP system with damage, death, invincibility
- Cursed energy with regen, TrySpend
- BaseCursedTechnique with cooldowns and energy hooks
- BasicBrawlerTechnique (melee hitbox)
- ProjectileTechnique (melee + projectile)
- BaseDomainExpansion stub with energy drain and duration
- PlayerAnimationController (safe parameter setting)
- MatchManager (spawn, death detection, auto-rematch)
- WorldHealthBar / WorldEnergyBar (per-prefab HP + energy bars via events; corner UI for the local player online, world head bar otherwise)
- MatchUI (FIGHT! / PLAYER X WINS!)

### Still Needs Work
- Scene not auto-built (manual setup required per this doc)
- Player prefab not created (manual setup required)
- No art assets copied from BS project yet
- No animator controllers wired up
- Photon Fusion not integrated (stubs in place)
- No audio (hook points exist via events)
- No camera follow script
- No domain expansion concrete implementation
- No cooldown UI indicators
- Mobile joystick support (input abstraction is clean for this)
