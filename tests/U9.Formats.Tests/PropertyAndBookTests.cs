using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Books;
using U9.Formats.Objects;
using Xunit;

namespace U9.Formats.Tests
{
    public class PropertyAndBookTests
    {
        static string BooksPath() => BookTexts.FilesIn(Path.Combine(GameData.Require(), "static")).First();

        static IEnumerable<ObjectMap> AllNonfixed()
        {
            foreach (var f in GameData.Files("runtime", n => n.StartsWith("nonfixed.", StringComparison.OrdinalIgnoreCase) && int.TryParse(Path.GetExtension(n).TrimStart('.'), out _)))
                yield return ObjectMap.Load(GameData.PathOf(f), ObjectFileKind.Nonfixed);
        }

        [Fact]
        public void BookRecordsReadAndEncodeBackToTheSameBytes()
        {
            var books = BookTexts.Load(BooksPath());
            using var a = U9.Formats.Flx.FlxArchive.Open(BooksPath());
            int read = 0;
            foreach (int n in books.Numbers)
            {
                var b = books.Get(n);
                Assert.NotNull(b);
                Assert.StartsWith($"{n}:", b.Value.Title);
                var raw = a.Read(n - 1);
                var again = BookTexts.Encode(b.Value.Title, b.Value.Text);
                Assert.Equal(raw, again);
                read++;
            }
            Assert.Equal(460, read);
            Assert.Equal("1: History of Britannia", books.Get(1).Value.Title);
            Assert.StartsWith("`f2The First Age of Darkness", books.Get(1).Value.Text);
        }

        [Fact]
        public void EveryBookNumberOnTheMapsHasARecord()
        {
            var books = BookTexts.Load(BooksPath());
            int uses = 0;
            var missing = new List<string>();
            foreach (var m in AllNonfixed())
                foreach (var o in m.Objects)
                    foreach (var p in m.PropertiesOf(o).Where(p => p.Id == PropertyNames.BookNumber))
                    {
                        uses++;
                        if (!books.Has(p.A)) missing.Add($"type {o.Type} book {p.A}");
                    }
            Assert.True(uses > 700, $"{uses} book numbers");
            Assert.True(missing.Count <= uses / 50, string.Join(", ", missing.Take(20)));
        }

        [Fact]
        public void ChangedBooksSaveAndTheRestStays()
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"books-{Guid.NewGuid():N}.flx");
            try
            {
                var books = BookTexts.Load(BooksPath());
                books.Set(6, "6: Banner of Compassion", "A new text\r\nwith two lines");
                int fresh = books.FreeNumber();
                books.Set(fresh, $"{fresh}: My book", "Hello, Britannia.");
                books.Save(tmp);
                var back = BookTexts.Load(tmp);
                Assert.Equal("A new text\r\nwith two lines", back.Get(6).Value.Text);
                Assert.Equal("Hello, Britannia.", back.Get(fresh).Value.Text);
                var orig = BookTexts.Load(BooksPath());
                foreach (int n in orig.Numbers.Where(n => n != 6)) Assert.Equal(orig.Get(n), back.Get(n));
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void PropertiesHaveTheGamesNamesAndChainsAreFollowed()
        {
            var m = ObjectMap.Load(GameData.PathOf(Path.Combine("runtime", "nonfixed.9")), ObjectFileKind.Nonfixed);
            var all = m.Objects.Select(o => (o, p: m.PropertiesOf(o))).Where(x => x.p.Count > 0).ToList();
            Assert.Contains(all, x => x.p.Any(p => p.Name == "Book Number"));
            Assert.Contains(all, x => x.p.Any(p => p.Name == "Light Range"));
            Assert.DoesNotContain(all, x => x.p.Any(p => p.Id == PropertyNames.NextPropertyPointer));
            // monster generators keep their spawn settings in a second segment
            var chained = all.Where(x => x.o.Properties.Ids.Contains(PropertyNames.NextPropertyPointer)).ToList();
            Assert.NotEmpty(chained);
            Assert.Contains(chained, x => x.p.Any(p => p.Name == "Creature Type") && x.p.Any(p => p.Name == "Spawn Percent"));
            Assert.All(chained, x => Assert.True(x.p.Count >= 2));
        }

        [Fact]
        public void ABagsContentsAreFoundAndTheirPropertiesEdit()
        {
            var bytes = File.ReadAllBytes(GameData.PathOf(Path.Combine("runtime", "nonfixed.9")));
            var m = ObjectMap.Read(bytes, ObjectFileKind.Nonfixed);
            var bags = m.Objects.Where(o => m.PropertiesOf(o).Any(p => p.Id == PropertyNames.Contents)).ToList();
            Assert.True(bags.Count >= 3, $"{bags.Count} containers");
            var withCoins = bags.Select(b => (b, items: m.ContentsOf(b))).First(x => x.items.Any(i => m.PropertiesOf(i).Any(p => p.Id == PropertyNames.Quantity)));
            Assert.All(withCoins.items, i => Assert.DoesNotContain(i, m.Objects));
            var coin = withCoins.items.First(i => m.PropertiesOf(i).Any(p => p.Id == PropertyNames.Quantity));
            var q = m.PropertiesOf(coin).First(p => p.Id == PropertyNames.Quantity);

            m.BeginEdit("quantity");
            m.SetProperty(coin, q, 777, 0);
            var e = m.EndEdit();
            Assert.Equal(777, m.PropertiesOf(coin).First(p => p.Id == PropertyNames.Quantity).A);
            Assert.Equal(withCoins.items.Count, m.ContentsOf(withCoins.b).Count);
            m.Undo(e);
            Assert.Equal(bytes, m.Data);
            Assert.Throws<InvalidOperationException>(() => m.SetProperty(withCoins.b, m.PropertiesOf(withCoins.b).First(p => p.Id == PropertyNames.Contents), 1, 0));
        }
    }
}
