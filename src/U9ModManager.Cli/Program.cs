using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Mods;

namespace U9ModManager.Cli
{
    static class Program
    {
        const string Usage = @"u9mod: the Ultima IX mod manager (docs/mod-format.md)

  u9mod info <mod.u9mod>                    what a mod changes
  u9mod make <game> <out.u9mod> --id ID --name NAME [--version V] [--author A] [--description D]
                                            a mod of what the editor changed in a game folder (against its .orig copies)
  u9mod import <mod.u9mod>                  add a mod to the library
  u9mod list                                the library
  u9mod remove <id>                         take a mod out of the library
  u9mod plan <game> <id>...                 what applying these mods (in order) would do; nothing is written
  u9mod apply <game> <id>... [--overwrite]  make the game folder what these mods give (all or nothing)
  u9mod original <game> [--overwrite]       put the original game back
  u9mod status <game>                       the active mods, and files changed since the manager wrote them
  u9mod recover <game>                      finish a change cut off half way
  u9mod play <game>                         start the game (u9-mod.exe when mods patch the executable)
  u9mod catalog [<url>]                     list the catalog's mods (the url is remembered)
  u9mod download <id>                       download a mod from the catalog into the library
  u9mod u9gl <game> [--noruntime]           download the latest U9GL into the game folder and open its launcher
                                            (--noruntime: the small zip, which needs .NET installed)

The manager keeps its library, originals and settings in %APPDATA%\U9ModManager (U9MM_HOME overrides).
u9.exe is never changed: executable patches go into u9-mod.exe.";

