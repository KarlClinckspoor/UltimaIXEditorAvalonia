# What u9.exe holds: research notes

Findings about the GOG `u9.exe` (4,593,152 bytes, SHA-256 `83915175…`) that the editor builds on. Each was found by
reading the disassembly and then checked by a second, independent pass before code used it; anything still unchecked
is marked **unaudited**. This file holds descriptions and addresses only, no decompiled code. The working notes,
scripts and disassembly dumps stay out of the repository (`analysis/`, ignored).

Address conventions: virtual addresses (VA) as the game sees them. File offsets: `.text` = VA − 0x400C00, `.rdata` =
VA − 0x401000, `.data` = VA − 0x401A00 (VAs from 0x837C00 up are uninitialised data, not in the file).

Confidence: **high** = read in code or data at the addresses given; **medium** = strong inference; **low** = guess.

## 1. Combat

Used by: `src/U9.Formats/Combat` (tables), `src/U9Editor.Core/CombatMath.cs` (rules), the Combat balance window.

### Where the numbers are

`TYPES.DAT` and the trigger scripts hold no combat stats. Weapon, shield and armour stats are fixed tables in
`u9.exe`; creature damage multipliers are written into memory by start-up code; creature HP and defence are in
`runtime/NPC.FLX`.

| Table | VA | File offset | Layout |
|---|---|---|---|
| Weapons T1 | 0x79F268 | 0x39D868 | 52 × 28 bytes: u32 type (low 16 bits compared), u32 class (0 fist, 1 one-handed, 2 two-handed, 3 staff/pole, 4 bow, 5 shield, 6 light), u32 min, u32 max (shield: block roll), f32 reach (bows: range), f32 hit-sphere radius, u32 magic-item id (not read by damage code) |
| Weapons T2 | 0x79F818 | 0x39DE18 | 13 more records, searched after T1; not exact twins (Broadsword 3780 has radius 10 against T1's 20; T2[11] is a suit of armour) |
| T1↔T2 pairs | 0x79F988 | 0x39DF88 | 13 u16 pairs |
| Weapon classes | 0x79F1F8 | 0x39D7F8 | 7 × 16 bytes; field 2 the equipment slot |
| Attack modes | 0x79FA00 | 0x39E000 | 53 × 20 bytes (count u32 at VA 0x79FE24): key (type, or class when < 16), mode 0-3, blockable, damage flags, on-hit special. Looked up by type first, then by class |
| Avatar armour | 0x780C70 | 0x37F270 | 72 × 16 bytes = 6 slots × 12 levels: u32 type, u32 slot (0 helmet, 1 gauntlets, 2 leggings, 3 arms, 4 chest, 5 boots), u32 level, u8 min, u8 max |
| Natural attacks | 0x785738 | 0x383D38 | 28 × 24 bytes: u32 min, u32 max, f32 reach, f32 radius, u32 damage flags, u32 special; bone names at VA 0x7859D8 |
| Multipliers | 0x84E598 (memory) | built by code at VA 0x44A040–0x44B503 (file 0x49440) | 64 kinds × 11 f32, one per damage bit |
| Unreported table | 0x79FE28 | 0x39E428 | 65 × 64 bytes keyed by type, read by FUN_005B61E0 (purpose unknown) |

Damage bits (attack-mode and natural-attack flags, and the multiplier columns): 0x1 slash, 0x2 blunt, 0x4 thrust,
0x8 arrow, 0x40 fire, 0x80 cold, 0x100 magic/mana, 0x400 lightning; 0x10, 0x20 and 0x200 unknown.

**The multiplier code** is a run of `mov dword [abs], imm32` instructions and of eleven pushes followed by a call
to the helper at VA 0x449EB0 (which stores its eleven arguments, the last pushed first, at `ecx`). Thirty-five values
are pushed as a one-byte `push 0` and cannot change in place. The editor reads the table by walking this code
(checked against an emulation of it), changes the `imm32` constants in place, and, when a one-byte push must change,
rewrites the function (5,316 bytes) as `push esi; push edi; mov esi, table; mov edi, 0x84E598; mov ecx, 704; cld;
rep movsd; pop edi; pop esi; ret` with the 704 floats right after it. Emulating the rewritten function gives exactly
the edited values, with the stack and registers kept. Nothing else jumps into the function (its only reference is
the thunk at VA 0x449E80).

**Combining multipliers** (FUN_00449F10): for each set bit, sum the multipliers and multiply the non-zero ones. A sum
of 0 or less gives 0 (immune); no flags give 0. A creature's row is copied at spawn (FUN_00449FD0) by its kind,
NPC.FLX record +0x44. Kinds range −1..62; the 58 records of kind −1 read before the table (and, per the attack
research below, never attack). A second factor by class and mode (this+0x17C) is always 1.0.

**`runtime/NPC.FLX`**: record 0 is 352 records × 316 bytes (room for 512 at run time). A creature's type number is its
record number; templates have 0x800 in +0x48 and spawned copies use 352-511. Fields: +0x04 name, +0x26 u8 default
defence, +0x28/+0x2C/+0x30 strength, dexterity, intelligence, +0x34/+0x36/+0x38 u16 HP, maximum, base maximum,
+0x3A/+0x3C/+0x3E mana, +0x44 i32 kind, +0x48 flags (bit 0 invulnerable, 0x20 immune to environment damage, 0x800
template), +0xC4 flags (bit 2 invulnerable, 0x200 undead/demon, 0x400 outgoing damage × 1.1, which no record has).
**Saved games carry all 512 NPC records and the TYPES table, so NPC.FLX changes reach new games only.**

### The damage rules

Random numbers: random(min, max) is `rand() × (max − min + 1) / 32767 + min` (FUN_005E88A0): uniform over min..max
(max + 1 one time in 32,768).

**Melee** (FUN_004F79D0):
1. No damage in no-combat mode when the defender is the Avatar.
2. Base: the attacker's property 0x76 (Min/Max Damage: low 16 bits min, high max) when it has one, else random(weapon
   min, max). The property replaces the weapon.
