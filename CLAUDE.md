# JJK — Cursed Energy Fighting Game

## Project Overview
A bare-bones online 1v1 top-down fighting prototype inspired by anime cursed-energy mechanics.
All characters, names, and art are original. No copyrighted content.

## Harness & Context Engineering

These rules govern *how* every task is executed in this repo. They are non-negotiable and apply to every run, regardless of how small the change seems.

### End-of-run discipline (clean exit)
Before declaring any task complete, the working tree must be in a strictly better state than it started:
- **No dead files.** Delete any script, prefab, scene, or asset that was created during exploration and is no longer referenced. Orphans rot the codebase.
- **No redundancy.** If two scripts, methods, or fields now do the same thing, collapse them into one. Never leave two ways to do the same job.
- **No half-implementations.** Every script that exists must compile *and* be wired into something that uses it. If a feature was abandoned mid-task, remove the partial code — do not leave stubs "for later."
- **No broken references.** After deleting/renaming, search the repo (`grep -r` or Unity's reference search) and fix every caller. Missing-script warnings in prefabs/scenes count as breakage.
- **No suboptimal placeholder.** If a `TODO`/`FIXME`/temporary hack was introduced, either resolve it this run or surface it explicitly to the user before claiming done. Silent debt is forbidden.
- **No commented-out code.** Either it's live or it's deleted. Git history is the archive.
- **No throwaway debug logging.** Remove `Debug.Log` statements added for debugging unless they belong in shipped code (errors/warnings on real fault paths).
- **Folder layout intact.** New files go in the folder that matches their type per `Folder Structure` above. Do not create new top-level directories without explicit user approval.

### Context engineering (work in the right context)
- **Read before writing.** Before editing a file or adding a new one, read the actual current state — never edit from memory or assumption. Files change between sessions.
- **Verify memory against reality.** Auto-memory may reference scripts, classes, or methods that have been renamed or deleted. Confirm every named symbol still exists before relying on it.
- **One responsibility per file.** If a change is making a file do a second unrelated thing, split it instead. Watch for the component stack in `Architecture Summary` — respect those boundaries.
- **Prefer editing over creating.** New files are a last resort. Almost every change belongs in an existing file.
- **No speculative abstraction.** Do not introduce interfaces, base classes, generics, or config knobs for hypothetical future use. Refactor when the second concrete case actually appears.
- **Match existing patterns.** Before inventing a new style of doing something (event wiring, serialization, naming, etc.), find the existing precedent in the codebase and follow it.

### Self-audit before reporting done
At the end of every task, before saying the work is complete, run this checklist mentally:
1. Does every file I touched still compile?
2. Did I delete every file/symbol I orphaned?
3. Is there now exactly one way to do each thing I changed?
4. Are there any `TODO`s, stubs, or `Debug.Log`s I introduced?
5. Did I update or remove any docs/memory entries that my changes invalidated?
6. If the user re-ran the project right now, would it behave as I claim?

If any answer is "no" or "not sure," fix it before reporting. Do not hand back a half-clean tree.

## Engine
- **Unity 2022+ (LTS)**
- 2D top-down perspective
- TMPro for text UI
- Networking: **stub/local only** — Photon Fusion integration ready (see DEV_NOTES.md)

## Folder Structure
```
Assets/
  Scripts/
    Player/          — Movement, health, energy, input, animation, combat controller, AutoAttackController, AimableAttackController, SpriteYSorter, TrainingDummy
    Domain/          — BaseDomainExpansion stub
    Combat/          — Hitbox, Hurtbox, AttackHitboxController
    Network/         — INetworkAdapter, LocalNetworkAdapter (Fusion stub)
    Match/           — MatchManager (spawn, death, blackout + respawn), ScreenEffects
    UI/              — MatchUI, WorldHealthBar, WorldEnergyBar, ScreenBlackout, ComboCounter
  Scenes/
    SampleScene.unity — test scene (set up per DEV_NOTES.md)
```

## Architecture Summary

### Core Rule
Base player components are **shared**. Character differences come from:
1. Configured `AutoAttackController` + (optionally) `AimableAttackController` components on the prefab — tune timing, anim names, hitbox refs, etc.
2. Optionally a child class of `BaseDomainExpansion` on the prefab
3. Serialized stat fields (HP, speed, energy values, cooldowns)

**Never** put character-specific logic inside `PlayerMovement` or `PlayerHealth`.

### Component Stack (on every player prefab)
```
PlayerInputHandler        → reads Unity input, produces PlayerInputData struct
PlayerMovement            → applies movement to Rigidbody2D, drives animator; reflects knockback off walls (OnCollisionEnter2D, `wallBounciness`) during hitstun so a hit player bounces instead of sticking — gated on `IsHitstun` + dynamic body so it works online (authority) and off, and skips kinematic proxies
PlayerHealth              → HP, TakeDamage, Heal, Die, invincibility
CursedEnergy              → energy pool with regen, TrySpend / Drain / Restore
PlayerAnimationController → wraps Animator; all animation calls go through here
PlayerCombatController    → bridges input → auto attack / aimable / domain
PlayerRoll                → universal movement skill (dash + i-frames + CE cost); locks velocity during dash window. Refuses to roll when cursed energy is below the cost (no partial spend)
PlayerOverdrive           → Shift-toggled stance (press on / press off, via Toggle()): drains CE then HP, tints sprite red, pulses the local player's screen white via ScreenEffects.SetOverdriveGlow (a flashing strobe between 0 and peak, gated on INetworkAdapter.IsLocalPlayer so an opponent's overdrive never glows your screen), boosts movement speed via MoveSpeedMultiplier; both attack controllers read IsActive at fire time and apply their overdrive variant (AutoAttackController also swaps to the heavy anim). Forced off on death / round reset. Its HP bleed is self-damage (source == self), which PlayerCombatController.OnDamaged ignores — no hurt flinch/i-frames/feedback
AutoAttackController      → auto attack (startup→active→endlag→cooldown, authored in animation frames) + overdrive heavy variant (bigger/stronger hitbox); optional throwable mode (hitbox dropped on the aim point); owns its "auto attack hitbox" (AttackHitboxController)
AimableAttackController   → aimable attack (hold-to-aim, release-to-fire) as a jump move (startup→jump[i-frames]→impact→endlag, authored in animation frames); overdrive variant scales hitbox size/damage/knockback at release; optional throwable mode (leaps to the aim point); owns its "aimable attack hitbox" (AttackHitboxController); costs cursed energy — refuses to enter aim mode or fire when CE is below the cost (no partial spend)
BaseDomainExpansion       → (subclass, optional) defines domain logic
LocalNetworkAdapter       → implements INetworkAdapter for offline play
Hurtbox                   → receives hits, passes to PlayerHealth. Routes through FusionPlayerCombat.RpcTakeDamage only when actually network-spawned (valid NetworkObject); otherwise applies damage directly (offline players + the training dummy)
AimingReticle             → world-space reticle that swaps between autoSize and aimableSize while AimableAttackController.IsAiming; for a throwable attack it draws a circle marker at the clamped aim point instead
SpriteYSorter             → dynamic top-down Y-sort: ranks all players by world Y each frame (static registry) and sets each main sprite's sortingOrder = baseSortingOrder + frontRank, so the lower-Y player draws in front. Orders stay in a tight band (base 51 →) below the head bars (100) / reticle (50). Wire targetSprite to the character's main SpriteRenderer.
```

### Input (PlayerInputData struct)
Network-safe. `PlayerInputHandler` fills it from Unity input.
Future: network layer intercepts and replaces it with received data.

### Animation State Naming (8-direction)
`PlayerAnimationController` drives the animator by **state name**, never by parameters (see project memory note).

**Default state names** (when `characterPrefix` is empty — the default):
```
{action}-{dir}
```

**Prefixed state names** (when `characterPrefix` is set, e.g. `sukuna`):
```
{characterPrefix}-{action}-{dir}      →  sukuna-idle-s, sukuna-punch-se
```

- `action` — `idle`, `walk`, `hurt`, `death` are built-in; `punch`, `skill`, and `roll` are the defaults for auto attack / aimable attack / dodge roll, configurable per-player (`autoAttackAnim` on `AutoAttackController`, `aimableAttackAnim` on `AimableAttackController`, `rollAnim` on `PlayerRoll`). So a sword character could set `autoAttackAnim = "slash"` and the controller will play `slash-se`, etc.
- **Overdrive auto attack** resolves dynamically: the controller first tries `overdrive-{autoAttackAnim}-{dir}` (e.g. `overdrive-punch-se`, `overdrive-slash-se`) and only falls back to `overdriveAttackAnim`-`{dir}` (default `heavy-{dir}`) when the convention-named state isn't authored. Author per-character heavies by naming alone; set `overdriveAttackAnim` only to override the fallback.
- **Overdrive idle / walk** follow the same convention: while `PlayerOverdrive.IsActive`, the controller looks for `overdrive-idle-{dir}` and `overdrive-walk-{dir}` first and silently falls back to the base `idle`/`walk` clips when absent. Authoring the overdrive set gives the stance a distinct standing/movement look; skip it and overdrive uses the normal idle/walk.
- `hurt`, `hurtf`, `death`, and `roll` are direction-less (single clip); everything else gets a `-{dir}` suffix. `hurtf` is the held-hurt pose. The hitstun window is `max(<hit's hitstun>, hurt clip length)` — hitstun never ends mid-`hurt`. Damage-invincibility on being hit is decoupled from this stun: it lasts only `hurtInvincibilityDuration` (default 0.1s, much shorter than the stun) so follow-up hits can combo while the victim is still staggered (movement/anim locked). Set `hurtInvincibilityDuration` to 0 for no i-frames on hit. **Combo decay:** `PlayerAnimationController` counts consecutive in-hitstun hits (`_comboHits`, incremented in `PlayHurt` while already `_locked`, reset to 0 when `HurtLock` finishes / on death / on reset) and exposes `ComboKnockbackMultiplier` (= `1 + (hits-1) * comboKnockbackGrowth`, capped at `comboKnockbackMax`). The damage path (`Hurtbox.ReceiveHit` offline, `FusionPlayerCombat.RpcTakeDamage` online) reads it right after `TakeDamage` and scales the knockback impulse, so each successive combo hit launches the victim further until they're out of range and the combo self-terminates; the multiplier resets to 1 once the victim recovers. Each `Hitbox` sets its own `hitstun` (seconds) in the inspector, carried to the victim via `ReceiveHit` → `PlayerAnimationController.SetNextHitstun`; `hurtLockDuration` on `PlayerAnimationController` is the fallback used only for damage with no source hitbox that still routes through `PlayHurt` (overdrive HP bleed is **not** one of these — it's self-damage and `PlayerCombatController.OnDamaged` skips it entirely, so no hurt anim plays). When `hurtf` is authored and the window is long enough to split, the controller plays it as: the first `hurtIntroSeconds` (default 0.15s) of `hurt` → hold on `hurtf` for the remaining lock → the last `hurtOutroSeconds` (default 0.375s) of `hurt`. **These bookends are in seconds, not clip frames, on purpose** — a clip's `frameRate`/`m_SampleRate` is the sampling rate, not the sprite display rate (e.g. a clip sampled at 100 with sprites every ~0.075s), so a frame-count basis would shrink the intro/outro to near-zero and it'd jump straight to `hurtf`. If `hurtf` isn't authored (or the clip/lock is too short to split), it simply holds `hurt` for the whole window and the animator rests on the last frame.

