using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Models;
using U9.Formats.Triggers;
using U9.Mods;
using Xunit;

namespace U9.Mods.Tests
{
    /// <summary>
    /// A small game folder in a temp directory: copies of u9.exe, TRIGGERS.FLX, TYPES.DAT and map 4's object files from
    /// the real install (gamedata/ or U9_GAMEDATA), and a manager home of its own. Never writes to the install.
    /// </summary>
    sealed class TempGame : IDisposable
    {
        public readonly string Root, Home;
        static readonly string[] Files = { "u9.exe", "static/TRIGGERS.FLX", "static/TYPES.DAT", "static/fixed.4", "runtime/nonfixed.4" };

        public static string Install()
        {
            string env = Environment.GetEnvironmentVariable("U9_GAMEDATA");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(Path.Combine(env, "static"))) return env;
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "gamedata", "static"))) return Path.Combine(d.FullName, "gamedata");
            return null;
        }

        public TempGame()
        {
            string src = Install();
            Assert.SkipWhen(src == null, "Ultima IX game data not found");
            Root = Path.Combine(Path.GetTempPath(), "u9mods-" + Guid.NewGuid().ToString("N"));
            Home = Path.Combine(Root, "_home");
            foreach (var f in Files)
            {
                string from = GamePaths.Find(src, f.ToLowerInvariant()) ?? (f == "u9.exe" ? GamePaths.FindExe(src) : null);
                Assert.NotNull(from);
                string to = Path.Combine(Root, f.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(from, to);
            }
        }

        public string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        public byte[] Bytes(string rel) => File.ReadAllBytes(P(rel));

        /// <summary>Edits a file the way the editor saves: the first time, the original is kept as .orig.</summary>
        public void Edit(string rel, Func<byte[], byte[]> change)
        {
            string p = P(rel);
            if (!File.Exists(p + ".orig")) File.Copy(p, p + ".orig");
            File.WriteAllBytes(p, change(File.ReadAllBytes(p)));
        }

        public void DropOrigs()
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*.orig", SearchOption.AllDirectories))
            {
                File.Copy(f, f[..^5], true);
                File.Delete(f);
            }
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    public class ModTests
    {
        static ModPackage Meta(string id, string name) => new ModPackage { Id = id, Name = name, Authors = { "test" }, MadeWith = "tests" };

        static byte[] SetType(byte[] types, int type, TypeField f, int value)
        {
            var t = TypeTable.Read(types);
            t.Set(type, f, value);
            return t.ToBytes();
        }

        static byte[] SetScript(byte[] flx, string tempPath, int slot, TriggerOp[] ops)
        {
            File.WriteAllBytes(tempPath, flx);
            var s = TriggerScripts.Load(tempPath);
            s.Set(slot, ops);
            s.Save();
            var b = File.ReadAllBytes(tempPath);
            File.Delete(tempPath);
            return b;
        }

        static readonly TriggerOp[] Ops1 = { new TriggerOp(0x1011, 0, 42), new TriggerOp(0x10FF, 0, 0) };
        static readonly TriggerOp[] Ops2 = { new TriggerOp(0x1011, 0, 77), new TriggerOp(0x10FF, 0, 0) };

        /// <summary>The Broadsword's max damage in u9.exe's weapon table (record 0 of T1 at file 0x39D868, +0xC).</summary>
        static byte[] SetWeaponMax(byte[] exe, int record, int max)
        {
            var e = (byte[])exe.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(e.AsSpan(0x39D868 + 28 * record + 0xC), max);
            return e;
        }

        [Fact]
        public void DeltasRebuildTheirFileExactlyAndCarryOnlyNewBytes()
        {
            var rnd = new Random(5);
            var original = new byte[300_000];
            rnd.NextBytes(original);
            var edited = original.Take(1000).Concat(new byte[] { 1, 2, 3 }).Concat(original.Skip(1000).Take(150_000)).Concat(original.Skip(200_000)).ToArray();
            edited[5000] ^= 0xFF;
            var d = Delta.Make(original, edited);
            Assert.Equal(edited, Delta.Apply(original, d));
            Assert.True(d.Length < 200, $"delta of {d.Length} bytes");
            Assert.Throws<InvalidDataException>(() => Delta.Apply(original.Take(1000).ToArray(), d));

            // a big file takes the sparse index and still finds its runs
            var big = new byte[20 << 20];
            rnd.NextBytes(big);
            var bigEdited = (byte[])big.Clone();
            bigEdited[123_457] ^= 1; bigEdited[10_000_001] ^= 1;
            var bd = Delta.Make(big, bigEdited);
            Assert.True(bd.Length < 100, $"delta of {bd.Length} bytes");
            Assert.Equal(bigEdited, Delta.Apply(big, bd));
        }

        [Fact]
        public void AModFromTheEditorsCopiesWritesReadsAndRebuildsTheFolder()
        {
            using var g = new TempGame();
            g.Edit("static/TYPES.DAT", b => SetType(b, 66, TypeField.HitPoints, 99));
            g.Edit("static/TRIGGERS.FLX", b => SetScript(b, g.P("tmp.flx"), 9000, Ops1));
            g.Edit("u9.exe", b => SetWeaponMax(b, 0, 60));
            var edited = new[] { "static/types.dat", "static/triggers.flx" }.ToDictionary(p => p, p => File.ReadAllBytes(GamePaths.Find(g.Root, p)));
            var mod = ModMaker.FromEditorCopies(g.Root, Meta("test.one", "One"));
            Assert.Equal(new[] { "static/triggers.flx", "static/types.dat" }, mod.Files.Select(f => f.Path).OrderBy(p => p));
            Assert.Single(mod.ExePatches);
            Assert.Equal(0x39D868 + 0xC, mod.ExePatches[0].Offset);

            string pkg = Path.Combine(g.Root, "one.u9mod");
            mod.Write(pkg);
            var back = ModPackage.Read(pkg);
            Assert.Equal(mod.Files.Select(f => (f.Path, f.Base, f.Target)), back.Files.Select(f => (f.Path, f.Base, f.Target)));
            // nothing of the originals: the package is a sliver of the files it changes
            Assert.True(new FileInfo(pkg).Length < 4096, $"package of {new FileInfo(pkg).Length} bytes");

            g.DropOrigs();
            var install = new GameInstall(g.Root, g.Home);
            var result = install.Apply(new[] { back });
            Assert.True(result.Ok, string.Join("; ", result.Clashes));
            foreach (var kv in edited) Assert.Equal(kv.Value, File.ReadAllBytes(GamePaths.Find(g.Root, kv.Key)));
            var modExe = File.ReadAllBytes(g.P("u9-mod.exe"));
            Assert.Equal(60, BinaryPrimitives.ReadInt32LittleEndian(modExe.AsSpan(0x39D868 + 0xC)));
            Assert.NotEqual(FileId.Of(modExe), install.ExeId);
            Assert.Equal(install.ExeId, FileId.Of(g.Bytes("u9.exe")));          // u9.exe untouched
            Assert.Equal(new[] { "test.one" }, install.Read().Active.Select(a => a.Id));
        }

        [Fact]
        public void TheOriginalGameComesBackWholeAndChangedFilesAreNotOverwritten()
        {
            using var g = new TempGame();
            var originals = new[] { "static/TYPES.DAT", "static/TRIGGERS.FLX", "u9.exe" }.ToDictionary(p => p, p => g.Bytes(p));
            g.Edit("static/TYPES.DAT", b => SetType(b, 66, TypeField.HitPoints, 99));
            g.Edit("u9.exe", b => SetWeaponMax(b, 0, 60));
            var mod = ModMaker.FromEditorCopies(g.Root, Meta("test.two", "Two"));
            g.DropOrigs();
            var install = new GameInstall(g.Root, g.Home);
            Assert.True(install.Apply(new[] { mod }).Ok);

            // the player (or the editor) changes a file the manager wrote: applying again must not lose it
            File.WriteAllBytes(g.P("static/TYPES.DAT"), SetType(g.Bytes("static/TYPES.DAT"), 67, TypeField.Weight, 1));
            var refused = install.Original();
            Assert.False(refused.Ok);
            Assert.Contains("static/types.dat", refused.Clashes.Single());

            Assert.True(install.Original(overwriteChanged: true).Ok);
            foreach (var kv in originals) Assert.Equal(kv.Value, g.Bytes(kv.Key));
            Assert.False(File.Exists(g.P("u9-mod.exe")));
            Assert.False(File.Exists(g.P(GameInstall.MarkerName)));
            Assert.Empty(Directory.EnumerateFiles(g.Root, "*" + GameInstall.NewSuffix, SearchOption.AllDirectories));
        }

        [Fact]
        public void ModsStackByBytesRecordsAndPatchesAndClashesAreRefused()
        {
            using var g = new TempGame();
            ModPackage Make(string id, Action edit)
            {
                edit();
                var m = ModMaker.FromEditorCopies(g.Root, Meta(id, id));
                g.DropOrigs();
                return m;
            }
            var a = Make("mod.a", () =>
            {
                g.Edit("static/TYPES.DAT", b => SetType(b, 66, TypeField.HitPoints, 99));
                g.Edit("static/TRIGGERS.FLX", b => SetScript(b, g.P("tmp.flx"), 9000, Ops1));
                g.Edit("u9.exe", b => SetWeaponMax(b, 0, 60));
            });
            var b = Make("mod.b", () =>
            {
                g.Edit("static/TYPES.DAT", x => SetType(x, 67, TypeField.Weight, 3));
                g.Edit("static/TRIGGERS.FLX", x => SetScript(x, g.P("tmp.flx"), 9001, Ops2));
                g.Edit("u9.exe", x => SetWeaponMax(x, 1, 70));
            });
            var clash = Make("mod.c", () =>
            {
                g.Edit("static/TRIGGERS.FLX", x => SetScript(x, g.P("tmp.flx"), 9000, Ops2));
                g.Edit("u9.exe", x => SetWeaponMax(x, 0, 80));
            });
            var install = new GameInstall(g.Root, g.Home);
            var r = install.Apply(new[] { a, b });
            Assert.True(r.Ok, string.Join("; ", r.Clashes));
            var types = TypeTable.Read(g.Bytes("static/TYPES.DAT"));
            Assert.Equal((99, 3), (types[66].HitPoints, types[67].Weight));
            var scripts = TriggerScripts.Load(g.P("static/TRIGGERS.FLX"));
            Assert.Equal(Ops1, scripts[9000]);
            Assert.Equal(Ops2, scripts[9001]);
            Assert.StartsWith("merged records", r.How["static/triggers.flx"]);
            var exe = g.Bytes("u9-mod.exe");
            Assert.Equal((60, 70), (BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(0x39D868 + 0xC)), BinaryPrimitives.ReadInt32LittleEndian(exe.AsSpan(0x39D868 + 28 + 0xC))));

            var before = new[] { "static/TYPES.DAT", "static/TRIGGERS.FLX", "u9-mod.exe" }.Select(g.Bytes).ToList();
            var bad = install.Apply(new[] { a, clash });
            Assert.False(bad.Ok);
            Assert.Contains(bad.Clashes, c => c.Contains("record (9000)"));
            Assert.Contains(bad.Clashes, c => c.Contains("u9.exe"));
            // nothing written
            Assert.Equal(before, new[] { "static/TYPES.DAT", "static/TRIGGERS.FLX", "u9-mod.exe" }.Select(g.Bytes).ToList());
        }

        [Fact]
        public void PackagesThatBreakTheRulesAreRefused()
        {
            using var g = new TempGame();
            g.Edit("static/TYPES.DAT", b => SetType(b, 66, TypeField.HitPoints, 99));
            var mod = ModMaker.FromEditorCopies(g.Root, Meta("test.rules", "Rules"));
            var ms = new MemoryStream();
            mod.Write(ms);
            Assert.Equal("test.rules", ModPackage.Read(new MemoryStream(ms.ToArray())).Id);

            mod.Files[0].Path = "savegame/slot1";
            Assert.Throws<InvalidDataException>(() => mod.Validate());
            mod.Files[0].Path = "static/../u9.exe";
            Assert.Throws<InvalidDataException>(() => mod.Validate());
            mod.Files[0].Path = "static/types.dat";
            mod.Id = "Bad Id";
            Assert.Throws<InvalidDataException>(() => mod.Validate());

            // a damaged delta: its hash no longer matches
            var raw = ms.ToArray();
            mod.Id = "test.rules";
            mod.Files[0].Delta[^2] ^= 0x55;
            var tampered = new MemoryStream();
            mod.Write(tampered);
            var t = ModPackage.Read(new MemoryStream(tampered.ToArray()));      // consistent with itself...
            Assert.Throws<InvalidDataException>(() => t.Files[0].ApplyTo(File.ReadAllBytes(g.P("static/TYPES.DAT.orig"))));  // ...but not the promised file

            // an executable patch on U9GL's signature is refused when making the mod
            g.DropOrigs();
            int site = ExePatches.FileOffset(g.Bytes("u9.exe"), 0x004163F0);
            Assert.True(site > 0);
            g.Edit("u9.exe", b => { var e = (byte[])b.Clone(); e[site] ^= 0xFF; return e; });
            var ex = Assert.Throws<InvalidOperationException>(() => ModMaker.FromEditorCopies(g.Root, Meta("test.sig", "Sig")));
            Assert.Contains("U9GL", ex.Message);
        }
    }
}
