# Resonance

A 2D top-down 1v1 fighting game in Unity. Every action — dodging, the special, the power stance, the
null field — spends from one regenerating **resonance** pool, so a match is a resource duel as much as a
reflex test. Plays local 2-player, against a bot, or online over Photon Fusion 2. All characters, names
and art are original.

**Unity 2022.3.62f2 (LTS)** · **C#**, 43 scripts / ~6,800 LOC · **Photon Fusion 2 (Host mode)** · TextMeshPro · Tilemap + Aseprite importer

> **Status:** playable prototype — the full combat loop runs offline and online in a best-of-5 match, but it is a gameplay-engineering prototype, not a shipped game. See [Limitations](#limitations).

## Demo

![Local 1v1 gameplay — attacks, hit reactions, and the resonance HUD](docs/demo.gif)

*An aimed auto attack connecting, hitstun and screen-flash feedback on the victim, the opponent's floating health bar, and the local player's HP (red) / resonance (blue) in the corner HUD.*

## Mechanics

| System | What it does |
|---|---|
| **Movement** | 8-direction top-down movement on a `Rigidbody2D`, Y-sorted so the lower fighter draws in front. |
| **Resonance** | One regenerating pool gating every ability. Run dry and you can't dodge, special, or power up. Attacking suppresses regen, so poking constantly starves you. |
| **Auto attack** | Frame-authored melee swing (startup → active → endlag → cooldown). Optional throwable or projectile mode. |
| **Aimable attack** | Hold-to-aim, release-to-fire leap with i-frames on the jump. Costs resonance; refuses to fire when short. |
| **Roll** | Universal dodge: dash + i-frames, at a resonance cost. |
| **Overdrive** | Toggled stance that spends resonance — then health — for more speed and heavier attacks. |
| **Null Field** | A resource-warfare state machine, deliberately *not* a guaranteed-hit super (below). |
| **Combos** | True-combo tracking with knockback that grows per hit, plus a wall-splat reaction when a hard hit pins you to a wall. |
| **Practice modes** | A heuristic bot (weighted move selection, scripted combo strings, light per-match adaptation) for Practice and Bot-vs-Bot, plus a training dummy with a live damage/combo readout. |

### Null Field

Not a cinematic win button but an economic gamble: a chunk of resonance up front, then a drain every
second it stays open. Yours grants a free overdrive and a speed bonus while it bleeds your meter, freezes
regen for *both* players, and locks the opponent out of their overdrive. If both open one it becomes a
**clash** — drain doubles, and whoever empties first collapses into a burnout window.

## Architecture

**Base components are shared; characters differ only by configuration.** Character-specific logic never
enters core movement or health. Every fighter is a stack of single-responsibility components:

```
PlayerInputHandler         Unity input → a network-safe PlayerInputData struct
PlayerMovement             applies movement, drives the animator, handles wall knockback
PlayerHealth               HP, damage, death, invincibility
Resonance                  the shared resource pool (regen / spend / drain)
PlayerAnimationController  the ONLY script that talks to the Animator (by state name)
PlayerCombatController     routes input → AutoAttackController / AimableAttackController / BaseNullField
PlayerRoll · PlayerOverdrive · BaseNullField    the dodge, the power stance, the field state machine
```

A new character is a duplicated prefab with different serialized values — frame counts, animation names,
hitbox references, costs, stats — plus optionally a `BaseNullField` subclass. No character `if`/`switch` exists in the core.

- **Input as a struct**, so the network layer swaps local input for received input without touching gameplay code, and **networking behind `INetworkAdapter`** — the same combat code runs offline and online.
- **Animator by state name, never by parameters.** One choke point calling `Animator.Play(state)`.
- **8-direction art from 4 clips** — west mirrors east via `SpriteRenderer.flipX` — and **frame-based attack timing**: phases are integer frame counts that renormalize to fill whatever clip is playing, so timing tracks the art instead of hardcoded seconds.

## Netcode

Photon Fusion 2 in **Host mode**: one peer is the server *and* a fighter, the other joins as a client.

- **The server has state authority over both fighters** and is the only peer that simulates movement, attacks, damage, resource drain and scoring. A client has **input authority over its own fighter only** — it sends a `FusionPlayerInput` struct (move direction, aim point, buttons as `NetworkButtons`) and renders replicated state.
- **Clients never assert damage.** `Hitbox` returns early unless the peer has state authority, so hits resolve server-side; the reaction (hurt animation, screen feedback, combo tally) then replicates by RPC.
- **Client-side prediction, movement only.** The client runs the same movement tick from its own input and Fusion reconciles against server snapshots, so local movement has no round-trip delay. Everything a predicted tick reads has to be `[Networked]` — the movement gate, the roll's dash, the leap, and even the previous button state, since a plain field wouldn't rewind on a resimulated tick.
- **Combat is deliberately *not* predicted.** The attack controllers hold coroutine and timer state a client can't reproduce or rewind, so they stay server-side and reach other peers by RPC.
- **Two authority questions, kept separate:** `IsAuthority` ("do I simulate this?" — the server, for both fighters) gates damage, drain and scoring; `IsLocalPlayer` ("is this the fighter I control?" — input authority, one per peer) gates the HUD, reticle and screen shake. Remote fighters are smoothed by Fusion's own `NetworkRigidbody2D` interpolation between confirmed snapshots — no second smoothing layer, no extrapolation.

## Running it locally

**Prerequisites:** Unity **2022.3.62f2** via Unity Hub.

1. Clone the repo and open the project folder in Unity Hub.
2. **Install Photon Fusion 2.** The SDK is licensed per developer by Exit Games and is *not* redistributed
   here, so the project won't compile until you add it: make a free account at
   [photonengine.com](https://www.photonengine.com/), import the **Fusion 2** Unity package, and paste your
   own **Fusion App ID** into the Photon App Settings asset via the Fusion Hub.
3. Open `Assets/Scenes/Arena.unity` and press **Play**.
4. The launch menu offers **Host Match** / **Join Match** (online) and **Practice vs Bot** / **Bot vs Bot**
   (offline, no App ID needed). To test online on one machine, use ParrelSync to run a second instance.

| Action | Player 1 | Player 2 |
|---|---|---|
| Move | `WASD` | Arrow keys |
| Auto attack | Left Click | Numpad 1 |
| Aimable special (hold → release) | `Q` | Numpad 2 |
| Roll | Right Click | Numpad 0 |
| Overdrive (toggle) | Left `Shift` | Right `Shift` |
| Null Field | `E` | Numpad 3 |

## Limitations

- **Scene and prefab wiring is manual.** Player prefabs are assembled from a documented component stack; there's no one-click bootstrap.
- **Online is 1v1 best-of-5 only** — host/join by room name, no matchmaking, reconnection or spectators.
- **Projectiles aren't network-replicated.** Damage resolves correctly on the server, but a client won't see the projectile.
- **No rollback netcode**, and **only one character** — the configuration-driven character pipeline exists, but a single fighter is authored.
- **Art is placeholder.** The animation, VFX and audio systems are real; the sprites are stand-ins.
