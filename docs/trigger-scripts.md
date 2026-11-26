# Trigger scripts: how the game runs them

Findings from reading u9.exe (GOG build) in Ghidra and capstone, checked by a second, independent reading
(2026-11-25). Groundwork for the trigger language (`trigger-language.md`). Names marked **?** are guesses;
the rest follow from the code. Addresses are virtual addresses in u9.exe.

Entries marked † come from a later reading, with the command forced in the running game through `tools/u9mon.py`
(`run`, which reports what changed in the records the script touched). They have not had the independent second
reading yet. "Live" in them means seen in the game.

## The machine

A script is a list of 6-byte ops `(u16 op, u16 A, u16 B)` in `static/TRIGGERS.FLX`. The interpreter is at
0x54F850; the commands it does not do itself go to 0x5510A0, once for each object that matches.

- **Command:** the op's low byte.
- **Link field:** the high byte's low 5 bits. 0x10 is +0, so offsets run from -15 to +15. 0 means "any link"
  for commands sent to objects, but **the current link** for 0x1F, 0x2A, 0x2F and 0x4B. The high byte's top
  3 bits are ignored (0 in all shipped data).
- **A:** its low 13 bits name the objects the op reaches, as a **usecode class** (0x1FFF: any). The game
  compares it with the usecode field (`TYPES.DAT` +4) of each object's type, not with the type. Most types
  are a class of their own (5,127 have their own number as usecode); class 2032 holds 16 types. On Britannia
  3,064 of 3,266 link-specific ops find an object by class, 2,669 by exact type. Some commands use A
  differently (see "What A means").
- **End:** command 0xFF. It is required: the game copies 0x1FE bytes (85 ops) into its buffer whatever the
  record's length, so a record without an end runs into stale ops. Shipped scripts 58 and 631 have none.

State while a script runs:

| State | What it is |
|---|---|
| **link** | starts as the triggering object's link; 0x11, 0x12 and 0x57 (on the trigger) change it |
| **range** | 0x500 object units (10 points) until 0x2C sets it. A square column: an object counts when both its x and its row are within the range of the trigger's (inclusive), at any height. At most 767 objects |
| **flag** (0xAE7C68) | cleared before every op; a test sets it or counts into it, and the same op branches on it |
| **options** (0x14) | which objects ops reach (below) |
| **jump count** | past 50 jumps in one run the game logs "Stopping Endless Trigger Loop!" and ends the script |

**Which objects an op reaches.** Only nonfixed objects (`nonfixed.N`) and NPCs: the search walks the map's
nonfixed list only (0x4C1AD0, mode 1), and fixed objects carry no link anyway. Of those, by default:

- objects with flag 0x10 or flag 0x40000. Flag 0x10 is set at run time when an object gets its live instance
  (0x4CBD80), so it is not in the map files; 0x40000 is on markers and zones, which never get one;
- not objects with both bits of 0x40100;
- **including** the triggering object itself.

Op 0x14 changes that: B & 1 clear leaves the trigger out (any 0x14 without bit 1 does: shipped B = 2, 4, 6,
204); B & 4 also reaches objects without flag 0x10 / 0x40000; B & 2 also reaches 0x40100 objects.

A = 0x1FFF with link field 0 (any class, any link) reaches nothing (0x5504EE).

**Scripts that never run.** An object whose link is 0 and whose type is 0x200 or more does not run its scripts
(0x54F8AB). On Britannia 12 of 1,768 objects with a trigger script have link 0.

**Entry labels.** The interpreter can start at the op 0x16 whose B matches a number it is given (0: the first
op). No caller in the game passes anything but 0 (all 97 call sites checked), so in practice every script
starts at its first op.

## Control flow

- **Labels:** op 0x16, label B. It is also sent to objects, but no object handles it.
- **Goto:** op 0x17 jumps to label B. Label 0, or a label the script lacks, ends the script.
- **Tests branch inside the same op**: when the flag ends up set, the op jumps to a label encoded in B.