**Wall splat:** the damage path records the applied knockback magnitude (`SetLastHitKnockback`) and immediately calls `CheckWallSplatOnHit`. A wall hit fires (via `NotifyWallHit`) when the knockback was `≥ wallHurtKnockbackThreshold` (default 7 ≈ an overdrive hit) AND the victim is at a wall — detected two ways: **adjacency at hit time** (`PlayerMovement.IsAdjacentToWall`, an OverlapCircle for a solid non-player collider within `wallCheckRadius`, so being pinned against a wall counts even with no fresh collision) or **knocked into one** later (`OnCollisionEnter2D` on a non-player collider — PlayerMovement for fighters, TrainingDummy for the dummy since its PlayerMovement is disabled). On a wall hit, the controller swaps the running hurt recovery for a `wall_hurt`/`wall_hurtf` sequence (same intro→hold→outro split as `hurt`/`hurtf`), deals fixed `wallSplatDamage` chip damage via `PlayerHealth.TakeReactionlessDamage` (no extra hurt reaction; still updates the bar and can kill), and the wall hitstun **decays by `wallHitstunDecay` (default 0.5 = 50%) per consecutive wall hit** (`_wallHitCount`, reset on recovery) so it can't lock forever. Once per hit (`_wallReactionDone`); silently no-ops if `wall_hurt` isn't authored. Online it runs on the wall-splatted player's own authority; proxies show normal `hurt` (position still syncs).
- `dir` — one of `-s`, `-se`, `-ne`, `-n`. Only **4 unique directional clips** per action — east folds onto `-se`, and west / southwest / northwest are produced by mirroring `-se` / `-ne` via `SpriteRenderer.flipX`. **Attack actions** (`PlayAutoAttack` / `PlayAimableAttack`) additionally accept an optional `-e` clip: if the controller defines `{action}-e`, east/west uses that instead of the folded `-se` (west still mirrors via flipX).

