using System;
using System.IO;
using System.Linq;
using System.Reflection;
using U9.Formats.Books;
using U9.Formats.Objects;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class ContentsTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        [Fact]
        public void PropertyEditsAreUndoStepsAlsoInsideAContainer()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var light = ed.Objects.All.First(o => ed.PropertiesOf(o.Source).Any(p => p.Name == "Light Range"));
                var range = ed.PropertiesOf(light.Source).First(p => p.Name == "Light Range");
                ed.SetProperty(light, light.Source, range, range.A + 100, range.B);
                Assert.Equal("set Light Range", ed.UndoDescription);
                Assert.Equal(range.A + 100, ed.PropertiesOf(light.Source).First(p => p.Name == "Light Range").A);
                Assert.True(ed.NonfixedChanged);
                ed.Undo();
                Assert.Equal(range.A, ed.PropertiesOf(light.Source).First(p => p.Name == "Light Range").A);
                Assert.False(ed.HasChanges);

                var bag = ed.Objects.All.First(o => ed.ContentsOf(o.Source).Any(i => ed.PropertiesOf(i).Any(p => p.Id == PropertyNames.Quantity)));
                var coins = ed.ContentsOf(bag.Source).First(i => ed.PropertiesOf(i).Any(p => p.Id == PropertyNames.Quantity));
                var q = ed.PropertiesOf(coins).First(p => p.Id == PropertyNames.Quantity);
                ed.SetProperty(bag, coins, q, 1234, 0);
                Assert.Equal(1234, ed.PropertiesOf(coins).First(p => p.Id == PropertyNames.Quantity).A);
                ed.Undo();
                Assert.Equal(q.A, ed.PropertiesOf(coins).First(p => p.Id == PropertyNames.Quantity).A);
                Assert.Throws<ArgumentOutOfRangeException>(() => ed.SetProperty(bag, coins, q, 70000, 0));
            }
        }

        [Fact]
        public void BookTextsAreUndoStepsAndSaveWithTheMap()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var g = GameFolder.FindNear(AppContext.BaseDirectory);
                string src = BookTexts.FilesIn(g.StaticFolder).First();
                string dir = Path.Combine(Path.GetTempPath(), $"u9books-{Guid.NewGuid():N}");
                Directory.CreateDirectory(dir);
                try
                {
                    string copy = Path.Combine(dir, Path.GetFileName(src));
                    File.Copy(src, copy);
                    ed.Books = BookTexts.Load(copy);
                    int edited = 0;
                    ed.BookEdited += n => edited = n;
                    var before = ed.Books.Get(6).Value;
                    ed.SetBook(6, before.Title, "Changed.");
                    Assert.Equal(6, edited);
                    Assert.True(ed.BooksChanged && ed.HasChanges);
                    Assert.Equal("text of book 6", ed.UndoDescription);
                    ed.Undo();
                    Assert.Equal(before.Text, ed.Books.Get(6).Value.Text);
                    Assert.False(ed.HasChanges);
                    ed.Redo();
                    var written = ed.Save(null, null);
                    Assert.Contains(copy, written);
                    Assert.True(File.Exists(copy + ".orig"));
                    Assert.False(ed.HasChanges);
                    Assert.Equal("Changed.", BookTexts.Load(copy).Get(6).Value.Text);
                }
                finally { Directory.Delete(dir, true); }
            }
        }
    }
}
