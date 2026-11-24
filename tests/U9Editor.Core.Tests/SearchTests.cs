using System;
using System.Linq;
using System.Reflection;
using U9.Formats.Objects;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class SearchTests
    {
        [Fact]
        public void QueriesMatchNamesNumbersIdsAndLinks()
        {
            var telescope = ObjectQuery.Parse("telescope");
            Assert.True(telescope.Matches(5, ObjectFileKind.Nonfixed, 3183, 0, "Telescope"));
            Assert.False(telescope.Matches(5, ObjectFileKind.Nonfixed, 3183, 0, "Orb of the Moons"));
            Assert.True(ObjectQuery.Parse("orb moons").Matches(1, ObjectFileKind.Nonfixed, 5525, 0, "Orb of the Moons"));
            Assert.False(ObjectQuery.Parse("orb sun").Matches(1, ObjectFileKind.Nonfixed, 5525, 0, "Orb of the Moons"));
            Assert.True(ObjectQuery.Parse("2894").Matches(1, ObjectFileKind.Nonfixed, 2894, 0, "Throne"));
            Assert.False(ObjectQuery.Parse("2894").Matches(1, ObjectFileKind.Nonfixed, 2895, 0, "Throne"));
            Assert.True(ObjectQuery.Parse("#12").Matches(12, ObjectFileKind.Fixed, 1, 0, null));
            Assert.False(ObjectQuery.Parse("#12").Matches(13, ObjectFileKind.Fixed, 1, 0, null));
            Assert.True(ObjectQuery.Parse("link:7 nonfixed").Matches(1, ObjectFileKind.Nonfixed, 1134, 7, null));
            Assert.False(ObjectQuery.Parse("link:7").Matches(1, ObjectFileKind.Fixed, 1134, 7, null));
            Assert.True(ObjectQuery.Parse("").IsEmpty);
            Assert.False(ObjectQuery.Parse("  ").Matches(1, ObjectFileKind.Fixed, 1, 0, "x"));
        }

        [Fact]
        public void TheThroneIsFoundOnBritanniaAndTelescopesOnSeveralMaps()
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            using var keep = (IDisposable)b;
            var ed = (MapEditor)b.GetType().GetField("Editor").GetValue(b);
            Assert.Single(ObjectSearch.InMap(ed.Objects, ObjectQuery.Parse("2894")));

            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            var names = TypeNames.Load(System.IO.Directory.EnumerateFiles(g.StaticFolder, "typename.flx", new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive }).First());
            var search = new ObjectSearch(g.Root, names);
            var maps = g.Maps();
            var telescopes = search.AllMaps(ObjectQuery.Parse("telescope"), maps, null);
            Assert.Contains(telescopes, t => t.Map == 9 && Math.Abs(t.X - 719) < 1 && Math.Abs(t.Row - 424) < 1);     // Moonglow's
            Assert.All(telescopes, t => Assert.Contains("telescope", t.Name.ToLowerInvariant()));
            // an open map's own files (with unsaved edits) are searched instead of the game folder's
            var throne = ed.Objects.All.Single(o => o.Source.Type == 2894);
            ed.Move(throne, throne.Source.MapX + 1280, throne.Source.MapY, throne.Source.Z);
            var found = search.AllMaps(ObjectQuery.Parse("2894"), maps, n => n == 9 ? (ed.Fixed, ed.Nonfixed) : null).Single(f => f.Map == 9);
            Assert.Equal(throne.Source.MapX / 128f, found.X, 3);
        }
    }
}
