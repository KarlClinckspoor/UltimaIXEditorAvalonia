using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Formats.Triggers;
using U9Editor.Core;

namespace U9Editor.Cli
{
    /// <summary>
    /// u9 script-places: where a command runs without anyone using anything: from objects that start scripts by
    /// themselves (trigger zones when the Avatar walks in, proximity objects when it comes near, puzzle timers while
    /// the map is loaded), following 0x28 (run another object's script), with the tests on the way that have to come
    /// out right. The starters come from u9.exe's usecode event table (0x7A7938...): class 1111's event 4 handler,
    /// class 5113's AvatarProximityProcess, class 1094's PuzzleTimerProcess. Target matching is approximate (link and
    /// range are tracked in op order, not per branch), so the list is of candidates to try with tools/u9mon.py.
    /// </summary>
    static class ScriptPlaces
    {
        const int Zone = 1111, Proximity = 5113, Timer = 1094, Marker = 1134, MaxDepth = 4;
        static readonly HashSet<int> Tests13 = new() { 0x0E, 0x3A, 0x45, 0x46, 0x47, 0x48, 0x4B, 0x53, 0x58, 0x5A, 0x5B, 0x61, 0x64, 0x29, 0x3B };
        static readonly HashSet<int> GlobalTests = new() { 0x0E, 0x5B };

        sealed record Hit(int Map, MapObject Root, string Kind, int Which, int Op, List<string> Chain, List<string> Conditions, List<(int Global, int Value)> Globals);