The controller sectors the input vector into 8 compass directions (45° per sector) and picks the right clip + flipX automatically. `TryPlay` silently no-ops on states the animator doesn't have, so a partial clip set is safe while authoring art.

Default clip list (no prefix), per character:
`idle-s`, `idle-se`, `idle-ne`, `idle-n`,
`walk-s`, `walk-se`, `walk-ne`, `walk-n`,
`punch-s`, `punch-se`, `punch-ne`, `punch-n`, *(optional)* `punch-e`,
`skill-s`, `skill-se`, `skill-ne`, `skill-n`, *(optional)* `skill-e`,
`overdrive-punch-s`, `overdrive-punch-se`, `overdrive-punch-ne`, `overdrive-punch-n`, *(optional)* `overdrive-punch-e`,    *(per-character overdrive auto-attack — substitute your `autoAttackAnim` name for `punch`. Authoring this set activates the convention path; if absent, falls back to `overdriveAttackAnim`, default `heavy-{dir}`.)*
`overdrive-idle-s`, `overdrive-idle-se`, `overdrive-idle-ne`, `overdrive-idle-n`,    *(optional overdrive standing pose — falls back to base `idle-{dir}`)*
`overdrive-walk-s`, `overdrive-walk-se`, `overdrive-walk-ne`, `overdrive-walk-n`,    *(optional overdrive moving pose — falls back to base `walk-{dir}`)*
`hurt`, `hurtf`, `death`, `roll`,    *(direction-less single clips. `hurtf` is optional — held-hurt pose used when hitstun outlasts the `hurt` clip.)*
*(optional)* `wall_hurt`, `wall_hurtf`.    *(direction-less wall-splat reaction played when a hard hit knocks the player into a wall; same intro/hold/outro split as `hurt`/`hurtf`. Omit them and hard wall hits just use normal `hurt`.)*