3. × 1.0 (class/mode matrix) × the defender's multiplier for the attack's flags, truncated.
4. Avatar attacker: + this+0x24C (5 with the Gauntlets of Fury) + 0/2/4/8 for strength 0-3.
5. × 1.1 when the attacker has +0xC4 bit 0x400.
6. − defence: the Avatar's armour (each worn piece's random(min, max), FUN_00413CA0) plus random(shield min, max) ×
   0.1; anyone else: property 0x77 (Min/Max Defense) when present, else random(0, record +0x26). No floor.
7. A block cancels the hit; the invulnerable take nothing; special 13 adds 10 against +0xC4 bit 0x200.
8. HP falls by the damage when it is above 0.

**Bows** (FUN_004F86B0): the same, with mode 0, no strength bonus, no × 1.1; specials 4 and 14 only. **Natural
attacks** (FUN_004F82F0): property 0x76, else the row; rows 0-2 (punch, kick, headbutt) are worked out from the
attacker's strength (+0x28): 2·Str+2, 3·(Str+1), 4·Str+4 (their min and max are unused). **Environment damage**
(FUN_004F89E0): multipliers and armour only with flag bit 4; 0x77 ignored.

On-hit specials: 4 poison (adds random(0, damage) over 8 s, 0x4F9850), 12 fire burst, 13 +10 against undead and
demons, 14 damage to mana instead, 15 20% charm, 16 lightning visual, 17 20% freeze, 18 Glass Sword shatters into type
0x12B8. Blocking uses the class row only; the per-weapon "blockable" field and specials 5-8 and 11 are not read by
damage code.

**Properties**: 0x10 is a breakable item's HP (else TYPES.DAT +0x0D; 0xFF indestructible) and a hazard's damage; on
NPCs (18 placements on map 9) its effect is unknown. 0x14 holds a spell number in its high 16 bits. Unique NPCs read
0x76/0x77 from a global store ([[0x91F0E0]+0xD0]).

### Who attacks with what

Research of 2026-11-25, audited the same day (every claim confirmed); the Combat balance's defaults follow it.

- The kind picks an AI class (FUN_004F5DC0, jump table 0x4F6AD4); each class calls FUN_004FA110 with its natural-attack
  row. Kind −1 creates no AI object.
- Humanoid classes share the weapon dispatcher FUN_004BAD10: at start each class takes the first item of its map
  contents whose weapon class matches exactly (FUN_004B7F70), else creates a fixed default type (FUN_004F3B20):
  kind 36 Scimitar 4789; 22 Buckler 753 + Broadsword 1042; 42, 57, 61 Kite Shield 754 + Longsword 5388; 62 Shield
  2661 + Broadsword 1042; 15 Battleaxe 2640; 41 Spiked club 4940; 9 Gargoyle Staff 5389; 40 Staff 3692; 38 Novice bow
  4836. Kinds 0, 14, 34, 39, 49, 56 and 63 fight unarmed (rows 0-2); mages (39, 56) also cast the spells in their
  properties 0x72/0x73 (bit masks).
