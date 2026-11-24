using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    public class GltfTests
    {
        static (FlxArchive Sap, FlxArchive Bmp) Open()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            return (FlxArchive.Open(Path.Combine(g.StaticFolder, "sappear.flx")), FlxArchive.Open(Path.Combine(g.StaticFolder, "bitmap16.flx")));
        }

        static string Key(ModelTriangle t) =>
            $"{t.Texture} {t.Frame} {t.Flags:X} {(t.Texture < 0 ? t.Colour : 0):X} " +
            string.Join(" ", new[] { t.P0, t.P1, t.P2 }.Select(p => $"{MathF.Round(p.X, 1) + 0f},{MathF.Round(p.Y, 1) + 0f},{MathF.Round(p.Z, 1) + 0f}")) + " " +
            string.Join(" ", new[] { t.T0, t.T1, t.T2 }.Select(p => $"{MathF.Round(p.X, 3) + 0f},{MathF.Round(p.Y, 3) + 0f}"));

        [Fact]
        public void ModelsComeBackFromGlbAsTheyWent()
        {
            var (sap, bmp) = Open();
            using (sap) using (bmp)
            {
                int models = 0;
                foreach (int i in sap.UsedIndices().Where(i => i % 7 == 0).Take(300))
                {
                    var r = sap.Read(i);
                    if (!SappearModel.IsModel(r)) continue;
                    var tris = SappearModel.Triangles(r);
                    if (tris.Count == 0) continue;
                    var glb = GltfModel.Export(tris, null, $"model {i}");
                    var back = GltfModel.Import(glb);
                    Assert.Equal(tris.Count, back.Count);
                    Assert.Equal(tris.Select(Key).OrderBy(k => k, StringComparer.Ordinal), back.Select(Key).OrderBy(k => k, StringComparer.Ordinal));
                    // normals survive too
                    var n0 = tris.Select(t => t.N0).Aggregate(Vector3.Zero, (a, b) => a + b);
                    var n1 = back.Select(t => t.N0).Aggregate(Vector3.Zero, (a, b) => a + b);
                    Assert.True(Vector3.Distance(n0, n1) < 0.01f * tris.Count + 0.01f);
                    models++;
                }
                Assert.True(models > 100, $"{models} models");
            }
        }

        [Fact]
        public void TheGlbIsWellFormedWithTexturesEmbedded()
        {
            var (sap, bmp) = Open();
            using (sap) using (bmp)
            {
                // a textured model
                int index = sap.UsedIndices().First(i => SappearModel.IsModel(sap.Read(i)) && SappearModel.Triangles(sap.Read(i)).Any(t => t.Texture >= 0 && t.Texture < bmp.Count && bmp.Has(t.Texture)));
                var tris = SappearModel.Triangles(sap.Read(index));
                var glb = GltfModel.Export(tris, t => t < bmp.Count && bmp.Has(t) ? bmp.Read(t) : null, $"model {index}");
                Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
                Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
                int jl = BitConverter.ToInt32(glb, 12);
                var root = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jl)).AsObject();
                Assert.Equal("2.0", root["asset"]["version"].GetValue<string>());
                Assert.NotEmpty(root["images"].AsArray());
                Assert.All(root["images"].AsArray(), im => Assert.Equal("image/png", im["mimeType"].GetValue<string>()));
                int bl = BitConverter.ToInt32(glb, 20 + jl);
                Assert.Equal(root["buffers"][0]["byteLength"].GetValue<long>(), bl);
                foreach (var a in root["accessors"].AsArray())
                {
                    var v = root["bufferViews"][a["bufferView"].GetValue<int>()];
                    Assert.True(v["byteOffset"].GetValue<long>() % 4 == 0);
                    Assert.True(v["byteOffset"].GetValue<long>() + v["byteLength"].GetValue<long>() <= bl);
                }
                Assert.All(root["meshes"][0]["primitives"].AsArray(), p => Assert.NotNull(root["accessors"][p["attributes"]["POSITION"].GetValue<int>()]["min"]));
                // the first image is a real PNG
                var img = root["bufferViews"][root["images"][0]["bufferView"].GetValue<int>()];
                int start = 20 + jl + 8 + img["byteOffset"].GetValue<int>();
                Assert.Equal(0x89, glb[start]);
                Assert.Equal((byte)'P', glb[start + 1]);
            }
        }

        [Fact]
        public void ImportFollowsNodeTransformsAndIndices()
        {
            // a hand-made glTF: one triangle, indexed, under a node that moves it one metre up (glTF y)
            var tri = new[] { new ModelTriangle { P0 = new Vector3(0, 0, 0), P1 = new Vector3(128, 0, 0), P2 = new Vector3(0, 128, 0), Texture = -1, Colour = 0xFF0000FF } };
            var glb = GltfModel.Export(tri, null);
            int jl = BitConverter.ToInt32(glb, 12);
            var root = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jl)).AsObject();
            root["nodes"][0]["translation"] = new JsonArray(0f, 1f, 0f);
            var json = Encoding.UTF8.GetBytes(root.ToJsonString());
            int pad = (4 - json.Length % 4) % 4;
            var bin = glb.AsSpan(20 + jl).ToArray();
            var o = new MemoryStream();
            var w = new BinaryWriter(o);
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + json.Length + pad + bin.Length));
            w.Write((uint)(json.Length + pad)); w.Write(0x4E4F534Au); w.Write(json); for (int i = 0; i < pad; i++) w.Write((byte)' ');
            w.Write(bin);
            var back = GltfModel.Import(o.ToArray());
            var t = Assert.Single(back);
            Assert.Equal(new Vector3(0, 0, 128), t.P0);      // up in model space is z
            Assert.Equal(new Vector3(128, 0, 128), t.P1);
            Assert.Equal(0xFF0000FFu, t.Colour);
        }
    }
}
