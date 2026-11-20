# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

"Blade & Reflex" — a final-year-project third-person melee combat game (Paladin player: sword & shield). The Unity project lives in the `FYP/` subfolder, not the repo root. The game design doc is `FYP/Blade_and_Reflex_GDD.docx`.

- Unity **6000.6.4f1** (Unity 6), URP 17, new **Input System** (`com.unity.inputsystem`) — legacy `Input.*` is not used.
- Main scene: `FYP/Assets/Scenes/SampleScene.unity`.
- Gameplay code: `FYP/Assets/Scripts/` (default `Assembly-CSharp`, no asmdefs). Editor-only code goes in `FYP/Assets/Scripts/Editor/`.
- No automated tests exist yet (Test Framework package is installed but unused). There is no CLI build/lint step; work is verified by entering Play Mode in the Unity Editor and reading the `[Combat]` / `[Health]` console logs.
- When adding/renaming scripts or assets, keep the matching `.meta` files in git. Don't edit `Library/`, `Temp/`, `Logs/`, `UserSettings/` or the generated `.csproj`/`.sln`.

## Architecture

All gameplay sits on the Player root GameObject as cooperating components that find each other via `GetComponent` in `Awake` (serialized references are optional and auto-filled):

- **`PlayerController`** — movement, turning (mouse/A-D; tank-style, W/S forward/back), run, roll (Space / pad East), cursor lock. Requires `CharacterController`. **No root motion anywhere**: rolls and attack steps are moved in script by sampling the Animator's `normalizedTime` for the state carrying a given *tag* (`"Dodge"`, `"Attack"`) and moving along an `AnimationCurve` share of `rollDistance` / `attackDistance`. It auto-adds `ThirdPersonCamera` to `Camera.main` if missing. Exposes `IsDodging`.
- **`CombatStateMachine`** — `Idle → Windup → Active → Recovery → Idle`, plus `Stunned`. LMB = light attack, hold RMB / pad LB = block. Transitions Windup→Active→Recovery→Idle are driven by **Animation Events** `ActiveStart`, `ActiveEnd`, `AttackEnd` on the attack clip (the receiver methods must keep those exact names; a fail-safe forces Idle after `failSafeSeconds`). Hit detection runs only in `Active`: an overlap sphere filtered to a forward arc, each `Health` hit once per swing. Exposes `CanMove`, `IsBlocking`, `State`, `StateChanged`.
- **`Health`** — shared by player and enemies. `TakeHit(damage, attacker)` resolves in order: dodging → `Dodged`; blocking with attacker inside `blockArc` → `Blocked` (chip damage); else `Hit`. Raises `Damaged`, `HitReceived` (CombatStateMachine listens for stun/knockback/block-impact), `Died`, `Refilled`. Draws its own OnGUI health bar.
- **`TrainingDummy`** — test enemy (requires `Health`) that telegraphs (yellow) and strikes the player on a timer; colour shows the result. Spawned via menu **Tools > Blade and Reflex > Add Training Dummy** (`Editor/TrainingDummyMenu.cs`), which also adds `Health` to the player.
- **`ThirdPersonCamera`** — LateUpdate follow camera behind the player's yaw, mouse/stick pitch, wall avoidance.

Cross-component coupling to keep in mind: `PlayerController` freezes movement while `combat.CanMove` is false or blocking; `CombatStateMachine` drops the block / blocks attacks while `IsDodging`; `Health` reads both to decide the hit result.

### Animator contract

`FYP/Assets/Animation/Player/PlayerAnimator.controller` is coupled to code by string names — changing either side requires changing the other:

- Parameters: `Speed` (float, real ground speed m/s), `MoveDirection` (float, ±1, reverses walk clip for backward walking), `AttackLight`, `Dodge`, `Hit`, `BlockHit` (triggers), `Blocking` (bool).
- State tags: `Attack` (Attack_Light), `Dodge`, `Block` (BlockRaise/BlockIdle/BlockImpact), `Hit` (HitReact).
- Locomotion blend tree thresholds 0 / 1.43 / 3.95 match the Idle/Walk/Run clip travel speeds and `walkSpeed`/`runSpeed` defaults so feet don't slide.

Animations are Mixamo FBX files in `FYP/Assets/Animation/`.