- Natural rows by kind: 1 → 26, 2 → 27, 3 and 25 → 6, 4 → 10 and 11 (two hits), 5 → 20, 8/47/48 → 7, 12 → 15, 13 → 17
  (mana only), 14 → 21, 17 → 19, 19 → 14, 20 → 5, 23 and 24 → 12, 52 → 24, 27 → 22, 44 → 23, 28-31, 45, 51 → 4,
  32 → 9, 55 → 16. No attack: −1, 16, 18, 21, 33, 35, 37, 50, 58-60.
- Kinds 6, 7, 10, 11 and 46 (Slasher Demon, dragons, winged gargoyles, Zhah, Liche) attack with spells or effect
  objects whose damage is not traced. Spell damage is a table built by FUN_005ECE90 through FUN_005ECC60 (47 spells,
  min/max bytes at +0x10/+0x11; e.g. Fireball 30-35, Lightning Bolt 22-25, Death 100). A caster adds 1 << Int when
  its record's +0x30 is 1-3 (0x5F0BC0); every NPC caster has 0 there, so only the Avatar gets the bonus.
- Unarmed strength: +0x28 read as a dword, so strength 3 gives 8/12/16. Three unarmed NPCs (Lydia 150, Batista 290,
  Creeper 197) hold byte-filled values there, which make some blows enormous (an instant kill) and others negative
  (medium; the calculator treats such strength as 0).
- Details checked by the audit: the Gazer hits with row 15 on animation 0x3F2 or 0x3F3 (0x4A6160); the Phase Spider's
  constructor writes row 25 (0x506614) but its first update replaces it with 12 (0x4A8842); kind 34's
  initialisation never calls its arm method, so peasants fight unarmed; kind 43 shares kind 33's class. No attack
  path leads from kinds −1, 16, 18, 21, 33, 35, 37, 50 or 58-60 (direct calls traced six levels; virtual calls not).
  No NPC.FLX record has the × 1.1 flag (tested at 0x4F8455 and 0x4F7C70).

## 2. Dialogue

Research of 2026-11-25, audited the same day (confirmed, with the corrections folded in below). Used by: `src/U9.Formats/Text`, the Dialogue window.

### Files

- **`static/text.flx`**: 7,656 records, each a NUL-terminated UTF-16LE line (the game measures it with `wcslen`, so
  the NUL must stay). Records ending "… .cpp : BEGIN FILE" mark where each conversation's source file began (266).
- **`static/text.dat`**: a hash table from a line's key to its text.flx index. `u32` bucket count (7,649), then per
  bucket a `u32` count and that many `{u32 bucket, u32 CRC, u32 index}`. Loaded by FUN_00545580 (FUN_004AF030);
  looked up by FUN_004AF230: bucket = (64·h + byte) mod 7,649 over the key, check = CRC-32/BZIP2 (polynomial
  0x04C11DB7, MSB first, preset 0xFFFFFFFF, inverted) over the key and its NUL. The key is `"<NPC record name> : "`
  or `"UI : "` followed by the 8-bit English literal compiled into the exe (FUN_00615FC0; prefixes from FUN_0061DE40,
  8,621 call sites, and FUN_0061DDE0, 340). Markers are keyed `"C:" + text` and never looked up. Rebuilding the
  file from these rules gives it back byte for byte; 28 records have keys whose literal is no longer in the exe.
  Matching TITAN's description (`titan/u9/text_keys.py`).
