using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using U9.Formats.Combat;
using U9Editor.Core;

namespace U9Editor.Cli
{
    /// <summary>u9 combat / combat-set / combat-balance / combat-exe: the combat tables from the command line.</summary>
    static class CombatCommands
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static (CombatEditing Ed, Func<int, string> Names) Open(string gamedata)
        {
            var ed = CombatEditing.Open(gamedata);
            var own = ed.TypeNames;
            return (ed, t => own?.Invoke(t));
        }

        /// <summary>Prints the tables (with the pending patches applied) and the pending patches.</summary>
        public static int Print(string gamedata)
        {
            var (ed, names) = Open(gamedata);
            var t = ed.Tables;
            Console.WriteLine("weapons (index type class min max reach radius name):");
            foreach (var w in t.Weapons()) Console.WriteLine($"  {w.Index,3} {w.Type,5} {CombatTables.ClassNames.ElementAtOrDefault(w.Class),-10} {w.Min,4} {w.Max,4} {w.Reach,6:0.#} {w.Radius,5:0.#}  {names(w.Type)}");
            Console.WriteLine("armour (index slot level min max name):");
            foreach (var a in t.ArmourPieces()) Console.WriteLine($"  {a.Index,3} {CombatTables.SlotNames[a.Slot],-9} {a.Level,2} {a.Min,3} {a.Max,3}  {names(a.Type)}");
            Console.WriteLine("natural attacks (row min max types bone):");
            foreach (var n in t.NaturalAttacks()) Console.WriteLine($"  {n.Index,3} {n.Min,4} {n.Max,4}  {CombatMath.FlagNames(n.Flags),-16} {n.Bone}");
            Console.WriteLine("damage multipliers (kind: " + string.Join(" ", CombatTables.BitNames.Select(b => b.Replace(" ", ""))) + ")" + (t.MultipliersAreTable ? " [rewritten as a table]" : "") + ":");
            for (int k = 0; k < CombatTables.Kinds; k++) Console.WriteLine($"  {k,2}: " + string.Join(" ", t.MultipliersOf(k).Select(v => v.ToString("0.##", Inv))));
            var patches = ed.ExePatchList();
            Console.WriteLine($"pending u9.exe patches ({U9.Mods.ExePatchFile.Name}): {patches.Count}");
            foreach (var p in patches) Console.WriteLine($"  0x{p.Offset:X6} {p.New.Length,4} bytes  {p.Note}");
            return 0;
        }

        /// <summary>
        /// Sets stats and saves: weapon:TYPE.min|max|reach|radius=V, armour:TYPE.min|max=V, natural:ROW.min|max|types=V,
        /// mult:KIND.BIT=V (BIT a number or a name: slash, fire...), npc:RECORD.hp|defence|kind|mana=V.
        /// </summary>
        public static int Set(string gamedata, IEnumerable<string> settings)
        {
            var (ed, names) = Open(gamedata);
            var t = ed.Tables;
            foreach (var s in settings)
            {
                int eq = s.IndexOf('='), colon = s.IndexOf(':'), dot = s.LastIndexOf('.', eq < 0 ? s.Length - 1 : eq);
                if (eq < 0 || colon < 0 || dot < colon) throw new InvalidDataException($"'{s}': expected what:which.field=value");
                string what = s[..colon], which = s[(colon + 1)..dot], field = s[(dot + 1)..eq].ToLowerInvariant(), value = s[(eq + 1)..];
                double V() => double.Parse(value, Inv);
                int W() => int.Parse(which, Inv);
                bool changed;
                switch (what)
                {
                    case "weapon":
                    {
                        var w = t.WeaponOf(W()) ?? throw new InvalidDataException($"no weapon of type {which}");
                        string n = $"{names(w.Type) ?? "type " + w.Type} ({w.Type})";
                        var stat = field switch
                        {
                            "min" => t.WeaponMin(w.Index, n), "max" => t.WeaponMax(w.Index, n),
                            "reach" => CombatTables.WeaponReach(w.Index, n), "radius" => CombatTables.WeaponRadius(w.Index, n),
                            _ => throw new InvalidDataException($"weapon field {field}: min, max, reach or radius"),
                        };
                        changed = ed.Set(stat, V());
                        break;
                    }
                    case "armour":
                    {
                        var a = t.ArmourPieces().FirstOrDefault(p => p.Type == W());
                        if (a.Type == 0) throw new InvalidDataException($"no armour of type {which}");
                        string n = names(a.Type) ?? "type " + a.Type;
                        var stat = field == "min" ? CombatTables.ArmourMin(a.Index, n) : field == "max" ? CombatTables.ArmourMax(a.Index, n) : throw new InvalidDataException("armour field: min or max");
                        changed = ed.Set(stat, V());
                        break;
                    }
                    case "natural":
                        changed = field switch
                        {
                            "min" => ed.Set(CombatTables.NaturalMin(W()), V()),
                            "max" => ed.Set(CombatTables.NaturalMax(W()), V()),
                            "types" => ed.Set(CombatTables.NaturalFlags(W()), value.StartsWith("0x") ? int.Parse(value[2..], NumberStyles.HexNumber, Inv) : int.Parse(value, Inv)),
                            _ => throw new InvalidDataException("natural field: min, max or types"),
                        };
                        break;
                    case "mult":
                    {
                        int bit = int.TryParse(field, out int b) ? b : Array.FindIndex(CombatTables.BitNames, x => x.Replace(" ", "") == field);
                        if (bit < 0 || bit >= CombatTables.Bits) throw new InvalidDataException($"damage type {field}");
                        changed = ed.SetMultiplier(W(), bit, (float)V());
                        break;
                    }
                    case "npc":
                    {
                        var f = field switch
                        {
                            "hp" => NpcField.Hp, "defence" => NpcField.Defence, "kind" => NpcField.Kind, "mana" => NpcField.Mana,
                            _ => throw new InvalidDataException("npc field: hp, defence, kind or mana"),
                        };
                        changed = ed.SetNpc(W(), f, (int)V());
                        break;
                    }
                    default: throw new InvalidDataException($"'{what}': weapon, armour, natural, mult or npc");
                }
                Console.WriteLine(changed ? $"set {s}" : $"unchanged {s}");
            }
            foreach (var p in ed.Save()) Console.WriteLine("wrote " + p);
            return 0;
        }

