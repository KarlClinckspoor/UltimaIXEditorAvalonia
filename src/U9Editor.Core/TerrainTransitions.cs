using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Terrain;

namespace U9Editor.Core
{
    /// <summary>
    /// The map's transition textures, learnt from the map itself. A transition texture has 9 frames: 0-2 an outer
    /// corner, 3-5 an inner corner, 6-8 an edge, each showing its second material (B) at known corners of the
    /// texture (frames 0-2 at the bottom left only, 3-5 everywhere but the top right, 6-8 along the left side) and
    /// its first (A) at the others. Which plain textures A and B are is read off the plain quads beside transition
    /// quads: on Britannia set 10 blends grass (8) into dirt (11), 11,896 and 14,602 votes against a few hundred.
    /// With it, ground painted per corner gets the right transition frame and rotation on every quad.
    /// </summary>
    public sealed class TerrainTransitions
    {
        public sealed class Set
        {
            public int Texture, A, B, VotesA, VotesB;
            public override string ToString() => $"{Texture}: {A} -> {B}";
        }

        public readonly Dictionary<int, Set> Sets = new Dictionary<int, Set>();
        readonly Dictionary<(int, int), Set> byPair = new Dictionary<(int, int), Set>();
        /// <summary>Frames the map uses for each plain texture (its variants).</summary>
        public readonly Dictionary<int, int[]> PlainFrames = new Dictionary<int, int[]>();

        /// <summary>Texture corner (u, v) shown at quad corner (u east, v north), as <see cref="TerrainPoint.Rotation"/> describes.</summary>
        public static (int U, int V) Shown(int rotation, int u, int v) => rotation switch { 0 => (u, 1 - v), 1 => (1 - v, 1 - u), 2 => (1 - u, v), _ => (v, u) };

        /// <summary>1 where a transition frame shows its second material at texture corner c.</summary>
        public static int Label(int frame, (int U, int V) c) => (frame / 3) switch
        {
            0 => c == (0, 1) ? 1 : 0,
            1 => c == (1, 0) ? 0 : 1,
            _ => c.U == 0 ? 1 : 0,
        };

        // quad corners as mask bits: (0,0) 1, (1,0) 2, (0,1) 4, (1,1) 8
        static readonly (int U, int V)[] Corners = { (0, 0), (1, 0), (0, 1), (1, 1) };
        /// <summary>Mask of the corners showing B -> frame group and rotation.</summary>
        static readonly Dictionary<int, (int Group, int Rotation)> Tiles = BuildTiles();

        static Dictionary<int, (int, int)> BuildTiles()
        {
            var d = new Dictionary<int, (int, int)>();
            for (int g = 0; g < 3; g++)
                for (int rot = 0; rot < 4; rot++)
                {
                    int mask = 0;
                    for (int k = 0; k < 4; k++) if (Label(g * 3, Shown(rot, Corners[k].U, Corners[k].V)) == 1) mask |= 1 << k;
                    d.TryAdd(mask, (g, rot));
                }
            return d;
        }

        public bool IsTransition(int texture) => Sets.ContainsKey(texture);

        /// <summary>The material (a plain texture number) at corner (u, v) of the quad; -1 when it cannot be told.</summary>
        public int CornerMaterial(TerrainPoint quad, int u, int v)
        {
            if (!Sets.TryGetValue(quad.Texture, out var s)) return quad.Texture;
            if (quad.Frame > 8) return -1;
            return Label(quad.Frame, Shown(quad.Rotation, u, v)) == 1 ? s.B : s.A;
        }

