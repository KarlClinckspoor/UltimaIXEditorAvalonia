# The trigger language

The editor shows each trigger script (`static/TRIGGERS.FLX`) in this language and compiles it back. It is
C-like, as the conversation language of the Underworld editor is. One statement is one op of the game's
script machine, so the text is a faithful picture of the bytes: printing any script and compiling the text
gives the same bytes back (checked for all 6,708 shipped scripts). How the game runs the ops is in
`trigger-scripts.md`.

The compiler is `src/U9.Formats/Triggers/Language/TriggerCompiler.cs`, the printer `TriggerPrinter.cs`, the
names `TriggerNames.cs`. On the command line: `u9 script <gamedata> <slot>` prints a script,
`u9 script-compile <gamedata> <slot> <file>` writes one.

## Example

```c
// a trigger zone that sends the camera on, until it has fired four times
range = 10
if (any(class == TrgZones, link).variable > 3) goto done
each(class == TrgZones, link).variable += 1
each(class == CameraPoints, link + 1).move(2s, GLIDE | STRAIGHT | THEN_SCRIPT1)
end
done:
each(class == 640, link).translucency = 0
```

Line by line: look 10 points around the trigger. If any trigger zone with the script's link has a Variable
above 3, go to `done`. Otherwise add 1 to the Variable of each of them, send the camera to the camera point at
link + 1 (a straight 2-second glide, then that point's first script runs), and end. At `done`, set the
Translucency of each class 640 object with the script's link to 0.

## Words

- **Statements** end at the end of the line. A `;` also ends one, so several fit on a line
  (`global[7] = 3; link += 2`); a `;` at the end of a line changes nothing. A statement never spans lines.
- **Comments** run from `//` to the end of the line. They carry nothing the compiler needs and are not kept
  in the game's file.
- **Numbers** are decimal (`120`), hex (`0x78`), or decimal with a unit: `2.5s` (seconds, in the game's half
  seconds), `10pt` (map points, 128 object units each), `1280u` (object units). Where a statement takes a
  distance, a plain number is in points.
- **Times of day** are written `18:30` (the game counts minutes since midnight).
- **Keywords:** `if goto end nop once no_end link range square_range default options pick change_map flags movie
  use_trigger global clock npc avatar raw each any count count_flagged class rgb random`.

### Names and their case

Anywhere a number stands for a thing, a name can stand instead. The case of a name says what kind of thing
it is:

| Case | Kind | Examples |
|---|---|---|
| `PascalCase` | object classes (named after their lead type) | `TrgZones` (1111), `CameraPoints` (2218), `Markers` (1134), `Lift` (2032) |
| `UPPER_SNAKE` | constants: flags, option bits, move parts, movies, globals | `GLIDE`, `UNLOADED`, `MOVIE_INTRO`, `FLAG_4000` |
| `lower_snake` | keywords, script state, properties, actions, labels | `link`, `variable`, `move`, `done` |

Class names come from three places, the later winning:

1. The game's own type names (`static/TYPENAME.FLX`) of the classes' lead types, in PascalCase: `"Altara's
   Sceptre"` becomes `AltarasSceptre`. Where several classes would share a name, each gets its number:
   `Statue_1051`.
2. The editor's names file, `names/trigger-names.txt` (shipped with the program), for classes the game leaves
   unnamed.
3. The user's names file, `%APPDATA%\U9Editor\trigger-names.txt`, which the script editor writes when you
   rename something (F2).

A names file has one entry a line, `#` starting a comment:

```
class 1111 TrgZones
global 12 MET_LORD_BRITISH
flag 0x100 DOOR_OPEN          # a flag bit as ops write it in B (here object flag 0x4000)
label 7765 1 done             # script, label number, name
```

A number always works (`class == 1111`, `global[12]`), and the printer uses a name when it has one. Flag bits
are named `FLAG_` and the object flag they set until named otherwise (`FLAG_4000` is B bit 0x100).

## Labels

```c
done:          // a label; the compiler picks its number
done #3:       // label number 3
L3:            // label number 3, unnamed
```

A label is an op of its own (command 0x16). Its number matters because a test can only jump to labels 1 to 7:
it keeps the label in three bits of the op. The printer writes `L<n>:`, or the name and number (`done #3:`)
when the names file names it; applying a script in the editor stores its label names. The compiler keeps
every number it is given and gives unnumbered labels free numbers, 1 to 7 first for those a test jumps to.

A jump to a name the script lacks is an error. A jump to `L<n>` that the script lacks is allowed with a
warning, because some shipped scripts have one: in the game it ends the script.

## Links

Every object in a map has a **link**, a 16-bit number stored with it. It means nothing by itself: objects
with the same link belong together. A cave entrance and its arrival marker share one; a pressure plate and
the door it opens share one; each camera point of a scene has its own, and the point's script reaches the
next point by it.

While a script runs it keeps one link of its own, the **script's link**. It starts as the link of the object
that triggered the script; `link = 120` and `link += 5` change it. A selection names objects by an offset
from it: `link + 1` reaches the objects whose link is the script's link plus 1. The op stores only the
offset (-15 to +15) or "any link", never a link number.

The shipped maps hand out links by place: objects with consecutive links are typically about 3 points apart
on Britannia, as if the original editor gave each new piece of work the next free number.

## Picking objects

An action or test works on the objects that match a selection:

```c
each(class == 640, link + 1)     // each class 640 object with the script's link + 1
each(class == TrgZones, link)    // each trigger zone with the script's link
each(class == any, link - 2)     // any class, link - 2
each(class == 640)               // class 640 with any link
```

- `class == C`: the objects' **usecode class**. The game compares the usecode of each object's type
  (`TYPES.DAT`) with the op, so one op reaches every type that shares a usecode. A class is named by the type
  whose usecode is itself: `class == 2032` reaches type 2032 and the 15 other types that run its usecode.
  Most types are a class of their own. A type number that is not a class gets a warning naming its class.
- `link`, `link + n`, `link - n` (n from 0 to 15): objects whose link is the script's link plus n. Leaving it
  out reaches every link. `class == any` needs a link: with neither, the game reaches nothing, so the
  compiler rejects it.
- Only objects within the range of the trigger count: a square column around it, the range each way in x
  and in row, at any height.
- Only **nonfixed** objects and NPCs can be reached; fixed objects have no link.
- By default an op reaches the trigger itself, and of the other objects those that are live (loaded in the
  world near the Avatar) plus markers and zones. `options` changes that.

`each(...)` is for actions: the op goes to every match. `any(...)` is for tests: the test passes when at
least one match passes. `count(...)` counts the matches.

## Statements

### Control

| Statement | Op | Meaning |
|---|---|---|
| `goto done` | 17 | go on at label `done` |
| `end` | 17 (B 0) | end the script |
| `if (test) goto done` | the test's op | go to `done` (label 1 to 7) when the test passes |
| `if (test) end` | the test's op | end the script when the test passes |
| `done:` | 16 | a label |
| `nop` | 00 | nothing |

There is no `else` and no block: the game's ops cannot express them. Every `if` is one op.

### Script state and the game

| Statement | Op | Meaning |
|---|---|---|
| `link = 120` | 11 | the script's link |
| `link += 5` `link -= 5` | 12 | |
| `range = 10` | 2C | look 10 points around the trigger |
| `range = 1280u` | 2C | in object units (up to 32,767) |
| `range = default` | 2C (B 0) | 10 points |
| `square_range = 10` | 2C | the same as `range` (a name that says the range is a square); the printer writes `range` |
| `once` | 13 | after this run the trigger's link becomes 0, which silences it (types 0x200 and above, which covers zones) |
| `options(SELF \| UNLOADED \| SKIPPED)` | 14 | which objects later ops reach: the trigger itself, objects that are not live, objects the game normally skips (flags 0x40100). A script without `options` reaches the trigger; `options(0)` leaves it out |
| `pick(3, class == 640, link)` | 21 | gather up to 3 of the matches (1 to 1,023; random once there are more). The next statement that goes to objects works on them, whatever selection it names; the printer marks it with a comment |
| `change_map(9)` `change_map(9, link + 2, flags: 0x40)` | 1F | go to map 9 (0: this map), arriving at the marker (class 1134) with the script's link, or the link written. The script ends here |
| `movie(MOVIE_AMBUSH)` | 37 | play a movie; the trigger's SF1 script runs when it ends (SF2 if it failed or was cut short) |
| `global[MET_LORD_BRITISH] = 2` | 0D | a game global (0 to 511) set to 0 to 127 |
| `clock = 6:00` `clock += 1:30` | 3F | the time of day |
| `npc[12].schedule = 3` `npc[12].push_schedule(3)` | 31 | an NPC's schedule (by NPC number) |
| `use_trigger(5)` | 30 (A 0xD7) | uses the trigger itself (needs it live) |
| `avatar.mana = 20` `avatar.mana += 5` | 52, 5F | the Avatar's value at +0x3A (mana, probably) |
| `avatar.attribute[2] += 1` `-= 1` `= 5` | 62 | one of the Avatar's eight attributes |

### Actions on objects

Written as `each(selection).something`:

| Statement | Op | Meaning |
|---|---|---|
| `.activate(n)` | 01 | the class's usecode event 2 ("activate") with parameter n |
| `.move(2.5s, GLIDE \| STRAIGHT \| THEN_SCRIPT1)` | 01 | the same, for camera points: the move's time and parts (`GLIDE`, `STRAIGHT`, `BACK`, `THEN_SCRIPT1`, `THEN_SCRIPT2`) |
| `.run_script(OO1)` | 28 | run the objects' OO1, OO2, SF1 or SF2 trigger script |
| `.link = 120` `.link += 5` `.link -= 5` | 02, 57 | the objects' link |
| `.type = 641` | 04 | the objects become another type |
| `.variable = 3` `.variable += 1` | 18, 19 | the Variable property (0x3C) |
| `.variable = random(10)` | 27 | a random number below 10 |
| `.translucency = 128` `+= 16` `-= 16` | 41, 43 | 0 to 255 |
| `.scale = 1.5` `.scale_x += 0.1` | 42, 44 | all axes, or one or two (`scale_xy`), in steps of 0.01 up to 40.95 |
| `.hit_points = 5` `.hint_level = 2` `.quantity = 1` `.owner = 3` | 03 | the four properties op 03 can set, 0 to 4,095 |
| `.tint = rgb(31, 0, 0)` | 4E | the Tint (5 bits a channel; channel order unchecked) |
| `.fade_tint(rgb(31, 0, 0))` | 4E | 10% of the way toward the colour each time the script runs |
| `.set_flags(F)` `.clear_flags(F)` `.toggle_flags(F)` | 06, 07, 08 | object flags, by name or number |
| `.activity(n, target)` | 40 | NPCs: their activity and its target |
| `.special(n)` | 3D | one of the game's built-in special functions (0 to 23 in the shipped scripts; 21 ends the calling script) |

### Tests

A test goes inside `if ( )`. Each is one op, and each allows only the comparisons the op can encode:

| Test | Op | Comparisons |
|---|---|---|
| `any(sel).variable > 3` | 1A | `<` `>`, 0 to 255 |
| `any(sel).translucency < 10` | 45 | `<` `>`, 0 to 255 |
| `any(sel).scale_x > 2.0` | 46, 47, 48 | `<` `>` |
| `any(sel).hit_points < 20` | 5A | `<` `>`, 0 to 4,095 |
| `any(sel).tint != rgb(31, 0, 0)` | 4F | `!=` only, and only `goto` label 1 or `end` |
| `any(sel).has_all_flags(F)` | 3B | the object has all of F |
| `any(sel).has_any_flags(F)` | 29 | the object has at least one of F |
| `count(sel) >= 2` | 15 | `==` `!=` `<` `>` `<=` `>=`, 0 to 255 |
| `count_flagged(sel) == 0` | 2D | the same; counts only objects with flag 0x4000 |
| `link == 7` `link + 2 == 7` | 2F | the same; `link` alone and `link + 0` both mean the script's link but are different bytes |
| `global[MET_LORD_BRITISH] > 2` | 0E, 5B | `<` `>`, 0 to 15 |
| `avatar.quantity(class == GoldKey) > 0` | 4B | `<` `>`, 0 to 4,095: how many the Avatar carries, containers included |
| `avatar.mana < 10` | 53 | `<` `>`, 0 to 4,095 |
| `avatar.attribute[2] > 5` | 61 | `<` `>`, 0 to 15 |
| `clock < 18:00` | 3A | `<` `>`, time of day |

A test whose comparison the op cannot encode (`any(sel).variable == 3`) is an error at its line, and the
message says which comparisons work.

## Fields the game ignores

Many shipped ops carry values in fields their command does not read: a class on `link = 21`, a link offset
on a label. They are kept so the bytes stay the same, written after the statement in brackets and shown
dimmed in the editor:

```c
link = 21 [A=Markers]
L1: [A=Statue_1051, L=+1]
movie(MOVIE_AMBUSH) [A=TrgZones]
```

`A=` is the A word (a class name or a number), `L=` the link field (`any`, `+n` or `-n`; `+0` is the usual
value and is not written). A statement only takes the fields it does not set itself.

## Unnamed commands and raw ops

Commands without a settled name are written by number, `op_` and two hex digits:

```c
each(class == Statue_1051, link + 1).op_20(21)    // command 0x20 sent to objects, B = 21
op_33(0x2005) [A=TrgZones]                        // command 0x33 that the interpreter does itself
if (op_58(3)) goto L2                             // an interpreter test without a name yet
raw(0x10FF, 0, 0)                                 // any op as its three words: op, A, B
```

The printer also uses `op_XX(B)` for a named command whose B has bits the named form does not cover, and
`raw(...)` for an op nothing else can write (end ops that are not the plain last one). In the shipped scripts that is
7,878 `op_XX` lines (mostly the commands still marked **?** in `trigger-scripts.md`) and 15 `raw` lines, of
20,355.

## Time

A script runs from start to end at once, in one game frame; no statement waits. A statement that takes time
starts it and the script goes on at the next statement in the same frame:

```c
each(class == CameraPoints, link + 1).move(2s, GLIDE | THEN_SCRIPT1)
each(class == 640, link).translucency = 0    // happens now, as the glide starts
```

Anything meant to happen after the glide goes in the script the camera point runs when it arrives
(`THEN_SCRIPT1`). That is how a scene chains its moves, and how an orbit is built: one camera point and one
script for each leg.

- **SF1 and SF2 are callbacks.** When a movie the script started ends, the trigger's SF1 runs (SF2 if the
  movie failed or was cut short). Moving an object to a marker and the other running actions run the
  object's SF1 when they finish and SF2 when they are blocked.
