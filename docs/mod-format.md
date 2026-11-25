# Mods for Ultima IX (`.u9mod`) and the mod manager

A mod is a package of **changes** to the game's own files, never a copy of them. The editor makes one from what it
changed (File > Export changes as mod, from the `.orig` copies it keeps); the mod manager (`src/U9ModManager`, and
`u9mod` on the command line) applies several at once, puts the original game back, downloads mods from a catalog and
fetches U9GL. Both use `src/U9.Mods`. The design follows the Underworld editor's format 2 (UltimaUnderworldEditorAvalonia
`docs/mod-format.md`), with stacking and executable patches added.

## Rules

1. **A mod changes files the game already has.** It may change `u9.exe` (as patches, below) and files under
   `static/` and `runtime/`. Nothing else: not `savegame/`, the options and key maps, `sound/` or `Movies/`. A mod never
   adds, deletes or renames a file.
2. **A mod carries differences, never an original file.** Each changed file is a delta: runs to copy from the original
   and the mod's own new bytes.
3. **A delta applies to one exact original.** Each file names the SHA-256 and size of the original it was made from
   and of the result; a file that is anything else is refused, not patched "close enough".
4. **The result is byte-exact by construction.** Making a mod applies every delta and compares the result with the
   edited file; installing checks each result's SHA-256 before writing it.
5. **The executable is patched, not replaced, and never in place.** `u9.exe` stays as shipped, so U9GL's launcher (which
   recognises the game by the exe's SHA-256) keeps working. The manager writes the patched program as `u9-mod.exe`
   beside it, with the large-address flag set as U9GL's `u9-laa.exe` has, and starts that. U9GL's `dsound.dll` checks
   code in memory, not the file, so it should load into the copy as into `u9.exe` (not yet tried in the game). A patch may not touch the code U9GL checks before it hooks the game (its build signature
   sites); such a mod is refused.
6. **Mods stack.** Several mods are active at once, in an order. Two mods may change the same file:
   - when the file keeps its length in both, byte by byte (they clash only where both change a byte differently);
   - FLX archives (`TRIGGERS.FLX`, `BOOKS-EN.FLX`, `sappear.flx`, `NPC.FLX`...) record by record;
   - otherwise (a map's object file grown by both) they clash, and the manager names the file and the mods.
   Executable patches clash where they overlap with different bytes. A clash refuses the whole set; nothing is written.

## The package

A zip file:

```
mod.json
deltas/static/triggers.flx.u9d
deltas/runtime/nonfixed.9.u9d
```

`mod.json`:

```json
{
  "format": 1,
  "game": "u9",
  "id": "karl.moonglow-garden",
  "name": "Moonglow garden",
  "version": "1.0.0",
  "authors": ["Karl"],
  "description": "",
  "made_with": "U9 Editor 0.0.5",
  "files": [
    {
      "path": "runtime/nonfixed.9",
      "base":   { "sha256": "…", "size": 2228224 },
      "target": { "sha256": "…", "size": 2232320 },
      "delta":  { "path": "deltas/runtime/nonfixed.9.u9d", "sha256": "…" }
    }
  ],
  "exe": {
    "base": { "sha256": "83915175…", "size": 4593152 },
    "patches": [ { "offset": 3790952, "old": "23000000", "new": "2d000000", "note": "Broadsword max damage 45" } ]
  }
}
```

- `id`: lower case letters, digits, `.`, `_` and `-`, 3 to 64 characters; `version` free text.
- `path`: lower case, `/`-separated, relative to the game folder, under `static/` or `runtime/`, no `..`. On disk the
  file is found whatever its case (the GOG release has `FIXED.140` beside `fixed.0`).
- `exe.patches`: file offsets into `u9.exe`, the bytes expected there (`old`) and the bytes written (`new`), the same
  length. `note` says what a patch does.
- Packages are untrusted: anything that breaks these rules, a delta whose hash does not match, or a delta reading outside
  its original is refused when it is imported.

## The delta (`.u9d`)

Little-endian; numbers as unsigned LEB128 varints (`v`), signed ones zigzag-coded (`z`):

```
"U9D1"  v:baseLength  v:targetLength
op*
0x00                                   end
```

| Op | Fields | Writes |
| --- | --- | --- |
| `0x01` COPY | `z` offset (from the end of the previous COPY), `v` length | that run of the original |
| `0x02` ADD | `v` length, bytes | the bytes given (the mod's own content) |

Making one: the original's 16-byte windows are indexed by hash (every window for files up to 16 MB, every 16th above,
so `bitmap16.flx`'s 320 MB index in about 80 MB); the edited file is walked taking the longest match, COPY for matches
of 8 bytes or more (4 when they continue the last COPY's displacement, bytes changed in place), ADD for the rest.

## The manager

- **Where it keeps things:** `%APPDATA%\U9ModManager` (`U9MM_HOME` overrides): `library/` (imported mods), `vault/<exe
  hash>/` (originals, copied the first time a file is changed), `settings.json` (the catalog URL, game folders).
- **The game folder** gets `.u9mm-target.json`: the active mods and the SHA-256 of every file the manager wrote. A file
  changed since (by the editor, by hand) is not overwritten: the manager names it and stops.
- **Applying** works out every file's new content first (all deltas applied and merged, every hash checked), writes them
  beside their targets as `*.u9mm-new`, records the set in a journal, then renames them into place. A change cut off
  half way (a crash, a power cut) is finished the next time (`u9mod recover`). Files no active mod changes are put back
  from the vault. "The original game" is applying no mods.
- **Saved games** are not touched. A new game copies `runtime/nonfixed.N`, so changes to nonfixed objects show in new
  games; a saved game keeps its own copy.
- **Catalog:** a JSON file at a URL set in the manager:

  ```json
  { "format": 1, "mods": [ { "id": "…", "name": "…", "version": "…", "authors": ["…"], "description": "…",
    "url": "https://…/mod.u9mod", "sha256": "…", "size": 12345, "homepage": "https://…" } ] }
  ```

  A download is checked against its SHA-256 and read as a package before it enters the library.
- **U9GL** (theGreyWanderer-uc/tgwUltima9GL): the manager downloads the latest release's zip, checks it against the
  release's `SHA256SUMS` (or a `.sha256` beside the zip), unpacks its `u9gl/` folder into the game folder and opens `u9gl\U9GL.exe`, whose own Install puts
  `dsound.dll` in place and keeps its backups. The manager does not install or remove U9GL's files itself.

## The editor's pending executable changes

The editor never writes `u9.exe`. Its changes to the executable's tables (Edit > Combat balance) are saved beside it
as `u9.exe.patches.json`:

```json
{ "format": 1, "exe": { "sha256": "…", "size": 4593152 },
  "patches": [ { "offset": 3791504, "old": "3c000000", "new": "50000000", "note": "Longsword (5388): max damage 60 -> 80" } ] }
```

`exe` is the `u9.exe` the patches were made for; a file for another executable is refused. Export changes as mod copies
the patches, notes and all, into the mod's `exe` section (the file is deleted when no change is left).