- **`sound/Speech.flx`**: record N voices line N (all 7,010). Header 0x3C bytes: u32 line, char[36] clip name
  (`Exferlem_07143.umt`), u32 data length, u32 rate, u32 bits, u32 channels, u32 codec. The codec factory
  FUN_004394A0 takes 0 raw PCM (no prefix: exactly the data length after the header), 1 EA-XA ADPCM, 2 EA MicroTalk
  (the game's own decoder at 0x439B20-0x43AF50). Any other value makes it return NULL, which FUN_00532970 then calls
  through without a check (0x5329C5): **probably a crash, never write one**. Codec-2 data starts with a u32 sample
  count. The DirectSound buffer takes rate, bits and channels from the header (0x5329E1); NPC voices play as 3D
  sound, so clips must be mono. Every shipped clip is MicroTalk, 22,050 Hz, 16-bit mono: codec 0 is untried in
  the game.
- **`static/misctext.flx`**: interface strings, read by index (FUN_00616120; 155 call sites with constants).
- Languages: `[Game] Language=` picks `text-fr.flx`, `speech-fr.flx` … for fr and de, and the joke translations
  `Tbrk/Mbrk/Tnbrk.<code>` in `static/` for br, vl, ns, fn, bf and jv (same indices).

### How a line reaches the screen

- The conversation logic is compiled C++: about 280 `…ConversationProcess` classes, each a state machine that queues
  lines (FUN_0061DE40) and replies with their next states. Which replies exist and where they lead is code.
- A line object (FUN_00615A10) holds the literal, the text.flx string, its index, the speaker and a duration of 10.
  **What is shown is the text.flx string**, converted with `WideCharToMultiByte(CP_ACP)`: subtitles (SayProcess
  FUN_006175C0), reply choices (TopicsProcessor FUN_00619520) and journal lines. A key missing from text.dat shows
  the exe's English literal for 10 s with no voice.
- The chosen reply is matched back by comparing its displayed text with the queued replies; the first match wins,
  so replies in one menu must differ after conversion to the code page, not only as literals.
- **Replies: at most 32 per menu, unchecked.** The copy loop (0x619A6E-0x619AB2) writes every queued reply into
  conv+0x10C / +0x18C; a 33rd overwrites the line objects and later ones the conversation state (conv+0x218).
- The speaker's name (NPC.FLX record name) is part of the key but is not drawn. It is formatted into a 36-byte
  buffer, so names must stay at 31 characters or fewer.
- A key whose literal differs from the text.flx wording still uses the literal (index 182: "Gargoyles proudest" in
  the exe, "Gargoyles' proudest" in text.flx). An existing key with an empty record behaves like a missing key.

### Text, fonts and limits

- Drawing is single-byte: each byte of the converted string is glyph `byte − 0x20` (0x5C79A3) of a 224-glyph bitmap
  font laid out as Windows-1252. CJK cannot be shown without changing the code; Latin-1 and the cp1252 quotes and
  dashes can (font 0x263 lacks only 7F, 80 €, 81, 8D, 8E Ž, 8F, 90, 9D, 9E ž, A0, A9 ©, AE ®, B5 µ, B6 ¶). What
  reaches the font depends on the player's ANSI code page; U+0092 (in lines 4257 and 5745) becomes "?".
- Control bytes in the layout (FUN_005C7870): 00 and 0A end a line; 0B and 0D end it and start a new page; 05 ends
  the scanned line but is not consumed by the layout loop (effect untraced: avoid it); 07 nn colour; 0C nn font
  slot (out of range: slot 0 when measuring); 08 a break point that makes room for "-" (a soft hyphen); 09 tab
  (4 spaces); other bytes below 0x20 abort the line. No shipped line contains any of them.
- On a double-byte Windows code page the conversion buffer is only wcslen + 1 bytes (0x615B58): the conversion
  fails part way (tested with cp932), so a CJK patch would need a larger buffer as well as new fonts.
  What CJK support would take, with the prior work on it: [cjk-u9gl.md](cjk-u9gl.md).
- Fonts: 19 slots (table 0x7A1698, loaded by FUN_005C5940) from records of `bitmapsh.flx`, `bitmap16.flx` or
  `bitmapC.flx`, whichever texture mode is active (record 0x263 has the same metrics in all three). Line spacing
  adds −6 for slots 0-7 and −1 for 10-12 and 15-18. Subtitles and replies use slot 0, record 0x263 (cells up to
  25 × 21, lines 15 px apart; a glyph fits while x + advance < 600); the options screen probably slot 12, record 0x25E. Record layout: u32 max width, u32
  height, u32 count (224), u32 flags, 224 × {u32 offset, u32 size}, glyphs as bitmap frames. No size setting; drawn
  at native pixels, so smaller at higher resolutions. GDI draws only a debug window and a "Waiting for…" dialog.
- The subtitle box is 600 × 200 px (FUN_0041BBB0): lines wrap at 600 px, a page holds 13 lines, a click or Enter
  turns the page. The longest shipped line takes 8 lines. Up to 32 replies.

### Timing and sound

- A line without voice (or with voices off) stays 10 s **per page**, a constant (0x615A86; the timer is re-armed at
  each page turn, FUN_0041BD50), or until a click or Enter.
- A voiced line stays until its sound stops (FUN_0041C2E0 polls the DirectSound buffer), even if later pages were
  never shown: a voiced line longer than 13 lines loses its tail.
- Voices play at the speaking NPC (3D), the Avatar's in 2D, at the SpeechVolume setting. There is no lip-sync data:
  the mouth just moves while the clip plays. `[Conversation] EnableAudio / EnableText / EnableConvoReplies` choose
  text, voice or both, and whether the Avatar speaks his replies.

## 3. Fonts

Read from the code (2026-11-25) and from the font records themselves.

- **Slots.** FUN_005C5940 loads 19 font slots, in order, from the table at VA 0x7A1698, out of whichever of
  `bitmap16.flx`, `bitmapsh.flx` or `bitmapC.flx` the texture mode uses (same metrics in all three):

  | Slot | Record | Cell | What uses it |
  |---|---|---|---|
  | 0-3, 5-9 | 0x263 | 25 × 21 | subtitles and reply menus (slot 0) |
  | 4 | 0x220 | 25 × 21 | same shapes as 0x263 |
  | 10, 11 | 0x25C, 0x25D | 22 × 18 | reagent descriptions in the spellbook (name, body) and the credits |
  | 12 | 0x25E | 21 × 15 | credits headings; probably the options screen labels (0x428970) |
  | 13, 14 | 0x202, 0x203 | 14 × 14, 18 × 17 | not found (see below) |
  | 15 | 0x261 | 18 × 17 | the Britannian rune alphabet (letters only) |
  | 16 | 0x262 | 19 × 19 | the Gargish script, moon phases, ankh, pentagram |
  | 17, 18 | 0x25F, 0x260 | 40 × 40 | illuminated drop capitals A-M and N-Z, drawn in the journal |

- **How a slot is chosen.** A text widget keeps its slot at +0x6C (constructor argument); text can switch slot
  inline with byte 0x0C followed by the slot number. The exe's own strings use 0x0C 0x0A / 0x0B (reagent
  descriptions at VA ~0x7847A4, the credits at ~0x781870), 0x0C 0x0C (credits headings) and 0x0C 0x11 / 0x0C 0x12
  followed by `%c` (journal drop capitals, ~0x7846B4 and ~0x784B98). The books contain no inline switch, so their
  font is fixed in code.
