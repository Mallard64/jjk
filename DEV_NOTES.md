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

## Online Play — Photon Fusion 2, Host Mode

Photon Fusion 2 is installed (`Assets/Photon/Fusion`, build 2.0.12). Online play runs in **Host mode**:
one peer is the server *and* a fighter, the other is a pure client.

### Who does what

| Role   | State authority | Input authority | Simulates |
|--------|-----------------|-----------------|-----------|
| Host   | both fighters   | its own fighter | everything: movement, attacks, damage, CE/HP drain, domains, scoring |
| Client | none            | its own fighter | nothing — it sends input and renders replicated state |

This is why `INetworkAdapter` has two flags that used to mean the same thing under Shared Mode:

- `IsAuthority` → **state** authority: "do I simulate this fighter?" Gate anything that must happen
  exactly once (damage, resource drain, scoring) on it.
- `IsLocalPlayer` → **input** authority: "is this the fighter I control?" Gate local-view concerns
  (aiming reticle, corner HUD, screen shake, combo readout) on it.

Getting these backwards is the main failure mode: keying HUD off `IsAuthority` puts the opponent's bar
in the host's corner, and keying damage off `IsLocalPlayer` lets a client claim hits.

### Running a match
1. Open `Assets/Scenes/Arena.unity` (it must be in Build Settings — `SceneRef.FromIndex` uses its index).
2. Confirm your App ID is set in `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset`.
3. Build and run on machine A, type a room name, press **Host Match**.
4. Run on machine B with the same room name, press **Join Match**.

Both peers must be on the same Photon region for the room to be visible.

### What syncs, and how

| Data                                     | Mechanism |
|------------------------------------------|-----------|
| Position / velocity                      | `NetworkRigidbody2D` (server-driven, interpolated on remotes) |
| HP / CE / dead / round wins              | `[Networked]` on `FusionPlayerSync`, mirrored in `Render()` |
| Move + aim direction                     | `[Networked]` on `FusionPlayerMovement` |
| Overdrive / domain / aiming state        | `[Networked]` on `FusionPlayerCombat`, mirrored in `Render()` |
| Round freeze                             | `[Networked] NetworkedRoundResetting`, read via the static `FusionPlayerSync.RoundResetting` |
| Player input                             | `FusionPlayerInput` (`INetworkInput`), polled once in `GameLauncher.OnInput` |
| Attack / roll / hurt one-shots           | `[Rpc]` — `RpcTargets.All`, `InvokeLocal = false` |

**Never use `RpcTargets.Proxies`.** In host mode the client controlling a fighter is that object's *input*
authority, not a proxy, so a Proxies-only RPC skips exactly the peer that pressed the button.

### Input and button edges
`GameLauncher.OnInput` is the single input poll per peer. Buttons ride as `NetworkButtons` carrying the raw
**held** state; the server derives edges with `GetPressed` / `GetReleased` against the previous tick. That is
what makes hold-to-aim / release-to-fire survive a dropped packet, and why a key held through a round-reset
freeze does not fire the moment control returns. `PlayerButton`'s ordering is part of the wire format —
append, never reorder. If you add fields to `FusionPlayerInput`, raise
`Simulation.InputDataWordCount` in `Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion`
(currently 8 words; the struct uses 5).

### Known limits
- **Combat actions still cost one RTT on a client.** Movement is predicted, but attacks, rolls and domains
  are resolved server-side and replicated by RPC, so there is a round trip between the press and the swing.
  Predicting them would mean making every attack controller's phase state `[Networked]` and rewindable —
  a much larger change than the movement prediction, and easy to desync.
- **Projectiles are not replicated.** `AttackHitboxController` uses a plain `Instantiate` on the simulating
  peer, so a projectile attack is visible on the host but not on the client. Damage still resolves correctly
  server-side. Fixing this means putting a `NetworkObject` on each projectile prefab and using `Runner.Spawn`.
- **No host migration.** If the host leaves, the match ends.

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