| Commands | Test | Jump when the flag is set |
|---|---|---|
| 0x0E 0x3A 0x45 0x46 0x47 0x48 0x4B 0x53 0x58 0x5A 0x5B 0x61 0x64, and 0x29 0x3B | see the tables below | to label `B >> 13` |
| 0x1A | Variable vs `(B >> 4) & 0xFF`: B & 1 `<`, else `>` | to label `(B >> 1) & 7` |
| 0x15 0x2D 0x2F | the flag as a number compared with `B >> 8` by `B & 7`: 0 `==`, 1 `!=`, 2 `>`, 3 `<`, 4 `>=`, 5 `<=` (unsigned) | to label `(B >> 5) & 7` |
| 0x4F | Tint `!=` B & 0x7FFF | B & 0x8000: label 1, else the script ends |

Labels a test can reach are 1 to 7. In the "value vs limit" tests, B & 0x1000 set means `value < limit`, clear
means `value > limit` (checked for 0E/5B, 3A, 45, 46-48, 4B, 53, 5A, 61).

On the shipped TRIGGERS.FLX: of 2,229 jumps (tests and gotos, 0x4F aside), 1,451 name label 0 and 769 a
label the script has; 9 name a label it lacks (they end the script).

## Time

A script runs from start to end at once, in one game frame; no op waits. Anything that takes time is started
and the script goes on: a camera move, a movie (0x37), a sound, the processes of 0x1B, 0x2A, 0x2E, 0x38,
0x39, 0x54 and the timed forms of 0x22/0x24/0x25. Later steps happen through callbacks:

- A camera point runs its own script when its move ends (`CameraMove`, bits 0x2000 and 0x1000). Scenes chain
  this way.
- **SF Trigger 1 and 2 are success and failure callbacks.** When a movie ends (callback 0x54F7B0) the
  triggering object's SF1 runs, or SF2 when B > 12 or the movie failed or was cut short (nothing runs when
  movies are off, path "NONE"). The processes of 0x1B, 0x2E, 0x39 and timed 0x22/0x24/0x25 run the object's
  SF1 when done and SF2 when blocked.

**Nested scripts.** 0x01, 0x28 and 0x3D can run other scripts in the middle of a script. They save the
script's state (the pool at 0xAE7C70) and the outer script goes on afterwards. Two paths do **not** save it:
0x1C when the move is blocked (0x5514C8) and the immediate forms of 0x22/0x24/0x25 (0x550E50). They overwrite
the outer script's ops, link, trigger and pick list, even when the inner script number is 0, and the outer
script then carries on in the other script's bytes. A compiler should warn about ops after those.

0x1F (change map), when it finds the arrival, starts the transfer and **ends the script at once**, skipping
the rest and the `once` (0x13) handling.

Changes to properties are immediate. The one gradual form is 0x4E with B & 0x8000, which moves the Tint 10%
of the way each run and snaps within 0.05.

## What A means

| Path | A |
|---|---|
| commands sent to objects, by link or any link | usecode class |
| 0x21 gathering | usecode class |
| the op after a 0x21 | ignored (it works on the picked objects) |
| 0x4B | link 0 (effective): exact type; otherwise class (items inside containers: exact type) |
| 0x1F | ignored: the arrival is always class 1134 |
| 0x30 | compared with 0xD7 |
| 0x31 | NPC number |
| 0x2A | passed on as it is (meaning unknown) |
| the marker helpers of 0x0F, 0x1B, 0x1C, 0x1E (0x4C2DE0) | class 1134, whatever A says |

## Commands the interpreter does itself (38)

