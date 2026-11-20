# Blade & Reflex: Game Design Document

## 1. Game Overview

Blade & Reflex is a single-player, third-person sword-duelling game where winning depends on timing, positioning and reading the opponent, not on button mashing. The player fights a series of one-on-one duels against AI opponents that react to how the player fights, ending in a boss encounter.

| Item | Detail |
|----|----|
| Title | Blade & Reflex: A Frame-Precise Melee Combat System for Single-Player Duels |
| Genre | Action, melee duelling (Souls-like combat, no open world) |
| Platform | PC (Windows) |
| Engine | Unity, C# for all gameplay systems, Animator Controller and Animation Events for animation timing, Unity UI for the HUD |
| Perspective | Third-person with lock-on camera |
| Players | Single-player |
| Target audience | Players of Sekiro, For Honor and Nioh; ages 16+ |
| Vertical slice length | About 15 to 20 minutes: 3 duels plus a boss |
| Inspirations | Sekiro: Shadows Die Twice (parry and poise), For Honor (directional guard), Absolver and Sifu (duel structure) |

**Research question.** Can precise timing, positioning and decision-making create deeper combat than reflex-based spam, and can enemy AI react meaningfully to that skill expression?

## 2. Design Pillars and Core Loop

Every feature in the game must support at least one of four pillars; anything that does not is cut.

1. **Skill over stats.** Outcomes come from timing and positioning. No gear or levelling can replace a well-timed parry.
2. **Readable combat.** Every attack is telegraphed by animation, sound and a clear direction, so a loss always feels earned.
3. **Risk and reward through stamina.** Attacking, blocking and dodging all cost stamina. Overcommitting leaves the player exposed.
4. **Opponents that adapt.** Enemies read the player's stance, stamina and recent habits, and punish predictable play.

### Moment-to-moment loop

1. Observe the enemy's stance, stamina and wind-up.
2. Choose a response: attack, block, parry, dodge or feint.
3. Commit, spending stamina.
4. Resolve: the hit lands, is blocked, is parried or is dodged.
5. Punish with a riposte if the enemy is staggered, or recover stamina if not.
6. Repeat until one fighter's health reaches zero.

### Session loop

Main menu, then Duel 1, Duel 2, Duel 3, then the boss, then the result screen. Losing a duel restarts that duel immediately so the player can iterate on what they learned.

## 3. Player Mechanics

The player controls one swordsman with weighty, deliberate movement. Movement only needs to be good enough to support combat, so it stays simple.

| Mechanic | Behaviour |
|----|----|
| Walk / run | Analogue speed. Running drains no stamina but cannot be combined with blocking. |
| Lock-on | Toggles a target. The camera frames both fighters and the player strafes around the enemy. |
| Dodge / roll | Short roll with about 250 ms of invulnerability. Costs stamina. |
| Guard direction | While locked on, the right stick or mouse flick selects Left, Right or Top. Attacks and blocks use this direction. |
| Light attack | Fast, low stamina, short punish window if blocked. Until guard directions are built, light attacks made within a short window of each other cycle through three swings; later the guard direction picks the swing. |
| Heavy attack | Slow wind-up, high stamina, high poise damage. |
| Block | Hold to guard in the chosen direction. Blocking the wrong direction means the hit lands. |
| Parry | Tap block just before impact (see Section 4). |
| Feint | Cancel an attack during its wind-up (see Section 4). |

### Controls (initial mapping)

| Action | Keyboard and mouse | Gamepad |
|----|----|----|
| Move | W A S D | Left stick |
| Camera | Mouse | Right stick (free camera only) |
| Lock-on toggle | Tab | Right stick click |
| Guard direction | Mouse flick | Right stick (while locked on) |
| Light attack | Left mouse button | Square / X |
| Heavy attack | Hold left mouse button | Hold Square / X |
| Block / parry | Right mouse button | L1 / LB |
| Dodge | Space | Circle / B |
| Feint | Right mouse button during wind-up | L1 / LB during wind-up |
| Pause | Esc | Options / Menu |

## 4. Combat System

The combat system is the project's main technical contribution. All numbers below are starting values to be tuned during playtesting.

### Directional attacks and blocks

