using System;
using System.Linq;
using System.Reflection;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class TypeAndPropertyEditTests
    {
        static MapEditor Britannia(out IDisposable keep)
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            keep = (IDisposable)b;
            return (MapEditor)b.GetType().GetField("Editor").GetValue(b);
        }

        [Fact]
        public void TypeFieldsAreUndoSteps()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                int throne = 2894, hp = ed.Types[throne].HitPoints;
                int seen = -1;
                ed.TypeEdited += t => seen = t;
                ed.SetTypeField(throne, TypeField.HitPoints, hp == 99 ? 98 : 99);
                Assert.Equal(throne, seen);
                Assert.True(ed.TypesChanged && ed.HasChanges);
                Assert.Equal("HitPoints of type 2894", ed.UndoDescription);
                ed.Undo();
                Assert.Equal(hp, ed.Types[throne].HitPoints);
                Assert.False(ed.HasChanges);
                ed.Redo();
                Assert.NotEqual(hp, ed.Types[throne].HitPoints);
                ed.Undo();
                Assert.Throws<ArgumentOutOfRangeException>(() => ed.SetTypeField(throne, TypeField.Weight, 300));
            }
        }

        [Fact]
        public void AddingAndRemovingAPropertyAreUndoSteps()
        {
            var ed = Britannia(out var keep);
            using (keep)
            {
                var npc = ed.Objects.All.First(o => o.Kind == ObjectFileKind.Nonfixed && ed.PropertiesOf(o.Source).Count == 0);
                var before = ed.Nonfixed.ToBytes();
                ed.AddProperty(npc, npc.Source, 0x76, 20, 40);
                Assert.Equal("add Min/Max Damage", ed.UndoDescription);
                var p = ed.PropertiesOf(npc.Source).Single();
                Assert.Equal((0x76, 20, 40), (p.Id, p.A, p.B));
                ed.RemoveProperty(npc, npc.Source, p);
                Assert.Empty(ed.PropertiesOf(npc.Source));
                ed.Undo(); ed.Undo();
                Assert.Equal(before, ed.Nonfixed.Data);
                Assert.False(ed.HasChanges);
            }
        }
    }
}
