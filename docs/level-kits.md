# How Ultima IX's maps are built, and building with their kits

Findings from the shipped map files (2026-11-25), and the editor's Kit tools that build on them. The numbers come
from `u9 kit` and from measurements over every map's `fixed.N`, `nonfixed.N` and `terrain.N`.

## Dungeons are pieces, not terrain

Every map has a terrain file, but in the dungeons it hardly matters. Ninety per cent or more of a dungeon's terrain is
one flat height, usually well below the level, where you never see it. The level itself is placed pieces.

| Map | Terrain height spread (middle 90%) | Fixed pieces | Terrain against the level |
|---|---|---|---|
| Britannia (9) | 682 | 17,618 | rolling ground (z 0–5,636) with objects on it |
| Trinsic (7) | 249 | 1,380 | ground and buildings together |
| Shame (10) | 0 | 650 | flat at z 48; the dungeon is at z 1,073–1,712 |
| Abyss (11) | 0 | 468 | flat at z 500, under the dungeon at 1,230 and up |
| Britain Sewers (24) | 0 | 1,966 | flat at about z 400, just under the floors |
| Deceit 1 (35) | 0 | 366 | flat |
| New Destard (47) | 0 | 1,694 | flat |

Some dungeons use a little terrain anyway: Wrong (45), spread 175; Covetous level 4 (19), 75; the Abyss Dungeon Map
(44), 1,200. The void maps (Ethereal Void, Anti-Void, the Blackthorn fight) are open spaces built on terrain. New
Shame (27) and Son of Covetous 2 (33) reuse a single 16×16 terrain block for the whole map.

The terrain header's flags split the two groups as well. Dungeons have bit 0x1 set (0x15, 0x35, 0x55, 0x1D); towns
have 0, Britannia 0x4E. That bit plausibly means "underground, no sky", but this hasn't been checked in the code.

## The pieces are a kit, reused

A dungeon uses a few dozen to a few hundred piece types, and most are repeated. About a third of the types appear
only once: set pieces. The rest is a kit.

| Map | Pieces | Types | Used once |
|---|---|---|---|
| Shame | 650 | 65 | 27% |
| Deceit 1 | 366 | 71 | 36% |
| Covetous 4 | 1,132 | 138 | 31% |
| New Destard | 1,694 | 292 | 35% |

In Shame, three types make up 371 of the 650 pieces:
- 666, a wall 256 long, 128 thick and 320 high;
- 664, a pillar 128 square;
- 663, a 256-square floor-and-ceiling tile: two plates, 320 apart, the height of the walls.

