# Chinese, Japanese and Korean text in Ultima IX with U9GL: what it would take

2026-11-25. Written from this repository's audited research on the GOG `u9.exe` (see
[exe-research.md](exe-research.md), "Dialogue"), U9GL 0.6.1's source, and the public work of others listed under
"What already exists". Addresses are virtual addresses in the GOG `u9.exe` (SHA-256 `83915175…`).

## In short

The stock game cannot draw CJK text, and no data file can change that: it turns every line into single bytes and
draws each byte from a 224-glyph bitmap font. Showing CJK needs code that runs inside the game. Two working
approaches exist already, neither written for U9GL:

- the Japanese edition's patched executable, which handles Shift-JIS byte pairs and draws them with Windows fonts;
- a Traditional Chinese proof of concept that keeps each line as UTF-16, hides the native placeholder glyphs and
  draws Chinese on top. It does this on the game's Direct3D back buffer, just before Flip.

U9GL replaces the game's renderer, so that Direct3D back buffer no longer exists. CJK support in U9GL means two
things. **The text half** must keep the Unicode line, measure it, wrap it and suppress the native glyphs; the
proof of concept has shown this works for subtitles and reply menus. **The drawing half** must draw the glyphs in
U9GL's OpenGL pass at native resolution. U9GL is well placed for that: it already draws overlay text in its present
pass and sees the subtitle as textured quads, with their screen geometry.

