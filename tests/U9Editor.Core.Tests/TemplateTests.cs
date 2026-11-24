using System;
using System.IO;
using System.Linq;
using System.Reflection;
using U9.Formats.Objects;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class TemplateTests
    {
        [Fact]
        public void ATypeBritanniaLacksTakesItsFlagsFromAnotherMap()
        {
            var open = typeof(EditorTests).GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static);
            var b = open.Invoke(null, null);
            using var keep = (IDisposable)b;
            var ed = (MapEditor)b.GetType().GetField("Editor").GetValue(b);
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            ed.Templates = new ObjectTemplates(g.Root);

            // a fixed type with nonzero flags that Moonglow has and Britannia does not
            var moonglow = ObjectMap.Load(Path.Combine(g.StaticFolder, "fixed.4"), ObjectFileKind.Fixed);
            var onBritannia = ed.Fixed.Objects.Concat(ed.Nonfixed.Objects).Select(o => o.Type).ToHashSet();
            var other = moonglow.Objects.FirstOrDefault(o => !onBritannia.Contains(o.Type) && o.Flags != 0);
            Assert.SkipWhen(other == null, "every Moonglow type with flags is on Britannia too");

            var template = ed.Templates.Find(ObjectFileKind.Fixed, other.Type);
            Assert.NotNull(template);
            Assert.Equal(ObjectFileKind.Fixed, ed.KindFor(other.Type));
            var placed = ed.Place(ObjectFileKind.Fixed, other.Type, 500 * 128, 550 * 128, ed.GroundZ(500 * 128, 550 * 128));
            Assert.Equal(other.Type, placed.Source.Type);
            Assert.Equal(BitConverter.ToUInt16(template, 0x14), (ushort)placed.Source.Flags);
            Assert.NotEqual(0u, placed.Source.Flags);
            ed.Undo();
            Assert.False(ed.HasChanges);
        }
    }
}
