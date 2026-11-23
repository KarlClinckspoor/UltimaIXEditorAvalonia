using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Triggers;

namespace U9Editor.Core
{
    /// <summary>Where a map change lands: an object in the destination map.</summary>
    public sealed class LinkArrival
    {
        public double PointX { get; init; }
        public double PointRow { get; init; }
        public double TerrainHeight { get; init; }
        public int Type { get; init; }
    }

    /// <summary>One map change: a trigger zone object and where its trigger sends the player.</summary>
    public sealed class MapLink
    {
        public int SourceMap { get; init; }
        public MapObject Zone { get; init; }
        public int TriggerSlot { get; init; }
        public string Ops { get; init; }
        public int DestinationMap { get; init; }
        public int DestinationLink { get; init; }
        /// <summary>Arrival marker type, 0 = any.</summary>
        public int MarkerType { get; init; }
        /// <summary>High byte of the change-map op's B (meaning unknown).</summary>
        public int Flags { get; init; }
        public List<LinkArrival> Arrivals { get; init; } = new List<LinkArrival>();
        public bool Resolved => Arrivals.Count > 0;
        public LinkArrival Arrival => Arrivals.FirstOrDefault();
    }

    /// <summary>
    /// Finds the map changes on a map. A map change is a nonfixed object (usually type 1111, invisible) whose
    /// property segment names a TRIGGERS.FLX script; a change-map op (0x1F) in that script sends the player to the
    /// object in the destination map with a matching link (usually a type 1134 marker). The destination link is
    /// the B of the last set-link op (0x11) before it, plus that op's offset; without one, the zone's own link plus
    /// the change-map op's offset. Resolves 612 of the 659 map changes in the shipped maps.
    /// </summary>
    public sealed class MapLinks
    {
        readonly TriggerScripts scripts;
        readonly Func<int, ObjectMap> loadNonfixed;
        readonly Dictionary<int, ObjectMap> cache = new Dictionary<int, ObjectMap>();

        /// <param name="loadNonfixed">Loads runtime/nonfixed.N, null when the map has none.</param>
        public MapLinks(TriggerScripts scripts, Func<int, ObjectMap> loadNonfixed)
        {
            this.scripts = scripts;
            this.loadNonfixed = loadNonfixed;
        }

        ObjectMap Nonfixed(int map)
        {
            if (!cache.TryGetValue(map, out var m)) cache[map] = m = loadNonfixed(map);
            return m;
        }

        /// <summary>Uses an already loaded nonfixed map instead of reading it again.</summary>
        public void Remember(int map, ObjectMap nonfixed) => cache[map] = nonfixed;

        public List<MapLink> From(int map)
        {
            var list = new List<MapLink>();
            var objects = Nonfixed(map);
            if (objects == null) return list;
            foreach (var zone in objects.Objects)
            {
                var props = zone.Properties;
                if (props == null) continue;
                foreach (int slot in props.TriggerSlots())
                {
                    var ops = scripts[slot];
                    TriggerOp? setLink = null;
                    foreach (var op in ops)
                    {
                        if (op.Command == TriggerOp.SetLink) setLink = op;
                        if (op.Command != TriggerOp.ChangeMap) continue;
                        int dest = op.B & 0xFF;
                        if (dest == 0) dest = map;
                        int link = setLink.HasValue
                            ? (setLink.Value.B + setLink.Value.LinkOffset) & 0xFFFF
                            : (zone.Link + op.LinkOffset) & 0xFFFF;
                        var arrivals = new List<LinkArrival>();
                        var target = Nonfixed(dest);
                        if (target != null)
                            foreach (var t in target.Objects)
                                if (t.Link == link && (op.A == 0 || t.Type == op.A))
                                    arrivals.Add(new LinkArrival { PointX = t.PointX, PointRow = t.PointRow, TerrainHeight = t.TerrainHeight, Type = t.Type });
                        list.Add(new MapLink
                        {
                            SourceMap = map, Zone = zone, TriggerSlot = slot, Ops = string.Join(" ", ops.Select(o => o.ToString())),
                            DestinationMap = dest, DestinationLink = link, MarkerType = op.A, Flags = op.B >> 8, Arrivals = arrivals,
                        });
                    }
                }
            }
            return list;
        }
    }
}
