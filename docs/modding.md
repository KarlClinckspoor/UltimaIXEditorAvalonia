# Modding Ultima IX: what can change, and how

Much of Ultima IX is hard-coded in `u9.exe`. This page sorts what a mod can change through data, what needs the
executable, and how executable changes can grow beyond swapping a few bytes. The facts behind it (addresses, layout)
are in [exe-research.md](exe-research.md); the mod package and manager in [mod-format.md](mod-format.md).

## What data can change (no executable work)

| Area | Files | Editor |
|---|---|---|
| Maps, objects, their properties and links | `static/fixed.N`, `runtime/nonfixed.N` | the editor |
| Terrain | `static/terrain.N` | the editor |
| Trigger scripts | `static/TRIGGERS.FLX` | the script editor (trigger language) |
| Item types (weight, hit points, model…) | `static/TYPES.DAT` | Selected object panel |
| Characters and creatures (HP, defence, kind) | `runtime/NPC.FLX` | Combat balance window |
| Which spells an NPC casts | properties 0x72 / 0x73 on the placed NPC | Properties panel |
| Dialogue text, books, item names, interface text | `static/text.flx`, `BOOKS-*.FLX`, `TYPENAME.FLX`, `misctext.flx` | Dialogue window, books on the Contents tab |
| Voices | `sound/Speech.flx` | Dialogue window (WAV import) |
| Models, textures, fonts | `static/sappear.flx`, `bitmap*.flx` | glTF round trip for models |

Saved games keep their own copies of nonfixed objects, NPC records and the TYPES table, so changes there reach new
games only.

## What is code

Conversation flow (which lines exist and where replies lead), creature AI per kind, what each spell does (one
C++ class per spell), the weapon, armour and natural-attack tables, damage multipliers, the spell table, font
choices, subtitle timing, and most quest logic.

## Changing the executable

`u9.exe` itself is never changed: U9GL recognises the game by its hash. Executable changes go into a copy,
`u9-mod.exe`, which the mod manager writes and starts.

The exe has no relocation table: it always loads at 0x400000 and every call, jump and data reference is a fixed
address. **Nothing can be moved or inserted**: one byte inserted in the middle would shift everything after it and
break thousands of references. Every technique below leaves existing code where it is.

1. **Change a value in place.** A constant in an instruction or a table entry is overwritten with a value of the
   same size. This is what the Combat balance window does for weapons, armour and natural attacks, and what mods
   carry today as executable patches (offset, old bytes, new bytes, note). Limits: the value must fit its
   encoding. A one-byte push (`6A xx`) holds −128..127, so a spell's damage written that way cannot become 200
   in place.
2. **Rewrite a function inside its own bytes.** When a function is only a long list of constants (the damage
   multipliers, the spell table), it can be replaced by a short copy loop plus a table of the values, in the same
   space. Every value then becomes a plain number of full size. The editor already does this for the damage
   multipliers, checked by emulating the rewritten code.
3. **Add a section and detour.** For new behaviour:
   - new code and data go into a **new section appended to the file** (the header has room for nine more section
     entries; the new section's address starts after 0xF38000). The file grows; no existing address moves;
   - the chosen spot in the old code gets a **5-byte jump or call** to the new code, which runs the instructions it
     replaced and jumps back. Only those 5 bytes of old code change;
   - the new code calls the game's functions and reads its globals at their fixed addresses, which never change.

   The game has almost no free space inside its code (one run of 352 filler bytes), so this is the only way to add
   more than a few instructions to the executable itself. It needs a 32-bit x86 compiler or assembler that builds
   for a fixed address, and an extension of the mod format: today's executable patches must keep the file the same
   length.
4. **Load a DLL.** New behaviour is written in C (or anything that builds a 32-bit Windows DLL), loaded into the
   game, and installs its detours at run time. The executable barely changes; see below.

Techniques 1 and 2 stack well between mods (the manager merges non-overlapping patches). Techniques 3 and 4 need
care when two mods detour the same function.

## Loading a mod DLL

The usual trick, a DLL named like a system DLL in the game folder, is taken: the GOG install already ships its own
`ddraw.dll` and `dshow.dll`, and U9GL's is `dsound.dll`. The cleanest way is to make `u9-mod.exe` load the DLL
itself. Two ways, both small:

- **A start-up stub.** A new section holds about 30 bytes of code: call `LoadLibraryA("u9mod.dll")` through the
  exe's own import (it already imports `LoadLibraryA` and `GetProcAddress`), optionally call an exported `Init`,
  then jump to the original entry point (RVA 0x314920). The header's entry point is pointed at the stub. The DLL
  loads after the game's other imports and before any game code runs, so it can install its hooks in `Init`, outside
  the loader lock.
- **An extra import.** A new section holds a copy of the import directory with one more entry, for `u9mod.dll` and
  one exported function; the header's import directory points at the copy. Windows then loads the DLL before the
  game starts, and the DLL's `DllMain` runs under the loader lock (keep it to patching memory; no loading or
  waiting there).

Either way:

- **It works on the original game and with U9GL.** Without U9GL, `u9-mod.exe` starts with the game's own renderer
  and loads the mod DLL. With U9GL, the same `u9-mod.exe` also loads U9GL's `dsound.dll` (it imports DSOUND.dll like
  the original), so both run. U9GL's six code-signature checks still pass as long as no detour touches those sites.
  U9GL's own launcher starts `u9.exe` or `u9-laa.exe`, not `u9-mod.exe`, so a modded game is started from the mod
  manager (or U9GL learns to start `u9-mod.exe`, which is the relaxation already requested of it).
- **One loader, many mods.** One `u9mod.dll` that loads every enabled mod's plugin (a folder of DLLs, in the order
  the manager sets) keeps the executable change fixed and small, and lets the manager stack plugins like it stacks
  data mods.
- **Mod DLLs are native code** running inside the game with the player's permissions. A catalog should say so, and
  the manager should show which mods carry code.

## What every approach must respect

- **U9GL's signature sites** (six short code ranges it checks before hooking, listed in `src/U9.Mods/ExePatches.cs`):
  never patch them.
- **Saved games**: they store objects and NPC records. A mod that changes how saved objects are laid out, or adds
  objects of new kinds, breaks old saves; one that only changes behaviour does not.
- **New content needs its interface too.** A new spell needs a table entry, a class (its behaviour), a spellbook
  page and icon, reagents, a way to learn it, and the AI choosing it if creatures should cast it.