- **Slots 13 and 14** (fonts 0x202 and 0x203) are selected by no inline switch, and no constant 13 or 14 reaches a
  text-widget constructor within three calls. Widgets are mostly created through virtual calls, so this does not
  prove them unused; logging the slot at layout time in the game would settle it.
- **Glyph coverage** (bytes 0x20-0xFF as Windows-1252; a glyph that draws nothing is a 1 × 1 transparent frame):
  - 0x263 and 0x220: everything but 7F, 80 €, 81, 8D, 8E Ž, 8F, 90, 9D, 9E ž, A0, A9 ©, AE ®, B5 µ, B6 ¶. Complete
    for Portuguese, Spanish, French, German and Italian letters.
  - 0x25C / 0x25D and 0x25E: all letters; some punctuation and symbols missing (ª, º, – — •, superscripts).
  - 0x202: no à, ò, ù, ú, Ñ, Ø, Ù, Þ, and almost nothing above `z`; 0x203: letters complete, punctuation above `z`
    mostly missing.
  - 0x261, 0x262, 0x25F, 0x260: special alphabets, letters only.

## 4. Spells: how they are defined (unaudited)

First reading of the decompile, 2026-11-25; the mechanics documents under `docs/mechanics/` go further once
checked.

- **The table.** Spell_BuildTable (VA 0x5ECE90, 1,881 bytes) calls Spell_Define (0x5ECC60) 47 times, every argument
  a constant pushed in the code: spell number, category (0 attack, 2 heal, 5 utility; 1 and 3 also occur),
  targeting flags, damage-type bits, minimum and maximum damage (bytes at +0x10/+0x11), a float (a duration or rate:
  Time Stop 20, Invisibility 1, Light 1/60), what appears to be the mana cost (Light 5, Fireball 15, Death 35), an
  8-bit mask that appears to be the reagents (Create Reagents 0xFF), a flag set for timed spells, the spell words and
  the name. Examples: Lightning Bolt lightning + slash 22-25; Fireball fire 30-35; Death 100; Acid Rain 10-85.
- **Encoding.** Most arguments are one-byte pushes (`6A xx`): a value can change in place within −128..127. Values
  above 127 (most reagent masks, pointers) are four-byte pushes. Changing a one-byte value beyond that range needs
  the same treatment as the damage multipliers: rewrite the builder as a copy from a table (the function is long
  enough for a short copy routine plus 47 records of about 36 bytes).
