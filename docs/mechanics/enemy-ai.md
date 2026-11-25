# Ultima IX: creature and NPC AI

How creatures and NPCs decide what to do in Ultima IX: Ascension (GOG `u9.exe`, SHA-256 `83915175...`), as the
decompilation shows it. Every statement names the function that implements it (`Name_ADDRESS` as in the decompile, or
`Class::vfNNN_ADDRESS` for a virtual method, `FUN_xxxxxxxx` for an unnamed function). The decompile has names only for a
few of the AI classes; the others are `Class_<vtable address>` and I give them the creature that uses them. This file
holds descriptions, names, addresses, constants and formulas only, no decompiled code. See [README.md](README.md) for
conventions.

Confidence tags: **[H]** read directly at the address given; **[M]** read, meaning inferred; **[L]** a guess.
*(unsure)* and *(inference)* mark single statements. Where this file repeats a result of
[`exe-research.md`](../exe-research.md) (audited), it says so; nothing here contradicts it, and the points that go
beyond it are marked.

Contents
1. [The AI objects](#1-the-ai-objects)
2. [Kind to AI class](#2-kind-to-ai-class)
3. [Class hierarchy and virtual methods](#3-class-hierarchy-and-virtual-methods)
4. [Behaviours](#4-behaviours)
5. [Attack choice](#5-attack-choice)
6. [Spell use](#6-spell-use)
7. [Dragons, winged gargoyles, Slasher Demon, Zhah, Liche](#7-special-creatures)
8. [Data or code](#8-data-or-code)

---

## 1. The AI objects

* Every creature or NPC that is alive in the world has **one AI object**, created by `Npc_CreateAI_004F5DC0` when its
  NPC record is activated. The object belongs to the NPC manager (`g_NpcManager_0098F324`); the manager keeps the
  list of all AI objects (head at manager +0x800) and indexes them by NPC record number (array at +0). **[H]**
* **Kind -1 creates no AI object at all** (the first test of `Npc_CreateAI_004F5DC0`), so those 58 records
  (quest givers, shrines, scenery NPCs) never act on their own. **[H]** (also in `exe-research.md`)
* An AI object is a **process-like object with a vtable** (about 90-140 slots). Its base class is `AI_Base` (vtable
  0x743E88, 92 slots, constructor `AI_Base_ctor_0043BFF0`). It holds pointers to its NPC record (+0x0C), to its world
  object (+0x18), to the movement and animation controllers (+0x10 and +0x14), and the record number (+0x20). **[H]**
* **Updates and events.** The brain runs in virtual slot 0x150 (a per-frame update in most classes). Animation events
  (an attack animation reaching its hit frame) arrive in slot 0xB0, with the animation id and an event argument. Slot
  0x04 is the generic process tick (in `AI_Base` it only advances a counter list). Save and load are slots 0xDC and
  0xE0 (`AI_Base::vf0DC_0043C440`, `vf0E0_0043C760`; save record version 5, about 40 fields, with the record
  pointer, object reference and controller pointers rebuilt after load). **[H]**
* Time: `DAT_0083D3A0` is the elapsed milliseconds of the frame; AI timers count it down, or compare
  `GetTickCount` with a stored deadline (the dragon does the latter). **[H]**

---

## 2. Kind to AI class

`Npc_CreateAI_004F5DC0` switches on the creature kind (NPC record +0x44, table of 64 entries, jump table 0x4F6AD4,
switch at 0x4F5E62). Several kinds build the object inline: they call the shared base constructor and then write a
class vtable. Kinds 16, 18, 35 and 37 (and any value above 63) fall to the default: a plain `AI_Base` with no attack
handler. Kind 43 shares kind 33's class. **[H]** (the switch; `exe-research.md` has the same list for who attacks with what)

"Examples" are NPC.FLX template records of that kind (templates have 0x800 in +0x48).

| kind | class (vtable or constructor) | object size | examples (records) |
|---|---|---|---|
| -1 | none | - | Captain Wolford 4, Court Guard 105, shrines |
| 0 | `Class_00741CA8` (Avatar controller, 138 slots, `ctor_00416300`) | 0x2D0 | Avatar 0, Castleguard1 154, Creeper 197, Door Mimic 199 |
| 1 | `AI_GiantBat` (`AI_GiantBat_ctor_004A7570`) | 0x2A0 | Giant Bat 193 |
| 2 | `AI_VampireBat` (`ctor_005B2B60`, calls the Giant Bat constructor) | 0x2B8 | Vampire Bat 35 |
| 3 | `AI_Vulture` (`ctor_005B4380`) | 0x2B8 | Vulture 187 |
| 4 | `Class_0074AE08` (inline) | 0x244 | Crusty 142 |
| 5 | `Class_007445B0` (`ctor_00447EF0`) | 0x2A4 | Demon 184, Stone Demon 134, Bloated Demon 33 |
| 6 | `Class_0074CC70` (`ctor_00528800`, parent `Class_00745360`) | 0x3A0 | Slasher Demon 130 |
| 7 | `Class_007449C0` (`ctor_004501E0`, parent `Class_00745360`) | 0x314 | Blue Dragon 149, Red Dragon 212, Dragon 326 |
| 8 | `Class_00751170` (`ctor_005C10F0`) | 0x228 | Zombie 206 |
| 9 | `Class_00745D58` (`ctor_004A1460`, parent `Class_00749840`) | 0x244 | gargoyle drones 137, 220, 330 |
| 10 | `Class_007462B0` (inline) | 0x27C | Winged Gargoyle 16, Terfin Winged Gargoyle 56, Generic Gargoyle Guard 61 |
| 11 | `Class_00745F38` (inline) | 0x29C | Zhah 217 |
| 12 | `Class_00746650` (`ctor_004A47A0`) | 0x298 | Gazer 202, Amy's Gazer 191 |
| 13 | `Class_007469F8` (`ctor_004A6C20`) | 0x224 | generic spirits 210 |
| 14 | `AI_GoblinGrunt` (vtable 0x74A940, derived from the humanoid base) | 0x27C | Goblin Grunt 192 |
| 15 | `Class_00747A50` (`ctor_004AC910`, humanoid) | 0x258 | Goblin Captain 34 |
| 16 | `AI_Base` | 0x214 | none |
| 17 | `Class_007437D0` (`ctor_00436A80`) | 0x230 | Chest Mimic 186 |
| 18 | `AI_Base` | 0x214 | none |
| 19 | `Class_0074AB10` (inline) | 0x26C | Creeper Plant 45 |
| 20 | `Class_007470D8` (`ctor_004A76C0`) | 0x234 | Giant Rat 194 |
| 21 | `AI_SmallRat` (`ctor_0052DB90`) | 0x294 | Small Rat 28 |
| 22 | `Class_0074C610` (`ctor_00525FD0`, humanoid) | 0x240 | Skeleton 203 |
| 23 | `AI_Spider` (`ctor_004A8590`) | 0x29C | Giant Spider 242 |
| 24 | `AI_PhaseSpider` (`ctor_00506550`) | 0x29C | Phase Spider 195 |
| 25, 26 | `Class_0074A7A0` (inline) | 0x280 | Wolf 198, Arctic Wolf 152 |
| 27 | `Class_0074A458` (inline) | 0x298 | Hellhound 47 |
| 28 | `AI_Songbird` (`ctor_00532320`) | 0x2A8 | Songbird 183 and other birds |
| 29 | `AI_Butterfly` (`ctor_00430600`) | 0x2A0 | Butterflies 86, 87, 103, 108 |
| 30 | `AI_Dog` (vtable 0x74A2A8) | 0x294 | Dog 304 |
| 31 | `AI_SmallFish` (`ctor_0052D6F0`) | 0x284 | Small Fish 37 |
| 32 | `AI_PredatoryFish` (`ctor_005169A0`) | 0x284 | Predatory Fish 209 |
| 33, 43 | `AI_Goat` (`ctor_005C06D0`) | 0x240 | Goat 293, Pig 298 |
| 34 | `Class_00749A20` (`ctor_004EFF00`, parent `Class_0074BC18`, humanoid) | 0x244 | peasants (54 records) |
| 35 | `AI_Base` | 0x214 | none |
| 36 | `Class_0074B420` (`ctor_00506B40`, humanoid) | 0x244 | pirates and bandits |
| 37 | `AI_Base` | 0x214 | Bandit 296 |
| 38 | `Class_0074BC18` (`ctor_0051C420`, humanoid) | 0x244 | archers (Generic Robinhood 50, Skeleton Archer 196) |
| 39 | `Class_00748F08` (`ctor_004DCEA0`, humanoid mage) | 0x2C0 | Generic Mage 76, Goblin Commander 17, cult members |
| 40 | `Class_00749840` (`ctor_004EF770`, humanoid) | 0x240 | ninjas, staff users |
| 41 | `Class_00742F00` (`ctor_0042F480`, humanoid) | 0x23C | brutes |
| 42 | `Class_00747C50` (`ctor_004ADB30`, humanoid) | 0x258 | guards |
| 44 | `Class_0074A600` (inline) | 0x290 | Ice Hound 46 |
| 45 | `AI_SeaMonster` (`ctor_005222E0`) | 0x2D0 | Sea Monster 155 |
| 46 | `Class_00748A40` (`ctor_004D4650`) | 0x268 | Liche 300, Khelereth 305 |
| 47 | `Class_007512F0` (`ctor_005C1DB0`) | 0x228 | Leggy 204 |
| 48 | `Class_00751650` (`ctor_005C2930`) | 0x228 | Torsoid 205 |
| 49 | `Class_0074E600` (`ctor_00545220`, humanoid) | 0x23C | male peasants, miners, thugs (65 records) |
| 50 | `AI_Kind50` (`ctor_0041E730`) | 0x214 | none in the template list |
| 51 | `AI_Fishschool` (`ctor_00491C60`) | 0x284 | Fishschool 106 |
| 52 | `Class_0074D5B0` (`ctor_0052EFB0`) | 0x29C | Small Spider 158 |
| 53 | `Class_0074AC90` (inline) | 0x218 | Pirate Ship 208 |
| 54 | `Class_00745990` (inline) | 0x214 | NPC Ship 185 |
| 55 | `Class_00749128` (`ctor_004DE170`) | 0x25C | Magic Dart 97 |
| 56 | `Class_00747E28` (`ctor_004AE360`, derived from the mage class) | 0x2C0 | Guardian 160 |
| 57 | `Class_0074E8B8` (`ctor_00549910`, humanoid) | 0x258 | Generic Wrong Guard 74 |
| 58 | `AI_Dragonfly` (vtable 0x74A138) | 0x220 | Dragonfly 297 |
| 59 | `AI_Swan` (vtable 0x749FC8) | 0x218 | Swan 144 |
| 60 | `AI_Chicken` (vtable 0x749E58) | 0x224 | Chicken 145 |
| 61 | `Class_00746B78` (`ctor_004A72E0`, humanoid) | 0x258 | generic ghosts, Salvador 89 |
| 62 | `Class_007506A8` (`ctor_005B5AF0`, humanoid) | 0x260 | guards, wyrmguards, Dupre 243 |
| 63 | `Class_00750890` (`ctor_005B6080`, humanoid) | 0x23C | no template creature uses it in the data I looked at |

The kind of each template is **[H]** from `runtime/NPC.FLX` (record +0x44); the class names for sizes and constructors
are **[H]** from the switch; the creature each class "is" follows from the kinds that reach it.

---

## 3. Class hierarchy and virtual methods

The hierarchy comes from the constructors (each constructor calls its parent's, `index/classes.tsv`). **[H]**

```
AI_Base (0x743E88, 92 slots)
 |-- plain creatures: AI_Chicken, AI_Dog, AI_Dragonfly, AI_Goat, AI_Kind50, AI_SmallRat, AI_Swan,
 |     Class_007437D0 (mimic), Class_00745990 (ship), Class_00745F38 (Zhah), Class_007462B0 (winged gargoyle),
 |     Class_00746650 (gazer), Class_007469F8 (spirit), Class_007470D8 (rat), Class_0074A458 (hellhound),
 |     Class_0074A600 (ice hound), Class_0074A7A0 (wolves), Class_0074AB10 (creeper plant), Class_0074AC90 (ship),
 |     Class_0074AE08 (Crusty), Class_00748A40 (Liche), Class_00749128 (magic dart), Class_00751170 (zombie),
 |     Class_007512F0 / Class_00751650 (Leggy, Torsoid), Class_007445B0 (demon)
 |-- Class_00745360 (105 slots): Class_007449C0 (dragon), Class_0074CC70 (Slasher Demon), Class_007432E8,
 |     Class_00746F30 (-> Class_00750070), Class_0074D838, Class_007502A0   [large-creature base]
 |-- Class_00747460 (105 slots): Class_0074B240, Class_0074D5B0 (small spider)   [spider base]
 |-- Class_0074E190 (104 slots): Class_00744FC0, Class_0074B800, Class_0074BE30, Class_0074CE68   [flyer and fish bases]
 |-- Class_007483E8 (116 slots) = the HUMANOID BASE
       |-- AI_GoblinGrunt, Class_00741CA8 (the Avatar), Class_00742F00 (brute), Class_00747A50 (goblin captain),
       |   Class_0074B420 (pirate), Class_0074C610 (skeleton), Class_0074E600 (-> Class_00750890),
       |   Class_00749840 (ninja; -> Class_00745D58 gargoyle), Class_0074BC18 (archer; -> Class_00749A20 peasant),
       |   Class_00747C50 (guard; -> Class_00746B78 ghost, Class_0074E8B8, Class_007506A8),
       |   Class_00748F08 (mage; -> Class_00747E28)
```

The flying creatures (bats, vulture, songbird, butterfly) share the constructor `AI_FlyingBase_ctor_004920B0` and the
fish share `AI_FishBase_ctor_0053FE80`; the spiders share `AI_Spider_ctor_004A8590`. **[H]**

### Virtual methods of `AI_Base` (named by address; slot = byte offset in the vtable)

| slot | function | what it does |
|---|---|---|
| 0x04 | `vf004_0043DC80` | process tick; advances a timer list at +0x128 |
| 0x20 | `vf020_0043F0F0` | stop or reset (called when an AI is dropped; the mage versions cancel a running cast first) |
| 0x64 | `vf064_0043EC70` | **one wander step** (section 4) |
| 0x74 | `vf074_0043DDB0` | turn towards a target (takes a target and an angle) |
| 0x84 | `vf084_0043DFE0` | **distance** to another NPC (centre to centre, optionally minus both radii; 99999 when the NPC does not exist) |
| 0x88 | `vf088_0043E0C0` | **can see**: true when the target is not invisible (target record +0x48 bit 0x200 clear) and, if the looker's object has flag 0x02, a ray test between the two eye points hits the target |
| 0x8C | `vf08C_0043E200` | **is friendly**: see "Hostility" below |
| 0xB0 | (per class) | animation-event handler |
| 0xDC, 0xE0 | `vf0DC_0043C440`, `vf0E0_0043C760` | save, load |
| 0xE4 | `AI_Base_InitCommon_0043CB00` | common initialiser, called from each class's init |
| 0x100 | `vf100_0043D0B0` | helper of the target list |
| 0x11C | `vf11C_0043CC50` | "is active" test (returns whether the AI may think this frame) |
| 0x124 | `vf124_00416B20` | "has something to do" (default 0) |
| 0x128 | `vf128_0043CF30` | **engage**: choose a target (below) |
| 0x12C | `vf12C_0043CEF0` | return the candidate with the smallest score (99999 as the start value) |
| 0x134 | `vf134_0043CC30` | clear the current target state |
| 0x13C | `vf13C_0043CD30` | **build the candidate list** (below) |
| 0x140 | `vf140_0043CE60` | scale the stored scores of the enemy lists and the current target |
| 0x148 | `vf148_0043EAB0` | **notice test** (below) |
| 0x150 | `vf150_0043CCA0` | per-frame brain: if active, run `vf128`, clear flags, run slot 0x04 (the humanoid and creature classes override it) |
| 0x15C | `vf15C_0043D5D0` | movement helper |

The humanoid base `Class_007483E8` adds: slot 0x00 destructor, 0x04 (`vf004_004B89D0`: sets the animation to the
current state and refreshes the target), 0x20, 0xB0 (`AI_HumanoidWeaponDispatch_004BAD10`, **the weapon dispatcher**),
0x184 (`AI_Humanoid_ArmNothing_004B7F60`, an empty "arm" method that the classes override), 0x188
(`AI_FindCarriedWeaponOfClass_004B7F70`), 0x1B8 (can attack), 0x1BC (`vf1BC_004B9520`, the 2.9 KB attack-and-move
controller; it also calls `Combat_EnvironmentDamage_004F89E0`) and many movement helpers 0x170-0x1CC. **[H]** for the
slots and names, **[M]** for the one-line meanings (only some bodies were read).

---

## 4. Behaviours

### Idle, wander and schedules

* **Schedules are not in the AI classes.** NPC daily behaviour (sitting, sleeping, praying, patrolling, drinking, forging,
  begging, reading, gesturing) is done by the **activity system**: `ActivityBase` (parent `Process`) and about 22
  activities (`ActivityWander`, `ActivityLoiter`, `ActivityPatrol`, `ActivityPausingPatrol`, `ActivityPace`,
  `ActivitySitInChair`, `ActivitySleepInBed`, `ActivityPray`, `ActivityBeg`, `ActivityDrink`, `ActivityHammerAtAnvil`,
  `ActivityLeanOnWall`, `ActivityReadBook`, `ActivityShipPatrol`, `ActivityWalkToMarker`, `ActivityCreatureIdle`, ...),
  each driven by a coordinator (`ActivityCoordinator` and about 24 `MasterActivity...` classes). The property
  **0x44 "Default Activity"** on a placed NPC names the activity and its target (A, B). The activity list, not the
  AI class, decides what an NPC does when nothing is hostile. I did not read the activities. **[M]** (class names and the
  property name; `PropertyHelp.cs`)
* **Wander step** (`AI_Base::vf064_0043EC70`): each call picks a new heading and distance. Heading change = `25 +
  random(0..25)` degrees, to the left or right (a coin flip), so 25-50 degrees. The step length is `random(a, b)` between
  two bounds that come from the caller. A stuck counter (+0x168) counts consecutive steps that ended farther than twice
  the step from the intended point; after **5** such steps the NPC turns around by `180 - random(0..50)` degrees. One
  step in nine (`random(0..8) == 0`) starts a four-step curve of 45 degrees each (counter at +0x16C). The movement then
  goes through a new pathfinder object (`NewPathfinder_____`, 0x1C0 bytes) or, if one exists, the old request. **[H]**
* **Idle animation timers** for creatures: dragon 1000 ms re-check in state 0; the Slasher Demon and the dragon run a
  small state machine (below). **[M]**

### Notice and perception

All numbers are in world units (the same as the positions in the maps). They come straight from the NPC record, so
they are data. **[H]**

* **Shape:** distances come from `AI_Base::vf084_0043DFE0`, the Euclidean length (`FUN_005B3BA0`) of the vector between
  the two positions; the candidate list calls it with its third argument set, which zeroes the height first, and
  without subtracting radii. The notice range is therefore a **vertical cylinder** (a circle on the ground, any height
  difference), unlike the trigger scripts' `range`, which tests each axis against an infinitely tall square. **[H]**
* **Range R** = NPC record **+0x54** (u16). Typical values in `NPC.FLX`: bats 600-1000, dragons 500-1800, demons 200-1000,
  gazers and Slasher Demon 1000, skeleton 400-600, Liche 450-1000, sea monster 2500, Pirate Ship 2000, Guardian 2000;
  townspeople and animals that never attack have 0.
* **Field of view** F = record **+0x56** (u8, degrees; 0 means all-round) and **close zone** C = record **+0x57** (u8,
  percent of R). Typical: F 0 for nearly everything, C 20 (0x14), 25, 30, 33, 50 or 100.
* **Candidate list** (`vf13C_0043CD30`): walks the NPC manager's list and keeps every other NPC that: is not this one;
  does not have record flag +0x48 bit 0x20 (immune to environment damage; such NPCs are never targets); is not a friend
  (`vf08C`, inverted when the first argument is 0); and is within R (distance by `vf084`; R defaults to the record's
  +0x54 when the argument is -1) or is already on this AI's remembered list. The Avatar is skipped when the view
  controller's special state (`+0x1C` of the camera object) says "not controllable". Kept NPCs are chained through
  their +0xF8 (score = distance) and +0xFC (next) fields; the head is in this AI's +0x100. **[H]**
* **Notice test** (`vf148_0043EAB0`), run each frame for an AI that has not yet engaged (record +0x48 bit 0x4000000
  clear). It rebuilds the candidate list (the list is in chain order, not sorted by distance; `vf12C` is what picks the
  nearest) and walks it. A candidate is skipped when it is invulnerable-ish (record +0xC4 bit 7, or +0x48 bit
  0x10000000; the Avatar is also skipped while a story flag (`FUN_00498630`) says so). For the first candidate that is
  not skipped:
  1. **Line of sight first.** If the AI's "check sight" flag (+0x171, set to 1 by `AI_Base_InitCommon_0043CB00`; the skeleton
     class `Class_0074C610` clears and sets it) is on, the test is rate-limited by a counter (+0x174, reloaded from +0x178 =
     5; I did not find where it counts down) and calls `vf088_0043E0C0`. If that says "cannot see", **the whole test
     returns "not noticed"** (no other candidate is tried that frame). `vf088` is true when the target is not invisible
     (record +0x48 bit 0x200 clear) and either the looker's world object lacks the flag 0x02 (no ray is cast, everything
     is visible) or a ray between the two eye points (`FUN_00566880`) reaches the target first.
  2. **Distance and angle.** The candidate is **noticed** when its distance is below `C x R / 100` (the close zone: no angle
     test), or below R with F = 0 (all-round vision), or below R and inside the cone of half-angle `F / 2` degrees in front
     of the AI (`vf074`).
  On a notice the AI calls `FUN_004F97B0(manager, self, 0)`: it **starts fighting**. If the AI already has anyone on its
  remembered-enemy list (+0x10C non-empty) it engages at once. **[H]**
* **An Avatar enclosed in walls** is therefore not noticed by a creature whose object has flag 0x02 and whose check-sight
  flag is on (the usual case) *(the flag 0x02 meaning is inferred from the ray-test code)*. Once a creature has engaged,
  `vf128` keeps choosing targets by distance within R only, with no line-of-sight test; whether it can reach the target is the
  pathfinder's business (not read). **[M]**
* **Invisibility** (spell 26, record flag +0x48 = 0x200) makes `vf088` return false, so creatures cannot notice an invisible
  target (the close zone does not help, because the sight test comes first). **[M]**

### Hostility

`vf08C_0043E200` ("is this other AI a friend?") is the only faction logic. **Creatures can attack other creatures**: the
candidate list (`vf13C`) takes every NPC that is not a friend and is within range, not only the Avatar, and the natural-attack
strike (`Combat_NaturalAttackHit_004FA110`) damages whatever creature its hit zone touches (`Combat_NaturalDamage_004F82F0`
per victim). **[H]**

**How the team is set at creation** (the end of `Npc_CreateAI_004F5DC0`, and `FUN_004F7280` for spawned creatures): take the
**link number** of the creature's world object (`FUN_004BEE30`: the word at object +0x1A, or 0 when the object's flag bit 0
is set; this is the "link" field of the placed object in the map). If it is **non-zero**: faction mode byte
(+0x139) = **1**, team = that link number (+0x13A). If it is **zero**: mode **0** for creatures (mode **3** for the Avatar
record 0), team = the **usecode id of the creature's type** (the u16 at +4 of its `TYPES.DAT` entry; "same class"). **[H]**

**The rules in order** (`vf08C`, `this` = the one looking, `other` = the candidate). **[H]** for the order, **[M]** for names:
1. `other` is itself, or has kind 54 (a ship): friend.
2. If `this` is **charmed** (record +0x48 bit 0x1000 set): friend iff `other` is its leader (AI field +0x140) or the leader's own
   `vf08C` says friend; with no leader (+0x140 = -1) everyone is a friend. Done.
3. If `this` has record +0xC4 bit 0x2000 and `other` is kind 0 (the Avatar): friend. (99 of the 352 records have this bit:
   it is "friendly to the Avatar".)
4. If `other` is on `this`'s first remembered list (+0x108, creatures that attacked it): **enemy**.
5. By mode: mode 0, same usecode id: friend. Mode 1, same link number (`FUN_004BEE30` of both objects): friend. Mode 2 (charm
   mode), `other`'s link equals the id in +0x13A: friend. (Mode 3 with the Avatar as `other`: friend.)
6. Otherwise, if `this`'s own **object flag 0x100** is clear (the normal case): if `this` is a kind-0 AI and `other`'s
   record has +0x48 bit **0x8000000** (all 64 monster templates have it): enemy; if `other` is kind 0 (the Avatar): **enemy**;
   if `other`'s **link number is non-zero**: **enemy**; else (link 0): **friend**. If `this`'s object flag 0x100 is set:
   `other` is a friend only when its type's usecode id is 0x12E, 0x3D, 0x51 or 0x4A (the guard types), otherwise an
   enemy (a creature "at war with everyone").

So by default every monster is hostile to the Avatar (rule 6) and friends with every other monster that has link 0.

**Charm** (`Class_00756598`, spell 17): `FUN_004F99E0(manager, caster, target)` stores the charm **only in the target's AI object
and NPC record**: saves the old mode (+0x13C) and team (+0x13E), sets mode 2, team (+0x13A) = the caster's record number,
leader (+0x140) = the caster's record number, and ORs 0x1000 into the target record's +0x48. `FUN_004F9A50` undoes it. Nothing
reads a charm from map data, so a charmed creature **cannot be placed as data**: the leader field is runtime state (it is
saved in the AI save record). The Crook of Charming's 20 % charm (weapon special 15 in `exe-research.md`) was not traced to its
call; the only caller of `FUN_004F99E0` that I found is the Charm spell class. **[H]** for the function, **[M]** for the Crook.
Data alternatives: record +0x48 bit 0x1000 on an NPC.FLX template with no leader makes that creature friend of
everyone (rule 2), that is, **peaceful**; record +0xC4 bit 0x2000 makes it friendly to the Avatar only.

**Property 0x43 "Combat AI"**: no code reads it (a scan of every `Object_GetProperty`-style read found no 0x43); the faction
logic above uses only the link number, the type's usecode id and the record flags. *(unsure: absence of evidence)*

**Making Crusty (kind 4, usecode 0x8E), Liche (kind 46, 0x12C) and Hellhound (kind 27, 0x2F) fight each other.** All three
have different usecode ids, so rule 5 does not make them friends; they are friends today only because of rule 6's last step
(`other`'s link is 0).
* **Data only (M):** give each of the three placed objects a **different non-zero link number** that no trigger uses (the link
  field of the placed object, +0x1A of the map record). Each one then runs in mode 1 with its own team, and rule 6 makes any
  other creature with a non-zero link an enemy. Giving only one creature a link makes the others hostile to it, one way. Notice
  range: Crusty 600, Liche 450 or 1000, Hellhound 1000 (record +0x54); they must stand within the notice range and have line of
  sight (and a Liche only if its object has flag 0x02). Risks: the link number is also what map triggers use to find an object;
  an unused number avoids that; whether editing tools keep the link on a creature object was not tested.
* **Exe patch (M):** in `vf08C_0043E200` at **0x43E4AB** (file offset 0x3D8AB) the bytes `75 05` (JNZ to "enemy") become `EB 05`
  (JMP to "enemy"). Every non-Avatar creature of a different class or team then counts as an enemy, even with link 0. Side
  effect: **all different creature types fight each other**, including townspeople of different types; same-usecode creatures
  remain friends. To limit it use the data method instead. Neither the data nor the patched variant was tested in the game.

### Choosing the target and holding it (`vf128_0043CF30`, `vf140_0043CE60`)

* On engage the AI sets record flag +0x48 bit **0x4000000** ("fighting"), rebuilds the candidate list and gives each
  candidate a score = its distance. `vf140` then **multiplies the scores** of three groups: the AI's first remembered
  list (+0x108, creatures that have attacked it) by 0.5, the second list (+0x118) by 0.075, and the current target
  (+0x104) by 0.5. `vf12C` picks the lowest score, so recent attackers and the current target are strongly preferred
  (hysteresis), the second list overwhelmingly. A candidate also has to pass `vf160` (a per-class acceptance test) and
  not be a friend. The chosen target's own list gets the AI's id added (`FUN_005DDE60`), so the two sides know each
  other. **[M]** (the multipliers and the list roles are read; which list is which is inferred.)
* **Giving up:** if the candidate list is empty (nobody within range), the AI sets its target to -1, clears the fighting
  flag (0xFBFFFFFF mask) and calls `FUN_004041E0` (return to the activity system). There is no separate give-up timer in
  `AI_Base`. **[M]** *(unsure for the classes that override `vf150`, for example the dragon, which has timers of
  3000-10000 ms between state changes)*.
* **Group behaviour:** the flag +0x48 bit **0x8000000** and the lists +0x108 and +0x118 implement alerting: `FUN_004F97B0`
  alerts the whole group of the noticing creature, and a creature with the flag set takes the first list into account.
  I did not read `FUN_004F97B0`. *(unsure)*

### Fleeing and morale

* I found **no flee or morale logic** in the AI classes (the strings "flee", "morale", "retreat" occur only in
  conversation scripts). Humanoids and animals keep attacking until they or the target die or lose range. *(unsure:
  absence of evidence, the humanoid controller `vf1BC` is large and was only partly read)*.
* The only retreat the code contains is the **mage's movement spell** (section 6): the category-3 weight
  (Levitate, Teleport) is 1.0 when the mage stands on top of its target and falls to 0 at half its range, so a
  threatened mage tends to teleport away.

### Stun

The kind-62 class (`Class_007506A8`) reads property **0x75 "Stun Time (in sec.)"** of its placed object
(`vf1D8_005B5DB0`): when the object has the flag 0x4000, the value times 1000 ms becomes the stun duration (+0x25C),
10000 ms when the property is 0. **[H]**

---

## 5. Attack choice

This section repeats the audited result of `exe-research.md` ("Who attacks with what") and adds how the choice is made.
The numbers (natural rows, weapon defaults) are not repeated in full; see that file and `analysis/item-stats/attacks.md`.

* **Natural attacks.** Each class calls `FUN_004FA110(ai, row)` (hit) or `FUN_004F9FD0(ai, row)` (reach test only) from
  an animation-event handler (slot 0xB0, keyed by an animation id such as 0x428 for the Creeper Plant, 0x3F3 for the
  Gazer, 0x3DE for spirits, 0x305/0x3BB/0x3BC for wolves, dogs and hellhounds, 0xCD for goblin grunts) or from the
  per-frame update (slot 0x150). The row is an immediate or a field set by the constructor. **[H]**
* **Humanoids** go through `AI_HumanoidWeaponDispatch_004BAD10`: look at the weapon class in record +0xA8, find the item
  in that slot, then bow logic (class 4), staff kick (class 3, mode 1, natural row 13), melee through
  `FUN_004F9BE0` and `FUN_004F79D0`, or the fist rows 0-2 (mode in the AI at +0x38). Which weapon: the first item of
  the right class in the NPC's map contents, else a fixed default (Scimitar 4789 for kind 36 and so on, listed in
  `exe-research.md`). **[H]**
* **Ranged vs melee.** A humanoid holding a bow (class 4) shoots; the arrow object's class applies bow damage
  (`FUN_004FA350` to `FUN_004F86B0`). Every other creature attacks in reach. Nothing in the AI base compares the two:
  a creature does what its class and its animation events say. Creatures with a ranged effect (dragon, winged
  gargoyle, Slasher Demon, Zhah, Liche, Magic Dart) are in section 7. **[H]**
* **When to attack:** the humanoid base asks `vf1B8` ("can attack") and, for the mage, `vf1B8`/`vf1E8` of the mage class,
  which compare the distance to the target with a per-class range at +0x22C (a float written by the class); the mage
  attacks when the distance is below that value. *(unsure: the numeric ranges per class were not collected)*

---

## 6. Spell use

* Only creatures whose class derives from the mage class cast spells chosen from their property masks:
  **kinds 39 and 56** (`Class_00748F08`, `Class_00747E28`). They read **properties 0x72 (bits 0-31) and 0x73 (bits 32-63)**
  of their placed object; bit n adds spell n. How the spell is picked (four category weights, mana-per-cost
  scores, 40 % chance of skipping the last spell, cooldown `random(2000, 4000)` ms) is in
  [`spells.md`](spells.md#8-npcs-and-creatures-that-cast). **[H]**
* **Which creatures use spells** (all via `FUN_005ED950`): kinds 39, 56 (masks); the **Slasher Demon** (Lightning Bolt, a
  fixed spell); the **Liche** (Summon Undead or Lightning Bolt); **Zhah** (the big stone, spell 47); Gazers (kind 12), demons
  (kind 5), hellhounds (kind 27), ice hounds (kind 44) and zombies (kind 8) also call the manager, the spell number was
  not read. The **dragons** and **winged gargoyles** use effect objects, not spells. **[H]** for the calls, *(unsure)*
  for the unread spell numbers.
* Mana: mage NPCs pay mana from record +0x3A, so a mage with 128 mana (the Generic Mage) casts a few spells and then
  stands still until it regenerates; the Liche and Zhah reset their mana to the maximum before each cast, so their
  casts are effectively free. **[H]**

---

## 7. Special creatures

Their attacks were not traced before. They use **animation-event handlers** (slot 0xB0) that start an **effect object**
through `Effect_Spawn_0060C8E0` / `Effect_Spawn2_0060C300` (both feed `Effect_Factory_0060CBE0`, a 32 KB switch on an effect
id and a variant 1-6) and **apply damage when that effect reports its hit event** (slot 0xCC) through `FUN_00623050`, the
area-damage call also used by spells. The damage is a literal range, and it reaches the target as environment damage
(`Combat_EnvironmentDamage_004F89E0`): the defender's multiplier for the type bits applies, and the Avatar's armour and
shield rolls are subtracted. Property 0x76 (Min/Max Damage) is **not** consulted on these paths (the range is a literal),
so a placed dragon cannot be made stronger through 0x76. **[H]** for the numbers below, **[M]** that 0x76 is not used (it is
read only in the natural, melee and bow paths).

| creature | trigger | effect | damage | radius | type bits |
|---|---|---|---|---|---|
| **Dragon** (kind 7, `Class_007449C0`) | animation event 0x34D (breath starts); effect hit event 0x3EC | effect id 0x3EC (flame breath), a second effect 0x3E9 on event 0x34F with a lifetime `random(75, 200)` ms | `random(35, 55)` | 70 | 0x240 (fire plus 0x200) |
| **Winged gargoyle** (kind 10, `Class_007462B0`) | event 0x384; effect hit event 0x0B | effect id 0x0B, a projectile at the current target | `random(10, 20)` | 50 | 0x210 |
| **Zhah** (kind 11, `Class_00745F38`) | event 0x37B | spell 47 (big stone) with mana reset to maximum | spell: `random(10, 20)` + Int bonus | 70 | 2 |
| **Liche** (kind 46, `Class_00748A40`) | event 0x359 | Lightning Bolt (22-25) or Summon Undead (Zombie); mana reset | spell damage | 75 | spell |
| **Slasher Demon** (kind 6, `Class_0074CC70`) | event 0x418 | Lightning Bolt, free, cooldown `random(3000, 5000)` ms | spell damage 22-25 | 75 | spell |

### Dragons (`Class_007449C0`, 0x314 bytes, parent `Class_00745360`)

* **Breath:** event 0x34D sets the "breathing" flags, creates effect 0x3EC (scale 3.0, length 25.0) and starts two timers
  (200 ms and 1200 ms: the damage window) and a 6000 ms lock (+0x2C4 = tick + 6000). When effect 0x3EC reports its hit the
  breath deals **35-55 damage in a radius of 70** at the effect's position (`vf0CC_00451580`), once, and the effect
  reference is cleared. **[H]**
* **States.** The class keeps a state number in +0x214 and picks the next one in `vf194_00450530` (the states have no
  names in the decompile; the transitions are **[H]**, the meanings **[L]**). From state 0 it goes to state 1 with
  probability 1/5 per check, to 6 when the "target near" flag (+0x228 bit 11) is set, else it stays. State 1 (the usual
  hovering or roaming state) goes to 7 when the near flag is set, to 10 when the "in breath range" flag (+0x2FD) and
  another flag (bit 0x200) are set, and otherwise stays and re-rolls a wait of `random(3000, 10000)` ms. States 2 and 3
  stay while the near flag is set and fall to 6 otherwise. State 6 goes to 2 when the target is near, to 1 when a
  flag (0x1000) is set and the target is not near, and to 1 when its timer (+0x2D8) runs out. State 7 goes to 0 with
  probability 1/4 and to 6 with probability 3/4 while the target object has object flag 0x8000 (a creature that is
  alive and fightable, *(inference)*). State 10 goes to 7 when the target is near, otherwise stays at 10 while the
  in-range flag is set. The update (`vf150_004507B0`) fetches the Avatar's position each frame, sets the "near"
  flag when the distance is below **0.8 x R** (R = record +0x54; the constructor also writes a default of 1200.0
  into +0x2F8 and then overwrites it from the record, I did not resolve the condition) and the "in range" flag when
  it is below R, and runs the state chosen. **[M]**
* **Constants** the constructor writes: flight-phase timers 16000 ms and 40000 ms (+0x280, +0x284), 1500 ms and 3000 ms
  (+0x28C, +0x290), 30 and 120 degrees (+0x294, +0x298), a default height of 512.0 (+0x250, 0x44FA0000), animation ids
  0x345, 0x351, 0x355, 0x34C and a pitch factor 1.2 (+0x3C). The dragon flies: `vf180_00450FA0` keeps it at a height
  relative to the target (values from the controller at +0x66C and +0x668) and adds random pitch steps of
  `random(10, 30)` degrees. **[M]**

### Winged gargoyles (`Class_007462B0`, 0x27C bytes, derived from `AI_Base`)

* On animation event 0x384 the class takes the NPC manager's current target of this AI (`FUN_004F9B20`), creates
  effect 0x0B (speed 24.0, scale 4.0) from itself to the target, binds it (`FUN_00515CB0`, 0x1E) and enters state 7. When
  the effect reports its hit (event 0x0B), the AI deals **10-20 damage in radius 50** and enters state 8. A wing-flap
  behaviour uses coin flips (`random(0,1)`, `random(0,3)`) and 300-1200 ms waits (`random(300, 0x4B0)`) between moves.
  **[H]** for the attack, **[M]** for the movement.

### Slasher Demon (`Class_0074CC70`, 0x3A0 bytes, parent `Class_00745360`)

* Its brain (`vf150_00528FE0`) counts down timers, runs `vf11C` ("active?") and `vf128` (engage), and has a
  cast window: while a deadline at +0x2EC has not passed (and +0x2F0 has), it spawns a flame effect (spell 23's effect
  slot 3) every 200 ms. The cast itself happens on event 0x418 (see the table). It picks a spell from the category lists
  the same way a mage does but then casts the constant spell 10. **[M]**

### Zhah (`Class_00745F38`, 0x29C bytes)

* Event 0x364 shakes the camera (`random(15, 25)` strength) and sets its target to the Avatar's controller; event
  0x37B refills mana (`+0x3A = +0x3C`), makes a target marker and casts spell 0x2F with flag word `0x180004 | local`;
  a 3000 ms field is set. Zhah therefore throws a **big stone for 10-20 damage** (the Int bonus is 0) in radius 70, before
  armour and multipliers. **[H]**

### Liche (`Class_00748A40`, 0x268 bytes)

* On event 0x359 (`vf004_004D4B20`) it refills mana, makes a target marker half the time and casts **Summon Undead (21)
  with parameter 0xCE (Zombie, record 206)** when the controller state says it may, else **Lightning Bolt (10)**; the
  cast spell object is kept at +0x258 and a Summon Undead result is also stored in a slot array at +0x25C so that it
  can be recalled. States: 8000-15000 ms between spell plans (`random(8000, 15000)`), 3000-10000 ms and 1000-5000 ms
  waits elsewhere. `vf174_004D5280` is a **blink**: it picks a point `random(50..75) %` of R from the target, rotated by
  `random(-45..45)` degrees, and starts the move (`vf0D0`). `vf020_004D4EF0` plays animation 0x35A, effect 0x3EA and a
  4000 ms follow-up (`FUN_004B2A20`); it is probably the death or defeat handler *(unsure)*. **[M]**

---

## 8. Data or code

| What | Where | Changeable without patching the exe |
|---|---|---|
| Which AI class a creature gets | NPC record **+0x44 (kind)** in `runtime/NPC.FLX`, switch in `Npc_CreateAI_004F5DC0` | **Yes**: change the kind of a record to give it another class (the class set is fixed; kind -1 makes it inert, 16/18/35/37 give an idle base AI). New games only (saved games hold their own NPC records) |
| Notice range, field of view, close zone | NPC record +0x54 (u16), +0x56 (u8), +0x57 (u8) | **Yes** |
| Mana and Int of a caster | record +0x3A/+0x3C, +0x30 | **Yes** |
| Immunity from being targeted | record flags +0x48 bit 0x20; +0xC4 bit 7; +0x48 bit 0x10000000 | **Yes** |
| Natural attack damage per creature | property **0x76** on the placed object (replaces the row) | **Yes** (map data). Not for the dragon, winged gargoyle, Zhah or spell users |
| Defence per creature | property 0x77 or record +0x26 | **Yes** |
| Which spells a mage casts | properties **0x72 and 0x73** on the placed object (kinds 39 and 56 only) | **Yes** |
| Stun time of kind-62 guards | property 0x75 on the placed object | **Yes** |
| Default activity (schedule) | property 0x44 on the placed object; the activity classes are code | the choice **yes**, the activities **no** |
| "Combat AI" (property 0x43) | the editor labels it; **no reader found** in the AI classes, so it probably has no effect *(unsure)* | - |
| Which weapon a humanoid carries | the first item of the right class in its map contents (placed items) | **Yes**; the default weapons are code |
| Spell damage, costs, dragon breath 35-55, gargoyle 10-20 | immediates in the code | No (exe patch) |
| Class behaviour: states, timers, cooldowns, animation ids | code in each AI class | No |
| Damage resistance (multiplier table) | start-up code (`exe-research.md`) | via exe patch |
| Perception and hostility rules (the 0.5 / 0.075 weights, faction modes) | code in `AI_Base` | No |

Not covered here (unread): the activity classes and the schedule data, the pathfinder, the humanoid controller `vf1BC`
and the other 0x170-0x1CC movement helpers, the animation controller, and the detailed state machines of the animals.
