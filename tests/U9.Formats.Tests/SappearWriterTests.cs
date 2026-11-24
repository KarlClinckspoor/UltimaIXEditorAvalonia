using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using U9.Formats.Flx;
using U9.Formats.Models;
using Xunit;

namespace U9.Formats.Tests
{
    public class SappearWriterTests
    {
        static string Key(ModelTriangle t) =>
            $"{t.Texture} {t.Frame} {t.Flags:X} {t.Colour:X} " + string.Join(" ", new[] { t.P0, t.P1, t.P2 }.Select(p => $"{p.X:0.00},{p.Y:0.00},{p.Z:0.00}")) +
            " " + string.Join(" ", new[] { t.T0, t.T1, t.T2 }.Select(p => $"{p.X:0.0000},{p.Y:0.0000}")) +
            " " + string.Join(" ", new[] { t.N0, t.N1, t.N2 }.Select(p => $"{p.X:0.000},{p.Y:0.000},{p.Z:0.000}"));

        static float F(byte[] r, int o) => BinaryPrimitives.ReadSingleLittleEndian(r.AsSpan(o));
        static int U32(byte[] r, int o) => BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(o));

        [Fact]
        public void RebuiltModelsReadBackTheSameAndTheirBoundsMatchTheGames()
        {
            using var sap = FlxArchive.Open(GameData.PathOf(Path.Combine("static", "sappear.flx")));
            int rebuilt = 0, boundsChecked = 0;
            var mismatched = new System.Collections.Generic.List<string>();
            foreach (int i in sap.UsedIndices().Where(i => i % 5 == 0))
            {
                var r = sap.Read(i);
                if (!SappearModel.IsModel(r) || U32(r, 0) != 1) continue;
                var tris = SappearModel.Triangles(r);
                if (tris.Count == 0) continue;
                var built = SappearWriter.Build(tris, r);
                Assert.True(SappearModel.IsModel(built));
                Assert.Equal(1, SappearModel.LodCount(built));
                var back = SappearModel.Triangles(built);
                Assert.Equal(tris.Select(Key).OrderBy(k => k, StringComparer.Ordinal), back.Select(Key).OrderBy(k => k, StringComparer.Ordinal));
                rebuilt++;
                // one LOD, no mount points: the header's box, centre, height and radius are the game's own
                int L = U32(r, 0x94);
                if (U32(r, 4) == 1 && U32(r, L + 0x40) == 0 && U32(r, L + 0x48) == 0)
                {
                    // some models keep vertices no face uses, which a rebuild cannot know about
                    var off = new[] { 0x08, 0x0C, 0x10, 0x14, 0x18, 0x30, 0x34, 0x38, 0x3C, 0x40, 0x44 }
                        .Where(o => Math.Abs(F(r, o) - F(built, o)) >= 0.05f + (o == 0x18 ? 0.01f : 0.001f) * Math.Abs(F(r, o))).ToList();
                    if (off.Count > 0) mismatched.Add($"model {i} +0x{off[0]:X2}: game {F(r, off[0])}, built {F(built, off[0])}");
                    boundsChecked++;
                }
            }
            Assert.True(rebuilt > 400, $"{rebuilt} rebuilt");
            Assert.True(boundsChecked > 200, $"{boundsChecked} bounds checked");
            Assert.True(mismatched.Count <= boundsChecked / 20, $"{mismatched.Count} of {boundsChecked}: " + string.Join("; ", mismatched.Take(8)));
        }

        [Fact]
        public void FacePlanesAndListsAreAsTheGameKeepsThem()
        {
            using var sap = FlxArchive.Open(GameData.PathOf(Path.Combine("static", "sappear.flx")));
            var r = sap.Read(0);      // a 320-unit cube
            var built = SappearWriter.Build(SappearModel.Triangles(r), r);
            int L = U32(built, 0x94), B = L + 4, faces = U32(built, L + 0x3C), fo = U32(built, L + 0x54);
            Assert.Equal(12, faces);
            for (int i = 0; i < faces; i++)
            {
                int f = B + fo + SappearWriter.FaceSize * i;
                var n = new System.Numerics.Vector3(F(built, f + 0x5C), F(built, f + 0x60), F(built, f + 0x64));
                Assert.Equal(1f, n.Length(), 3);
                Assert.Equal(-160f, F(built, f + 0x68), 2);      // every face of the cube is 160 from its middle
            }
            for (int k = 0; k < 4; k++)
            {
                int at = B + U32(built, L + 0x54 + 4 * (5 + k));
                Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(at)));
                var order = Enumerable.Range(1, faces).Select(j => (int)BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(at + 2 * j))).ToList();
                Assert.Equal(Enumerable.Range(0, faces), order.OrderBy(x => x));
            }
            Assert.Equal(U32(built, 0) == 1 ? U32(built, L) + L + 4 : 0, built.Length);
        }
    }
}