8-direction → clip mapping:

| Input dir | Clip suffix | flipX |
|-----------|-------------|-------|
| S | `-s` | false |
| SE | `-se` | false |
| E | `-se` | false |
| NE | `-ne` | false |
| N | `-n` | false |
| NW | `-ne` | true |
| W | `-se` | true |
| SW | `-se` | true |

## Networking Approach
- **Now:** Local 2-player, both on same machine
- **Next:** Photon Fusion (see DEV_NOTES.md for setup)
- All gameplay code calls `INetworkAdapter` — `LocalNetworkAdapter` covers offline; `FusionPlayerSync` (paired with `FusionPlayerMovement` + `FusionPlayerCombat`) covers Shared Mode online
- Never trust client damage claims: authority validates hits when networking is active

## Coding Standards

### Style
- Small files, one class per file, clear name = type
- `SerializeField` private fields; public only what other scripts genuinely need
- Events (`Action<>`) for cross-component communication; no direct polling
- No magic numbers — use serialized fields or named constants
- No giant god classes; split responsibilities by component

### Comments
Write comments only when the WHY is non-obvious. No docblocks on straightforward methods.

### Naming
- Classes: `PascalCase`
- Methods: `PascalCase`
- Private fields: `_camelCase` (underscore prefix)
- Constants: `PascalCase`
- Animator params: match existing controller exactly

