using System;
using System.IO;
using System.Linq;
using U9.Formats.Objects;
using U9.Formats.Triggers;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class MapLinkTests
    {
        static (GameFolder g, MapLinks links) Open()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            var scripts = TriggerScripts.Load(Path.Combine(g.StaticFolder, "TRIGGERS.FLX"));
            var links = new MapLinks(scripts, n =>
            {
                string f = Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), $"nonfixed.{n}",
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
                return f == null ? null : ObjectMap.Load(f, ObjectFileKind.Nonfixed);
            });
            return (g, links);
        }

        static MapLink Near(System.Collections.Generic.List<MapLink> list, double x, double row, int dest) =>
            Assert.Single(list, l => l.DestinationMap == dest && Math.Abs(l.Zone.PointX - x) < 1 && Math.Abs(l.Zone.PointRow - row) < 1);

        [Fact]
        public void CaveBetweenBuccaneersDenAndNewMagincia()
        {
            var (_, links) = Open();
            var fromBritannia = links.From(9);
            var west = Near(fromBritannia, 572.99, 262.44, 89);
            Assert.InRange(west.Arrival.PointX, 60.9, 61.9);
            Assert.InRange(west.Arrival.PointRow, 63.9, 64.9);
            var east = Near(fromBritannia, 629.01, 248.90, 89);
            Assert.InRange(east.Arrival.PointX, 117.1, 118.1);
            Assert.InRange(east.Arrival.PointRow, 80.3, 81.3);

            var fromCave = links.From(89);
            var backWest = Near(fromCave, 61.48, 61.55, 9);
            Assert.InRange(backWest.Arrival.PointX, 572.5, 573.5);
            Assert.InRange(backWest.Arrival.PointRow, 260.2, 261.2);
            var backEast = Near(fromCave, 117.60, 81.88, 9);
            Assert.InRange(backEast.Arrival.PointX, 628.3, 629.3);
            Assert.InRange(backEast.Arrival.PointRow, 250.0, 251.0);
        }

        [Fact]
        public void CovetousEntranceAndReturn()
        {
            var (_, links) = Open();
            var inLink = Near(links.From(9), 704.27, 635.41, 16);
            Assert.InRange(inLink.Arrival.PointX, 37.9, 38.9);
            var outLink = Near(links.From(16), 37.2, 17.25, 9);
            Assert.InRange(outLink.Arrival.PointX, 703.0, 704.0);
        }

        [Fact]
        public void MostMapChangesResolve()
        {
            var (g, links) = Open();
            int total = 0, resolved = 0;
            foreach (var m in g.Maps())
                foreach (var l in links.From(m.Number)) { total++; if (l.Resolved) resolved++; }
            Assert.InRange(total, 640, 680);
            Assert.True(resolved >= 600, $"{resolved} of {total} resolved");
        }
    }
}
