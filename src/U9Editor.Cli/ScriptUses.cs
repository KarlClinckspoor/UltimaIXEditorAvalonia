using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using U9.Formats.Objects;
using U9Editor.Core;

namespace U9Editor.Cli
{
    /// <summary>
    /// u9 script-uses: which scripts use a command, and which placed objects run those scripts, so that one can be
    /// picked, travelled to and run in the game (tools/u9mon.py: tp, run).
    /// </summary>
    static class ScriptUses
    {
        static readonly string[] Which = { "OO A", "OO B", "SF A", "SF B" };

        public static int Run(string gamedata, string opText, string[] opts)
        {
            int op = int.Parse(opText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? opText[2..] : opText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int onlyMap = Opt(opts, "--map", -1), max = Opt(opts, "--max", 20);
            var (scripts, _) = Program.ScriptSetup(gamedata);
            var game = GameFolder.TryOpen(gamedata) ?? throw new IOException("not a game folder: " + gamedata);

            // slot -> the ops of that command in it
            var users = new Dictionary<int, int>();
            for (int s = 0; s < scripts.Count; s++)
            {
                if (!scripts.IsUsed(s)) continue;
                int n = scripts[s].Count(o => o.Command == op && o.Op != U9.Formats.Triggers.TriggerOp.End);
                if (n > 0) users[s] = n;
            }
            if (users.Count == 0) { Console.WriteLine($"no script uses command {op:X2}"); return 0; }

            // the placed objects that run each slot, over every map's nonfixed file
            var runners = new Dictionary<int, List<string>>();
            string typenames = Directory.EnumerateFiles(game.StaticFolder, "typename.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            var names = typenames != null ? TypeNames.Load(typenames) : null;
            var files = Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed.*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Select(f => (File: f, Map: int.TryParse(Path.GetExtension(f).TrimStart('.'), out int m) ? m : -1))
                .Where(f => f.Map >= 0 && (onlyMap < 0 || f.Map == onlyMap)).OrderBy(f => f.Map);
            foreach (var (file, map) in files)
            {
                var m = ObjectMap.Load(file, ObjectFileKind.Nonfixed);
                foreach (var o in m.Objects)
                {
                    if (o.Removed || o.PropertyOffset == 0) continue;
                    var slots = Cutscenes.ScriptSlots(o);
                    for (int k = 0; k < 4; k++)
                    {
                        if (slots[k] == 0 || !users.ContainsKey(slots[k])) continue;
                        if (!runners.TryGetValue(slots[k], out var list)) runners[slots[k]] = list = new List<string>();
                        string name = names?[o.Type];
                        list.Add(FormattableString.Invariant(
                            $"map {map} @{o.FileOffset:X6} type {o.Type}{(name != null ? " " + name : "")} at {o.PointX:F2},{o.PointRow:F2} z {o.Z} link {o.Link} script {k} ({Which[k]})\n        tp {map} {o.MapX},{o.MapY},{o.Z + 64}\n        run {o.Type} {o.X} {o.Y} {o.Z} {o.Link} {k}"));
                    }
                }
            }

            var order = users.Keys.OrderBy(s => runners.ContainsKey(s) ? 0 : 1).ThenBy(s => scripts[s].Length).ToList();
            Console.WriteLine($"command {op:X2}: {users.Values.Sum()} uses in {users.Count} scripts, {order.Count(runners.ContainsKey)} run by placed objects{(onlyMap >= 0 ? $" on map {onlyMap}" : "")}");
            foreach (int s in order.Take(max))
            {
                Console.WriteLine($"script {s}: {scripts[s].Length - 1} ops, {users[s]} of them {op:X2}");
                if (runners.TryGetValue(s, out var list))
                    foreach (var r in list.Take(5)) Console.WriteLine("    " + r);
                else
                    Console.WriteLine(onlyMap >= 0
                        ? $"    (no placed object on map {onlyMap} runs it directly)"
                        : "    (no placed object runs it directly: called from another script or from code)");
                if (runners.TryGetValue(s, out var all) && all.Count > 5) Console.WriteLine($"    ... {all.Count - 5} more");
            }
            if (order.Count > max) Console.WriteLine($"... {order.Count - max} more scripts (--max)");
            return 0;
        }

        static int Opt(string[] opts, string name, int fallback)
        {
            int i = Array.IndexOf(opts, name);
            return i >= 0 && i + 1 < opts.Length ? int.Parse(opts[i + 1], CultureInfo.InvariantCulture) : fallback;
        }
    }
}