- **Behaviour.** Each spell is a class derived from `Spell` (vtable 0x754800, 25 virtual methods): 38 classes such
  as `SpellFireball`, `SpellTimeStop`, `SpellSummonDeamon`, named from the strings their constructors pass to
  Process_SetName (0x5DDC40). What a spell does lives in its class's methods, so new behaviour means new code.
- **Who casts.** An NPC's known spells are the bit masks in its properties 0x72 / 0x73 (bit k = spell k; data on
  the placed object). A caster adds 1 << Int to the damage when its record's +0x30 is 1-3; every NPC caster has 0
  there. Which spell an NPC chooses, and when, is AI code.

## 5. The executable as a whole: what patching can and cannot do

- **No relocations.** The image always loads at 0x400000 and its relocation table is stripped: every call, jump and
  data reference is a fixed address. Code can never be moved or have bytes inserted; it can only be overwritten in
  place or detoured.
- **Sections.** `.text` (VA 0x401000, 3.4 MB), `.rdata` (0x740000), `.data` (0x77F000; 12 MB in memory, 0.75 MB in
  the file), `_ADATA` (0x1336000), `.rsrc` (0x1337000). SizeOfImage 0xF38000. The 0x400-byte headers have 376 free
  bytes after the section table: room for 9 more section entries. The PE checksum is not checked by Windows for an
  exe.
- **Free space.** Almost none inside the code: one run of 352 filler bytes in `.text`. New code needs a new section
  at the end of the file (or a DLL).
- **Imports.** dshow.dll, DDRAW.dll, DSOUND.dll, WINMM.dll, KERNEL32.dll (127 functions, including LoadLibraryA and
  GetProcAddress), USER32, GDI32, ADVAPI32, Quartz, comdlg32. Entry point RVA 0x314920. The GOG install already
  puts its own `ddraw.dll` and `dshow.dll` in the game folder, and U9GL's is `dsound.dll`: the common
  "replace a system DLL" loading trick is taken three times over.

How these facts shape mods: [modding.md](modding.md).

## 6. Watching and driving the running game

`tools/u9mon.py` hooks these addresses with Frida (it changes no files). Everything below was checked on the
running game on 2026-11-25, on the test arena (map 91) and on Britannia, unless marked otherwise.

### The frame and the main loop

- The main loop pumps window messages (0x5D6F70) and then calls **Game_FrameUpdate 0x49F610** once a frame, as
  `__thiscall(world = [0x850228], 0)`. Code run after it returns sits between two frames, on the game's own thread.
  World_ChangeMap and Trigger_RunScript both ran cleanly from there.
- `[0x850228]` is the world. `world+0xCC` is the current map's manager, and `manager+0x20` is its map number.
- `[0x850220]` is 1 while the game is in play. The interpreter does nothing unless it is 1.

### Map objects in memory

- A loaded map object **is its map-file record**: +4, +6, +8 x, y, z in its chunk, +0x0A type, +0x14 flags, and
  +0x1A link. The engine's link getter (0x4BEE30) returns +0x1A unless flag bit 0 is set. Records sit in 4 KB pages
  laid out like the file's pages. The page header (address & ~0xFFF) holds the chunk origin at +0x0C (x) and +0x10 (y),
  and the position getter (0x4BE730) adds it.
- So a placed object can be found in memory by scanning for its x, y, z and type (8 bytes from +4) plus its link at
  +0x1A. The monitor's `find` and `run` commands do this. Lord British's teleporter (type 2121, link 666) gave exactly
  one match.
- Objects that need it get a handle in a table at `[0x8515D0]`: entries of 12 bytes (+0 free-list link, +4 map slot,
  where −1 means free, +8 object pointer), with the capacity in `[0x8515CC]` (0x800 at first). AI and NPC code refers
  to objects by handle. The handle is kept in the high bits of object +0x14 (`>> 0x15`).

### Trigger scripts

- **Trigger_RunScript 0x54F850** is `__thiscall(state = [0xAE7C6C], object, which, label)`. `which` 0 or 1 runs the
  object's property **0x3E** (OO Trigger) slot A or B, and 2 or 3 runs its property **0x3B** (SF Trigger) slot A or B.
  `label` non-zero starts at the op 0x16 whose B matches it. It returns at once unless `[0x850220]` is 1 and state
  +0x38 is 0. During a run, state +8 holds the object and `[state]` the script's ops.
- **The loop head is 0x54F9C3.** There `esi` points at the current 6-byte op (u16 op, A, B), `ebx` is the state, and
  `ebp` holds the command byte. The next instruction clears Trigger_Flag (0xAE7C68). So the flag read at a loop head
  is what the previous op left.