- `change_map` ends the script, and so does `special(21)`; the compiler warns about statements after them.

## The end of a script

The closing end op is not printed; the compiler always adds it (the game needs it: it reads a fixed 85 ops).
A script that does not end that way (two shipped scripts have no end op; a few others have an end op with
values in it, or more than one) is printed with its ops as they are, any end op among them as `raw(...)`, and
`no_end` as the last line, so it compiles back unchanged. A script
holds at most 84 statements.

## The script editor

Edit > Trigger scripts (Ctrl+T) in the main window, or Script editor on the Scenes tab.

- **The list** on the left has every script, with its op count and how many objects of the open map run it;
  ◆ marks the selected object's scripts. Filter by number, or show only this map's scripts.
- **New** makes an empty script in the first free slot after the shipped ones; **Delete** frees a slot (it
  says how many objects of this map run the script). **Apply** (Ctrl+Enter) compiles the text into the
  script. All three are undo steps of the open map: Ctrl+Z there takes them back, Ctrl+S saves
  TRIGGERS.FLX (keeping `.orig` the first time).
- **Check as you type:** errors are underlined red, warnings amber, and listed under the text; click one to
  go there, F8 goes to the next.
- **Completion:** after `each(...).` the actions, after `any(...).` the tests, after `class ==` the classes,
  inside `global[` the named globals, in `move(...)`, `options(...)`, `run_script(...)`, `movie(...)` and the flag
  calls their constants, after `goto` the labels, at the start of a line the statements. It opens by
  itself after two letters or a `.`; Ctrl+Space opens it anywhere.
- **Find** (Ctrl+F) and **Replace** (Ctrl+H).
- **Rename** (F2) on a label renames it in the script (Apply stores the name); on a class (after `class ==` or
  `A=`), a global (in `global[...]`) or a flag, it names it for every script and saves the user's names file.
- **Globals** lists the globals the scripts set and test, how many scripts do each, and their names;
  double-click inserts one, F2 names it. **Symbols** lists this script's labels, classes and constants and
  every named class; double-click goes to a label or inserts a name.

## Open questions

- Names for the commands marked **?** in `trigger-scripts.md`, and for most object flags.
- Whether a trigger zone runs its script once when entered or every frame while the Avatar is inside: that
  decides whether `fade_tint` fades in one visit.
- Whether to show `if (test) goto L1 ... L1:` patterns as `if (!test) { ... }` blocks once the language can be
  trusted to compile them back exactly.