Every attack and block uses one of three directions: Left, Right or Top. A block only stops an attack from the matching direction. A wrong-direction block still reduces damage slightly but costs extra stamina.

### Stamina

Both fighters have 100 stamina. It regenerates at 25 per second after 0.8 seconds without spending. At zero stamina the fighter cannot attack or dodge, and blocks break instantly.

### Poise and stagger

Both fighters have 100 poise. Hits, blocked heavy attacks and parries reduce it. At zero poise the fighter is staggered for 1.5 seconds and takes bonus damage, then poise resets.

### Parry and riposte

Pressing block within the parry window (150 to 200 ms before impact, set by animation events) deflects the attack with no stamina loss. A successful parry removes 40 poise from the attacker and opens a 1.2 second riposte window where the player's next light attack deals double damage.

### Feints

An attack can be cancelled into a block or dodge during its wind-up. A feint costs half the attack's stamina. This lets the player bait enemy blocks and parries, and lets enemies do the same to the player.

### Hit detection

Damage comes from the weapon, not from animation timing alone. Hits are decided by maths on positions the game already holds, not by Unity's physics engine: no colliders, rigidbodies, triggers or physics queries are used for combat. Each swing can hit a given target once.

**Model.** The blade is a line segment from the hilt point to the tip point, read from two skeleton bones each frame, with a small thickness. Each fighter's body is a set of capsules (head, torso, arms, legs) stored as data and positioned from skeleton bones. A capsule is a line segment plus a radius.

**Each frame during the active window:**

1. Read the blade segment now and in the previous frame.
2. Split the movement between the two poses into sub-steps, enough that the blade tip never moves more than one capsule radius per step, so fast swings cannot skip past a target.
3. At each sub-step, find the shortest distance between the blade segment and each body capsule's segment.
4. If that distance is no more than the blade thickness plus the capsule radius, the swing hits that capsule.
5. Check the defender's state (dodging, blocking, parrying) and guard direction to decide the result. Blocks and parries depend on timing and direction, not on blades touching.
6. Record the target so one swing hits it only once.

**Why calculated rather than collision.** The result does not depend on frame rate or the physics step, it can be unit tested with fixed inputs, and every hit can be explained and drawn in a debug view. The maths lives in a pure C# class (`HitMath`) with no Unity physics calls, and `HitCalculator` feeds it bone positions each frame after animation has updated.

### Frame data (initial values)

| Action | Start-up (ms) | Active (ms) | Recovery (ms) | Stamina cost |
|----|----|----|----|----|
| Light attack | 300 | 120 | 350 | 15 |
| Heavy attack | 600 | 150 | 550 | 30 |
| Dodge | 0 | 250 (invulnerable) | 300 | 20 |
| Parry window | n/a | 150 to 200 | 400 on a miss | 0 on success, 10 on a miss |
| Block (per hit) | n/a | held | n/a | 10 plus 0.5 per damage point |
| Feint cancel | up to end of start-up | n/a | 200 | 50% of the attack's cost |

## 5. Enemy AI

Enemies choose actions by scoring them against the player's current state, so they fight differently depending on how the player behaves. The AI is a C# finite state machine for what the enemy is doing (idle, approach, attack, block, retreat, staggered) plus a utility scorer that picks the next action.

### What the AI reads

- Player stamina and poise (low stamina makes the player vulnerable)
- Player's current state and guard direction
- Distance and angle to the player
- Last five player actions, to detect spam (for example the same attack three times in a row)
- The enemy's own stamina, poise and health

### How it decides

Each tick the scorer rates every available action (attack left, attack right, attack top, block, parry attempt, feint, retreat, wait). Scores are weighted by the archetype's personality and the inputs above, and the highest score wins. A reaction delay of 200 to 400 ms is added before the enemy responds to a player action, so the AI stays beatable and difficulty can be tuned with a single value.

### Archetypes