        /// <summary>
        /// The quad for corners of materials (corner order (0,0), (1,0), (0,1), (1,1)): a plain texture when all are
        /// one material, else a transition frame if the map has a set for the two. False when there is no such quad
        /// (three materials, or two with no set).
        /// </summary>
        public bool TryQuad(int[] corners, Random random, out int texture, out int frame, out int rotation)
        {
            texture = frame = rotation = 0;
            int m0 = corners[0];
            int other = corners.FirstOrDefault(c => c != m0, m0);
            if (corners.Any(c => c != m0 && c != other) || corners.Any(c => c < 0)) return false;
            if (other == m0)
            {
                texture = m0;
                var fr = PlainFrames.TryGetValue(m0, out var f) && f.Length > 0 ? f : new[] { 0 };
                frame = fr[random.Next(fr.Length)];
                rotation = random.Next(4);
                return true;
            }
            if (!byPair.TryGetValue((m0, other), out var s) && !byPair.TryGetValue((other, m0), out s)) return false;
            int mask = 0;
            for (int k = 0; k < 4; k++) if (corners[k] == s.B) mask |= 1 << k;
            // B on two opposite corners has no frame: show B on one more corner
            if (mask == 0b1001 || mask == 0b0110) mask |= mask == 0b1001 ? 0b0010 : 0b0001;
            if (!Tiles.TryGetValue(mask, out var tile)) return false;
            texture = s.Texture;
            frame = tile.Group * 3 + random.Next(3);
            rotation = tile.Rotation;
            return true;
        }

        public static TerrainTransitions Learn(TerrainRaster r)
        {
            var t = new TerrainTransitions();
            var frames = new Dictionary<int, HashSet<int>>();
            foreach (uint raw in r.Points)
            {
                var p = new TerrainPoint(raw);
                if (!frames.TryGetValue(p.Texture, out var f)) frames[p.Texture] = f = new HashSet<int>();
                f.Add(p.Frame);
            }
            var nine = frames.Where(kv => kv.Value.SetEquals(Enumerable.Range(0, 9))).Select(kv => kv.Key).ToHashSet();
            foreach (var kv in frames) if (!nine.Contains(kv.Key)) t.PlainFrames[kv.Key] = kv.Value.OrderBy(x => x).ToArray();

            var votes = new Dictionary<(int T, int L), Dictionary<int, int>>();
            int[] dxs = { 1, -1, 0, 0 }, dys = { 0, 0, 1, -1 };
            var sides = new[] { new[] { (1, 0), (1, 1) }, new[] { (0, 0), (0, 1) }, new[] { (0, 1), (1, 1) }, new[] { (0, 0), (1, 0) } };
            for (int y = 1; y < r.Height - 1; y++)
                for (int x = 1; x < r.Width - 1; x++)
                {
                    var a = r[x, y];
                    if (!nine.Contains(a.Texture) || a.Hole) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        var b = r[x + dxs[k], y + dys[k]];
                        if (nine.Contains(b.Texture) || b.Hole) continue;
                        var cs = sides[k];
                        int l0 = Label(a.Frame, Shown(a.Rotation, cs[0].Item1, cs[0].Item2)), l1 = Label(a.Frame, Shown(a.Rotation, cs[1].Item1, cs[1].Item2));
                        if (l0 != l1) continue;
                        if (!votes.TryGetValue((a.Texture, l0), out var d)) votes[(a.Texture, l0)] = d = new Dictionary<int, int>();
                        d[b.Texture] = d.GetValueOrDefault(b.Texture) + 1;
                    }
                }
            foreach (int tex in nine)
            {
                if (!votes.TryGetValue((tex, 0), out var va) || !votes.TryGetValue((tex, 1), out var vb)) continue;
                var a = va.OrderByDescending(kv => kv.Value).First();
                var b = vb.OrderByDescending(kv => kv.Value).First();
                // trust a set only when one plain texture clearly wins on each side
                if (a.Key == b.Key || a.Value < 20 || b.Value < 20 || a.Value < va.Values.Sum() * 0.6 || b.Value < vb.Values.Sum() * 0.6) continue;
                var s = new Set { Texture = tex, A = a.Key, B = b.Key, VotesA = a.Value, VotesB = b.Value };
                t.Sets[tex] = s;
                // two sets for one pair: keep the better attested
                if (!t.byPair.TryGetValue((s.A, s.B), out var had) || had.VotesA + had.VotesB < s.VotesA + s.VotesB) t.byPair[(s.A, s.B)] = s;
            }
            return t;
        }
    }
}