| Cmd | Name | B |
|---|---|---|
| 00 | nop | |
| 0D | set global | global B & 0x1FF = (B >> 9) & 0x7F (a 1,024-byte table at 0x1333E64; scripts reach 0-511) |
| 0E | if global | global B & 0x1FF vs (B >> 9) & 7; jump B >> 13 |
| 5B | if global | as 0E with the value + 8 |
| 11 | link = | B (the op's link offset is ignored) |
| 12 | link += | B < 0x8000: + B, else − (B & 0x7FFF) |
| 13 | once | after the run, the trigger's link becomes 0; that silences it only for types 0x200 and above (see "Scripts that never run") |
| 14 | options | B & 1 keep the trigger, B & 2 also 0x40100 objects, B & 4 also objects without flag 0x10/0x40000 |
| 17 | goto | label B (0 ends) |
| 1F | change map | map B & 0xFF (0: this one); B's high byte unknown; arrival: the class 1134 object with the current link (+ offset), map-wide; ends the script |
| 21 | pick | B from 1 to 1023 (else nothing): gather up to B matching objects (random replacement once full, random(B+1), not uniform). The next **dispatched** op (label and no-op commands included) works on them, ignoring its own A, link and range. Interpreter ops and gotos do not take the list. An empty pick still swallows the next op |
| 2A | fire a missile † | a `FireMissleProcess` (0x657DA0, 0xD0 bytes): kind B from the trigger, at objects of class A & 0x1FFF with the op's link. Live: script 2499 (B 0x2600, at markers) killed the Avatar, who stood at the target, within 2 s |
| 2C | range | B × 128 units, or B & 0x7FFF with the top bit; 0 = 0x500 |
| 2F | if link | compare as 0x15 |
| 30 | A = 0xD7: use the trigger | needs the trigger's flag 0x10. B 0: 0x4BDC60 on it; B ≠ 0: global 0x837BA4 = B, the trigger retyped to 0xD7 for a moment and used (0x4BEE70). Other A: sent to objects |
| 31 | NPC schedule | NPC A (< 352); B & 0x100: push schedule B & 0xFF on its stack, else replace the current one |
| 33 4C 4D 35 36 | music / ambient sound **?** | the sound manager at 0xAD1324. 33: 0x539F80 with B & 0x1FFF and B >> 13 †; 35: B & 0xFF and (B >> 8) × 100 †; 36: B × 100 † (the × 100 values look like milliseconds) |
| 34 | stop NPC fights **?** † | every NPC on the manager's active list whose +0x40 is 0: record +0xF4 = 0xFFFF and +0xE6 = 0, the same reset the per-frame fight code (0x4F97B0) does. Live: no change seen with no fight going on |
| 37 | play movie | movie B: 0 `Column`, 1 `UAStandinLogoTreatment`, 2 `Intro`, 3 `Ambush`, 4 `Samhayne`, 5 `Raven`, 6 `armageddon`, 7 `goodEndgame`, 8 `maelstrom`, 9 `moongate`, 10 `prison`, 11 `pyros`, 12 `skaraBrae`; SF1 / SF2 run when it ends |
| 3A | if clock | minutes since midnight vs B & 0xFFF; jump B >> 13 |
| 3E | **?** | 0x5B7220(B) on the current character's +0xD8 structure. Seen live with B = 0, 1, 6, 10, always returning 1. Map 201 ("Dave's Sound Test") has a row of zones running 3E with B = 0, 1, 2, 3, which suggests a sound or music setting |
| 3F | set / add clock | (B & 0x7FFF) × 9000; the top bit adds; clamps at 23:59 |
| 4B | if the Avatar carries | the Avatar's inventory, containers included (see "What A means"); the item's Quantity (0 when absent) vs B & 0xFFF; no range |
| 52 | set Avatar mana | B & 0xFFF, capped by max mana (+0x3C); B & 0x1000: 0x4F3FE0 raises max mana by B & 0xFFF instead; 0x2000 or 0x3000: 0x4F4030. Live †: B 0x1001 took mana, max mana and +0x3E from 10 to 11 |
| 5F | add to it | B & 0xFF; B & 0x8000 adds, clear subtracts; no floor (below 0 ends at the cap) |
| 53 | if it | vs B & 0xFFF; jump B >> 13 |
| 5C | Avatar switches **?** | B & 1, B & 2 set +0x110 and +0x114 of the structure at world +0xC0. Live †: 5C(0) changed nothing watched |
| 5E | busy switch **?** † | `[0x131F668]+0x50` = 1 when B & 1 is clear, 0 when set: the flag the map changer clears once a map change is done. No shipped script uses 5E |
| 60 | Avatar breathes under water † | the Avatar's movement controller (`AvatarMovementCon::vf1E8`) byte +0x68D = 1 when B & 1 is clear, 0 when set; while it is 1 the air (NPC record +0xB4) is not used up. Live: in Britannia's underwater zone (link 1 at 660, 700) 60(0) refilled the air to 100, 60(1) let it fall again (100, 91, 77) |
| 61 | if Avatar attribute | attribute B & 0xF (0-7: three at +0x28.., five at +0x124..) vs (B >> 4) & 0xF |
| 62 | change Avatar attribute | attribute B & 0xF; (B >> 8) & 0xF: 0 +1 (to 3), 1 −1, 2 set; attribute 2 sets +0x3A to 10× |
| 58 | if character state **?** | the bytes of the current character's +0xD8 structure picked by B & 0xFF (table 0x7930B0: 0, 1, 2, 5, 6, 7, 8, 9) are all nonzero |
| 63 | fade a screen effect **?** † | on 0x13206D8 (the sky structure that holds the clock): two levels 0-255 of a renderer effect (renderer +0x100, +0x738) step by ±1 (B & 0x80: down) every B & 0x7F and (B >> 8) & 0x7F ms; either 0: switch it on or off at once (0x5BEBC0 / 0x5BEC10). It stops when both reach 0. Live: nothing watched changed |
| 64 | if game setting **?** | |

## Commands sent to objects (0x5510A0)

Property names are the editor's (`ObjectProperties.cs`).

| Cmd | Name | B |
|---|---|---|
| 01 | activate | the object's usecode event 2; camera points: `CameraMove` |
| 02 | set link | B; on the trigger it also changes the script's link |
| 03 | set property | property `[Quantity, Hit Points, Owner, Hint Level][B & 3]` = B >> 4 (table 0x793138; the shipped scripts use Hit Points and Hint Level) |
| 04 | set type | B |
| 05 | set model | the object's model (record +0x18) = B, through 0x4CD290 (`__thiscall(store, object, B, 0)`). Checked in the running game: Deceit 1's lever script 244 switched a Button's model 1222 → 1239 → 1222 |
| 06 07 08 | set / clear / **toggle** flags | B's bits mapped to object flags (below); 08 XORs them |
| 09 0B | conceal / destroy † | 0x54F350 (the index's name `Trigger_Move` is wrong). 09: an object without both flags 0x40100 gets them (dormant again) and loses its live instance; B & 3: 1 fades it out over 4 s, 2 over 2 s, otherwise a shrink (`ScaleItem`) or a delay (`DelayConcealDestroy`). 0B: not read in full (destroy?) |
| 0A 0C | reveal / create † | 0x54EC00 (the index's name `Trigger_Teleport` is wrong). 0A: only objects with both flags 0x40100: clears them and creates the live instance, usecode event 0xD; B & 3: 1 fades in over 4 s, 2 over 2 s; B & 0x4000 skips the free-space test; B & 0x8000 waits for the space (`WaitToCreate`) instead of giving up. 0C makes a new object there (0x4C63E0), with the same fades: not read in full. Watched in the game: at the ring of virtue statues south of Britain, the Compassion zone (link 70 at 540.59, 613.19) swaps a Glyph and a Rune of Compassion with the same link above the statue. Its OO A (617): `09(2)` on the glyph, `0A(0x4002)` on the rune, `20(51)` on the rune. Its OO B (622): the reverse. The flags changed as described at each run. At the pings for 617 (the runs with `20`), the user heard a shimmering "magic appear/disappear" sound. The objects themselves were not seen: the area had not finished loading |
| 0F | spawn an effect † | effect (B & 0x3F) + 1 at the object (`Effect_Spawn2` 0x60C300). When the byte table 0x7A35D8 is non-zero at index B & 0x3F (2, 10 and 12 among the first 16, so effects 3, 11 and 13), the effect is aimed at the class 1134 marker with the object's link + 1 within 0x500 units instead (`Effect_Spawn` 0x60C8E0). Count ((B >> 7) & 7) + 1. Two sizes from the table 0x793188 {1, 8, 20, 40, 90, 170, 300, 500}, picked by (B >> 10) & 7 and B >> 13. B & 0x40 is passed on as a flag. Watched in the game: script 6591 (B 0xFD81: effect 2, four, size 500) at a marker on the sea west of Yew gave many bubbles, more with each run. Moonglow's script 918 (B 0x4485: effect 6, sizes 8 and 20) showed nothing visible |
| 10 | **?** | 0x50C2D0 on the object. Live †: sent to a marker, which returned 1; within 2 s the marker's record +0x1C (where 2E's mover put a handle, below) went back to 0 |
| 15 | count | flag += 1 for each object reached (it skips 0x40100 objects, which are only reached with 0x14 B & 2) |
| 2D | count flagged | flag += 1 for each object with flag 0x4000 (not 0x40100) |
| 18 | Variable = | B (property 0x3C) |
| 19 | Variable += 1 | |
| 1A | if Variable | see the branch table |
| 27 | Variable = random | random below B, keeping its top bit |
| 1B | move to marker | the first class 1134 object within 0x500 units whose link is (B >> 8) & 0xFF; SF1 when done, SF2 when blocked |
| 1C 1E | move by / to a marker **?** | 1E moves by an offset with jitter (B >> 8) & 3; 1C runs a script directly when blocked (see Nested scripts) |
| 1D | face nearest **?** | finds the nearest object; the interpreter then turns toward it |
| 20 26 32 | sound | the object's sound, B its number (20 plays, 26 the same with a last argument 1 (looped?), 32 stops the object's sound: 0x538170) †. Heard in the game: 20(51) on the Rune of Compassion, a shimmering "magic" sound (at the same moment as a 0A fade-in, so the sound could also belong to the fade) |
| 22 24 25 | turn | 0x550E50 modes 0, 1, 2; timed forms run SF1/SF2, immediate forms run them directly. Live †: 22 on Britannia's windvane (script 1427) kept changing its rotation (record +0x10/+0x12) for the next 6 s |
| 28 | run script | the object's OO1, OO2, SF1 or SF2 (B & 3) |
| 29 | if has any flags | flag set when the object has any of B's mapped flags (B & 0x1FFF) |
| 3B | if has all flags | flag set when it has all of them |
| 2B | stop its processes † | 0x576E50(object, −1): every running process whose vf4C claims the object is ended (vf14), chained ones included. The engine calls it too when it teleports or moves an object and when an AI goes away. Watched in the game: at Yew's archery range the lever's OO A (script 2792, `1B` to the target) set the target sliding toward its marker (a mover's handle in record +0x1C). Its OO B (script 2793, `2B`) 1.5 s later stopped it about 74 units short and cleared +0x1C. Pulled again, it moved on |
| 2E 54 38 39 | start a process | 2E / 39 run SF1 / SF2 at the end. 2E is an `AbsItemMover` (0x54B1D0) †, 38 a `FloatingItem`, 39 a `PathEggFollower`. Live †: 2E (script 4997, B 0x1F60) put a handle in three objects' record +0x1C and lowered them 232 units within 2 s |
| 30 | use / talk | objects with flag 0x10 (A ≠ 0xD7): 0x4BDC60, and when B ≠ 0 also used with B in 0x837BA4. Live †: 30(100) on Barny (script 6771) started a conversation: global 680 set, the Avatar's record +0x4A gained 0x1000 for about 15 s |
| 3C | sky toward the object **?** † | not a push: 0x5B8A10 on 0x13206D8 (the sky structure that holds the clock), toward the object's position over B & 0x7FF (at least 1), level (B >> 12) / 15, B & 0x800 halves a second value; not on map 61. Live: nothing watched changed |
| 3D | special function | hard-coded function B of 0x548900 (shipped 0-23); 21 ends the calling script |
| 40 | NPC activity | NPCs (types < 0x200): activity B & 0x7F, target (B >> 7) & 0x1FF |
| 41 | Translucency = | B & 0xFF (property 0x20) |
| 43 | Translucency += | B & 0xFF; B & 0x8000 subtracts; 0 to 255 |
| 45 | if Translucency | vs B & 0xFF |
| 42 | scale = | (B & 0xFFF) × 0.01; B & 0x1000 / 0x2000 / 0x4000 leave X / Y / Z out |
| 44 | scale += | the same; B & 0x8000 subtracts |
| 46 47 48 | if scale X / Y / Z | vs (B & 0xFFF) × 0.01 |
| 49 | cast a spell † | the object casts spell B & 0x3F (`SpellManager_Cast` 0x5ED950) at the class 1134 marker with link (B >> 7) & 0x1FF within 0x500 units (else at itself); B & 0x40: stop that spell of the object instead. Live: 49(0xA88) made Meranthon move (spell 8 at marker 21) |
| 59 | cast a spell at it † | the reverse of 49: the trigger casts spell B & 0x3F at each object; B & 0x40: stop the trigger's spell B & 0x3F |
| 4A | **?** | property 0x4C (Chunk Crossing) = 1, then 0x4CB2D0 files the object into the chunk under its position (x, y >> 10) †; objects with flag 0x100000 go through 0x572B20 first |
| 4E | Tint = | 5-5-5 colour (channel order unchecked); B & 0x8000: 10% of the way toward it |
| 4F | if Tint differs | |
| 50 | damage † | NPCs (types < 0x200): `NpcRecord::TakeEnvironmentDamage` (0x4F3660), amount B & 0xFF, kind table 0x7931A8[B >> 12], from the trigger's position; B & 0x100/0x200/0x400 flags; B & 0x800: 0x4F3580 instead. Other objects: usecode event 6 with the amount (capped at 255). Live: 50(0xA064) on a Demon Triumvirate took the Avatar's HP to 0. That demon was probably not a live NPC there, so its record lookup gave record 0, the Avatar. The table was named a sound table before |
| 55 56 | NPC looks / turns toward **?** | the nearest object in range, or the Avatar |
| 57 | link += | B & 0xFFF; B & 0x4000 subtracts; B & 0x8000: only on the trigger |
| 5A | if Hit Points | property 0x10 (NPCs: their record) vs B & 0xFFF |
| 5D | NPC switches **?** | B & 1, B & 2 |

**Flag map** (0x7930D8 → 0x793108), used by 06, 07, 08, 29 and 3B:

| B bit | 0x1 | 0x2 | 0x4 | 0x8 | 0x20 | 0x100 | 0x200 | 0x400 | 0x800 | 0x1000 |
|---|---|---|---|---|---|---|---|---|---|---|
| object flag | 0x2 | 0x40 | 0x8 | 0x100 | 0x400 | 0x4000 | 0x8000 | 0x10000 | 0x20000 | 0x40000 |

B bits 0x10, 0x40 and 0x80 map to nothing. What most object flags mean is still open (0x10 is "has a live
instance", 0x40000 is on markers and zones).

## Open

- Names of the commands marked **?**; the meaning of object flags 0x100, 0x4000, 0x40000; 0x1F's B high
  byte; 0x37's success test; the Tint channel order.
- Whether a trigger zone runs its script once on entry or every frame while the Avatar is inside. Seen so far: a
  zone (class 1111) runs its OO A as the Avatar arrives, even by teleport. The call comes from class 1111's usecode
  event 4 handler (0x632C40, the call returning to 0x632EE2). Zones on Earth (script 3132) and on map 201 (script
  1045) each ran once in the 10 s that followed, not every frame. A teleport 64 units above a zone that floats high
  (Britannia's at z 5700) did not set it off.
- The editor's `TriggerScripts.Describe` adds the link offset to 0x11, which the game ignores: fix with the
  language work.
