using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    public enum PrefabGround { None, Heights, HeightsAndTextures }

    /// <summary>How a prefab lands.</summary>
    public sealed class PasteOptions
    {
        public PrefabGround Ground = PrefabGround.HeightsAndTextures;
        /// <summary>Raise or lower the pasted ground as a whole so its edge meets the ground here (off: the heights as copied).</summary>
        public bool FitHeights = true;
        /// <summary>Over the prefab's margin the pasted heights fade into the ground here, and textures stop halfway across it.</summary>
        public bool BlendEdge = true;
        /// <summary>Move the objects' links, all by one amount, to numbers this map does not use (see <see cref="MapEditor.LinkShiftFor"/>).</summary>
        public bool NewLinks = true;
        /// <summary>Give each script the objects run a new slot (a copy), so editing one leaves the original alone.</summary>
        public bool CopyScripts = true;
        /// <summary>Delete the objects already standing inside the prefab's area (within its margin).</summary>
        public bool ClearArea;
        /// <summary>Without pasted ground: each object keeps its own height above the ground (off: all move by one height, as a building's parts must).</summary>
        public bool ObjectsFollowGround = true;
    }

    public sealed class PasteResult
    {
        public List<SceneObject> Objects = new List<SceneObject>();
        /// <summary>Objects left out: outside the map, or of a file this map has not.</summary>
        public int Skipped;
        public int Cleared;
        public int LinkShift;
        public Dictionary<int, int> Slots = new Dictionary<int, int>();
        /// <summary>The ground's height change (point heights) when fitted.</summary>
        public int HeightOffset;
        public bool GroundPasted;
        /// <summary>Textures the pasted ground uses (texture, frame), so a view can load any it lacks.</summary>
        public HashSet<(int Texture, int Frame)> Textures = new HashSet<(int, int)>();
        public List<string> Notes = new List<string>();
    }

    public sealed partial class MapEditor
    {
        /// <summary>
        /// Pastes a prefab with its first point at (x, row): ground, objects (each a whole tree: properties, chains and
        /// contents) and copies of their scripts, as one undo step. Objects of the map already on the pasted ground go
        /// up and down with it, as with a terrain stroke.
        /// </summary>
        public PasteResult PastePrefab(Prefab p, int x, int row, PasteOptions opt)
        {
            if (InStroke) EndStroke();
            opt ??= new PasteOptions();
            var res = new PasteResult();
            int W = p.Width, H = p.Height, rw = Raster.Width, rh = Raster.Height;
            if (x + W <= 0 || row + H <= 0 || x >= rw || row >= rh) throw new InvalidOperationException("The prefab would land outside the map.");

            bool ground = p.HasGround && opt.Ground != PrefabGround.None && Terrain != null;
            if (p.HasGround && opt.Ground != PrefabGround.None && Terrain == null) res.Notes.Add("this map's terrain cannot be edited, so the ground was left out");
            var change = new TerrainChange();
            if (ground) PasteGround(p, x, row, opt, change, res);

            bool In(int i, int j) => x + i >= 0 && row + j >= 0 && x + i < rw && row + j < rh;
            pendingScripts = new List<(int, TriggerOp[], TriggerOp[])>();
            var maps = new[] { Fixed, Nonfixed }.Where(m => m != null).ToList();
            foreach (var m in maps) m.BeginEdit("paste prefab");
            var added = new List<MapObject>();
            try
            {
                int ux0 = (x + p.Margin) * 128, uy0 = (row + p.Margin) * 128, ux1 = (x + W - 1 - p.Margin) * 128, uy1 = (row + H - 1 - p.Margin) * 128;
                if (opt.ClearArea)
                    foreach (var m in maps)
                        foreach (var o in m.Objects.Where(o => !o.Removed && o.MapX >= ux0 && o.MapX <= ux1 && o.MapY >= uy0 && o.MapY <= uy1).ToList())
                        { m.Remove(o); res.Cleared++; }
                if (!change.IsEmpty)
                    foreach (var m in maps)
                        foreach (var (o, z) in FollowMoves(change, new TerrainBrush(), m)) o.MoveTo(o.MapX, o.MapY, z);

                // links: the prefab's ordinary links move up together above the map's highest
                var links = p.Objects.Where(o => o.Kind == ObjectFileKind.Nonfixed).Select(o => o.Tree.Link).Where(l => l > 0 && l < 0xF000).ToHashSet();
                int shift = 0;
                if (opt.NewLinks && links.Count > 0 && Nonfixed != null) shift = LinkShiftFor(links);
                res.LinkShift = shift;

                // scripts: a copy each (or the slot shared when it still holds what was copied)
                var slotMap = new Dictionary<int, int>();
                if (Scripts != null)
                    foreach (var kv in p.Scripts.OrderBy(k => k.Key))
                    {
                        if (!p.Objects.Any(o => Prefab.SlotsOf(o.Tree).Any(s => s.Slot == kv.Key))) continue;
                        bool same = Scripts.IsUsed(kv.Key) && Scripts[kv.Key].SequenceEqual(kv.Value);
                        if (!opt.CopyScripts && same) continue;
                        var ops = RelinkScript(kv.Value, links, shift);
                        int slot = Scripts.FreeSlot();
                        SetScript(slot, ops);
                        slotMap[kv.Key] = slot;
                        if (!opt.CopyScripts) res.Notes.Add($"script {kv.Key} has changed since the prefab was copied: the paste uses a copy of the old one in slot {slot}");
                    }
                res.Slots = slotMap;
                if (Scripts == null && p.Scripts.Count > 0) res.Notes.Add("there is no TRIGGERS.FLX, so the objects' scripts were not copied");

                int dz = 0;
                if (!ground && !opt.ObjectsFollowGround && p.Objects.Count > 0)
                {
                    // one height change for all: the ground's mean change under them
                    var inside = p.Objects.Where(o => In(o.X / 128, o.Y / 128)).ToList();
                    if (inside.Count > 0) dz = (int)Math.Round(inside.Average(o => GroundZ(x * 128 + o.X, row * 128 + o.Y) - o.GroundZ));
                }
                foreach (var po in p.Objects)
                {
                    var map = po.Kind == ObjectFileKind.Fixed ? Fixed : Nonfixed;
                    int mx = x * 128 + po.X, my = row * 128 + po.Y;
                    if (map == null || mx < 0 || my < 0 || mx >= map.Width * ObjectMap.ChunkUnits || my >= map.Height * ObjectMap.ChunkUnits ||
                        mx >= rw * 128 || my >= rh * 128) { res.Skipped++; continue; }
                    var t = po.Tree.Clone();
                    if (po.Kind == ObjectFileKind.Nonfixed && shift != 0 && links.Contains(t.Link)) t.Link += shift;
                    foreach (var s in Prefab.SlotsOf(t).ToList())
                        if (slotMap.TryGetValue(s.Slot, out int ns)) t.SetParam(s.Segment, s.Place, s.Which, ns);
                    int z = ground || opt.ObjectsFollowGround ? po.Z - po.GroundZ + GroundZ(mx, my) : po.Z + dz;
                    added.Add(map.AddTree(t, mx, my, Math.Clamp(z, 0, 0xFFFF)));
                }
            }
            catch
            {
                foreach (var m in maps) { var partial = m.EndEdit(); if (partial != null) m.Undo(partial); }
                foreach (var sc in Enumerable.Reverse(pendingScripts)) Scripts.Set(sc.Slot, sc.Before);
                pendingScripts = null;
                if (!change.IsEmpty) ApplyTerrain(change, after: false);
                throw;
            }
            var edits = new List<(ObjectMap, ObjectEdit)>();
            foreach (var m in maps) { var e = m.EndEdit(); if (e != null) edits.Add((m, e)); }
            var scripts = pendingScripts;
            pendingScripts = null;
            if (edits.Count == 0 && change.IsEmpty && scripts.Count == 0) return res;
            undo.Add(new Step
            {
                Description = $"paste {Count(added.Count)}", Terrain = change.IsEmpty ? null : change,
                Follow = edits.Count > 0 ? edits : null, Scripts = scripts.Count > 0 ? scripts : null,
            });
            redo.Clear();
            if (!change.IsEmpty) terrainVersion++;
            if (scripts.Count > 0) { scriptVersion++; RaiseScriptsEdited(); }
            foreach (var (m, e) in edits) { Bump(m, +1); Publish(e); }
            res.Objects = added.Select(o => Objects.Of(o)).Where(o => o != null).ToList();
            return res;
        }

        /// <summary>
        /// How far to move a prefab's links so that none of them, nor 15 either side (the reach of a script's link
        /// offset), meets a link this map's nonfixed objects use: 0 when they are free as they are (a paste onto
        /// another map often is), else the smallest shift up that finds them room.
        /// </summary>
        public int LinkShiftFor(ICollection<int> links)
        {
            if (links.Count == 0 || Nonfixed == null) return 0;
            const int reach = 15, top = 0xF000;
            var blocked = new bool[top + reach + 1];
            foreach (var o in Nonfixed.Objects)
            {
                int l = o.Link;
                if (l <= 0 || l >= top) continue;
                for (int k = Math.Max(0, l - reach); k <= l + reach; k++) blocked[k] = true;
            }
            int lo = links.Min(), hi = links.Max();
            for (int s = 0; hi + s < top; s++)
                if (links.All(l => !blocked[l + s])) return s;
            return FreeLink() - lo;
        }

        void PasteGround(Prefab p, int x, int row, PasteOptions opt, TerrainChange change, PasteResult res)
        {
            int W = p.Width, H = p.Height, rw = Raster.Width, rh = Raster.Height;
            bool In(int i, int j) => x + i >= 0 && row + j >= 0 && x + i < rw && row + j < rh;
            int offset = 0;
            if (opt.FitHeights)
            {
                // the patch's outer ring against the ground it lands on
                double here = 0, there = 0; int n = 0;
                for (int j = 0; j < H; j++)
                    for (int i = 0; i < W; i++)
                    {
                        if (i != 0 && j != 0 && i != W - 1 && j != H - 1) continue;
                        if (!In(i, j)) continue;
                        here += Raster.Points[x + i + (row + j) * rw] & 0xFFF;
                        there += p.Heights[i + j * W];
                        n++;
                    }
                if (n > 0) offset = (int)Math.Round((here - there) / n);
            }
            res.HeightOffset = offset;
            float Weight(int i, int j)
            {
                if (!opt.BlendEdge || p.Margin <= 0) return 1;
                int d = Math.Min(Math.Min(i, j), Math.Min(W - 1 - i, H - 1 - j));
                float t = Math.Clamp(d / (float)p.Margin, 0, 1);
                return t * t * (3 - 2 * t);
            }
            bool textures = opt.Ground == PrefabGround.HeightsAndTextures;
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    if (!In(i, j)) continue;
                    int at = x + i + (row + j) * rw;
                    uint before = Raster.Points[at];
                    float w = Weight(i, j);
                    int here = (int)(before & 0xFFF), pasted = Math.Clamp(p.Heights[i + j * W] + offset, 0, TerrainPoint.MaxHeight);
                    int hgt = Math.Clamp((int)MathF.Round(here + (pasted - here) * w), 0, TerrainPoint.MaxHeight);
                    uint after = (before & ~0xFFFu) | (uint)hgt;
                    // a quad takes the prefab's texture where all four corners are at least halfway into the prefab
                    if (textures && i < W - 1 && j < H - 1 &&
                        (!opt.BlendEdge || Math.Min(Math.Min(Weight(i, j), Weight(i + 1, j)), Math.Min(Weight(i, j + 1), Weight(i + 1, j + 1))) >= 0.5f))
                    {
                        uint q = p.Quads[i + j * (W - 1)];
                        after = q | (uint)hgt;
                        res.Textures.Add(((int)(q >> 22), (int)((q >> 16) & 0x3F)));
                    }
                    if (after == before) continue;
                    change.Points[at] = (before, after);
                    change.Grow(x + i, row + j);
                    Terrain.SetPoint(x + i, row + j, new TerrainPoint(after));
                }
            if (change.IsEmpty) return;
            res.GroundPasted = true;
            Raster.Refresh(Terrain, change.X0, change.Y0, change.X1 - change.X0 + 1, change.Y1 - change.Y0 + 1);
            TerrainEdited?.Invoke(change.X0, change.Y0, change.X1 - change.X0 + 1, change.Y1 - change.Y0 + 1);
        }

        /// <summary>
        /// A copy of a script with its absolute links (op 0x11) into the prefab moved by the links' shift. A link set just
        /// before a change of map names the arrival on the other map and stays.
        /// </summary>
        public static TriggerOp[] RelinkScript(TriggerOp[] ops, ICollection<int> links, int shift)
        {
            var r = (TriggerOp[])ops.Clone();
            if (shift == 0) return r;
            for (int i = 0; i < r.Length; i++)
            {
                if (r[i].Command != TriggerOp.SetLink || !links.Contains(r[i].B)) continue;
                bool toArrival = false;
                for (int k = i + 1; k < r.Length; k++)
                {
                    int c = r[k].Command;
                    if (c == TriggerOp.ChangeMap) { toArrival = true; break; }
                    if (c == TriggerOp.SetLink || c == 0x12 || c == 0x57 || c == TriggerOp.End) break;
                }
                if (!toArrival) r[i] = r[i].WithB(r[i].B + shift);
            }
            return r;
        }
    }
}