### Error Handling
- Null-check components that might be missing (`?.` operator)
- Log warnings for missing required references, don't throw
- Don't validate internal invariants that can't break

## How to Add a New Character

1. Duplicate an existing player prefab (`Player1` or `Player2`).
2. Tune the `AutoAttackController` component:
   - Pick an `autoAttackAnim` (e.g. `punch`, `slash`) — controller plays `{anim}-{dir}`.
   - Set the phase lengths in **animation frames**: `autoAttackStartupFrames` / `autoAttackActiveFrames` / `autoAttackEndlagFrames` (these three should sum to your attack animation's frame count — they're spread across the full clip, so the swing always plays in full) plus `autoAttackCooldownFrames` (same frame unit). No frame rate to set; timing auto-scales to the clip's length.
   - (Optional) `takeDirection` — true plays `{anim}-{dir}` and aims the hitbox; false plays the bare `{anim}` clip with the hitbox at its authored placement.
   - (Optional) `throwable` — true drops the hitbox at the aim point (clamped to `throwRadius`) instead of a directional swing; the reticle becomes a circle at that point.
   - Wire its `autoAttackHitbox` field to a child `AttackHitboxController` whose `Hitbox` is sized for the character's reach (or set that controller's `isProjectile` + `projectilePrefab`/`projectileSpeed`/`projectileDuration` to fire a projectile instead — see **Attack Phases → Projectile**).
   - Set the overdrive variant fields (`overdriveAttack*Frames`, damage multiplier, optional `overdriveAttackAnim` fallback).