| Enemy | Behaviour | How the player beats it | What it teaches |
|----|----|----|----|
| The Brawler | Aggressive, chains light attacks, ignores its own stamina | Block or dodge the chain, then punish when its stamina empties | Stamina management |
| The Warden | Defensive, blocks the player's last-used direction, counters spam | Vary attack direction, use heavy attacks to break poise | Directional mix-ups |
| The Trickster | Feints often, tries to bait early parries | Wait for the real swing, parry late | Parry timing and patience |
| The Champion (boss) | Uses all three behaviours in two phases, adds one unblockable attack | Combine every skill from the previous duels | Mastery |

### Boss phases

Phase 1 (100% to 50% health) mixes Brawler and Warden behaviour. Phase 2 (below 50%) adds Trickster feints and a telegraphed unblockable attack that must be dodged.

## 6. Game Structure and Level Design

The game is one short, replayable gauntlet in a single arena. Content is kept small on purpose so that time goes into combat quality.

### Flow

1. Main menu (Play, Settings, Quit)
2. Duel 1: The Brawler
3. Duel 2: The Warden
4. Duel 3: The Trickster
5. Boss: The Champion
6. Result screen with stats (time, parries landed, deaths) and a Play Again option

### Arena

One circular stone arena about 20 metres across, with a flat floor and a low wall so the camera never clips. The arena is reused for every duel, with lighting and props changed between fights to give each its own mood. Fighters start about 8 metres apart.

### Win, lose and retry

- **Win a duel:** reduce the enemy's health to zero. A short pause and a health restore lead into the next duel.
- **Lose a duel:** the player's health reaches zero. The same duel restarts within 3 seconds, with no loading screen.
- **Win the game:** defeat the boss, then the result screen appears.

### Out of scope for the vertical slice

Open world, inventory, equipment, levelling, multiple weapons, online play.

## 7. UI/UX and HUD

The HUD stays minimal so the player watches the opponent, not the screen edges. Clarity of feedback matters more than decoration, because timing-based combat is only fair if the player can read what happened.

### Combat HUD

| Element | Location | Purpose |
|----|----|----|
| Player health bar | Bottom left | Remaining health |
| Player stamina bar | Under health | Shrinks on every action, flashes when empty |
| Enemy health and poise bars | Top centre | Shows how close the enemy is to defeat or stagger |
| Guard direction indicator | Around the lock-on reticle | Shows the chosen Left, Right or Top guard |
| Enemy attack telegraph | On the enemy's weapon | Brief glow at the start of an attack, so players can learn the timing |

### Feedback cues

| Event | Visual | Audio |
|----|----|----|
| Successful parry | White spark and short screen flash | Sharp metallic ring |
| Blocked hit | Small sparks, blade recoil | Dull clang |
| Hit landed | Brief hit-pause of 3 to 5 frames, camera shake | Impact thud |
| Stagger | Enemy poise bar flashes, stumble animation | Grunt, low sting |
| Stamina empty | Stamina bar pulses red | Heavy breathing |

### Menus

Main menu, pause menu and result screen. Settings cover volume, mouse and stick sensitivity, and control remapping. Menus work with both keyboard and mouse and a gamepad.

## 8. Art, Animation and Audio

Art and audio are kept deliberately light so that animation quality, which carries the combat feel, gets the most time.

### Visual direction

Stylised, slightly gritty medieval look with a limited palette: stone greys, warm torchlight and one accent colour per enemy so each duel feels distinct. Characters and the arena use licensed or free asset packs where possible. Open question: which asset packs and licences are allowed under the course rules (the Paladin character and its animations currently come from Mixamo).

### Animation

Combat animations are the priority and are tuned by hand to match the frame data in Section 4.

- **Per fighter:** idle, walk and run (strafe in four directions), dodge, block in three directions, parry, stagger, death
- **Attacks:** light and heavy in three directions each, with clear wind-up poses
- **Feints:** reuse the wind-up of each attack, with a cancel animation
- **Boss:** one unblockable attack and a phase-change animation

Animation Events mark the start and end of each attack's active frames, the parry window and the cancel window, so gameplay timing always comes from the animation itself.

### Audio

- Sword clashes, parries, hits, whooshes and footsteps
- Short tension music for duels and a longer track for the boss
- Stinger sounds for stagger, stamina empty and victory

Audio cues double as gameplay information, so the parry ring and the stagger sting must stand out from the rest of the mix.

## 9. Technical Design