        /// <summary>u9.exe with the pending patches, written elsewhere (never over u9.exe).</summary>
        public static int WriteExe(string gamedata, string output)
        {
            var (ed, _) = Open(gamedata);
            if (Path.GetFullPath(output).Equals(Path.GetFullPath(ed.ExePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("will not write over u9.exe (U9GL identifies the game by it): give another path");
            File.WriteAllBytes(output, ed.Tables.Image);
            Console.WriteLine($"wrote {output} ({ed.ExePatchList().Count} patches)");
            return 0;
        }

        static string Hits(HitsToKill h) => h.Never ? "never" : h.Expected >= 1000 ? ">999" : h.Expected.ToString("0.0", Inv);

        /// <summary>Hits for the Avatar to kill each fighter with one weapon, and for each to kill the Avatar (their default attack).</summary>
        public static int Balance(string gamedata, string[] opts)
        {
            var (ed, names) = Open(gamedata);
            var t = ed.Tables;
            var a = new AvatarSetup { Hp = ed.Npcs[0].MaxHp };
            int weaponType = 5388, level = 0;
            for (int i = 0; i + 1 < opts.Length; i++)
            {
                switch (opts[i])
                {
                    case "--weapon": weaponType = int.Parse(opts[++i]); break;
                    case "--armour": level = int.Parse(opts[++i]); break;
                    case "--str": a.Strength = int.Parse(opts[++i]); break;
                    case "--hp": a.Hp = int.Parse(opts[++i]); break;
                    case "--shield": a.Shield = t.WeaponOf(int.Parse(opts[++i]))?.Index; break;
                }
            }
            var pieces = t.ArmourPieces();
            for (int slot = 0; slot < 6; slot++) a.Armour[slot] = level == 0 ? null : pieces.Where(p => p.Slot == slot && p.Level == level).Select(p => (int?)p.Index).FirstOrDefault();
            var w = t.WeaponOf(weaponType) ?? throw new InvalidDataException($"no weapon of type {weaponType}");
            string wn = names(w.Type) ?? "type " + w.Type;
            Console.WriteLine($"{wn} {w.Min}-{w.Max}; armour level {level}; strength {a.Strength}; Avatar HP {a.Hp}");
            Console.WriteLine("creature                        HP  def  you-kill-it  it-kills-you  (its attack)");
            foreach (var n in ed.Npcs.Records().Where(n => n.Fighter && n.MaxHp > 1).GroupBy(n => n.Name).Select(g => g.First()))
            {
                var mine = CombatMath.AvatarHits(t, a, w, wn, n);
                var defaults = CreatureAttacks.DefaultsOf(n);
                string theirs = defaults.Contains("none") ? "never" : "", how = defaults.Contains("none") ? "never attacks" : "attack not known";
                // Its most dangerous default attack.
                var worst = defaults.Select(c => CreatureAttacks.Resolve(t, c, names, n)).OfType<Attack>()
                    .Select(at => (At: at, H: CombatMath.CreatureHits(t, a, n, at))).OrderBy(x => x.H.Expected).FirstOrDefault();
                if (worst.H != null)
                {
                    theirs = Hits(worst.H);
                    how = $"{worst.At.Label} {worst.At.Min}-{worst.At.Max}";
                }
                Console.WriteLine($"{n.Name,-28} {n.MaxHp,5} {n.Defence,4}  {Hits(mine),11}  {theirs,12}  ({how})");
            }
            return 0;
        }
    }
}
