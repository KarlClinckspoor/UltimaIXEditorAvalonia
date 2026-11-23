using System;
using System.IO;
using System.Linq;
using U9.Formats.Flx;
using Xunit;

namespace U9.Formats.Tests
{
    public class FlxTests
    {
        static bool IsFlx(string name)
        {
            string n = name.ToLowerInvariant();
            return n.EndsWith(".flx") || n.StartsWith("texture8.") || n.StartsWith("texture16.");
        }

        public static TheoryData<string> AllArchives()
        {
            var d = new TheoryData<string>();
            foreach (string sub in new[] { "static", "runtime", "sound" })
                foreach (string f in GameData.Files(sub, IsFlx)) d.Add(f);
            return d;
        }

        [Theory]
        [MemberData(nameof(AllArchives))]
        public void PreserveRoundTripIsByteIdentical(string relative)
        {
            string path = GameData.PathOf(relative);
            using var a = FlxArchive.Open(path);
            using var h = new HashingStream();
            a.Save(h, FlxLayout.Preserve);
            Assert.Equal(new FileInfo(path).Length, h.Length);
            Assert.Equal(GameData.Sha256(path), h.Finish());
        }

        [Theory]
        [MemberData(nameof(AllArchives))]
        public void CompactRoundTripKeepsEveryRecord(string relative)
        {
            string path = GameData.PathOf(relative);
            Assert.SkipWhen(new FileInfo(path).Length > 64 << 20, "large archive: compact layout covered by the smaller ones");
            using var a = FlxArchive.Open(path);
            var ms = new MemoryStream();
            a.Save(ms, FlxLayout.Compact);
            ms.Position = 0;
            using var b = FlxArchive.Open(ms);
            Assert.Equal(a.Count, b.Count);
            Assert.Equal(ms.Length, HeaderSizeOf(a) + a.UsedIndices().Sum(i => (long)a.LengthOf(i)));
            for (int i = 0; i < a.Count; i++)
            {
                Assert.Equal(a.Has(i), b.Has(i));
                if (a.Has(i)) Assert.Equal(a.Read(i), b.Read(i));
            }
            Assert.Equal(a.Comment, b.Comment);
            Assert.Equal(a.Version, b.Version);
            Assert.Equal(a.Reserved60, b.Reserved60);
        }

        static long HeaderSizeOf(FlxArchive a) => FlxArchive.HeaderSize + (long)a.Count * FlxArchive.DirectoryEntrySize;

        [Fact]
        public void PreserveEditMovesOnlyWhatGrew()
        {
            string path = GameData.PathOf(Path.Combine("static", "treedat.flx"));
            using var a = FlxArchive.Open(path);
            var used = a.UsedIndices().ToList();
            int grow = used[0], shrink = used[1], add = Enumerable.Range(0, a.Count).First(i => !a.Has(i));

            var bigger = new byte[a.LengthOf(grow) + 100];
            new Random(1).NextBytes(bigger);
            var smaller = a.Read(shrink).Take(4).ToArray();
            var added = new byte[] { 1, 2, 3 };
            a.Write(grow, bigger);
            a.Write(shrink, smaller);
            a.Write(add, added);

            var ms = new MemoryStream();
            a.Save(ms, FlxLayout.Preserve);
            byte[] original = File.ReadAllBytes(path);
            Assert.Equal(original.Length + bigger.Length + added.Length, ms.Length);

            ms.Position = 0;
            using var b = FlxArchive.Open(new MemoryStream(ms.ToArray()));
            Assert.Equal(bigger, b.Read(grow));
            Assert.Equal(smaller, b.Read(shrink));
            Assert.Equal(added, b.Read(add));
            using var orig = FlxArchive.Open(path);
            foreach (int i in used.Skip(2)) Assert.Equal(orig.Read(i), b.Read(i));
        }

        [Fact]
        public void NewArchiveRoundTrips()
        {
            using var a = new FlxArchive();
            a.Resize(10);
            a.Write(3, new byte[] { 9, 8, 7 });
            a.Write(7, new byte[] { 1 });
            var ms = new MemoryStream();
            a.Save(ms);
            byte[] bytes = ms.ToArray();
            Assert.Equal(FlxArchive.HeaderSize + 80 + 4, bytes.Length);
            Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 0x58));
            using var b = FlxArchive.Open(new MemoryStream(bytes));
            Assert.Equal(10, b.Count);
            Assert.Equal(new[] { 3, 7 }, b.UsedIndices().ToArray());
            Assert.Equal(new byte[] { 9, 8, 7 }, b.Read(3));
            Assert.Null(b.Read(0));
        }
    }
}