The game is built in Unity with C#. Player and enemies share one combat core so that every rule (stamina, poise, parry) is written once and tested once. All tuning values live in ScriptableObjects, so balancing never needs a code change.

### Main classes

| Class | Responsibility |
|----|----|
| Fighter (MonoBehaviour) | Base for player and enemies; owns health and references to the components below |
| CombatStateMachine | States: Idle, Attacking (start-up, active, recovery), Blocking, Parrying, Dodging, Staggered, Dead. Validates every transition |
| StaminaComponent | Spending, regeneration delay and regeneration rate |
| PoiseComponent | Poise damage, stagger trigger and reset |
| HitCalculator | Reads blade and body-capsule positions from bones each frame, calls HitMath during active frames and reports hits once per swing |
| AnimationEventRelay | Receives Animation Events and forwards them as C# events (active start/end, parry window, cancel window) |
| PlayerController | Reads the Input System, handles movement, lock-on and guard direction |
| LockOnCamera | Drives a free camera and a lock-on Cinemachine camera (which keeps both fighters in view), blended by the Cinemachine Brain |
| EnemyBrain | Reads player state, scores actions and sends commands to the combat state machine |
| AttackData, FighterStats, EnemyProfile (ScriptableObjects) | Frame data, stat values and archetype weightings |

### Key technical decisions

- **Animation drives timing.** Animation Events mark attack, parry and cancel windows, so the hit window always matches what the player sees.
- **Calculated hit detection.** Hits come from segment-to-capsule distance maths in a pure C# class, with no colliders or physics queries, and sub-stepping stops fast swings missing a target.
- **Same rules for everyone.** The player and enemies use the same combat components, so the AI plays by the same stamina and poise rules.
- **Data-driven tuning.** Frame data and AI weights are ScriptableObject assets that can be changed in the Inspector during play mode.
- **Debug tools.** An on-screen overlay shows each fighter's state, stamina, poise and the enemy's top-scored action, to speed up AI tuning and to produce evidence for the report.

### Packages

Input System, Cinemachine, TextMeshPro, and the Unity AI Assistant for development only (Section 13). Version control with Git and Git LFS for large assets.

## 10. Development Schedule

The project runs over six months, with a playable (if rough) combat loop by the end of Month 3 and no new features in the final month.

| Month | Milestone | Deliverable | Done when |
|----|----|----|----|
| 1 | Movement and basic combat | Character controller, lock-on camera, one attack, training dummy, calculated hit detection | Player can lock on and land a hit that deals damage |
| 2 | Directional combat and stamina | Three guard directions, stamina, poise and stagger, basic HUD | Direction and stamina visibly change the outcome of a fight |
| 3 | Parry, riposte and feints | Parry window, riposte, attack cancelling, feints | Parry and riposte loop works reliably against the dummy |
| 4 | Reactive enemy AI | Brawler, Warden and Trickster behaviours, AI debug overlay | Each enemy feels different and reacts to player habits |
| 5 | Content and boss | Arena, duel sequence, boss, menus, audio, retry flow | Full run from menu to win screen is playable |
| 6 | Polish, balance and testing | Playtests, tuning, hit-pause and VFX, gameplay video, written report | Final build and report submitted, with the last week reserved for fixes only |

**Rule of thumb.** If Month 3 runs late, cut feints before cutting the parry system, since the parry is the core hook.

## 11. Risks and Scope Cuts

The biggest risk is that combat tuning and animation take longer than planned, especially in Month 3.

| Risk | Likelihood | Impact | Mitigation |
|----|----|----|----|
| Parry and feint system overruns Month 3 | High | High | Prototype in isolation first; cut feints before parry |
| Animations do not match gameplay timing | Medium | High | Use Animation Events for all windows; start with placeholder animations and swap later |
| AI feels unfair or too easy | Medium | Medium | Single tuning value for reaction delay; AI debug overlay; test early in Month 4 |
| Scope creep (extra enemies, bigger level) | High | Medium | Pillars list in Section 2; no new content after Month 5 |
| Lost work or broken build | Low | High | Git with daily commits and a tagged build each month |
| Not enough playtesters | Medium | Medium | Recruit testers by Month 4; accept 5 as the minimum |
| AI-generated code contains subtle errors or is not understood | Medium | High | Unit tests, play mode checks, and nothing kept until the developer can explain it (Section 13) |