People have asked: [U9GL issue #8](https://github.com/theGreyWanderer-uc/tgwUltima9GL/issues/8) (2026-11-16) asked
about building a Traditional Chinese localisation on U9GL and whether U9GL's renderer helps with CJK fonts. Its
author closed it 24 minutes later with no reply on the issue. That author's own project, below, has done most of
the research; their fork of U9GL has no changes.

## Why the stock game cannot

Audited findings (details and addresses in [exe-research.md](exe-research.md)):

1. **Lines are UTF-16, but only on disk.** `static/text.flx` stores UTF-16. The line object (FUN_00615A10) keeps
   it at +8 and converts it with `WideCharToMultiByte(CP_ACP)` into the string it displays (+0). That conversion
   buffer is only `wcslen + 1` bytes (0x615B58), so on a double-byte code page such as cp932 the conversion fails
   part way.
2. **Drawing is one byte, one glyph.** The layout (FUN_005C7870) and the drawing code (0x5C5A80) index glyph
   `byte − 0x20` of a font with 224 glyphs. There is no notion of a byte pair, and nowhere to put thousands of
   glyphs.
3. **Fonts are bitmaps.** 19 font slots load from `bitmap16.flx`, `bitmapsh.flx` or `bitmapC.flx`. Subtitles and
   reply menus use slot 0, record 0x263: cells up to 25 × 21 px, lines 15 px apart. GDI draws only a debug window.
4. **Wrapping is Latin.** Lines break only at spaces, tabs and after `! . ; ? , - :`, inside a 600 × 200 px box (13
   lines a page). CJK text has no spaces, so it would never wrap.
5. **The subtitle is low-resolution.** It is drawn into a small 16-bit raster (595 × 37 at any resolution, measured
   by the Chinese project) that the engine then scales up as textured geometry. CJK glyphs drawn into that raster
   would be blurry at 1600 × 1200.
6. **Reply choices are matched back by their text.** The menu compares the chosen reply's converted text with the
   queued replies by `strcmp` (0x6199CA-0x619A41) and takes the first match. Any scheme that turns CJK characters
   into placeholder bytes must keep different replies different after conversion.
7. **The lookup key stays English, which helps.** `text.dat` finds a line by `"<NPC record name> : "` plus the
   English literal compiled into the exe, never by the displayed text. A translated `text.flx` needs no text.dat
   change. But the NPC record names in `runtime/NPC.FLX` are part of every key: translate them and every line goes
   missing. They are never drawn in subtitles anyway.

## What already exists

### The Japanese edition's executable

[Fenyx4/UltimaPatcher](https://github.com/Fenyx4/UltimaPatcher) installs a Japanese language pack. It applies an
Octodiff delta to `u9.exe` (giving `u9.jp.exe`) and starts it through Locale Emulator with a Japanese code page. The
Chinese project rebuilt that executable privately and compared it with the English one (its
`docs/research/U9_JP_CJK_IMPLEMENTATION.md`):

- the conversion asks for the needed size first, which fixes the `wcslen + 1` bug;
- layout and drawing step over Shift-JIS lead/trail pairs (leads 81-9F and E0-FC);
- glyphs come from a cache filled on demand by GDI (`CreateFontA`, `TextOutA`) into a 64 × 64 DIB, with the font
  named in `fcache.ini` (MS P Gothic by default);
- glyphs are small (14 or 28 px sources, clamped to 32 or 64) and drawn into the same low-resolution raster.

It shows the engine can take CJK. It is tied to Shift-JIS, which cannot simply be switched to Big5 or GB2312
(different byte ranges and table sizes). It needs a Japanese code page, and its glyphs stay low-resolution.

### The Traditional Chinese proof of concept

[pmanyeh/Ultima-9-for-zh](https://github.com/pmanyeh/Ultima-9-for-zh): research notes, tools and a native
runtime (`runtime/U9ChtRuntime`). Its approach ("Strategy B", `docs/architecture/P5_UNICODE_RENDERER_OPTIONS.md`):

- **Keep the UTF-16 line.** At the conversion (0x615B76) it fills the byte string with the ASCII as is and one
  opaque 0x7F per CJK character. It ties the UTF-16 to that string and follows the string through its copies; the
  hooks are listed in `docs/architecture/P5_1_HOOK_MANIFEST.md`.
- **Measure and suppress.** For those placeholders the width readers (0x5C7BB3, 0x5C5F62) get the CJK glyph's
  advance, and the native glyph draw (0x5C5A80) is skipped. Latin text goes through the game untouched.
- **Draw at native resolution.** Glyphs are rasterised with GDI `GetGlyphOutlineW` from Noto Sans CJK TC (OFL) at
  16 logical units × the UI scale (20 px at 800 × 600, 40 px at 1600 × 1200). They are blended into the game's
  Direct3D back buffer between EndScene and Flip (0x597B1A).
- **Install at run time.** A separate launcher starts the game suspended and loads the runtime DLL; the executable
  is not changed on disk.

Status by its own reports (`reports/P6S2_REPRESENTATIVE_SURFACES.md`): a bounded proof of concept, "PASS WITH
LIMITATIONS".
- **Works:** the opening subtitles; a full NPC conversation with its reply menus (19 lines, through save and reload
  and a normal exit); one inventory item name.
- **Unfinished:** more item names, readable books and the rest of the interface. Only 800 × 600 and 1600 × 1200
  are supported.
- **On hold:** work stopped at the author's request on 2026-11-02.

The repository has no licence file, so its code cannot be reused without the author's permission. Its research is
public.

## What U9GL changes

- **There is no Direct3D back buffer.** U9GL installs its own renderer object (`U9RendererVtbl`, with Present), so
  the proof of concept's "lock the back buffer before Flip" step has nothing to attach to. Drawing has to happen in
  U9GL.
- **U9GL already has the drawing half's pieces:**
  - it renders the engine's UI runs in an orthographic pass sized to the engine's resolution (`glraster.c`);
  - it can tell interface runs from the world;
  - it draws its own overlay text at the end of the frame (`console.c`, `metrics.c`; ASCII only, through
    `wglUseFontBitmaps`).
  - A CJK glyph atlas drawn as textured quads after the interface runs is a natural extension.
- **It sees where the subtitle is.** The subtitle raster reaches the renderer as textured quads with their final
  screen coordinates (the Chinese project measured a scale of 2.5 at 1600 × 1200). U9GL can place glyphs relative
  to the run that carries a given raster, at any resolution rather than two fixed ones.
- **It patches the game in memory already** (`injection/patch.c`), and its launcher identifies the game by the
  executable's hash. Text hooks would run in the same process: as a U9GL module, or as a separate DLL that U9GL
  tolerates. They must stay clear of the six code sites U9GL checks before it hooks the game.

## The work, in order

1. **Text identity (renderer-independent).**
   - Keep each displayed line's UTF-16 tied to the byte string the game measures and draws, through every copy.
   - Do it surface by surface; each needs its own mapping and test:

   | Surface | Path |
   |---|---|
   | Subtitles | SayProcess |
   | Reply menus | TopicsProcessor; several lines share one raster |
   | Journal | FUN_0042E0A0 → 0x615DB0 |
   | Books | `books-en.flx`, also UTF-16 converted to bytes |
   | Item names | `TYPENAME.FLX`; the label copy at 0x41AD54 |
   | Interface strings | `misctext.flx`, read by index (FUN_00616120, 155 call sites) |
   | Save-game names, map and shop text | Untraced |

   - The proof of concept has done subtitles, replies and one item name.
2. **Reply matching.** With placeholder bytes, two different CJK replies of the same length would convert to the
   same bytes, and the `strcmp` in point 6 above would pick the wrong branch. The placeholders must stay unique
   per reply, or the comparison must use the UTF-16. The proof of concept's reports describe several replies
   sharing one raster; whether this case is handled is not stated.
3. **Measuring and wrapping.**
   - Feed CJK advances to both width readers (layout and tight bounds).
   - Add break opportunities between CJK characters, with the usual line-start and line-end rules. The native
     layout breaks only at spaces.
   - Keep the 600 px box and the 13-line pages, or adjust them for taller glyphs.
   - A voiced line closes when its sound ends, so a line that wraps past one page loses its tail.
4. **Glyphs.**
   - Choose a font with a licence that allows redistribution (Noto Sans CJK or Source Han Sans, OFL).
   - Rasterise with DirectWrite, FreeType or stb_truetype into an OpenGL atlas at the output scale: 16 logical
     units, so 40 px at 1600 × 1200.
   - Cache by Unicode character; Traditional Chinese alone needs several thousand glyphs.
   - Match the native text colour (the proof of concept takes it from the native raster) and its fades.
5. **Drawing in U9GL.**
   - Record each placeholder's position in the native raster and that raster's identity.
   - Find the interface run that draws the raster in U9GL's pass, transform the positions to the screen, and draw
     the glyph quads after that run with its clip and alpha.
   - The native glyphs are already suppressed, so nothing is drawn twice.
6. **Data.** A translated `text.flx`, `books-*.flx`, `TYPENAME.FLX` and `misctext.flx` (all UTF-16 already).
   - `text.dat` and the NPC.FLX names stay as shipped.
   - 34 shipped lines differ from the English text in the exe; 28 have no literal in the exe at all. A missing or
     empty line shows the English text from the exe.
7. **Installation.**
   - Decide one: a U9GL module, or a separate runtime that U9GL loads.
   - A setting for the language and font file.
   - Work with U9GL's launcher, and with the mod manager's `u9-mod.exe` if executable patches are used.

## Choosing an approach

| Approach | For | Against |
|---|---|---|
| **A. Double-byte, as the Japanese edition** (Big5, GBK or Shift-JIS pairs through the byte pipeline) | Fewer hooks once done; text stays ordinary bytes | Depends on the player's code page or a locale emulator; fix the conversion size, layout and drawing for pairs; low-resolution glyphs unless drawing moves out anyway; one encoding per build |
| **B. Unicode identity plus U9GL drawing** (the proof of concept's text half, U9GL's GL pass) | Any CJK (and any other script) in one build; native resolution; Latin untouched | Many small hooks, each surface tracked separately; reply matching (step 2) |
| **C. U9GL draws every text surface itself** (read the line objects and gump state, skip the engine's text) | Full control of wrapping, sizes and fonts | Re-implements subtitles, menus, journal and books; touches far more game state; heaviest |

**B is the recommendation.** It is the path with working evidence for subtitles and replies. It needs the least new
engine knowledge, because the hooks are found. And U9GL removes its weakest part, the Direct3D back-buffer
blending, by owning the frame.

## Rough size of the job

Not an estimate in hours; the stages, from most to least known:

- **Known and shown to work:** subtitles and reply menus (the text half), by the proof of concept.
- **Known, still to build:** drawing in U9GL's pass, the glyph atlas, CJK wrapping, reply uniqueness.
- **Mapped but not working yet:** item names (one shown), books, journal.
- **Not traced:** `misctext.flx` interface screens, save names, shops, the options screen's font (slot 12, record
  0x25E), and resolutions other than the two the proof of concept measured.

## Licences

- **U9GL:** the U9GL Non-Commercial Copyleft Licence 1.0.
- **The Chinese proof of concept:** no licence file. Its code needs the author's permission; its findings can be
  cited.
- **TITAN:** MIT (`tgwUltima/titan-ultima`); its FLX and text tools are already used by both projects.
- **Fonts:** Noto Sans CJK and Source Han Sans, SIL Open Font License 1.1.
- **The Japanese pack:** UltimaPatcher distributes a delta, not the executable. The Japanese language assets are
  separate and were not examined here.

## Open questions

- Whether the proof of concept handles two same-length CJK replies in one menu (step 2).
- Where the remaining surfaces' strings are converted and drawn (books' font slot, `misctext.flx` screens, save
  names).
- How U9GL's interface runs map back to the engine's text rasters at every resolution: needed to place glyphs
  without hard-coded scales.
- Whether the U9GL author and the Chinese project's author want the work in U9GL, in a separate runtime, or shared.