- **Trigger_SendToObject 0x5510A0** is `__thiscall(state, command, op, object)`, called once for each matching object.
- **Command 3E.** Script 1814, the one op `3E` with B = 6 on the Lord British teleporter, called 0x5B7220 with ecx = 6
  (B) and got 1 back. What 0x5B7220 changes is still open.
- **Forcing commands (session 3, 2026-11-25, not yet audited).** One short Britannia script for each unknown command
  was run with the monitor's `run`. Afterwards the monitor compared the 16-bit words of every record the script
  touched, the Avatar's NPC record and the globals table. It compared them at the end of the run and again 2 and
  6 seconds later. The commands named that way:
  - 2A fires a missile (`FireMissleProcess`);
  - 30 on an NPC starts a conversation;
  - 49 and 59 cast spells;
  - 50 deals damage, and its table at 0x7931A8 holds damage kinds, not sounds;
  - 52 sets mana;
  - 60 lets the Avatar breathe under water;
  - 2E's `AbsItemMover` moves objects;
  - 22 turns them.

  The details are in `trigger-scripts.md`, marked †. 09, 0A, 0F, 20, 2B, 33, 34, 3C, 55, 5C, 5D and 63 changed
  none of the watched bytes.
- **Watched by a person (session 4).** One command per game, with a sound on the PC at each run, and a person
  describing what they saw. These settled more commands:
  - 0F spawns particle effects (effect 2 at the largest size: bubbles, more with each run);
  - 2B stops an object's processes (an archery target stopped mid-slide);
  - 09 and 0A conceal and reveal dormant objects (flags 0x40100), with fades;
  - 20 plays the object's sound.

  The decompile index names the helpers of 09 and 0A `Trigger_Move` (0x54F350) and `Trigger_Teleport` (0x54EC00),
  but they conceal and reveal objects.
- **What the comparisons also showed:**
  - The Avatar's NPC record holds his position as u32s at +0x5C, +0x60 and +0x64 (object units), and his air at
    +0xB4. The air falls under water from 100 and is refilled while command 60's switch is on.
  - +0x3C is max mana. The word at +0x3E rose with it when command 52 raised the maximum.
  - Bit 0x1000 of +0x4A is set during a conversation.
  - A map object's record +0x10/+0x12 is its rotation: command 22 kept changing it on a windvane.
  - +0x1C (u32) received a handle when command 2E started a mover on the object, and went back to 0 shortly after
    command 10.
- Many scripts run without the Avatar touching anything. Near Lord British's castle, script 3197 (a type 1094 trigger)
  ran every frame, called from 0x654010's class: `cmd 29` to a type 2913 object, then op 0x17. Eight other type
  1094 triggers ran on arrival.

### Travel

- **Map_GetManager 0x4DF250** (`__fastcall(map)`) returns a map's manager and loads the map if needed. The terrain
  height function at 0x4E02A0 (`__thiscall(manager, tileX, tileY)`, tile = object units / 128) gives the ground.
- **World_ChangeMap 0x49ED20** is `__thiscall(world, manager, destObject, relative, location, rotation, sameMap)`.
  `location` is three floats in object units, or null to use `destObject`'s position. `rotation` is a quaternion
  (w, x, y, z), or null to take `destObject`'s. MapChanger::vf004 (0x54A590), the process behind script command 1F,
  calls it and then SaveLoad_Enable (0x522150) on `[0xAD126C]` and clears `[0x131F668]+0x50`.
- The same sequence called between frames moved the Avatar from map 91 to Britannia (64516, 67498, 2599). The game
  kept running.

### Combat facts seen live

- **Npc_SubtractHP 0x4F1CC0** is a clamped u16 subtract on any record field. The damage paths use it on +0x34 (HP),
  and the mana code (0x5F0ED0) on +0x3A (mana).
- **SpellManager_Cast 0x5ED950** takes the spell number as its 2nd argument: the Liche cast 10, the Hellhound 15.
- **Hellhound** (Class_0074A458::vf004 0x4AFE40): it bites when the distance (vf084, world units minus both radii)
  is under 15. Otherwise it refills its mana to the maximum and casts Fireball (15) for 15 mana, and sets a 4000 ms
  timer. In the arena it cast every 4 to 40 s and hit about one cast in five to four in five, depending on the range.
