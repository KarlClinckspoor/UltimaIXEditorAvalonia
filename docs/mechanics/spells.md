# Ultima IX: the magic system

How magic works in Ultima IX: Ascension (GOG `u9.exe`, SHA-256 `83915175...`), as the decompilation shows it. Every
statement names the function that implements it (`Name_ADDRESS` as in the decompile, or `FUN_xxxxxxxx` for an unnamed
one). Formulas and constants are as the code has them. This file holds descriptions, names, addresses, constants and
formulas only, no decompiled code. See [README.md](README.md) for conventions.

Confidence tags: **[H]** read directly in code or data at the address given; **[M]** read, but the meaning is an
inference from context; **[L]** a guess. *(unsure)* and *(inference)* mark single statements in the same way.

Contents
1. [Where the data lives](#1-where-the-data-lives)
2. [The spell table](#2-the-spell-table)
3. [The spells, one by one](#3-the-spells-one-by-one)
4. [How a cast works](#4-how-a-cast-works)
5. [Spell classes and their virtual methods](#5-spell-classes-and-their-virtual-methods)
6. [Damage, resistance and multipliers](#6-damage-resistance-and-multipliers)
7. [The Avatar: learning and casting](#7-the-avatar-learning-and-casting)
8. [NPCs and creatures that cast](#8-npcs-and-creatures-that-cast)
9. [Data or code](#9-data-or-code)

---

## 1. Where the data lives

* **The spell table is code.** `Spell_BuildTable_005ECE90` (called once at start-up from `FUN_0049D890`) makes 47
  calls to `Spell_Define_005ECC60`, one per entry, with the numbers as immediate arguments. No file holds spell
  stats. The entries are stored in the spell manager object (`g_SpellManager_0084F810`) as an array of pointers; the
  entry for spell number `n` is at index `n - 1`, so most code reads the entry as "manager base + 4n - 4". **[H]**
* **Names come from `static/misctext.flx`.** `Spell_Define_005ECC60` replaces the English literal in the table with
  the misctext string at index `n + 0x65` for spells 1-36 (for example record 102 is "E - Stone"), index `n + 0x71` for
  spells 37-45 (the rituals, records 150-158) and record 102 again for spell 47. The prefix letter is the element:
  E earth, A air, F fire, W water (spell number mod 4 = 1, 2, 3, 0; see section 4, *Element blocks*). **[H]** for the
  indices, **[M]** for the element reading of the letters.
* **Mantras ("words") are literals in the exe**, copied into the entry. The ritual entries hold the placeholder
  "Foo" and "Bind"; the real incantation for a ritual is not in the table *(unsure: BindRitual compares typed text,
  see section 7)*.
* **Items** that cast: scrolls are item types 5330-5371 (0x14D2-0x14FB, names "Scroll: ..." in `TYPENAME.FLX`),
  mapped to spell numbers by `FUN_005F0220`. The eight reagents are item types 2192-2199 (0x890-0x897): Black pearl,
  Blood moss, Garlic, Ginseng, Mandrake root, Nightshade, Silk, Sulphurous Ash. Property 0x14 ("Enchantment") on an item
  holds a spell number in its high 16 bits (`Object_GetEnchantSpell_004C57A0`). **[H]**
* **Creatures that cast** read two properties of their placed object, 0x72 and 0x73 (64-bit mask, bit n = spell n), and
  the caster's mana from `runtime/NPC.FLX` (record +0x3A current, +0x3C max). **[H]**
* **The Avatar's known spells** are a 64-bit mask inside the Avatar's NPC record (+0x118, bit n = spell n), which is
  saved with the game. `FUN_004F3740` sets a bit, `FUN_004F3780` clears one, `FUN_004F3700` tests one. **[H]**

---

## 2. The spell table

### Entry layout

`Spell_Define_005ECC60` allocates 0x24 bytes per entry. **[H]**

| offset | size | meaning |
|---|---|---|
| +0x00 | u32 | spell number (1-47; 46 is the failure marker) |
| +0x04 | u32 | category: 0 offensive, 1 control/summon, 2 healing, 3 movement, 5 utility. Used by the NPC spell chooser (section 8). **[M]** |
| +0x08 | u32 | targeting flags (see below) |
| +0x0C | u32 | damage-type bits (the same bits as the combat multiplier columns: 0x1 slash, 0x2 blunt, 0x20 earth?, 0x40 fire, 0x80 cold, 0x100 magic, 0x400 lightning; 0x10 unknown) |
| +0x10 | u8 | minimum damage |
| +0x11 | u8 | maximum damage |
| +0x14 | f32 | mana drained **per second** while a sustained spell is active (0 = not drained) |
| +0x18 | u8 | mana cost of one cast |
| +0x19 | u8 | unknown (values 0x01-0xFF; one per spell). *(unsure: probably an effect or sound id)* |
| +0x1A | u8 | 1 = sustained (timed) spell, a cast toggles it off again |
| +0x1C | char* | mantra (words) |
| +0x20 | char* | display name |

**Targeting flags** *(inference from how the Avatar and the NPC mage code test them: `Class_00741CA8::vf150_00417150`,
`Class_00748F08::vf1D0_004DD490`)*: value 2 = no target, cast on self or around the caster; bit 2 (4) = a point on the
ground (the game creates a marker object of type 0x6F6 there); bit 3 (8) = an object or item; bit 4 (0x10) = a
creature; 0x80000000 = extra flag on Gust and Fireball (probably "projectile" or "uses a line of fire").

**The damage min and max are used only by spells that call `Spell_EffectDamage_005F1E40`.** Spells with 0-0 deal no
damage this way. The roll is `random(min, max)` plus `1 << Int` when the caster's Int (NPC record +0x30) is 1-3
(`Spell_RollDamage_005F0B70`). **[H]**

### The table

Spell numbers, mantras, categories and costs are from `Spell_BuildTable_005ECE90` **[H]**. "Circle" is from
`FUN_005F0C20` (spell number -> circle; spells that fall to the default, 1-4, are circle 0) **[H]**; the spellbook
pages and strings "First Circle ... Eighth Circle", "Linear Spells" fit (circle 0 = Linear). "Sust." = mana per second
while active. Targeting is shown as the raw value.

| # | Name (misctext) | Mantra | Circle | Cat | Target | Dmg type | Dmg | Mana | Sust. | Class (update method) |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | E - Stone | In Bet Ylem | 0 | 0 | 4 | 0x2 | 1-3 | 0 | - | SpellStone (`vf004_00643550`) |
| 2 | A - Gust | In Hur | 0 | 5 | 0x80000004 | - | - | 0 | - | SpellGust (`vf004_00644680`) |
| 3 | F - Ignite | In Flam | 0 | 5 | 8 | - | - | 0 | - | SpellIgnite (`vf004_00644C60`) |
| 4 | W - Douse | An Flam | 0 | 5 | 0xC | - | - | 0 | - | SpellDouse (`vf004_006453A0`) |
| 5 | E - Create Reagents | In Ort Ylem | 1 | 5 | 2 | - | - | 5 | - | SpellCreateReagents (`vf004_00650BD0`) |
| 6 | A - Telekinesis | Ort Por Ylem | 3 | 5 | 8 | - | - | 15 | - | SpellTelekinesis (`vf004_00651180`) |
| 7 | F - Light | In Lor | 1 | 5 | 2 | - | - | 5 | 0.0167 | Class_007569C8 (`vf004_00651A20`), unnamed |
| 8 | W - Light Heal | Mani | 1 | 2 | 2 | - | - | 5 | - | SpellLightHeal (`vf004_006522E0`) |
| 9 | E - Crystal Barrier | In Vas Ylem | 2 | 1 | 4 | - | - | 10 | - | SpellCrystalBarrier (`vf004_00640B10`) |
| 10 | A - Lightning Bolt | Ort Grav | 1 | 0 | 0x14 | 0x410 | 22-25 | 5 | - | SpellLightningBolt (`vf004_00641180`) |
| 11 | F - Infernal Armor | Uus Flam Sanct | 2 | 5 | 2 | 0x40 | 5-10 | 10 | 0.5 | SpellInfernalArmor (`vf004_00642530`) |
| 12 | W - Cure | An Nox | 2 | 2 | 2 | - | - | 10 | - | SpellCure (`vf004_00642D30`) |
| 13 | E - Meteorite | Kal Jux Ylem | 6 | 0 | 4 | 0x20 | 50-55 | 30 | - | SpellMeteorite (`vf004_0064E4F0`) |
| 14 | A - Wizard Eye | Por Ort Wis | 4 | 5 | 2 | - | - | 20 | 0.5 | SpellWizardEye (`vf004_0064EF50`) |
| 15 | F - Fireball | Vas Flam Hur | 3 | 0 | 0x80000014 | 0x40 | 30-35 | 15 | - | SpellFireball (`vf004_0064F840`) |
| 16 | W - Fog | An Wis Hur | 3 | 5 | 2 | - | - | 15 | 0.25 | SpellFog (`vf004_0064FE90`) |
| 17 | E - Charm | An Xen Ex | 3 | 0 | 0x10 | - | - | 15 | 0.25 | Class_00756598 (`vf004_0064C990`), unnamed |
| 18 | A - Ethereal Sight | In Quas Wis | 2 | 5 | 2 | - | - | 10 | 0.05 | SpellEtherealSight (`vf004_0064CFC0`) |
| 19 | F - Day | Vas Lor | 4 | 5 | 2 | - | - | 20 | 0.0167 | SpellDay (`vf004_0064D860`) |
| 20 | W - Freeze | Vas An Flam Grav | 4 | 1 | 0x18 | 0x80 | 0-0 | 20 | - | SpellFreeze (`vf004_0064DD60`) |
| 21 | E - Summon Undead | Kal Corp Xen | 5 | 1 | 0xC | - | - | 25 | - | SpellSummonUndead (`vf004_0064ABF0`) |
| 22 | A - Levitate | Por Uus Ylem | 5 | 3 | 2 | - | - | 25 | - | SpellLevitate (`vf004_0064B3C0`) |
| 23 | F - Bolt of Flame | Vas Flam Por | 5 | 0 | 4 | 0x40 | 10-15 | 25 | 1.5 (not flagged sustained) | SpellFlameThrower (`vf004_0064B6E0`) |
| 24 | W - Full Heal | Vas Mani | 5 | 2 | 2 | - | - | 25 | - | SpellFullHeal (`vf004_0064BEB0`) |
| 25 | E - Death | Kal An Lor Corp | 7 | 0 | 0x10 | 0x20 | 100-100 | 35 | - | SpellDeath (`vf004_0063E130`) |
| 26 | A - Invisibility | Sanct Lor | 6 | 5 | 2 | - | - | 30 | 1.0 | SpellInvis (`vf004_0063EB10`) |
| 27 | F - Ring of Fire | Kal Por Flam Grav | 6 | 0 | 2 | 0x40 | 20-35 | 30 | - | SpellRingOfFire (`vf004_0063FB00`) |
| 28 | W - Mana Breath | Ort Mani Hur | 6 | 5 | 2 | - | - | 30 | 2.5 | SpellManaBreath (`vf004_0063FF50`) |
| 29 | E - Time Stop | An Tym | 4 | 5 | 2 | - | - | 20 | 20.0 | SpellTimeStop (`vf004_00648C40`) |
| 30 | A - Teleport | Ort Por | 7 | 3 | 4 | - | - | 35 | - | SpellTeleport (`vf004_00649350`) |
| 31 | F - Summon Daemon | Kal Vas Corp Xen | 7 | 5 | 4 | - | - | 35 | - | SpellSummonDeamon (`vf004_00649DC0`) |
| 32 | W - Frost Storm | Vas An Flam Hur | 7 | 0 | 2 | 0x80 | 5-40 | 35 | - | SpellFrostStorm (`vf004_0064A390`) |
| 33 | E - Earthquake | Vas Por Ylem | 8 | 0 | 2 | 0 | 30-50 | 40 | - | SpellEarthquake (`vf004_00646440`) |
| 34 | A - Lightning Storm | Vas Grav Hur | 8 | 0 | 4 | 0x410 | 40-45 | 40 | - | SpellLightningStorm (`vf004_00646E60`) |
| 35 | F - Inferno | Vas Kal Flam Grav | 8 | 0 | 2 | 0x40 | 35-40 | 40 | - | SpellInferno (`vf004_006476B0`) |
| 36 | W - Searing Rain | Vas Kal An Flam Jux | 8 | 0 | 2 | 0x80 | 10-85 | 40 | 2.5 | SpellAcidRain (`vf004_00648070`) |
| 37 | Sanctify (ritual) | Foo | - | 5 | 4 | - | - | 0 | - | no class |
| 38 | Bind (ritual) | Bind | - | 5 | 8 | - | - | 0 | - | BindRitual (`vf004_0065E4E0`) |
| 39 | Restoration (ritual) | Foo | - | 5 | 2 | - | - | 0 | - | no class |
| 40 | Return (ritual) | Foo | - | 5 | 4 | - | - | 0 | - | no class |
| 41 | Spirit Speak (ritual) | Foo | - | 5 | 2 | - | - | 0 | - | no class |
| 42 | Reunite Body and Spirit (ritual) | Foo | - | 0 | 2 | - | - | 0 | - | no class |
| 43 | Summon Pyros (ritual) | Foo | - | 5 | 2 | - | - | 0 | - | no class |
| 44 | Barrier of Life (ritual) | Foo | - | 5 | 8 | - | - | 0 | - | no class |
| 45 | Armageddon (ritual) | Foo | - | 5 | 2 | - | - | 0 | - | SpellArmageddon (`vf004_00645FE0`) |
| 46 | SpellFailure | Foo | - | 5 | 2 | - | - | 0 | - | SpellFailure (`vf004_006486F0`) |
| 47 | E - Stone (second entry) | In Bet Ylem | - | 0 | 8 | 0x2 | 10-20 | 10 | - | SpellBigStone (`vf004_00645950`) |

Notes on the table. **[H]** unless marked.

* Display names differ from the exe literals in three places: entry 23 is "Bolt of Flame" in misctext (literal "Flame
  Thrower"), entry 36 "Searing Rain" (literal "Acid Rain"), entries 39-45 take the ritual names (literals such as
  "Mana Recover", "Moongate", "Berserker", "Resurrect" are leftovers of an earlier design). The class names in the
  decompile follow the literals.
* The numbering is by element and tier: spells 1-36 are four elements (E, A, F, W) by nine tiers in steps of four, but
  the **circle** (`FUN_005F0C20`) does not follow the tier; for example Lightning Bolt (10) is circle 1 and Telekinesis
  (6) circle 3. The circles are:

  | circle | spells |
  |---|---|
  | 0 | 1 Stone, 2 Gust, 3 Ignite, 4 Douse |
  | 1 | 5 Create Reagents, 7 Light, 8 Light Heal, 10 Lightning Bolt |
  | 2 | 9 Crystal Barrier, 11 Infernal Armor, 12 Cure, 18 Ethereal Sight |
  | 3 | 6 Telekinesis, 15 Fireball, 16 Fog, 17 Charm |
  | 4 | 14 Wizard Eye, 19 Day, 20 Freeze, 29 Time Stop |
  | 5 | 21 Summon Undead, 22 Levitate, 23 Bolt of Flame, 24 Full Heal |
  | 6 | 13 Meteorite, 26 Invisibility, 27 Ring of Fire, 28 Mana Breath |
  | 7 | 25 Death, 30 Teleport, 31 Summon Daemon, 32 Frost Storm |
  | 8 | 33 Earthquake, 34 Lightning Storm, 35 Inferno, 36 Searing Rain |

* Spells 37-45 are the nine **rituals** (misctext 150-158); their table entries carry no costs or damage. Only 38 (Bind)
  and 45 (Armageddon) have spell classes; the other rituals are done by conversation scripts and world objects
  *(unsure: not traced)*.
* Entry 47 is a second "Stone" (a larger version thrown by Zhah, section 8). It is not in any circle.
* **Costs are mostly 0 for circle 0 and 5-40 above it**: the cost grows with the entry's position in the table, not with
  the circle (Lightning Bolt, circle 1, costs 5; Death, circle 7, 35; Earthquake 40).
* The mana pool is the caster's NPC record (+0x3A current, +0x3C max). The Avatar's record in `NPC.FLX` starts with 10
  (current, max and base all 10); the shipped Generic Mage has 128, the Liche 500, the Slasher Demon 150. **[H]**
* Spell 22 Levitate, 29 Time Stop and the weather spells are described in section 3.

---

## 3. The spells, one by one

Every spell class is a process (section 5). The common skeleton is the same, and the section only lists what differs.
Times are in milliseconds; the frame time is in `DAT_0083D3A0` (elapsed ms per update) **[H]**.

**Skeleton.** In phase 0 the update method starts the cast animation (`FUN_005F3250`, skipped for item casts, flag
bit 0) and creates the visual effect object through `FUN_005F59D0` or `FUN_005F5490` (arguments: spell number, slot 2
or 3, caster, target). The effect object's reference is kept in the spell at +0xF8. In phase 1 the spell waits until
that effect object is gone (its reference becomes null), then applies its result and ends itself with virtual slot 0x2C.
*(inference from the update methods of SpellFireball, SpellCure, SpellLightHeal, SpellStone)*

### Circle 0

* **1 Stone (`SpellStone`).** Throws a stone (it creates an item of type 0x108F, 4239) and, when it lands, damages in
  an area of radius 64 with damage type 2 (blunt) through `FUN_00623050`. Table damage 1-3; the exact damage value
  passed to the area call was not traced *(unsure)*. Costs no mana. **[M]**
* **2 Gust (`SpellGust`).** Creates an effect object with the Gust visual and ends when it is gone; no damage in the
  code read. Probably pushes or extinguishes *(unsure)*.
* **3 Ignite (`SpellIgnite`), 4 Douse (`SpellDouse`).** Effect-only update methods (one effect object, then end). Their
  target preconditions are `FUN_00644A80` and `FUN_00645160` (called from `FUN_005EF880`). Ignite lights and Douse
  puts out flammable objects such as torches *(inference from names and targeting values 8 and 0xC)*.

### Circle 1

* **5 Create Reagents (`SpellCreateReagents`).** Creates a set of reagent items at the caster (types 0x891, 0x892,
  0x893 and 0x896, or 0x891, 0x892, 0x893 without 0x896, or 0x896, 0x895, 0x891, 0x892, 0x893, depending on a condition
  that was not decoded) **[M]**. Precondition `FUN_00650440`. The scroll "Summon Reagents" (type 5340) is the same spell.
* **7 Light (`Class_007569C8`, unnamed).** A sustained light on the caster. Mana drain 1/60 per second, which is one mana
  point per minute. **[M]**
* **8 Light Heal (`SpellLightHeal`).** Heals the caster by 30 (calls `FUN_004F3580(record, 0x1E)`) once its effect object
  is gone. **[H]** (the 0x1E; **[M]** that `FUN_004F3580` is a heal).
* **10 Lightning Bolt (`SpellLightningBolt`).** Strikes the target creature. When the effect ends it calls the area
  damage with **radius 75 (0x4B)**, damage `Spell_EffectDamage` (22-25), damage type 0x410, mode 3. **[H]**

### Circle 2

* **9 Crystal Barrier (`SpellCrystalBarrier`).** Creates a barrier effect at the aimed point. Precondition
  `FUN_00640690`. Cast from a scroll with flag bit 3 it behaves differently (the cast code sets bit 3 when an item of
  spell 9 is used up). No damage. *(unsure about what blocks movement: the effect object, not read)*
* **11 Infernal Armor (`SpellInfernalArmor`).** Sustained (0.5 mana/s) fire aura. When cast from an item it lasts
  **45,000 ms**. A timer accumulates frame time; **every 1000 / 0.2 = 5000 ms** (constant 0.2 at +0x128) it applies two area
  damage bursts around the caster, **radius 128 (0x80)** and **radius 32 (0x20)**, fire damage type 0x40, once in mode 2
  and once in mode 1 *(the damage values come from a computed field, not decoded)*. The aura also creates the effect
  object of class `Class_00755BD8`. **[M]**
* **12 Cure (`SpellCure`).** After its effect it clears the target's poison (`FUN_004F9900` on the target's AI object,
  then `FUN_004F9A50`) *(inference: `FUN_004F9850` is the poison adder in `exe-research.md`)*. **[M]**
* **18 Ethereal Sight (`SpellEtherealSight`).** Sustained (0.05 mana/s; 45,000 ms from an item). While active it scans
  objects within radius 1024 (0x400) of the caster and reveals the ones that carry the flag 0x1000 and property 0x20 =
  0xFF *(hidden or ethereal objects)*. **[M]**

### Circle 3

* **6 Telekinesis (`SpellTelekinesis`).** Uses an object at a distance: for an item (type >= 0x200) it calls the item's
  Use with the caster as the user (`Object_Use_004BEE70`). Precondition `FUN_00651870`. **[H]**
* **15 Fireball (`SpellFireball`).** A projectile. At impact: damage `Spell_EffectDamage` (30-35), **radius 70 (0x46)**,
  damage type 0x40 fire, mode 3; plus a second call with **radius 200**, damage 0, type 2 (a push; it moves nearby
  creatures), and a random shove of every object caught in a radius of 150 (it pushes each by a random vector of up to
  +-200). **[H]**
* **16 Fog (`SpellFog`).** Sustained (0.25 mana/s). Sets a fog colour and range on the renderer (`FUN_005B8A10`, range
  5.0) and calls `FUN_004F9AE0` on the NPC manager (probably "NPCs lose sight") *(unsure)*. Fog and Searing Rain replace
  each other (section 4).
* **17 Charm (`Class_00756598`, unnamed).** Needs a creature target (object flag 0x10). Calls the NPC manager
  (`FUN_004F99E0`) to make the target act as the caster's follower; ending it uses `FUN_004F9A50`. Sustained (0.25
  mana/s). From an item it lasts **15,000 ms** (the check is "< 15000"). Targets whose record flag +0x48 bit 0x20
  (immune to environment damage) are tested, probably to refuse them *(unsure)*. **[M]**

### Circle 4

* **14 Wizard Eye (`SpellWizardEye`).** Sustained (0.5 mana/s; **30,000 ms** from an item). Makes a floating eye object
  from NPC record 234 (0xEA) at the caster's position plus an offset, hides the caster (`FUN_004BEB00` with -3) and
  takes over the camera (it saves the caster's heading and the camera state). The view returns to the caster at the end.
  **[M]**
* **19 Day (`SpellDay`).** Sustained (1/60 mana/s; **120,000 ms** from an item). Forces daylight while active *(inference
  from the name; the clock call was not traced)*.
* **20 Freeze (`SpellFreeze`).** Table damage 0-0, so the roll is 0 (plus the Int bonus for an Int of 1-3). The target's
  AI is frozen (`FUN_004F9800(ai, 1, 1)`) and unfrozen at the end (`FUN_004F9800(ai, 0, 1)`). It applies the (zero)
  roll as environment damage with type 0x80 cold. **[M]**
* **29 Time Stop (`SpellTimeStop`).** Sustained at **20 mana/s**. From an item it lasts **10,000 ms**. While it runs the
  Avatar cannot cast anything else (section 4) and the game clock is held (`FUN_005DD8A0(0x40)` at start, the clock
  reference `DAT_0083D39C` is used instead of the frame time in the update). **[M]**

### Circle 5

* **21 Summon Undead (`SpellSummonUndead`).** Creates an undead near the target point: NPC type 0xCB (203, Skeleton) or
  0xCE (206, Zombie), chosen at random when the spell's parameter (high 16 bits of the flags) is 0; otherwise the
  parameter is the type. The Liche passes 0xCE. Precondition `FUN_0064AA50`. **[H]**
* **22 Levitate (`SpellLevitate`).** Creates an object of type 0x1409 on the caster. Only one Levitate can exist at a
  time (section 4). Precondition `FUN_0064B0A0`. **[M]**
* **23 Bolt of Flame (class `SpellFlameThrower`).** Table 10-15 fire, 1.5 mana/s. The spell keeps casting while the
  Avatar holds the button (it reads the targeting object from the Avatar's controller). The per-hit damage is dealt by
  a helper class with the static flame-thrower trap (`StaticFlameThrower::vf004_0064BA30` also calls
  `Spell_RollDamage_005F0B70`). **[M]**
* **24 Full Heal (`SpellFullHeal`).** After **2250 ms** (constant at +0x110) plus the effect, sets the target's HP to
  its maximum. **[H]**

### Circle 6

* **13 Meteorite (`SpellMeteorite`).** Needs open sky: the precondition `FUN_0064E2B0` traces a ray 1500 units up from
  the target point and fails if it hits something. The meteor starts 1500 above the target, lands, and deals area damage
  **radius 128 (0x80)**, damage `Spell_EffectDamage` (50-55), type bits **0x42** passed to the area call (fire plus blunt;
  the table says 0x20). **[H]** for 1500, 0x80 and 0x42.
* **26 Invisibility (`SpellInvis`).** Sets the caster's record flags (+0x48) to 0x200. Sustained at 1 mana/s; from an
  item (or potion) it lasts **45,000 ms**. Also cast by the invisibility potion (spell 0x1A, flag bit 0). **[H]**
* **27 Ring of Fire (`SpellRingOfFire`).** Creates a `Class_007559C0` object ("FireRingWatcher", 0x288 bytes) that
  keeps up to 0x20 + 0x40 fire segments; creatures crossing the ring take `Spell_RollDamage` (20-35) with environment
  damage, scaled by 0.6 of a height field *(unsure)*. **[M]**
* **28 Mana Breath (`SpellManaBreath`).** Sustained at 2.5 mana/s; **59,999 ms** limit from an item. Works on the
  Avatar only (it reads the Avatar's record and controller state 3) *(inference)*: an effect object around the
  Avatar.

### Circle 7

* **25 Death (`SpellDeath`).** Works only on creatures that can be killed: not flagged invulnerable (record +0x48 bit 0),
  with an AI object, base max HP of at most 99, and whose type's class field is not 0x82 (`FUN_0063DFA0`). The target
  shrinks between 2500 ms and 4000 ms (the target's scale is multiplied by `1 - progress`) and the spell ends at
  10,000 ms. Damage goes through environment damage with type 0x100 (magic): if the target's current HP is at most
  `multiplier(kind, 0x100) x min(HP, 100)` a lethal blow of `HP + 255` is dealt, otherwise two ordinary hits. The
  precondition and the 100-100 table damage are **[H]**; the lethal-hit formula **[M]**.
* **30 Teleport (`SpellTeleport`).** Creates a marker (type 0x168F) at the aimed point and moves the caster there; the
  precondition `FUN_00649150` rejects invalid points. A global flag 0x2F1 is set while the marker exists (it is read by
  the trigger scripts) and then cleared. **[M]**
* **31 Summon Daemon (`SpellSummonDeamon`).** Creates NPC type 0xB8 (184, "Demon", kind 5) at the target point; the
  daemon fights for the caster *(inference: its AI is set up through `FUN_004F9800`, the same call as Freeze, and the
  spell is re-entered through the manager at the end)*. Precondition `FUN_00649C20`. **[M]**
* **32 Frost Storm (`SpellFrostStorm`).** Damage `Spell_EffectDamage` (5-40), type 0x80 cold. Weather spell
  (section 4). **[M]**

### Circle 8

* **33 Earthquake (`SpellEarthquake`).** Lasts **20,000 ms** (constant at +0x114). It shakes the camera
  (`FUN_00434250`) and, at intervals of `1000 + random(0..3000)` ms, picks a creature from the NPC manager's list of
  nearby creatures (`FUN_004F9B20`) and deals `Spell_EffectDamage` (30-50) as environment damage. Only one at a time. **[M]**
* **34 Lightning Storm (`SpellLightningStorm`).** Repeating strikes at random points within +-384 (0x180) of a centre
  (each strike is `random(0..0x2FF) - 0x180` in x and y), **radius 50 (0x32)**, damage `Spell_EffectDamage` (40-45),
  type 0x410, at intervals of `1000 + random(0..1000)` ms. Only one at a time. **[M]**
* **35 Inferno (`SpellInferno`).** Creates `Class_007559C0` fire objects repeatedly at intervals of `1000 +
  random(0..2000)` ms and uses a random pick `random(30, 45)` *(unsure: the pick is probably a count or a damage)*.
  Table 35-40 fire. Only one at a time. **[M]**
* **36 Searing Rain (`SpellAcidRain`).** Sustained at 2.5 mana/s; **30,000 ms** from an item. Every `1 / 0.25 = 4000 ms`
  (constant 0.25 at +0x1AC) it picks a random creature from its list and damages it with `Spell_EffectDamage` (10-85),
  type 0x80. Weather spell. **[M]**

### Rituals

* **38 Bind (`BindRitual`).** See section 7.
* **45 Armageddon (`SpellArmageddon`).** A class exists (`ctor_00645E50`, update `vf004_00645FE0`) but contains no
  constants of interest; the ritual text says "Its effects are unknown and must remain that way" (misctext 167). Not
  traced.
* The other seven rituals (37, 39-44) have table entries but no spell class; they are performed by conversation
  scripts and special objects *(unsure)*. The Avatar learns them through conversations (section 7).

### Spell 47, the big stone

`SpellBigStone` (ctor `FUN_006454C0`, update `vf004_00645950`): damage `Spell_EffectDamage` (10-20), **radius 70 (0x46)**,
type 2. It is the attack of the creature Zhah (section 8). The spell parameter is the high 16 bits of the flag word. **[H]**

---

## 4. How a cast works

Entry point: `FUN_005ED950` (this = the spell manager; I call it *SpellManager_Cast*). Arguments: caster object
reference, spell number, target reference and four more values (the decompile's argument order for this function is
unreliable, so the roles below come from the callers and from `Spell::ctor_005F20A0`): the **flag word** that ends up in
the spell object at +0xD8, an animation id that is -1 for "pick by spell" (stored at +0xE0), and a trailing value that
callers pass as 0x10 (Avatar, mage) *(unsure what it is)*. Flag word: bit 0 = cast from an item or potion (no mana,
no animation), bit 2 = no cast animation (`FUN_005F3470`), bit 3 = set when a Crystal Barrier scroll is consumed; the
high 16 bits are a spell parameter (the summon type for Summon Undead, 0x78FDA8 and similar constants for creature
casts). The mage code passes 0, so it pays mana; the Slasher Demon, Liche and Zhah pass a flag word of their own. It
returns the new spell object, or null on failure. It is called by the Avatar
controller (`Class_00741CA8::vf150_00417150`), by creature AIs (see section 8), by `DrinkPotionProcess` (five potions
cast spells 0x1A Invisibility, 0x1C Mana Breath, 0x0C Cure, 0x08 Light Heal, 0x0B Infernal Armor with flag bit 0), by the
Bind ritual and by conversation scripts. **[H]**

The steps, in order:

1. **Avatar-only gates (caster type 0).**
   * **Time Stop.** If a Time Stop is active (`FUN_005F0A10(manager, 0x1D, caster)`), only Time Stop itself can be
     cast; anything else returns null silently.
   * **Element blocks.** Global flags 0x16D, 0x16E, 0x16F and 0x170 each disable one element (spell number mod 4 =
     1, 2, 3, 0, so earth, air, fire, water) and flag 0x171 disables every spell. The check is a global-flag read
     (`FUN_006159D0` on the trigger flag table `DAT_01333E64`). A blocked cast creates a `SpellFailure` (fizzle). The
     scripts set these flags *(not traced which)*. **[H]** for the check, **[L]** for what sets them.
   * A target is chosen for spells that need a creature and have none: the first hostile creature in the NPC manager
     (`FUN_004F8FD0`).
2. **Weather exclusion** (spells 0x10 Fog, 0x24 Searing Rain, 0x20 Frost Storm): casting one terminates the other two
   if they are running (the Fog and Searing Rain are replaced, and re-casting Frost Storm fails with a `SpellFailure`). **[M]**
3. **Single-instance spells.** Levitate (0x16), Earthquake (0x21), Lightning Storm (0x22) and Inferno (0x23) look for a
   running spell with process class 0x80, 0x8B, 0x8C or 0x8D (their own classes) and fail if one is running. **[M]**
4. **Toggle.** For sustained spells (entry +0x1A) the manager looks for a running instance of the same spell owned by
   the same caster; if found and the cast is not from an item, it terminates that instance and returns (a second cast
   switches the spell off). Charm (0x11) looks up its instance by target instead. **[H]**
5. **Target precondition.** `FUN_005EF880` is a switch over a handful of spells with their own check (3, 4, 5, 7, 9, 0xD,
   0xE, 0x11, 0x15, 0x16, 0x19, 0x1E, 0x1F; for example Meteorite needs open sky, Death needs a killable target,
   Teleport needs a valid point). If it fails the result is a `SpellFailure`. **[H]**
6. **Mana.** For a normal cast (flag bit 0 clear) `FUN_005ED7E0` compares the entry's cost (+0x18) with the caster's
   current mana (record +0x3A); if the cost is higher, `SpellFailure`. For a creature whose type is above 0x1FF the check
   always passes. If the Avatar wears the **Arms of the Magi** (arms armour level 7, type 5420; `AvatarArmourLevels
   [3] == 7`), the cost is multiplied (quarter, minimum 1 when the cost is above 0; the factor is explicit only in
   the sustain code, see below) *(inference for the single-cast factor)*. On success `FUN_005F0ED0` subtracts the cost
   (the mana never goes negative). **[H]**
7. **Item casts** (flag bit 0): if the used item is a scroll (type class 0x1592) whose spell (`FUN_005F0220`) matches, the
   scroll object is deleted (`FUN_004C4210`); if the item has the object flag 0x200 a sound plays. **[M]**
8. **Spell object creation.** A `switch` on the spell number news the right class (sizes 0x130 bytes for most, 0x118 for
   `SpellFailure`) and calls its constructor with the caster, target and flags. The result is stored in the manager's
   process list at +0xC0 (`FUN_005F0FD0` adds, `FUN_005F1610` removes). Numbers with no class (37, 39-44, 46) return
   null. **[H]**

**Sustained spells** (entry +0x1A = 1, or Bolt of Flame): each update calls `FUN_005F2760`, which converts the elapsed
time to mana: `elapsed_ms x 0.001 x entry+0x14` (and x 0.25 if the Avatar wears the Arms of the Magi). Once the
accumulated amount exceeds 1.0, that many points are subtracted from the caster's mana (`FUN_005F0ED0`) and the
remainder is kept. When mana reaches 0 the update ends the spell. Casts from an item skip this and use a fixed time
limit (section 3). **[H]**

**Cast animation** (`FUN_005F3250`): only for creatures below type 0x200. If the spell's animation id (+0xE0) is -1 it is
chosen from the spell number (the code's switch is over spell number minus one; the list below is already converted):
0x3C4 for spells 1, 15, 17, 23 and 47; 0x3DA for 2; 0x3DB for 3 and 4; 0x1A6 for 5, 7, 8, 11, 12, 14, 16, 18, 19, 22, 26,
29, 30 and 40; 0x365 for 6; 0x3C3 for 9 and 20; 0x1A8 for 10; 0x3BA for 13, 27, 34, 36 and 46; 0x366 for 21, 25, 32 and
45; 0x1A7 for 24; 0x1A9 for 31 and 43; 0x367 for 33; 0x45A for 35; no animation is set for 28, 37, 38, 39, 41, 42 and 44.
The animation ids are the same kind of id the creature AIs react to in their animation-event handlers (for example
0x37B, 0x359 and 0x418 in section 8). **[H]** for the table, **[M]** that it is read as spell minus one.

**Failure.** `SpellFailure` (class vtable 0x756190, ctor `FUN_00648570`) is created for every failed step above. Its
update plays the fizzle sound (strings "Spell Fizzle 1", "Spell Pitch 1/2") and ends. **[M]**

**Effect objects.** `FUN_005F59D0` and `FUN_005F5490` call `FUN_005F5CB0`, a 45 KB function that builds the visual
effect for (spell number, slot). Effects not tied to a spell are built by `Effect_Factory_0060CBE0` (32 KB, switch on an
effect id and a variant 1-6). Effect ids 1-0x24 follow the spell numbers (the Bind ritual passes 0x26 with slots 2-5);
ids 0x3E9-0x3F2 are creature effects (dragon breath 0x3EC, 0x3E9 and Liche 0x3EA; see `enemy-ai.md`). **[M]**

---

## 5. Spell classes and their virtual methods

All spells derive from `Spell` (vtable 0x754800, 25 slots, parent `Process`). Constructors `Spell::ctor_005F20A0` and each
subclass's first constructor are called by the manager; a second constructor per class (listed in the decompile as
`ctor_...` with callers `FUN_005F1A50` and the start-up loader) rebuilds a saved sustained spell. **[H]**

| slot | method | what it does |
|---|---|---|
| 0x00 | `vf000` | scalar deleting destructor (per class) |
| 0x04 | `vf004` | **update**, one call per frame; the whole spell logic (state machine below) |
| 0x08 | `Spell::vf008_005F23B0` | save to the save file (record version 4: caster ref, flags, spell number, target ref, effect ref, animation, state fields) |
| 0x0C | `Spell::vf00C_005F2510` | load (version 4 only; any other version returns false) |
| 0x14 | `Spell::vf014_005F22C0` | **terminate**: releases the effect object, deletes a leftover target marker (type 0x6F6), runs the per-spell end callback from the table at VA 0x78F640, unregisters from the manager |
| 0x2C | `Class_00754310::vf02C_005DDBD0` | "finish" (used as `this->vf02C()` in every update to stop the spell) |
| 0x54, 0x58 | per class | small hooks, mostly empty |
| 0x5C | `Spell::vf05C_005F1E70` | for sustained spells: copies the spell state into a 0x2C-byte save record (Invisibility, Mana Breath, Infernal Armor, Ethereal Sight, Day and Light use it) |
| 0x60 | `Spell::vf060_005F1F60` | the matching restore; re-registers the spell with the manager |

**Fields** of a `Spell` object (offsets in the object): +0x18 process class id (0x6A + spell number for spells 1-36:
Stone 0x6B ... Searing Rain 0x8E; Armageddon 0x97, Big Stone 0xA9); +0xD8 flag word; +0xDC spell number; +0xE0 cast
animation id; +0xE4 **phase** (0 setting up, 1 running); +0xE8 **sub-state** (2 and 3 are the two effect steps, 5 means
waiting for a spawned object; Inferno also uses 4); +0xEC elapsed ms in the running phase; +0xF0 caster reference;
+0xF4 target reference; +0xF8 effect object reference; +0xFC parameter (high 16 bits of the flag word for Fireball and
Big Stone); +0x100 spell number again; +0x104 the **end callback** from the table at 0x78F640 (spell 1-36; 37 function
pointers, entry 0 empty). *(unsure: purpose of the callbacks; they run at the end and take the caster's reference)*; from
+0x110 the per-spell fields (timers, positions). **[H]** for the layout, **[M]** for the names.

The table at VA 0x78F640 (file offset 0x38D240) holds function pointers `FUN_00632200, FUN_004F9B90, FUN_00622250,
FUN_00622350, ...` indexed by spell number.

**Helper classes** that spells create: `Class_007559C0` (the ring-of-fire / inferno fire segments, 0x288 bytes, update
`vf004_0063F320`), `Class_00755BD8` (the Infernal Armor visual, `ctor_006416E0`), `Class_00755088`, `Class_007550C0`,
`Class_00755160`, `Class_00755588`, `Class_00755878` (effect processes for the lightning and storm spells, not read
in detail). `MagicDartMovementController` and `Class_00749128` implement the "Magic Dart" creature (kind 55), which
uses natural row 16, not a spell.

---

## 6. Damage, resistance and multipliers

* A spell's damage number comes from `Spell_RollDamage_005F0B70`: `random(min, max)` with `Random_Range_005E88A0`
  (uniform min..max) plus `1 << Int` if the caster's Int (record +0x30) is 1, 2 or 3, else plus 0. Every NPC caster has
  Int 0, so only a caster with those Int values gets the bonus (same result as `exe-research.md`). **[H]**
* The damage is then applied by one of two routes. **Area route:** `FUN_00623050(radius, damage, position, type bits,
  source, mode)` collects every object within the radius (up to 4096). For a creature (type < 0x200) it calls
  `NpcRecord::TakeEnvironmentDamage_004F3660(damage, position, flags = 6, type bits, source type)`; for an item it calls
  `FUN_00624370` (breakable item damage, capped at 255, only when mode bit 0 is set). Mode bit 1 skips the caster
  itself. **Single route:** a spell's update calls `TakeEnvironmentDamage_004F3660` directly for one target. **[H]**
* `Combat_EnvironmentDamage_004F89E0` takes a flag word (the area route passes 6, that is bits 2 and 4). **Bit 4
  (value 4) applies the defender's multiplier for the damage-type bits** (`CombatMath_ApplyMultiplier_00449F10`, which
  returns 0 for "no bits" and for an immune creature) and **subtracts defence** (`random(0, record +0x26)` for creatures,
  the Avatar's armour roll plus the shield roll for the Avatar). Bit 1 (value 1) scatters the hit position by +-500
  in x and y (it is not set here); the meaning of value 2 was not read. The result is floored at 0 and is 0 for an
  invulnerable defender. So fire, cold and lightning spells **are resisted through the same 64 x 11 multiplier table as
  weapons** (`exe-research.md` section 1): a creature with a fire multiplier of 0 takes no fire damage. A negative
  result never heals. **[H]**
* **The type bits that reach the multiplier are the ones the spell's code passes to the area or damage call, not
  always the table's column.** Seen: Fireball 0x40 (table 0x40); Stone 2 (table 2); Infernal Armor 0x40 (table 0x40);
  **Meteorite 0x42, fire plus blunt (table 0x20)**; Death 0x100 (table 0x20). Lightning Bolt, Lightning Storm,
  Earthquake, Frost Storm, Freeze and Searing Rain pass values that the decompile does not show clearly *(unsure)*.
  For a spell that passed no bits at all (possible for Earthquake, table type 0) the combiner's "no bits gives 0" rule
  would remove the damage *(unsure whether it does)*.
* Freeze and the other non-damaging effects do not use the multiplier; Death's lethal blow does (type 0x100), see
  section 3.

---

## 7. The Avatar: learning and casting

**Spellbook.** The item "Spellbook" is type 945 (`TYPENAME.FLX`). The book is a user interface class
(`Class_00742C70`, `open_spellbook` string) with pages `spellbook_circle_0` to `spellbook_circle_8`, `spellbook_rituals`,
`spellbook_glossary` and `spellbook_reagents` (strings at 0x783A00-0x783ADC). It lists spells whose bit is set in the
Avatar's known-spell mask (record +0x118) and shows the circle headings from misctext ("First Circle" ... "Eighth
Circle", "Linear Spells", "RITUALS", "Reagents"). **[M]** (the list logic was not traced).

**What is known at the start of a new game** (`Class_00745CC8` start-up): spells 1, 2, 3, 4 (circle 0) and 0x26 (the Bind
ritual). The Avatar's record also gets a byte at +0x25 set to 0 *(unsure: it is set to 8 by the "learn all" helper
`FUN_0042DDA0`)*. **[H]**

**Learning a spell: the Bind ritual** (`BindRitual`, vtable 0x65DE90 ctor, text "Enter the incantation." and
"Incorrect Incantation"). The ritual needs: a pentagram object (type 0xB60 with the flag 0x4000, up to 5 of them are
collected), reagent items on it (the eight reagents map to bits 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80; see the case table
at 0x65E1A3), and a **scroll** (item class 0x1592) of the spell to learn. The scroll gives the spell number
(`FUN_005F0220`); `FUN_005F0C20` gives its circle; `FUN_004F4240` counts how many of eight global flags (2, 0x31, 0x75,
0x7B, 0xA4, 0xDE, 0xEC, 0xF9) are set: the number of circles "freed", presumably one per completed Virtue quest. The
player types the incantation (the spell's words). On success `FUN_004F3740(record, spell)` sets the bit, and the
ritual plays effect slots 2-5 of effect id 0x26. The text of misctext 160 says: "This ritual is used to bind a spell
into a mage's spellbook. A binding pentagram is needed, and the corresponding level of magic must be freed." **[M]**
(the circle check itself, the reagent sets required per circle and the incantation comparison were not traced to the
byte).

**Learning a ritual:** conversation scripts call `FUN_004F3740` directly: Altomar (0x26), Hawkwind (0x2C and 0x2D),
Malchir (0x2B), Mariah (0x27), Myrea (0x29), Nicodemus (0x28), Shamino's voice (0x2A) and the Shrine of Compassion
(0x25). **[H]** (the calls; the story conditions are in the conversation classes).

**Casting.** The Avatar controller class (`Class_00741CA8`, kind 0, 138 virtual slots) holds the selected spell at +0x240
and the target at +0x244. Its update (`vf150_00417150`) enters state 0xE (casting selection) and, according to the
entry's targeting field (+8): value 2 casts at once; otherwise it asks for a target and for point targets creates the
type-0x6F6 marker at the aim point before calling `FUN_005ED950` (trailing value 0x10, flag word 0 *(unsure)*).
If the manager returns null the state is reset. **[H]**

**Items.**
* *Scrolls* (types 0x14D2-0x14FB): cast the matching spell with the free flag and are deleted (step 7).
* *Wands* (types 1257-1261, "Wand of Crystal Barrier" 5613) and any item with the flag 0x200 and property 0x14: cast
  the spell in the property's high 16 bits (`Object_GetEnchantSpell_004C57A0`: from the object's own property, or from
  the type's property store for a template item) with the free flag. Uses are not counted in the code read *(unsure)*. **[M]**
* *Potions* (`DrinkPotionProcess`): five cast a spell as listed in section 4.
* The *Arms of the Magi* quarter the Avatar's mana costs (step 6).

**Reagents.** Reagents are **not consumed by ordinary casts**: the cast flow has no reagent check (steps 1-8). They are
used by the Bind ritual (bits in the ritual's state), and the spellbook's "Reagents" page describes their roles
(projectile focus, movement, heat, warding, noxious fungus, binding). Create Reagents (spell 5) creates them. **[H]**
for "no reagent check in the cast path".

---

## 8. NPCs and creatures that cast

* **Mages.** Kinds 39 (`Class_00748F08`) and 56 (`Class_00747E28`, derived) read the spell masks in
  `Class_00748F08::AI_Mage_InitSpells_004DD000`: property 0x72 gives bits 0-31 and 0x73 bits 32-63; for each set bit n
  (n from 1 to 46) the table entry n is added to one of six lists, **by the entry's category** (+0x04); the lists are at
  object offsets 0x23C + category x 12. **[H]**
* **Choosing a spell** (`Class_00748F08::vf1E0_004DD940`). Let D be the distance to the target, R the caster's
  **record +0x54** (u16, the "range" of the NPC, see `enemy-ai.md`), M the current mana (record +0x3A), HP and baseHP
  record +0x34 and +0x38. Four category weights are computed, with D clamped to 0..R: offensive (cat 0) `D / R`;
  control (cat 1) `1 - D / R`; movement (cat 3) `(8/(3R)) x -D + 1.33` clamped to 0..1; healing (cat 2) `1 - HP / baseHP`.
  Category 5 (utility) is never picked. Within a category only spells with cost <= M are candidates, and a score is
  computed: offensive and healing `(M/cost) x (avg(min,max)/cost)`, control `M/cost - avg(min,max) x (-0.5)`... (the exact
  expression for categories 1 and 3 differ: category 3 prefers the **lowest** cost). A candidate within 0.025-0.05 of
  the best wins a coin flip, and the **last cast spell is skipped 60 % of the time** when its category has more than one
  spell. The winner among the categories is the one with the highest weight whose list produced a candidate (offensive
  first). **[H]** for the structure, **[M]** for the scores *(the decompile's float arithmetic is awkward to read)*.
* **Casting** (`Class_00748F08::vf1D0_004DD490`): the caster's state becomes 0xB, a target marker (type 0x6F6) is made
  for ground targets (distance `random(15, 25)` in the offset for category 3), and `FUN_005ED950` is called with flag
  word 0 (so the **mana is paid**). After the cast (`vf0C8_004DD780`) a **cooldown of `random(2000, 4000)` ms** starts
  (+0x2B8); `vf1E8` ("can cast") needs: state not 0xB, a target in view, mana above 0, at least one affordable spell
  (`vf1EC`) and the cooldown at or below 0. **[H]**
* **The Slasher Demon** (kind 6, `Class_0074CC70`): when animation event 0x418 fires it **always casts spell 10, Lightning
  Bolt**, with flag word `0x0078FDA8 << 16 | 1` (free), and starts a cooldown `random(3000, 5000)` ms. It also runs the
  same category chooser and stores the result in +0x360, but the cast call uses the constant 10. **[H]** for the constant,
  **[M]** that the chooser result is unused. Its other attack is a melee animation *(not traced)*.
* **The Liche** (kind 46, records 300 and 305, `Class_00748A40`): on its cast animation event it refills its mana to
  the maximum (record +0x3C into +0x3A, so casts are free) and casts **Summon Undead (21) with parameter 0xCE
  (Zombie)** when its summon slot state allows, otherwise **Lightning Bolt (10)** with parameter `DAT_0078A528 << 16`; half
  the time it makes a target marker first. It also teleports around the target (`vf174_004D5280`: a new position at
  `random(50..75)` % of its range from the target, rotated by up to +-45 degrees). **[M]**
* **Zhah** (kind 11, record 217, `Class_00745F38`): on animation event 0x37B it **sets mana to maximum, then casts spell
  0x2F (47, the big stone)** with flags `local | 0x180004`, sets a 3000 ms field. Its other event 0x364 shakes the
  camera `random(15, 25)`. **[H]**
* **Gazers** (kind 12): `Class_00746650` casts a spell through `FUN_005ED950` in addition to its natural attack (the
  call is at 0x4A5B40 in `exe-research.md`); the spell number was not read *(unsure)*.
* **Other callers of the manager.** `Class_007445B0` (kind 5, demons), `Class_0074A458` (kind 27, hellhounds),
  `Class_00748A40`, `Class_0074A600` (kind 44, ice hounds), `Class_00751170` (kind 8, zombies), `Class_00745F38`
  (Zhah) and `ActivityBase` call the manager; the spell numbers for demons, hounds and zombies were not read. *(unsure)*
* **The dragons** (kind 7) and **winged gargoyles** (kind 10) do not use spells. They use effect objects (see
  `enemy-ai.md`).
* **Int bonus.** All NPC records have Int 0 at +0x30 in `NPC.FLX`, so the `1 << Int` bonus never applies to them.
  The 16-bit mana pool is the limit: the Avatar's 10 points in `NPC.FLX` are raised by game logic *(unsure where)*.

---

## 9. Data or code

| What | Where | Changeable without patching the exe |
|---|---|---|
| Spell number, mantra, category, targeting, damage type, min/max damage, mana cost, drain rate | immediate arguments in `Spell_BuildTable_005ECE90` (`.text`) | No (exe patch; the 47 calls are one block) |
| Display names, circle headings, ritual text | `static/misctext.flx` (records 100-179) | **Yes** |
| Mantras, "Spell Fizzle" etc. | literals in the exe | No |
| Circle of each spell | `FUN_005F0C20` (a switch) | No |
| Spell for each scroll type | `FUN_005F0220` (a switch) | No (but the scroll item types and names are in `TYPES.DAT` / `TYPENAME.FLX`) |
| Which spell an enchanted item casts | property 0x14 (high 16 bits) on the placed object or template | **Yes** (map data) |
| Which spells a creature knows | properties 0x72 and 0x73 on the placed NPC object; bit n = spell n | **Yes** (map data) |
| A caster's mana pool, Int | `runtime/NPC.FLX` +0x3A/+0x3C, +0x30 | **Yes** (new games only; saved games hold their own copy) |
| Avatar's known spells | record +0x118 (saved); set by the bind ritual and conversation scripts | via the script, not the exe |
| Element blocks (flags 0x16D-0x171) and freed circles (flags 2, 0x31, ...) | global trigger flags, set by scripts | **Yes** (trigger scripts) |
| Damage resistance per creature kind | multiplier table written by start-up code (`exe-research.md`) | via exe patch |
| Effect visuals | `FUN_005F5CB0`, `Effect_Factory_0060CBE0`, animation data | not examined |
| Durations (45 s, 30 s, 120 s ...), radii (70, 75, 128 ...) | immediates in each spell's constructor or update | No |