        public static int Run(string gamedata, string[] args)
        {
            var ops = new HashSet<int>(args.TakeWhile(a => !a.StartsWith("--")).Select(a => int.Parse(a.StartsWith("0x") ? a[2..] : a, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            var opts = args.SkipWhile(a => !a.StartsWith("--")).ToArray();
            int onlyMap = Opt(opts, "--map", -1), max = Opt(opts, "--max", 8);
            int bi = Array.IndexOf(opts, "--b-mask");          // only ops whose B has one of these bits (hex)
            int bMask = bi >= 0 && bi + 1 < opts.Length ? int.Parse(opts[bi + 1].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
            var (scripts, _) = Program.ScriptSetup(gamedata);
            var game = GameFolder.TryOpen(gamedata) ?? throw new IOException("not a game folder: " + gamedata);
            var eo = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
            var types = TypeTable.Load(Directory.EnumerateFiles(game.StaticFolder, "types.dat", eo).First());
            string tn = Directory.EnumerateFiles(game.StaticFolder, "typename.flx", eo).FirstOrDefault();
            var names = tn != null ? TypeNames.Load(tn) : null;
            int Class(int type) => types[type].Usecode;

            var hits = new List<Hit>();
            var files = Directory.EnumerateFiles(Path.Combine(game.Root, "runtime"), "nonfixed.*", eo)
                .Select(f => (File: f, Map: int.TryParse(Path.GetExtension(f).TrimStart('.'), out int m) ? m : -1))
                .Where(f => f.Map >= 0 && (onlyMap < 0 || f.Map == onlyMap)).OrderBy(f => f.Map);
            foreach (var (file, map) in files)
            {
                var m = ObjectMap.Load(file, ObjectFileKind.Nonfixed);
                var objs = m.Objects.Where(o => !o.Removed).ToList();
                foreach (var root in objs)
                {
                    if (root.PropertyOffset == 0 || (root.Link == 0 && root.Type >= 0x200)) continue;   // the latter never run scripts
                    int c = Class(root.Type);
                    var slots = Cutscenes.ScriptSlots(root);
                    // (which script, what starts it): AvatarProximityProcess runs script 0 on coming near and 1 on going
                    // away; patrol markers (class 1134) run SF A when a patrolling NPC arrives (ActivityPatrol::vf054)
                    var starts = c == Zone ? new[] { (0, "walk into the zone") }
                        : c == Proximity ? new[] { (0, "come near it"), (1, "come near, then go away") }
                        : c == Timer ? new[] { (0, "be on the map (puzzle timer)") }
                        : c == Marker ? new[] { (2, "wait for an NPC patrolling to this marker") }
                        : Array.Empty<(int, string)>();
                    foreach (var (which, kind) in starts)
                    {
                        if (slots[which] == 0) continue;
                        Explore(slots[which], root, new List<string>(), new List<string>(), new List<(int, int)>(), 0,
                            (op, chain, conds, globals) => hits.Add(new Hit(map, root, kind, which, op, chain, conds, globals)));
                    }
                }

                void Explore(int slot, MapObject trigger, List<string> chain, List<string> conds, List<(int, int)> globals, int depth,
                             Action<int, List<string>, List<string>, List<(int, int)>> found)
                {
                    var s = scripts[slot];
                    if (s.Length == 0 || chain.Count(x => x.StartsWith($"script {slot} ")) > 0) return;
                    // link and range in op order (an approximation: branches are ignored)
                    var link = new int[s.Length]; var range = new int[s.Length];
                    int l = trigger.Link, r = TriggerOp.DefaultRange;
                    for (int i = 0; i < s.Length; i++)
                    {
                        link[i] = l; range[i] = r;
                        var o = s[i];
                        if (o.Command == 0x11) l = o.B;
                        else if (o.Command == 0x12) l = o.B < 0x8000 ? l + o.B : l - (o.B & 0x7FFF);
                        else if (o.Command == TriggerOp.Range) r = o.RangeUnits;
                    }
                    foreach (var (i, path) in Reach(s))
                    {
                        var o = s[i];
                        var c2 = conds.Concat(path.Select(p => p.Text)).ToList();
                        var g2 = globals.Concat(path.Where(p => p.Global >= 0).Select(p => (p.Global, p.Value))).ToList();
                        var here = chain.Append($"script {slot} op {i}").ToList();
                        if (ops.Contains(o.Command) && (bMask == 0 || (o.B & bMask) != 0)) found(o.Command, here, c2, g2);
                        if (o.Command == 0x28 && depth < MaxDepth)
                        {
                            foreach (var t in objs)
                            {
                                if (o.TargetType != TriggerOp.AnyType && Class(t.Type) != o.TargetType) continue;
                                if (o.UsesLink && t.Link != link[i] + o.LinkOffset) continue;
                                if (Math.Abs(t.MapX - trigger.MapX) > range[i] || Math.Abs(t.MapY - trigger.MapY) > range[i]) continue;
                                int next = t.PropertyOffset != 0 ? Cutscenes.ScriptSlots(t)[o.B & 3] : 0;
                                if (next == 0) continue;
                                Explore(next, t, here.Append($"-> type {t.Type} link {t.Link} script {o.B & 3}").ToList(), c2, g2, depth + 1, found);
                            }
                        }
                    }
                }
            }

            foreach (int op in ops.OrderBy(x => x))
            {
                // the same object (type, place, link) on several maps (map copies such as 164/168/169 of Britannia) once
                var mine = hits.Where(h => h.Op == op)
                    .GroupBy(h => (h.Root.Type, h.Root.MapX, h.Root.MapY, h.Root.Z, h.Root.Link))
                    .Select(g => (Best: g.OrderBy(h => h.Conditions.Count).ThenBy(h => h.Map).First(), Maps: g.Select(h => h.Map).Distinct().OrderBy(x => x).ToList()))
                    .OrderBy(x => x.Best.Conditions.Count - x.Best.Globals.Count).ThenBy(x => x.Best.Conditions.Count).ThenBy(x => x.Best.Chain.Count).ToList();
                Console.WriteLine($"== command {op:X2}: {mine.Count} starting places{(onlyMap >= 0 ? $" on map {onlyMap}" : "")}");
                foreach (var (h, maps) in mine.Take(max))
                {
                    var t = h.Root;
                    string name = names?[t.Type];
                    string also = maps.Count > 1 ? $" (also maps {string.Join(", ", maps.Where(x => x != h.Map))})" : "";
                    Console.WriteLine(FormattableString.Invariant($"  map {h.Map}{also}: {h.Kind}: type {t.Type}{(name != null ? " " + name : "")} link {t.Link} at {t.PointX:F2},{t.PointRow:F2} z {t.Z}"));
                    Console.WriteLine($"      tp {h.Map} {t.MapX},{t.MapY},{t.Z + 64}      run {t.Type} {t.X} {t.Y} {t.Z} {t.Link} {h.Which}");
                    Console.WriteLine($"      path: {string.Join(" ", h.Chain)}");
                    var set = h.Globals.Distinct().Where(g => g.Value != 0).ToList();
                    var zero = h.Globals.Distinct().Where(g => g.Value == 0).ToList();
                    if (set.Count > 0)
                        Console.WriteLine($"      set first: {string.Join(", ", set.Select(g => $"global {g.Global} {g.Value}"))}");
                    if (zero.Count > 0)
                        Console.WriteLine($"      needs 0 (as in a new game): {string.Join(", ", zero.Select(g => $"global {g.Global}"))}");
                    var other = h.Conditions.Where(c => !c.StartsWith("global")).ToList();
                    if (other.Count > 0) Console.WriteLine($"      and needs: {string.Join("; ", other)}");
                    if (h.Conditions.Count == 0) Console.WriteLine("      no conditions: a teleport should do");
                }
                if (mine.Count > max) Console.WriteLine($"  ... {mine.Count - max} more (--max)");
            }
            return 0;
        }

        /// <summary>
        /// Each op the script can reach from its start, with the test outcomes on the shortest way there. A test op
        /// jumps when its flag comes out set; label 0 (or a missing label) ends the script.
        /// </summary>
        static IEnumerable<(int Index, List<(string Text, int Global, int Value)> Path)> Reach(TriggerOp[] s)
        {
            var labels = new Dictionary<int, int>();
            for (int i = 0; i < s.Length; i++) if (s[i].Command == 0x16 && !labels.ContainsKey(s[i].B)) labels[s[i].B] = i;
            var best = new Dictionary<int, List<(string, int, int)>>();
            var queue = new Queue<int>();
            best[0] = new(); queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                if (i < 0 || i >= s.Length) continue;
                var o = s[i];
                var path = best[i];
                void Go(int to, (string, int, int)? cond)
                {
                    if (to < 0 || to >= s.Length || best.ContainsKey(to)) return;
                    var p = new List<(string, int, int)>(path);
                    if (cond.HasValue) p.Add(cond.Value);
                    best[to] = p; queue.Enqueue(to);
                }
                int Label(int b) => b != 0 && labels.TryGetValue(b, out int at) ? at : -1;
                int c = o.Command;
                if (c == TriggerOp.End || c == TriggerOp.ChangeMap || (c == 0x3D && o.B == 21)) continue;
                if (c == 0x17) { Go(Label(o.B), null); continue; }
                int jump = Tests13.Contains(c) ? o.B >> 13 : c == 0x1A ? (o.B >> 1) & 7 : (c == 0x15 || c == 0x2D || c == 0x2F) ? (o.B >> 5) & 7 : -1;
                if (jump >= 0)
                {
                    Go(Label(jump), Test(o, true));
                    Go(i + 1, Test(o, false));
                }
                else if (c == 0x4F) { if ((o.B & 0x8000) != 0) Go(Label(1), Test(o, true)); Go(i + 1, Test(o, false)); }
                else Go(i + 1, null);
            }
            return best.OrderBy(k => k.Key).Select(k => (k.Key, k.Value));
        }

        /// <summary>A test's outcome as text, and for a global test the value that gives it.</summary>
        static (string, int, int) Test(TriggerOp o, bool taken)
        {
            if (GlobalTests.Contains(o.Command))
            {
                int g = o.B & 0x1FF, v = ((o.B >> 9) & 7) + (o.Command == 0x5B ? 8 : 0);
                bool less = (o.B & 0x1000) != 0;
                // jumps when global > v (or < v): true needs v + 1 (v - 1), false needs v
                int want = taken ? (less ? v - 1 : v + 1) : v;
                string rel = taken ? (less ? "<" : ">") : (less ? ">=" : "<=");
                if (want < 0) return ($"never: global {g} < 0", -1, 0);
                return ($"global {g} {rel} {v}", g, want);
            }
            return ($"{(taken ? "" : "not ")}[{o.Describe()}]", -1, 0);
        }

        static int Opt(string[] opts, string name, int fallback)
        {
            int i = Array.IndexOf(opts, name);
            return i >= 0 && i + 1 < opts.Length ? int.Parse(opts[i + 1], CultureInfo.InvariantCulture) : fallback;
        }
    }
}