### Cut order if time runs short

1. Feints for the player (enemies keep theirs)
2. Heavy attack variations
3. Boss phase 2
4. Gamepad support
5. Per-duel lighting changes

## 12. Testing and Evaluation

Evaluation answers the research question: does the combat reward skill, and does the AI react to it? It combines logged gameplay data with short player questionnaires.

### Method

1. Run informal tests with 2 to 3 peers each month from Month 2, to catch feel problems early.
2. In Month 6, run structured sessions with at least 5 testers, split between players who know this genre and players who do not.
3. Give no instructions beyond the on-screen controls, and observe where players struggle.
4. After each session, testers fill in a short questionnaire (1 to 5 scale) and give written comments.
5. Tune the build between sessions and record what changed and why, for the report.

### Metrics logged by the game

| Metric | What it shows | Starting target |
|----|----|----|
| Parry success rate | Whether the parry window is learnable | 30% to 60% for new players by the third duel |
| Average duel length | Pacing and difficulty | 60 to 120 seconds |
| Deaths per duel | Fairness of each enemy | 1 to 4 on average |
| Win rate per archetype | Relative difficulty of enemies | Brawler higher than Warden, Warden higher than Trickster |
| Stamina-empty events | Whether players manage stamina | Falls over successive duels |
| Attack direction variety | Whether the Warden's counter-spam AI changes behaviour | Rises after the Warden duel |

### Questionnaire topics

Combat feel, fairness of deaths, clarity of parry feedback, enemy variety, whether the AI felt like it was reacting to the player, and overall enjoyment. Targets above are initial guesses and will be revised after the first round of testing.

## 13. Use of AI

AI appears in this project in two unrelated ways: AI tools help build the game, and the game will have AI opponents. AI use is declared openly in this document and in the final report.

### AI in the game

The enemy AI in Section 5 is designed as hand-written rules, not machine learning or generative AI, and is planned for Month 4. Every enemy decision will be traceable through the debug overlay (Section 9), which shows each fighter's state, stamina and poise and the enemy's top-scored action. No AI model runs while the game is being played; the Unity Inference Engine package is installed in the project but is not used by any game code.

### AI tools used in development

| Tool | What it is | How it is used |
|---|---|---|
| Claude (Anthropic, chat app) | An AI chat assistant | Used for the early work, with Claude Code: helping write this design document and the first code |
| Claude Code (Anthropic) | An AI coding assistant that works in the project's Git repository | Writes code and unit tests, reviews code, helps edit this document, and keeps the project's technical notes up to date |
| Unity AI Assistant | Unity's AI package for the Editor. Only its Editor link is used: it lets AI tools work with the Unity Editor through the Model Context Protocol (MCP), a standard way for AI tools to control other programs | Lets Claude Code compile scripts, read the Unity console, run the unit tests and step through play mode, so changes are checked in the real game, not only on paper |

### What AI is used for

This table records all AI-assisted work so far. It is updated each month, and the final report gives the complete record.

