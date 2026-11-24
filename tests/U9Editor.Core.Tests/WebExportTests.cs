using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using U9.Formats.Flx;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9Editor.Core;
using Xunit;

namespace U9Editor.Core.Tests
{
    /// <summary>The checks UltimaCartographer/u9viewer/EXPORT.md asks of the export, on Moonglow (map 4).</summary>
    public class WebExportTests
    {
        [Fact]
        public void MoonglowExportIsSoundAndRepeatable()
        {
            var g = GameFolder.FindNear(AppContext.BaseDirectory);
            Assert.SkipWhen(g == null, "Ultima IX game data not found");
            string a = Path.Combine(Path.GetTempPath(), "u9web-" + Guid.NewGuid().ToString("N")), b = a + "-again";
            try
            {
                var report = WebExport.Run(g, a, new WebExportOptions { Maps = new[] { 4 } });
                WebExport.Run(g, b, new WebExportOptions { Maps = new[] { 4 } });
                Assert.Empty(report.Problems);

                // same input, same bytes
                var files = Directory.GetFiles(a, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
                Assert.Equal(files, Directory.GetFiles(b, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(b, f)).OrderBy(f => f, StringComparer.Ordinal));
                foreach (var f in files) Assert.True(File.ReadAllBytes(Path.Combine(a, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, f))), f);

                // counts match the editor
                var terrain = TerrainMap.Load(Path.Combine(g.StaticFolder, "terrain.4"));
                using var bmp = FlxArchive.Open(Path.Combine(g.StaticFolder, "bitmap16.flx"));
                Assert.Equal(TerrainTextures.Build(new TerrainRaster(terrain), i => i < bmp.Count ? bmp.Read(i) : null).Layers.Count, report.TerrainLayers);
                var fx = ObjectMap.Load(Path.Combine(g.StaticFolder, "fixed.4"), ObjectFileKind.Fixed);
                var nf = ObjectMap.Load(Directory.EnumerateFiles(Path.Combine(g.Root, "runtime"), "nonfixed.4", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).First(), ObjectFileKind.Nonfixed);
                using var objectsJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(a, "maps", "4", "objects.json")));
                Assert.Equal(fx.Objects.Count + nf.Objects.Count, objectsJson.RootElement.GetArrayLength());

                // every drawn object's model is in models.json, grouped and in ascending order
                using var modelsJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(a, "models.json")));
                var models = modelsJson.RootElement.GetProperty("models");
                var objs = File.ReadAllBytes(Path.Combine(a, "maps", "4", "objects.bin"));
                Assert.Equal(0, objs.Length % 24);
                int last = -1;
                for (int i = 0; i < objs.Length; i += 24)
                {
                    int model = BinaryPrimitives.ReadUInt16LittleEndian(objs.AsSpan(i + 20)), index = BinaryPrimitives.ReadUInt16LittleEndian(objs.AsSpan(i + 22));
                    Assert.True(models.TryGetProperty(model.ToString(), out _), $"model {model}");
                    Assert.True(model >= last);
                    last = model;
                    Assert.True(objectsJson.RootElement[index].GetProperty("drawn").GetBoolean());
                }

                // every texture reference names a real layer; every index stays inside its LOD's vertices
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(a, "manifest.json")));
                var arrays = manifest.RootElement.GetProperty("textures").GetProperty("model").EnumerateArray().Select(e => e.GetProperty("count").GetInt32()).ToArray();
                var bin = File.ReadAllBytes(Path.Combine(a, "models.bin"));
                long vertexCount = modelsJson.RootElement.GetProperty("vertexCount").GetInt64(), indexOffset = modelsJson.RootElement.GetProperty("indexOffset").GetInt64();
                for (long v = 0; v < vertexCount; v++)
                {
                    int tex = BinaryPrimitives.ReadUInt16LittleEndian(bin.AsSpan((int)(v * 32 + 24)));
                    if (tex == 0xFFFF) continue;
                    Assert.InRange(tex >> 12, 0, arrays.Length - 1);
                    Assert.InRange(tex & 0xFFF, 0, arrays[tex >> 12] - 1);
                }
                foreach (var m in models.EnumerateObject())
                    foreach (var lod in m.Value.GetProperty("lods").EnumerateArray())
                    {
                        long first = lod.GetProperty("firstIndex").GetInt64(), count = lod.GetProperty("indexCount").GetInt64(), bv = lod.GetProperty("baseVertex").GetInt64();
                        Assert.Equal(0, count % 3);
                        for (long k = first; k < first + count; k++)
                            Assert.InRange(bv + BinaryPrimitives.ReadUInt32LittleEndian(bin.AsSpan((int)(indexOffset + 4 * k))), 0, vertexCount - 1);
                    }

                // terrain: chunk indices below the chunk count, layers below the layer count
                var tb = File.ReadAllBytes(Path.Combine(a, "maps", "4", "terrain.bin"));
                int chunks = (int)BinaryPrimitives.ReadUInt32LittleEndian(tb);
                Assert.Equal(4 + chunks * 1024 + terrain.TilesX * terrain.TilesY * 2, tb.Length);
                for (int i = 0; i < terrain.TilesX * terrain.TilesY; i++)
                    Assert.InRange(BinaryPrimitives.ReadUInt16LittleEndian(tb.AsSpan(4 + chunks * 1024 + 2 * i)), 0, chunks - 1);
                for (int i = 0; i < chunks * 256; i++)
                {
                    uint layer = BinaryPrimitives.ReadUInt32LittleEndian(tb.AsSpan(4 + 4 * i)) >> 16;
                    Assert.True(layer == 0xFFFF || layer < report.TerrainLayers);
                }
            }
            finally
            {
                if (Directory.Exists(a)) Directory.Delete(a, true);
                if (Directory.Exists(b)) Directory.Delete(b, true);
            }
        }
    }
}
