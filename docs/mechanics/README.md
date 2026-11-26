# Ultima IX game mechanics

How Ultima IX: Ascension works, read out of the GOG `u9.exe` (4,593,152 bytes, SHA-256 `83915175...`) and cross-checked
against the game data. These documents describe; they do not contain decompiled code. The audited low-level findings
(file formats of the exe tables, combat tables, dialogue files) are in [`../exe-research.md`](../exe-research.md); these
documents build on it and do not repeat it.

## Documents

| file | what it covers |
|---|---|
| [spells.md](spells.md) | The magic system: all 47 table entries (number, name, mantra, circle, cost, damage, drain, class), how a spell is defined and cast, the spell classes and their virtual methods, damage and resistance, how the Avatar learns and casts (spellbook, Bind ritual, scrolls, wands, potions), how creatures cast, and what is data versus code |
| [enemy-ai.md](enemy-ai.md) | Creature and NPC AI: kind to AI class for all 64 kinds, the class hierarchy and `AI_Base` methods, perception ranges, target choice, wandering, attack choice, spell use, the dragon, winged gargoyle, Slasher Demon, Zhah and Liche attacks, and what can be changed through NPC.FLX and object properties |

## Conventions

* **Addresses** are virtual addresses as the game sees them, in hexadecimal. A function is written as
  `Name_ADDRESS` when the decompile has a name (for example `Spell_BuildTable_005ECE90`), as `Class::vfNNN_ADDRESS` for a
  virtual method (`NNN` is the byte offset of the slot in the vtable, so `vf150` is slot 0x150), and as `FUN_xxxxxxxx`
  when the decompile has no name. Data addresses are written as plain VAs (`0x78F640`). Object offsets such as "+0x54"
  are byte offsets inside the structure named in the sentence (an NPC record, an AI object, a spell object).
* **NPC record** means one 316-byte record of `runtime/NPC.FLX`, record 0 (see `exe-research.md`); "record +0x54" is
  byte 0x54 of it. **Kind** is the u32 at record +0x44.
* **Numbers**: `0x` prefix for hex; times are milliseconds unless the text says seconds; `random(a, b)` is the uniform
  integer from a to b (`Random_Range_005E88A0`, as in `exe-research.md`); "radius" is in world units as used by the maps.
* **Confidence tags:** **[H]** the statement was read directly in code or data at the address given; **[M]** it was read,
  but the meaning is an inference from context (names, how values are used); **[L]** a guess. In running text,
  *(unsure)* means the statement is plausible but not read, and *(inference)* means it follows from other facts that
  were read. Anything with no tag in a bullet that names its function is a direct reading.
* **Sources.** The decompile is a private working tree (`analysis/u9exe`, not in the repository). Cross-checks use the game
  data under `gamedata/` (`runtime/NPC.FLX`, `static/misctext.flx`, `static/TYPENAME.FLX`). The statements about spell
  names, circle headings and ritual text come from `misctext.flx`.
* **What was not done.** No claim here was verified by running the game. A reviewer should treat every **[M]**, **[L]**
  and *(unsure)* as open.

## Further mechanics documents worth writing

What the decompile already shows for each, in one line.

1. **combat.md** (hit rolls and the animation-event model): damage comes from tables and a few functions that are already
   audited (`exe-research.md`); still open is the hit-sphere test and blocking (`Combat_WeaponHit_004F9BE0`,
   `FUN_004F9FD0`, the humanoid controller `Class_007483E8::vf1BC_004B9520`), arrow flight, and the Avatar's attack
   modes in `Class_00741CA8`.
2. **npc-schedules.md** (activities and daily life): about 22 `Activity...` classes under `ActivityBase`, about 24
   `MasterActivity...` coordinators, property 0x44 "Default Activity", and the global highway manager that moves
   NPCs between towns. The activities aren't read yet; how NPCs find their way (the per-move A* search, the highway
   network in `static/highway.dat`, scripted marker routes) is in [npc-movement.md](npc-movement.md).
3. **rituals.md** (the nine rituals): spells 37-45 have no costs; `BindRitual` (vtable 0x65DE90, reagent bits, pentagram
   object type 0xB60, typed incantation), the Moongate ritual (orb in a circle of stones), Sanctify, Restoration and
   Armageddon each have their own process or conversation script; the circle gate `FUN_004F4240` counts eight global flags.
4. **effects-and-particles.md**: `Effect_Factory_0060CBE0` (32 KB switch, ids 1-0x24 follow the spells, 0x3E9-0x3F2 are
   creature effects), `FUN_005F5CB0` (45 KB), the effect classes `Class_007550C0` ... `Class_00755878`, and the fog and weather
   calls that Fog, Frost Storm and Searing Rain make on the renderer.
5. **items-and-use.md**: item type classes (0x1592 scroll, 0x1593 enchantment) and the usecode-style classes
   (`DrinkPotionProcess`, `UseValveProcess`, `FloatingItemProcess`, `HangingItem`), equipping (`Npc_Equip_004F0530`,
   properties 0x70 and 0x74), breakable items (property 0x10, `Item_GetBreakableHP_00623B60`) and the item property table.
6. **avatar-progression.md**: the Avatar's NPC record (mana 10 at the start, a byte at +0x25, a known-spell mask at +0x118,
   the armour-level array `AvatarArmourLevels_00837EF0` that triggers the Arms of the Magi mana discount), the eight
   virtue-quest flags that free circles, regeneration properties 0x54 and 0x55, and the shrine and meditation code.
7. **conversation-engine.md**: about 246 `...ConversationProcess` classes (state machines) that also grant spells
   (`FUN_004F3740`), give items and set global flags; `exe-research.md` covers only the text files.
8. **save-format.md**: how processes are saved (`Spell::vf008_005F23B0` version 4, `AI_Base::vf0DC_0043C440` version 5, the
   512 NPC records and the TYPES table), which fields are rebuilt on load, and what a mod can change safely.
9. **world-time-and-weather.md**: the game clock (`DAT_0083D39C` versus the frame time `DAT_0083D3A0`, used by Time Stop),
   the Day spell, fog and storm spells, and the global flag table `DAT_01333E64` (`FUN_006159D0`) that scripts and spells share.
