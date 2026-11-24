using System;
using System.IO;
using System.Linq;
using System.Reflection;
using U9.Formats.Objects;
using U9.Formats.Triggers;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class PlaceAndLinkTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        static MapLink Resolve(MapEditor ed, MapObject zone) =>
            new MapLinks(ed.Scripts, n => n == 9 ? ed.Nonfixed : null).From(9).Single(l => l.Zone == zone);

        [Fact]
        public void PlacingAThroneDrawsItsModel()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                Assert.Equal(ObjectFileKind.Nonfixed, ed.KindFor(2894));        // the shipped throne is nonfixed
                var o = ed.Place(ObjectFileKind.Nonfixed, 2894, 500 * 128, 550 * 128, ed.GroundZ(500 * 128, 550 * 128));
                Assert.Equal(2894, o.Source.Type);
                Assert.Equal(1793, o.Model);                                  // the throne model, as TYPES.DAT says
                Assert.True(ed.Models.SlotOf(o.Id) >= 0);
                ed.Undo();
                Assert.True(o.Source.Removed);
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void RetargetingTheCaveSendsItToANewArrivalAndUndoPutsItBack()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var zone = ed.Nonfixed.Objects.Single(o => o.Type == 1111 && Math.Abs(o.PointX - 573.0) < 0.3 && Math.Abs(o.PointRow - 262.4) < 0.3);
                var link = Resolve(ed, zone);
                Assert.Equal(89, link.DestinationMap);
                int oldSlot = link.TriggerSlot;

                int newLink = ed.FreeLink();
                var arrival = ed.NewArrival(newLink, 510 * 128, 560 * 128, ed.GroundZ(510 * 128, 560 * 128));
                ed.RetargetLink(link, 9, newLink, 1134);

                var now = Resolve(ed, zone);
                Assert.NotEqual(oldSlot, now.TriggerSlot);                     // a new slot: other zones keep the old script
                Assert.Equal((9, newLink), (now.DestinationMap, now.DestinationLink));
                Assert.Equal(arrival.Source, now.Arrival.Object);
                Assert.True(ed.ScriptsChanged);

                ed.Undo();
                ed.Undo();
                var back = Resolve(ed, zone);
                Assert.Equal((oldSlot, 89), (back.TriggerSlot, back.DestinationMap));
                Assert.False(ed.Scripts.IsUsed(now.TriggerSlot));
                Assert.False(ed.HasChanges);
            }
        }

        [Fact]
        public void ANewLinkCopiedFromTheCaveLandsOnItsArrival()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var cave = ed.Nonfixed.Objects.Single(o => o.Type == 1111 && Math.Abs(o.PointX - 573.0) < 0.3 && Math.Abs(o.PointRow - 262.4) < 0.3);
                var seg = ed.Nonfixed.Data.AsSpan(ed.Nonfixed.DataStart + cave.PropertyOffset, 16).ToArray();
                int newLink = ed.FreeLink();
                var arrival = ed.NewArrival(newLink, 505 * 128, 548 * 128, ed.GroundZ(505 * 128, 548 * 128));
                var zone = ed.NewLink(cave.Raw, seg, 520 * 128, 540 * 128, ed.GroundZ(520 * 128, 540 * 128), 9, newLink);

                var l = Resolve(ed, zone.Source);
                Assert.Equal(arrival.Source, l.Arrival.Object);
                Assert.Equal(cave.Properties.Params[0], zone.Source.Properties.Params[0]);   // same trigger area
            }
        }
    }
}