3. (Optional) Add an `AimableAttackController` for ranged characters:
   - Pick an `aimableAttackAnim` (e.g. `skill`, `shoot`).
   - Set the phase lengths in **animation frames**: `aimableAttackStartupFrames` / `aimableAttackJumpFrames` / `aimableAttackImpactFrames` / `aimableAttackEndlagFrames` (these four sum to your animation's frame count — spread across the full clip, as above) plus `aimableAttackCooldownFrames` (same frame unit) and `aimableAttackEnergyCost` (energy, not frames). The `jump` phase is invincible (i-frames) with no hitbox; `impact` enables the hitbox.
   - (Optional) `takeDirection` (as above) and `throwable` — when throwable, releasing leaps the player to the clamped aim point and the hitbox lands there; the reticle becomes a circle at that point.
   - Wire its `aimableAttackHitbox` field to a separate `AttackHitboxController` child (longer reach than the auto's).
4. Tune the `AimingReticle` `aimableSize` to match the aimable's projected reach (or `throwMarkerSize` if the attack is throwable).
5. (Optional) Add a `BaseDomainExpansion` subclass in `Scripts/Domain/`.
6. Tune serialized values: `maxHp`, `moveSpeed`, `maxEnergy`, damage, etc.
7. Assign the character's animator controller to the Animator component.
8. Test locally; when Fusion is online: test sync with a remote client.

### Attack Phases
Both controllers author phase lengths in **animation frames** (int) that spread across the whole attack clip. At fire/release time the controller reads the length of the clip it just played (`PlayerAnimationController.CurrentClipLength()`) and computes `secondsPerFrame = clipLength / spanFrames`, where `spanFrames` is the sum of the phases that cover the animation (auto: startup+active+endlag; aimable: startup+jump+impact+endlag). So those phases always fill the full clip — the swing/leap plays in its entirety — and each phase's share is its frame fraction. Cooldown uses the same `secondsPerFrame`. There's no frame rate to configure; the attack auto-scales to each clip's length (falls back to 1/60s per frame only when no clip is readable). PlayerRoll still uses seconds — only the two attack controllers are frame-based.

Each controller also has an **`{auto,aimable}AttackPlaybackSpeed`** (default 1, range 0.1–4): the animator plays the attack clip at that rate (`PlayerAnimationController` sets `Animator.speed`) and `secondsPerFrame` is divided by it, so the phase windows + cooldown stay in lockstep with the faster (>1) or slower (<1) animation. Independently, **per-phase frame scales** (`startupFrameScale`/`activeFrameScale`/`endlagFrameScale` on auto; `startup`/`jump`/`impact`/`endlagFrameScale` on aimable, default 1) multiply each phase's frame count before the split, reshaping *where* the phase boundaries (e.g. the active/impact hitbox window) fall within the clip without re-authoring the ints. Because the phases always renormalize to fill the clip, the total swing duration stays the clip length — the scales move the boundaries, playback speed changes the overall rate. The animator speed is reset to 1 on every exit from an attack (`RefreshMovementState`/`PlayHurt`/`PlayDeath`/`PlayRoll`/`ResetState`) so a sped-up swing never bleeds into hurt/idle/walk. Online, the proxy-side RPC handlers in `FusionPlayerCombat` pass the proxy's own (identical serialized) `PlaybackSpeed` into `PlayAutoAttack`/`PlayAimableAttack`, so the replicated swing runs at the same rate.

The **auto attack** runs as: **startup → active → endlag → (cooldown completes)**. During the whole startup+active+endlag window the player keeps a fraction of move speed (`AutoAttackController.attackMoveSpeedMultiplier`, default 0.35) so they can drift to reposition mid-swing — the swing animation itself is frozen by the animator's attack lock, so the drift never disturbs it. The hitbox is only enabled during the active phase. Cooldown is measured from attack start — if it's shorter than `startup + active + endlag`, there is no extra wait after recovery. Taking a hit cancels an in-progress attack (`AutoAttackController.Cancel()` / `AimableAttackController.Cancel()`), which also tears the hitbox down.

The **aimable attack** is a **jump move**: **startup → jump → impact → endlag → (cooldown completes)**. Movement is locked for the whole window. During the `jump` phase the player is invincible (`PlayerHealth.SetInvincible(true)`), the hitbox is off, and the player's root GameObject is parked on `jumpLayer` (serialized, default 2) so its body collider doesn't collide with the other player — it's the airborne leap. On `impact` the original layer is restored, the hitbox enables, and the player is vulnerable again. `Cancel()` drops the i-frames and restores the layer in case it interrupts mid-jump. (Configure the Physics2D collision matrix so `jumpLayer` actually ignores the opponent; note the Hurtbox lives on its own child layer, so this swap governs body collision, not hitbox detection — damage immunity during the leap comes from the i-frames.)

**Throwable** (`throwable` on either controller) makes the attack target a world point — the aim point clamped to `throwRadius` around the player — instead of a heading. The hitbox is dropped on that point (`AttackHitboxController.PlaceAt`) rather than offset along the aim (`Orient`). For the aimable, the `jump` phase carries the player to the point (`Rigidbody2D.MovePosition` lerp, immune to drag); for the auto, the player stays put and only the hitbox lands there. Input carries the point via `PlayerInputData.AimPoint`; the reticle swaps its rectangle for a circle marker at the clamped point.

**Projectile** (`isProjectile` on `AttackHitboxController`, default false) changes how that controller's active/impact window manifests: instead of toggling its in-place collider, `Enable` spawns the `projectilePrefab` (a `Projectile`-component prefab carrying its own `Hitbox`) and `Disable` becomes a no-op — the projectile lives by its own `projectileDuration`, not the attack's active frames. A directional attack launches it from the player along the aim at `projectileSpeed`; a `throwable` attack spawns it stationary at the clamped aim point ("just appears" there). Damage/knockback/size multipliers (overdrive, etc.) pass straight through to the spawned `Hitbox`. The owned `Hitbox` is unused in projectile mode (may be left unassigned). Projectiles are spawned locally (no Fusion replication yet) — online proxies won't see the projectile, though damage still routes through `Hurtbox`/`RpcTakeDamage` on the authority.

Aimable attack is **hold-to-aim / release-to-fire**: pressing the aimable key enters aim mode (`AimableAttackController.IsAiming` flips true, `AimingReticle` swaps to its `aimableSize`); releasing the key calls `ReleaseAttack`, which spends cursed energy and runs the jump-move phase pipeline. Both `StartAiming` (via `CanStartAiming`) and `ReleaseAttack` require enough CE for the full cost — short on energy, you can't aim or fire (the release re-checks because overdrive can drain CE mid-aim).

## Training Dummy
`Assets/Prefabs/TrainingDummy.prefab` is a copy of `Player.prefab` with a `TrainingDummy` component on the root. That component disables every control + aiming + networking behaviour (`PlayerInputHandler`, `PlayerMovement`, `PlayerCombatController`, both attack controllers, `PlayerRoll`, `PlayerOverdrive`, `AimingReticle`, and the three `Fusion*` scripts) in `Awake` — before their `Start` runs, so it never takes input, moves, aims, or fights back. It keeps the health/animation/hurtbox/HUD stack and:
- **Bars on its head:** calls `WorldHealthBar.ForceWorldView()` / `WorldEnergyBar.ForceWorldView()` so HP/CE float above the dummy instead of hijacking the local-player corner UI (the view decision is otherwise driven by `FusionPlayerSync`, which stays valid even when the component is disabled).
- **Plays its own hurt** on `PlayerHealth.OnDamaged` (the disabled `PlayerCombatController` no longer does, and that also drops its screen shake/flash).
- **Never dies:** a would-be killing blow heals it back to full inside `OnDamaged`, before `PlayerHealth`'s death check.
- **Resets** to full HP at its start position after `resetDelay` seconds without damage, or immediately on **Enter** (Return / Keypad Enter). A reset also clears the readout and tops every real player's **HP + CE** back to full via `RestorePlayers` — HP/CE only, never their position or control, and never a round-reset/blackout (skips dummies and online proxies).
- **Detailed readout:** tracks the current true-combo count, last hit damage, and combo total (victim-side, off its own `IsHitstun`) and draws them top-right via OnGUI. `Hitbox` skips the global corner `ComboCounter` for dummy victims so there's no top-left clutter.

Its attack hitboxes stay off because `AttackHitboxController.Awake` disables them and nothing re-enables them. Best used as a **solo / offline** practice target (it's not driven to attack and its networking is disabled); damage still reaches it because `Hurtbox` routes online only when there's a live `NetworkObject` and Fusion RPCs deliver to spawned-but-disabled behaviours.

## How to Add a Domain Expansion

1. Create `YourDomain.cs` in `Scripts/Domain/`, inherit `BaseDomainExpansion`.
2. Override `OnActivated()` — spawn domain VFX, enable domain collider, etc.
3. Override `OnDeactivated()` — clean up effects.
4. Override `CanActivate()` if the domain has special prerequisites.
5. Tune `activationEnergyCost`, `energyDrainPerSecond`, `maxDuration` in Inspector.
6. Assign the component to a character prefab.

## What NOT to Do
- Do not put character-specific code in `PlayerMovement` or `PlayerHealth`
- Do not let attack controllers own a separate energy variable — use `CursedEnergy` on the player
- Do not directly set `Animator` parameters from combat or movement scripts — go through `PlayerAnimationController`
- Do not trust client-side damage claims when networking is live
- Do not add rollback netcode — that is future scope
- Do not add features beyond the current milestone before the core loop is solid
- Do not write multi-line comment blocks or docstrings on simple methods
- Do not leave orphaned scripts, redundant components, commented-out code, or stray `Debug.Log` calls after a task — see `Harness & Context Engineering → End-of-run discipline`
- Do not create a new file when an existing one is the right home for the change
- Do not edit a file from memory — read its current state first

## How to Run/Test
1. Open `Assets/Scenes/SampleScene.unity` in Unity Editor.
2. Set up the scene per `DEV_NOTES.md → Scene Setup`.
3. Press Play.
4. P1: WASD to move, **Left Click** auto attack (aimed at cursor), **Right Click** roll (dash + i-frames, costs CE), **Q (hold to aim, release to fire)** aimable attack, **E** domain, **Left Shift (toggle)** overdrive — press to turn on (drains CE then HP, glows red, turns the next Left Click into the heavy auto variant), press again to turn off.
5. P2: Arrow keys to move, **Numpad1** auto attack (auto-aimed at P1), **Numpad0** roll, **Numpad2 (hold/release)** aimable, **Numpad3** domain, **Right Shift (toggle)** overdrive.
6. When a player hits 0 HP they play their death animation, both players freeze, the screen blacks out (~3s), then both respawn at full HP/CE on their own spawn points, via `ScreenBlackout`. Offline this is orchestrated by `MatchManager`; online (the `Arena` scene's `GameLauncher`/Fusion path) each peer runs the reset in `FusionPlayerSync` — it watches every player it knows (own + opponent proxies) and, on any death, freezes the local player (static `RoundResetting`, read by `FusionPlayerMovement`/`FusionPlayerCombat`), blacks out its own screen, and respawns its own player at its spawn. Death/respawn animation replicates to proxies through `NetworkedDead`.

## HUD / Visible HP & Energy
Live, event-driven views (no polling). Each player prefab carries `WorldHealthBar` + `WorldEnergyBar`, which pick their presentation by who's viewing — no separate scene HUD object. Both reference two pre-authored, prefab-child object sets and activate only the one for the current view (so respawns rebind automatically; nothing to instantiate). The choice is **resolved per-frame, not at `Awake`** — Fusion only assigns the `NetworkObject` in `Spawned()` (after `Awake`), so authority isn't known at startup; both sets start hidden and the correct one switches on once the view resolves (same per-frame pattern as `PlayerMovement` / `AimingReticle`, with a possible 1-frame transient):
- **`cornerRoot` / `cornerFill`** — a Screen Space - Overlay Canvas group anchored top-left (pins to the corner regardless of player position) with a filled `Image` (Type = Filled, Horizontal, origin Left). Used for the **local player in online play**.
- **`worldRoot` / `worldFill`** — a world-space Canvas floating above the head (via `offset`) with a filled `Image`. Used for the **online opponent and for both players offline**. Only the head bar honours `hideWhenFull`; the corner UI is permanent.

A static icon sprite (heart / CE) can sit alongside each fill; the bars do not drive it (the icons are plain `Image`s, no `Animator`). Both bars **flash the fill toward `flashColor` (white) whenever the value changes** (gain or loss), fading back to the fill's authored colour over `flashDuration` — a quick visual cue that HP/CE moved.

- **`WorldHealthBar`** — online local player → top-left corner UI; everyone else (online opponent, both offline) → world head bar.
- **`WorldEnergyBar`** — online local player → top-left corner UI; offline → world head bar; **online opponent → hidden entirely** (you never see the enemy's cursed energy, keyed off `FusionPlayerSync.IsAuthority`).
- **`ComboCounter`** — reference-only **true-combo** readout. `Hitbox` captures the victim's pre-hit `IsHitstun` and calls `ComboCounter.Instance.Register(owner, victimAnim, victimWasStunned)` on each connecting hit (attacker authority only; whiffs on dead/i-framed victims are skipped). The combo grows only while the victim is kept in hitstun — a hit on a recovered victim restarts it at 1 — and clears once the victim recovers (after a brief display hold). Self-building singleton (no scene wiring), drawn via OnGUI. Online a peer only tallies its own local player's hits.