        static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.Net.Http.HttpRequestException)
            {
                Console.Error.WriteLine("u9mod: " + ex.Message);
                return 1;
            }
        }

        static int Run(string[] a)
        {
            if (a.Length == 0 || a[0] is "help" or "-h" or "--help") { Console.WriteLine(Usage); return a.Length == 0 ? 1 : 0; }
            string home = GameInstall.DefaultHome;
            var lib = new ModLibrary(home);
            var settings = ManagerSettings.Load(home);
            switch (a[0])
            {
                case "info" when a.Length == 2:
                    {
                        var m = ModPackage.Read(a[1]);
                        Console.WriteLine($"{m.Name} {m.Version} ({m.Id}) by {string.Join(", ", m.Authors)}");
                        if (m.Description.Length > 0) Console.WriteLine(m.Description);
                        foreach (var f in m.Files) Console.WriteLine($"  {f.Path}: {f.Base.Size} -> {f.Target.Size} bytes, delta {f.Delta.Length}");
                        foreach (var p in m.ExePatches) Console.WriteLine($"  u9.exe {p}");
                        return 0;
                    }
                case "make" when a.Length >= 3:
                    {
                        var meta = new ModPackage { MadeWith = "u9mod" };
                        for (int i = 3; i < a.Length; i++)
                        {
                            string v = i + 1 < a.Length ? a[i + 1] : throw new InvalidOperationException(a[i] + " needs a value");
                            switch (a[i])
                            {
                                case "--id": meta.Id = v; break;
                                case "--name": meta.Name = v; break;
                                case "--version": meta.Version = v; break;
                                case "--author": meta.Authors.Add(v); break;
                                case "--description": meta.Description = v; break;
                                default: throw new InvalidOperationException("unknown option " + a[i]);
                            }
                            i++;
                        }
                        var m = ModMaker.FromEditorCopies(a[1], meta);
                        if (m.Files.Count == 0 && !m.ChangesExe) throw new InvalidOperationException("Nothing changed: the folder's files match their .orig copies.");
                        m.Write(a[2]);
                        Console.WriteLine($"{m}: {m.Summary()} -> {a[2]} ({new FileInfo(a[2]).Length} bytes)");
                        return 0;
                    }
                case "import" when a.Length == 2:
                    Console.WriteLine("imported " + lib.Import(a[1]).Mod);
                    return 0;
                case "list" when a.Length == 1:
                    foreach (var i in lib.Items()) Console.WriteLine(i.Mod != null ? $"{i.Mod.Id,-30} {i.Mod.Name} {i.Mod.Version}: {i.Mod.Summary()}" : $"{Path.GetFileName(i.Path)}: {i.Problem}");
                    return 0;
                case "remove" when a.Length == 2:
                    lib.Remove(a[1]);
                    return 0;
                case "plan" when a.Length >= 2:
                case "apply" when a.Length >= 2:
                    {
                        bool overwrite = a.Contains("--overwrite");
                        var ids = a.Skip(2).Where(x => x != "--overwrite").ToList();
                        var items = lib.Items();
                        var mods = ids.Select(id => items.FirstOrDefault(i => i.Mod?.Id == id)?.Mod ?? throw new InvalidOperationException($"No mod {id} in the library (u9mod list).")).ToList();
                        var game = new GameInstall(a[1], home);
                        var r = a[0] == "plan" ? game.Plan(mods) : game.Apply(mods, overwrite, settings.LargeAddressAware);
                        foreach (var kv in r.Files) Console.WriteLine($"  {kv.Key}: {(r.How.TryGetValue(kv.Key, out var how) ? how : "")}");
                        if (r.ExePatches.Count > 0) Console.WriteLine($"  u9-mod.exe: {r.ExePatches.Count} patches");
                        foreach (var c in r.Clashes) Console.Error.WriteLine("  refused: " + c);
                        if (r.Ok && a[0] == "apply")
                        {
                            settings.Chosen[Path.GetFullPath(a[1])] = ids;
                            if (!settings.Games.Contains(Path.GetFullPath(a[1]), StringComparer.OrdinalIgnoreCase)) settings.Games.Add(Path.GetFullPath(a[1]));
                            settings.Save(home);
                            Console.WriteLine(ids.Count == 0 ? "the original game" : "applied " + string.Join(", ", ids));
                        }
                        return r.Ok ? 0 : 2;
                    }
                case "original" when a.Length >= 2:
                    {
                        var r = new GameInstall(a[1], home).Original(a.Contains("--overwrite"));
                        foreach (var c in r.Clashes) Console.Error.WriteLine("  refused: " + c);
                        if (r.Ok) { settings.Chosen.Remove(Path.GetFullPath(a[1])); settings.Save(home); Console.WriteLine("the original game"); }
                        return r.Ok ? 0 : 2;
                    }
                case "status" when a.Length == 2:
                    {
                        var game = new GameInstall(a[1], home);
                        var s = game.Read();
                        Console.WriteLine(s.Active.Count == 0 ? "the original game" : "active: " + string.Join(", ", s.Active.Select(x => $"{x.Name} {x.Version} ({x.Id})")));
                        Console.WriteLine($"files written: {s.Written.Count}{(s.ExeCopy ? ", u9-mod.exe" : "")}; u9.exe {game.ExeId.Sha256[..16]}...");
                        foreach (var c in game.ChangedSince()) Console.WriteLine("  changed since: " + c);
                        string u9gl = ModLibrary.InstalledU9gl(game.Root);
                        Console.WriteLine("U9GL: " + (u9gl ?? "not in the game folder"));
                        return 0;
                    }
                case "recover" when a.Length == 2:
                    new GameInstall(a[1], home);         // recovers on opening
                    Console.WriteLine("nothing left half done");
                    return 0;
                case "play" when a.Length == 2:
                    new GameInstall(a[1], home).Launch();
                    return 0;
                case "catalog" when a.Length <= 2:
                    {
                        if (a.Length == 2) { settings.CatalogUrl = a[1]; settings.Save(home); }
                        if (string.IsNullOrEmpty(settings.CatalogUrl)) throw new InvalidOperationException("No catalog set: u9mod catalog <url>.");
                        foreach (var e in lib.FetchCatalog(settings.CatalogUrl).GetAwaiter().GetResult())
                            Console.WriteLine($"{e.Id,-30} {e.Name} {e.Version}  {e.Description}");
                        return 0;
                    }
                case "download" when a.Length == 2:
                    {
                        if (string.IsNullOrEmpty(settings.CatalogUrl)) throw new InvalidOperationException("No catalog set: u9mod catalog <url>.");
                        var e = lib.FetchCatalog(settings.CatalogUrl).GetAwaiter().GetResult().FirstOrDefault(x => x.Id == a[1])
                                ?? throw new InvalidOperationException($"The catalog has no mod {a[1]}.");
                        Console.WriteLine("downloaded " + lib.Download(e).GetAwaiter().GetResult().Mod);
                        return 0;
                    }
                case "u9gl" when a.Length >= 2:
                    {
                        var rel = lib.LatestU9gl(a.Contains("--noruntime")).GetAwaiter().GetResult();
                        Console.WriteLine($"U9GL {rel.Version}: {rel.ZipName}");
                        string exe = lib.DownloadU9gl(rel, a[1]).GetAwaiter().GetResult();
                        Console.WriteLine("unpacked; opening " + exe + " (its Install puts dsound.dll in place)");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
                        return 0;
                    }
            }
            Console.Error.WriteLine(Usage);
            return 1;
        }
    }
}