(The Kit tab's pictures of the models show this.)

## Sections on their own turned grids

Absolute positions in the map files snap to no grid. That is because a map is made of **sections, each laid on its
own grid, turned and offset**.

- **Shame:** pieces sit at five angles: −10° (215 pieces), −30° (161), 35° (104), 15° (86) and 0° (64). Sections
  turned alike still have different origins.
- **Deceit 1 and New Destard:** mostly square, at 0° (298 of 366 pieces, and 1,337 of 1,694).

Inside a section the kit is clean. Measured from one piece to the next in the first piece's own frame:
- a wall follows a wall 256 along its x axis;
- a pillar sits 192 from a wall (half the wall's 256 plus half the pillar's 128);
- corners join with quarter turns;
- Shame's larger pieces repeat every 512.

Offsets of 255 and 256 occur for the same join, one unit of jitter. The joins of Shame fit a 64-unit grid. Hythloth
and Deceit fit only 16.

So the developers' editor most likely snapped pieces to a grid and to each other within a section, and each section
could be turned and placed freely. In Shame, consecutive wall pieces are strung end to end along whatever direction
the wall runs, for example 664 at (6777, 16362), 666 at (6966, 16329), 664 at (7155, 16296): steps of (189, −33), at
the walls' turn of 350°. The developers' own test maps also measured per-chunk budgets ("64 by 64, chunk crossing
items", "high-poly count", "20 polys per"), which fits levels assembled from many placed pieces.

## Pieces are one-sided

The game draws a face from its front only, and kit pieces have only the faces you are meant to see:
- Shame's wall 666 has a single drawn side, plus its top and bottom.
- Its corner pillar 664 has two drawn sides, at a right angle: a block for the inside corner of a room.

So which way a piece is turned matters. A wall turned round, or a pillar a quarter turn off, shows sky through the
level in the game, although the editor (which draws both sides) shows it whole. `KitCatalog` works out each piece's
facing from its model, the area-weighted direction of its drawn sides: 666 faces +y in its own frame, 664 (−1, 1)/√2.
The front faces wind clockwise as seen, the opposite of the usual convention, which was found the hard way in the
game.

In the game (arena test, 2026-11-25), a room of Shame's pieces built on a new map behaves as it should:
- the Avatar walks on its floor and is stopped by its walls, so placed pieces collide like the originals;
- with every piece facing the room's middle, the corners close and all the walls show.

## How the editor builds with this

**`KitCatalog`** (`src/U9Editor.Core/KitCatalog.cs`) reads a map's pieces:
- each type's use count and model bounds;
- its joins: every pair of nearby pieces turned alike or a quarter turn apart, measured in the first piece's frame.
  Offsets that recur at least three times are kept, and those within 2 units of a multiple of 16 are snapped onto it;
- the grid step the joins imply.

A spatial hash keeps it fast: Britannia's 17,618 pieces take under a second.

**`KitBuilding`** (`src/U9Editor.Core/KitBuilding.cs`) puts pieces down:
- **Snapping:** near a placed piece, every join from that kind to the new one, or the other way round, gives a
  candidate spot, and the nearest within reach wins. With no join in reach, the spot snaps to the placed piece's own
  turned grid. A global grid would not fit, because of the sections.
- **Walls:** a pattern of types is strung along clicked points. Each pair steps by the join that continues the run's
  direction; with none, the pieces go end to end from their bounds. Later legs turn round the last piece. Corner
  angles can snap (90° gives square rooms), and a corner piece can go at each turn. "Other face out" runs the pieces
  the other way round.
- **Floors:** tiles fill a clicked polygon, on a grid along its first edge. The step is the tile's join to itself
  (Shame's 663: 256), else its size.
- **Rooms:** three clicks give a rectangle (the first side, then the depth). Both sides are made whole wall steps, so
  the walls close: corner to corner is the corner-to-wall step, whole wall steps, then the wall-to-corner step. Corner
  pieces stand on the corners. Floor tiles fill the inside, the rectangle less half the wall's thickness all round.
  Shame's numbers fit exactly: pillar to pillar is 128 + 256n, and the inside 256n holds n tiles of 663. Walls and
  corner pieces are turned so their drawn sides face the room; "Other face out" turns the walls the other way, for
  a block seen from outside.
- New pieces are copies of a piece of that type on the map being edited, or on the source map. So Shame's kit can be
  used to build on any map.
- **Pictures:** `ModelThumbnail` draws each piece's model on the CPU, textured and lit. The Kit tab shows them, and
  `u9 kit --thumbs DIR` writes them as PNGs.

**The Kit tab** (in the editor):
- Pick the source map, then a piece. The list is sorted by use, with sizes and joins.
- **Place:** the piece follows the mouse as a ghost and snaps; `[` and `]` turn it.
- **Draw wall:** click the points. Enter builds, Backspace takes back a point, Esc cancels.
- **Fill floor:** click the corners of an area, then Enter.
- **Room:** three clicks (a fourth moves the depth), then Enter. It uses the wall pattern, the Corner piece and the
  Room floor tile.
- **Build height** gives the mouse a level plane to land on, for empty space. In dungeons the terrain is far below,
  so without it clicks would land there. Clicks on existing floors land on the floor itself.
- With the Kit tab open, dragging a kit piece in Move mode snaps it too.
- Every placement is one undo step.

**On the command line:**
- `u9 kit <gamedata> <map> [--type N]` lists a map's kit.
- `u9 wall <gamedata> <map> --kit N --types 666 --corner 664 --z Z --angle 90 x,y x,y ...` builds a wall into
  `fixed.<map>`, or into a staged copy with `--file`. `--dry-run` only lists the pieces.
- `u9 floor ... --type 663 x,y x,y x,y ...` fills a floor.
- `u9 room ... --types 666 --corner 664 --floor 663 a b c` builds a room.

Arena tests `04-kit-wall` and `05-kit-room` (in the private `analysis/arena-tests`) put such a wall, and such a room
round the start point, on a flat new map, to look at in the game.

## Open

- **Walls drawn freehand face whichever way the run goes.** Only the Room tool turns pieces to face the room. With
  the Wall tool, check which side shows ("Other face out" turns it) until the editor can draw only front faces as
  the game does.
- What terrain-header bit 0x1 does.
- Joins between sections at different angles (where Shame's sections meet) aren't learned. Those seams need hand
  placement.
