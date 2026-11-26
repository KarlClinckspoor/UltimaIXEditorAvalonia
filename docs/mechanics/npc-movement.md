# How NPCs and creatures find their way

From reading the decompile (2026-11-25) and checking the data. The A* search itself is read but not audited. The
`highway.dat` layout is checked against the shipped file in full. Addresses are in
[exe-research.md](../exe-research.md) section 7.

Ultima IX has no navigation mesh and no precomputed walk grid. A creature finds its way by searching on the spot,
against the level's real geometry. Long trips follow a road network of placed markers.

## 1. Moving about: a small A* search for each move

When an NPC or a creature sets off somewhere (to an attack target, a schedule spot, the next marker), the game makes
a pathfinder for that move. It lays a small grid along the straight line from the creature to its goal:
- cells about the creature's width (its radius × √2, at most 64 units);
- at least 10 cells along the line, up to 15 across it.

It then runs A* over the grid:
- moves to the four neighbours cost 1 each;
- the estimate is the number of cells to the goal (Manhattan distance);
- the work is spread over several frames;
- if the goal can't be reached, the creature heads for the closest cell it found.

**What blocks the way is the level itself.** To try a cell, the game puts the creature's own collision body there,
asks the physics code how high the ground is, and takes the body back. A cell is open when:
- the step up or down from the cell before is small enough, or a little larger on a gentle slope;
- and the slope isn't too steep.

These limits are the creature's own. Creatures with a "flies" style flag treat every cell as open. Terrain and placed
pieces block alike, so **new levels need no navigation data**: NPCs walk round walls built with the Kit tab as they do
round the originals. The search's state is kept in savegames.

## 2. Long trips: the highway network (`static/highway.dat`)

NPC schedules (going home, to work, to the tavern) send people between places along **highways**: chains of points
the designers placed as markers. The game walks each leg between two points with the search above.

**The points are placed markers.** Each highway point is a marker (type 1134) whose link number is the point's id:
50000 to 64117. The file repeats its position. For example, point 53501 is at (42449, 32328) object units, exactly
where Britannia's marker with link 53501 stands.

**The file**, little-endian:

| Part | Layout |
|---|---|
| Header | u32 point count (815), u32 highway count (149) |
| Points | point count × 12 bytes: u32 id (a marker's link), u32 x, u32 y (object units, 128 per terrain point) |
| Highways | u32 byte size (2,612), then highway count records of u16 words: from-point, to-point, point count, cost, 0, then the point ids in order (the first is from, the last to) |

- The cost is the path's length in object units divided by 16. For example, highway 53508 → 53501 has 8 points,
  1,318 units long, cost 82.
- 311 of the 815 points lie on a highway. The others are kept in the file all the same.

The game's messages show the limits:
- "HighwayManager : Out of moving npc slots!": it moves a fixed number of NPCs at a time.
- "Npc trying to move on unknown highway (%d to %d)!": a schedule asks for a pair of points with no highway between
  them.

A leftover straight-line mover (`BeeLinePathFind`) is wired to a switch that nothing turns on.

## 3. Scripted routes and patrols

- **Script command 0x39** starts a follower that moves an object from marker to marker (type 1134): the one with the
  current link, then link + 1, link + 2 and so on. A route is a run of markers with consecutive links.
- **Patrols** walk between markers. On reaching one, they run its SF Trigger A script.
- Between markers, both use the search of section 1.

## For editing

- **Walls, rooms and other geometry** need nothing more: the search reads the level as it is.
- **New routes** for scripts and patrols are markers with consecutive links. The editor can place those today.
- **New highways** need entries in `highway.dat` for markers whose links are the new points' ids. A highway editor is
  planned: show the network over the map, add and move points (markers and file entries together), join them into
  highways, and recompute each highway's cost from its length.