| Month | Work | What AI did | What the developer did | How it was checked |
|---|---|---|---|---|
| 1 | This design document (Sections 1 to 12) | Helped write and edit the text | Gave the instructions, reviewed and accepted the text | Developer's review |
| 1 | Movement, camera and first combat state machine (`PlayerController`, `ThirdPersonCamera`, `CombatStateMachine`) | Helped write the code (the camera was later replaced by `LockOnCamera`) | Set the requirements, reviewed and accepted the result | Developer's play testing in Unity |
| 1 | Health, blocking and training dummy (`Health`, block and hit reactions, `TrainingDummy`) | Helped write the code, the editor menu and the Animator block and hit states; moved rolls and attack steps from root motion into code; added a first hit check (later replaced) | Set the requirements, reviewed and accepted the result | Developer's play testing in Unity |
| 1 | Hit detection maths (`HitMath`) | Wrote the code and its 32 unit tests from the rules in Section 4 | Set the requirement, reviewed and accepted the result | Unit tests pass in Unity |
| 1 | Blade and body hit detection (`HitCalculator`, `HitBody`) | Wrote the components, connected them to the combat state machine in place of the old hit check, added an editor menu to set them up, measured the sword model to place the blade on the sword bone, and set up the scene | Set the requirement, reviewed and accepted the result | Checked in play mode against the training dummy: one hit per swing |
| 1 | Lock-on, strafing and camera (`PlayerController` lock-on, `LockOnCamera`, `LockOnReticle`, an editor set-up menu) | Wrote the lock-on targeting and strafe movement, the Cinemachine free and lock-on camera rig, a placeholder reticle, and a menu that builds the rig and a 2D movement blend tree; measured the walk-back and strafe clips' travel speeds so the feet don't slide; set up the scene | Set the requirements (Sections 3 and 9); when asked, chose Cinemachine and chose to add strafe animations; reviewed and accepted the result | Compiles with no warnings; checked in play mode against the training dummy (locking on and off through the method the Tab key calls, turning to face the target, strafing around it including diagonals at walking speed, walking back, no running while locked, an attack while locked on dealing damage); separate AI correctness and quality reviews. Not yet tested: the lock breaking at range or when the target dies |
| 1 | Animation variety (idle, attacks, hit reactions) | Measured the project's spare clips (sword-tip speed for attack timing, travel speed, which clips were duplicates), wrote the code that cycles idles, chains three light swings and adds a held heavy attack, and an editor menu that builds the Animator states and places each light clip's Animation Events | Asked for more idle, attack, run, block and dodge animations (run, directional block and dodge variants were not built: no suitable clips, or they need guard directions); reviewed and accepted the result | Compiles; checked in play mode against the training dummy (light swings 1, 2, 3 in turn, each dealing damage from sword range; the chain restarting after a pause; the heavy attack dealing its higher damage; random hit reactions; the idle changing while standing still); separate AI correctness and quality reviews. Not yet tested: the block-reaction variants, the gamepad attack button, the heavy attack's step distance |
| 1 | Small fixes | Replaced a Unity function deprecated in Unity 6.6 (`FindFirstObjectByType`) | Asked for the fix | Compiles with no warnings |
| 1 | Project notes, Markdown copy and Section 13 | Wrote the technical notes, the Markdown conversion of this document and a draft of this section | Gave the instructions, reviewed and accepted the text | Checked for accuracy against the code and the rest of this document |

No art, animation or audio in the game is AI-generated. The Paladin character, its textures and its animations come from Mixamo, the training dummy uses Unity's built-in shapes, and the game has no audio yet.

### What stays with the developer

Decisions about the game's design stay with the developer: the concept, pillars, mechanics, numbers and schedule in this document, and the order features are built in. The developer also decides whether each AI suggestion is accepted, changed or rejected. Game feel (timing, weight and fairness) is judged by playing the game and through the playtests in Section 12, not by an AI.

### How AI output is checked

AI output is treated as a draft. From the hit detection work (`HitMath`) onward, before any of it is kept:

1. The code is compiled in Unity and the unit tests are run.
2. Gameplay changes are checked in play mode against the training dummy, using the game's console logs, which record every player combat state change, blade hit and hit result.
3. The developer reviews the change, makes sure they can explain how it works, and decides whether to keep it before it is committed to Git.
4. Separate AI reviews check correctness and quality, and the work is revised until neither finds a critical or important issue. This adds to steps 1 to 3 and does not replace them; the developer makes the final decision on what is committed.

Earlier work (the first three rows of the table) was checked only by the developer's own review and, for code, play testing.

**Example.** The AI correctness review of `HitCalculator` found that the blade's movement was not checked in the frame where an attack's active window opened or the frame where it closed, so a very short window could miss entirely. This was fixed, and play mode checks confirmed one hit per swing, before the code was committed.

### Limits and risks

- AI suggestions can be wrong while sounding confident, so nothing is accepted on the AI's word alone.
- AI-written code is harder to maintain if it is not understood, so each commit covers one feature, and only kept once the developer can explain them.

**To do before submission.** Confirm the module's rules on AI-assisted work with the supervisor and reference them here.