- **0x4F97B0** runs every frame while a creature fights. It is not a one-off "start fighting" event.
- The friend test showed that creatures with different link numbers see each other as ENEMY in mode 1 (a team
  from the link).

## 7. How NPCs find their way (read in the decompile, 2026-11-25, not yet audited)

There is no navigation mesh and no precomputed grid for moving about. Movement has two layers: a road network for
long trips, and a search made on the spot for each move.

**Local moves: `NewPathfinder` (0x501D50, a process of 0x1C0 bytes).**
- **Who makes one:** the AI's movement calls (`AI_Base::vf0D0`, `vf0D4`, `vf064`) and the highway mover (below).
- **The grid:** each request builds a small grid of its own (0x503F70), laid along the line from the creature to its
  goal:
  - its cells are the creature's radius × √2 across, at most 64 units;
  - it runs at least 10 cells along the line and at most 15 across it;
  - each cell takes 0x28 bytes: grid x and y, flags, cost, f and h, the cell's position, the ground height found
    there, and a link in the open list.
- **The search:** A* (0x5044F0).
  - Each step costs 1. The heuristic is the Manhattan distance in cells to the goal cell. The open list is kept sorted
    by f = g + h. The four neighbours are tried, and a cell's direction bits (0x2, 0x4, 0x8, 0x10) record where it
    came from, so the path can be traced back (0x504390, 0x504840).
  - The search is spread over frames: each call expands a given number of cells.
  - It finishes when it reaches the cell flagged as the goal (0x20). The best cell so far (the smallest h) is kept,
    in case the goal can't be reached.
- **What makes a cell walkable:** the creature's own physics body (+0x50) is moved to the cell's centre, the collision
  code is asked for the ground there (0x565920, 0x56EAB0), and the body is put back.
  - The cell is walkable when the height change from the cell before is under a step height (+0x40), or under a
    larger one (+0x48) where the slope is gentle (under +0x4C), and the slope is no steeper than +0x44. These limits
    come from the creature at construction: a value from its vf0B8 times its +0x1F8, and its +0x200; an angle of 5°
    (0.0873) is also stored.
  - A creature with flag 0x80000 (at +0x4C) treats every cell as walkable, at height 32767. Probably flyers.
  - So the level's geometry, terrain and placed models alike, is what blocks a path.
- The pathfinder's state, grid included, goes into savegames.
- When start and goal are in the same cell, the grid is dropped and the creature walks straight.

**Long trips: the highway network (`static/highway.dat`, loaded at 0x4B5730; `HighwayManager`).**
- **The file** (checked over the whole shipped file):
  - u32 node count (815) and u32 highway count (149);
  - the nodes, 12 bytes each: u32 id, u32 x, u32 y, in object units;
  - u32 size and the highways (2,612 bytes, used up exactly). Each highway is u16 from-node, to-node, node count,
    cost and 0, then the node ids in order. The first id is always the from-node and the last the to-node, and every
    id is one of the nodes.
  - The cost is the path length in object units divided by 16 (for example, 53508 → 53501: 8 nodes, 1,318 units,
    cost 82).
  - 311 of the 815 nodes lie on a highway.
- **The nodes are placed markers.** Node ids are the links of class 1134 markers, in 50000–64117. Node 53501's
  (42449, 32328) is exactly the position of Britannia's marker with link 53501, (331.63, 252.56) points × 128.
- **Who uses it:** NPC schedules (`NpcSchedule_Replace` 0x404B50, `NpcSchedule_Push` 0x404C40, and the activity
  code at 0x4036A0…0x4043D0) move NPCs between places along highways.
  - "HighwayManager : Out of moving npc slots!" means it moves a fixed number of NPCs at a time.
  - "Npc trying to move on unknown highway (%d to %d)!" means a schedule names a node pair with no highway.
- **Each leg** (0x4B4BA0) runs a pathfinder: `NewPathfinder` normally. `BeeLinePathFind` (0x4B4000), a straight line,
  is used only when `[0xAE7C9C]` is 1. Nothing in the code writes that, so it's a leftover developer switch.

**Scripted paths and patrols.**
- **Script command 0x39** (in `Trigger_SendToObject`) starts a `PathEggFollower` (0x54B8B0). It moves an object from
  marker to marker: the nearest class 1134 marker with the current link, then link + 1, and so on (0x54BA10).
  Markers with consecutive links make the path.
- **Patrols** (`ActivityPatrol::vf054`, 0x407AA0) walk between markers too. On arriving at one, they run its
  SF Trigger A script (seen live).
- In both, the walking between markers is again the pathfinder's job.
