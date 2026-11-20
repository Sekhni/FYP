# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

"Blade & Reflex" — a final-year-project third-person melee combat game (Paladin player: sword & shield). The Unity project lives in the `FYP/` subfolder, not the repo root. The game design doc is `FYP/Blade_and_Reflex_GDD.md` (Markdown; Section 13 records how AI is used, so update its table when AI-assisted work lands).

- Unity **6000.6.4f1** (Unity 6), URP 17, new **Input System** (`com.unity.inputsystem`) — legacy `Input.*` is not used.
- Main scene: `FYP/Assets/Scenes/SampleScene.unity`.
- Gameplay code: `FYP/Assets/Scripts/` (default `Assembly-CSharp`). Editor-only code goes in `FYP/Assets/Scripts/Editor/`.
- `FYP/Assets/Scripts/CombatMath/` is its own assembly (`BladeReflex.CombatMath`, auto-referenced by Assembly-CSharp) so it can be unit tested. Keep it pure maths: no MonoBehaviours, no physics queries, and it cannot reference Assembly-CSharp scripts. Because of that, `HitCalculator` and other MonoBehaviours that use `Health`/`CombatStateMachine` stay in `Scripts/`, not `CombatMath/`.
- EditMode tests: `FYP/Assets/Tests/EditMode/` (NUnit). Run them from Window > General > Test Runner > EditMode. From the CLI (only with the Editor closed, run from the repo root): `"C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe" -batchmode -projectPath FYP -runTests -testPlatform EditMode -testResults results.xml` (add `-testFilter HitMathTests` for one fixture).
- Beyond the tests there is no build/lint step; gameplay is verified in Play Mode via the `[Combat]` / `[Health]` / `[Hit]` console logs.
- When adding/renaming scripts or assets, keep the matching `.meta` files in git. Don't edit `Library/`, `Temp/`, `Logs/`, `UserSettings/` or the generated `.csproj`/`.sln`.

## Architecture

All gameplay sits on the Player root GameObject as cooperating components that find each other via `GetComponent` in `Awake` (serialized references are optional and auto-filled):

- **`PlayerController`** — movement, turning (mouse/A-D; tank-style, W/S forward/back), run, roll (Space / pad East), cursor lock. Requires `CharacterController`. **No root motion anywhere**: rolls and attack steps are moved in script by sampling the Animator's `normalizedTime` for the state carrying a given *tag* (`"Dodge"`, `"Attack"`) and moving along an `AnimationCurve` share of `rollDistance` / `attackDistance`. Also owns lock-on (GDD section 9): Tab / right-stick click calls `ToggleLockOn()`, which picks the `HitBody` nearest the camera's view centre; while locked the player faces the target, A/D strafe, no running. Exposes `IsDodging`, `LockTarget`, `IsLockedOn`, `LockPoint`, `LockTargetChanged`.
- **`CombatStateMachine`** — `Idle → Windup → Active → Recovery → Idle`, plus `Stunned`. LMB = light attack, hold RMB / pad LB = block. Transitions Windup→Active→Recovery→Idle are driven by **Animation Events** `ActiveStart`, `ActiveEnd`, `AttackEnd` on the attack clip (the receiver methods must keep those exact names; a fail-safe forces Idle after `failSafeSeconds`). Deals damage: listens to `HitCalculator.TargetHit` and calls `Health.TakeHit(attackDamage, transform)` (`OnBladeHit` is the extension point for blocks/parries/poise). Exposes `CanMove`, `IsBlocking`, `State`, `StateChanged`.
- **`HitMath`** (`CombatMath/`; a static class, not a component). Blade = `BladePose` segment + radius, body part = `BodyCapsule`. `SweepBladeVsBody` sweeps the blade and the body together from last frame's poses to this frame's in sub-steps (blade direction slerps so the tip follows the arc; steps sized so relative tip movement ≤ `maxStepDistance`, capped at `maxSteps`) and returns the earliest contact as a `BladeHit` with everything a debug view needs. Pass `includeStart: false` when the start poses were already tested or lie outside the attack window (see `HitCalculator`).
- **`Health`** — shared by player and enemies. `TakeHit(damage, attacker)` resolves in order: dodging → `Dodged`; blocking with attacker inside `blockArc` → `Blocked` (chip damage); else `Hit`. Raises `Damaged`, `HitReceived` (CombatStateMachine listens for stun/knockback/block-impact), `Died`, `Refilled`. Draws its own OnGUI health bar.
- **`HitCalculator`** — physics-free blade hits (GDD section 4). Blade = `bladeBone` (`mixamorig:Sword_joint` on the Paladin) + `hiltLocal`/`tipLocal`. Swing window from `CombatStateMachine.StateChanged` (see the class summary for the mid-frame event timing). In `LateUpdate` it sweeps against every other `HitBody` with `HitMath.SweepBladeVsBody`, hits each target once per swing, logs `[Hit]` and raises `TargetHit`. Scene-view gizmos show the blade, the sub-steps and the last hit. No colliders or physics queries.
- **`HitBody`** — a fighter's body as serialized capsule parts (bone + local start/end + radius), with a static `HitBody.All` registry. Fill a humanoid via its context menu "Fill parts from humanoid rig" (re-run it if the rig is re-imported). A target needs a `HitBody` to be hit and a `Health` (same object or a parent) to take damage.
- **`TrainingDummy`** — test target (requires `Health`; has a 2-capsule `HitBody`) that telegraphs (yellow) and strikes the player on a timer; colour shows the result. Its own strike is still a range/arc check, not a blade sweep.
- **Editor menu** (`Editor/BladeReflexMenu.cs`): **Tools > Blade and Reflex > Add Training Dummy** (also adds `Health` to the player) and **Set Up Hit Detection** (Player gets `HitBody` + `HitCalculator` with the sword bone; dummies get a `HitBody`; safe to re-run).
- **`LockOnCamera`** (on "Camera Rig") — drives a pivot followed by two Cinemachine cameras (`CinemachineThirdPersonFollow`): Free (behind the player's yaw, mouse/stick pitch) and Lock-On (pivot aimed at `PlayerController.LockPoint`, priority raised while locked); the `CinemachineBrain` on the Main Camera blends them. Draws the lock-on reticle. Built by **Tools > Blade and Reflex > Set Up Lock-On** (`Editor/LockOnSetup.cs`), which also builds the 2D Locomotion blend tree.

Cross-component coupling to keep in mind: `PlayerController` freezes movement while `combat.CanMove` is false or blocking; `CombatStateMachine` drops the block / blocks attacks while `IsDodging`; `Health` reads both to decide the hit result.

### Animator contract

`FYP/Assets/Animation/Player/PlayerAnimator.controller` is coupled to code by string names — changing either side requires changing the other:

- Parameters: `MoveX` / `MoveZ` (float, local ground velocity m/s; drive Locomotion), `Speed` (float, ground speed m/s), `AttackLight`, `Dodge`, `Hit`, `BlockHit` (triggers), `Blocking` (bool).
- State tags: `Attack` (Attack_Light), `Dodge`, `Block` (BlockRaise/BlockIdle/BlockImpact), `Hit` (HitReact).
- Locomotion is a 2D freeform directional blend tree ("Locomotion 2D") on MoveX/MoveZ. Each clip sits at the velocity it actually travels (idle 0, walk 0/1.43, run 0/3.95, walk-back 0/−1.09, strafe right 1.19/0, strafe left −1.05/0), matching `walkSpeed`/`runSpeed`/`strafeSpeed` so feet don't slide.

Animations are Mixamo FBX files in `FYP/Assets/Animation/`.
