using System;
using System.Collections.Generic;
using System.Linq;
using U9.Formats.Combat;

namespace U9Editor.Core
{
    /// <summary>A probability distribution over whole numbers (damage per hit).</summary>
    public sealed class Odds
    {
        public readonly int Min;
        public readonly double[] P;     // P[i] = chance of Min + i
        public Odds(int min, double[] p) { Min = min; P = p; }
        public int Max => Min + P.Length - 1;

        /// <summary>Every whole number from min to max equally likely (the game's random(min, max)).</summary>
        public static Odds Uniform(int min, int max)
        {
            if (max < min) max = min;
            var p = new double[max - min + 1];
            Array.Fill(p, 1.0 / p.Length);
            return new Odds(min, p);
        }

        public static Odds Exactly(int v) => new Odds(v, new[] { 1.0 });

        /// <summary>Each value mapped through f (values that meet are added together).</summary>
        public Odds Map(Func<int, int> f)
        {
            var d = new Dictionary<int, double>();
            for (int i = 0; i < P.Length; i++)
            {
                if (P[i] == 0) continue;
                int v = f(Min + i);
                d[v] = d.GetValueOrDefault(v) + P[i];
            }
            int lo = d.Keys.Min(), hi = d.Keys.Max();
            var p = new double[hi - lo + 1];
            foreach (var (k, v) in d) p[k - lo] = v;
            return new Odds(lo, p);
        }

        /// <summary>The distribution of a + b for independent a and b.</summary>
        public Odds Plus(Odds b)
        {
            var p = new double[P.Length + b.P.Length - 1];
            for (int i = 0; i < P.Length; i++)
            {
                if (P[i] == 0) continue;
                for (int j = 0; j < b.P.Length; j++) p[i + j] += P[i] * b.P[j];
            }
            return new Odds(Min + b.Min, p);
        }

        public Odds Minus(Odds b) => Plus(b.Map(v => -v));

        public double Mean => Enumerable.Range(0, P.Length).Sum(i => P[i] * (Min + i));
        /// <summary>The mean of the damage that lands (values of 0 or less do nothing).</summary>
        public double MeanLanded => Enumerable.Range(0, P.Length).Sum(i => P[i] * Math.Max(0, Min + i));
        /// <summary>The chance that a hit does any damage.</summary>
        public double Lands => Enumerable.Range(0, P.Length).Where(i => Min + i > 0).Sum(i => P[i]);
    }

    /// <summary>What one attacker does to one defender: damage per hit and the hits it takes to bring the HP to 0.</summary>
    public sealed class HitsToKill
    {
        public Odds Damage;
        public int Hp;
        public double Expected;        // mean number of hits; +inf when no hit can do damage
        public int Best;               // fewest hits (every hit at its best)
        public string How = "";        // the steps, for a tool tip
        public bool Never => double.IsInfinity(Expected);
    }

    /// <summary>One attack: damage range, damage-type flags and special, and where it comes from.</summary>
    public readonly record struct Attack(string Label, int Min, int Max, int Flags, int Special);

    /// <summary>The Avatar as the calculator sees them.</summary>
    public sealed class AvatarSetup
    {
        public int Hp = 30;
        public int Strength;              // 0-3: +0 / +2 / +4 / +8 damage on melee hits
        public int?[] Armour = new int?[6];   // armour table index per slot (helmet .. boots), null = nothing
        public int? Shield;               // weapon table index of the shield
        public int Mode = -1;             // attack 0-3, or -1 for the best one against each creature
    }

    /// <summary>
    /// The game's damage rules (analysis/item-stats/report.md and audit.md): the attacker's random(min, max) times the
    /// defender's multiplier for the attack's damage types (truncated); the Avatar's melee adds the Gauntlets of Fury's 5
    /// and 0/2/4/8 for strength 0-3; attackers with +0xC4 bit 0x400 do a tenth more; the defender's defence is taken
    /// off (the Avatar: random(min, max) of each armour piece plus a tenth of the shield's; others: random(0, +0x26));
    /// a hit of 0 or less does nothing. Not counted: blocking, poison and other on-hit specials (except the +10 of
    /// special 13 against undead and demons), per-object 0x76/0x77 overrides, spells.
    /// </summary>
    public static class CombatMath
    {
        public static readonly int[] StrengthBonus = { 0, 2, 4, 8 };
        public const int FuryGauntlets = 5423;

        /// <summary>The mean number of hits to take <paramref name="hp"/> to 0 or below (exact, by dynamic programming).</summary>
        public static double ExpectedHits(Odds damage, int hp)
        {
            if (hp <= 0) return 0;
            double miss = 1 - damage.Lands;
            if (damage.Lands <= 1e-12) return double.PositiveInfinity;
            var e = new double[hp + 1];
            for (int h = 1; h <= hp; h++)
            {
                double s = 1;
                for (int i = 0; i < damage.P.Length; i++)
                {
                    int d = damage.Min + i;
                    if (d <= 0 || damage.P[i] == 0) continue;
                    s += damage.P[i] * (d >= h ? 0 : e[h - d]);
                }
                e[h] = s / (1 - miss);
            }
            return e[hp];
        }

        static HitsToKill Result(Odds damage, int hp, string how)
        {
            var r = new HitsToKill { Damage = damage, Hp = hp, How = how };
            r.Expected = ExpectedHits(damage, hp);
            r.Best = damage.Max > 0 ? (int)Math.Ceiling(hp / (double)damage.Max) : int.MaxValue;
            return r;
        }

        /// <summary>A creature's defence roll: random(0, +0x26).</summary>
        public static Odds CreatureDefence(NpcRecord n) => Odds.Uniform(0, n.Defence);

        /// <summary>The Avatar's defence roll: each worn piece's random(min, max), plus a tenth of the shield's roll (rounded down).</summary>
        public static Odds AvatarDefence(CombatTables t, AvatarSetup a)
        {
            var d = Odds.Exactly(0);
            var pieces = t.ArmourPieces();
            foreach (var idx in a.Armour)
            {
                if (idx is not int i) continue;
                var p = pieces.FirstOrDefault(x => x.Index == i);
                if (p.Type != 0) d = d.Plus(Odds.Uniform(p.Min, p.Max));
            }
            if (a.Shield is int s)
            {
                var w = t.WeaponAt(s);
                d = d.Plus(Odds.Uniform(w.Min, w.Max).Map(v => (int)(v * 0.1f)));
            }
            return d;
        }

        /// <summary>The Avatar's melee bonus: Gauntlets of Fury (+5) and strength.</summary>
        public static int AvatarBonus(CombatTables t, AvatarSetup a)
        {
            int bonus = StrengthBonus[Math.Clamp(a.Strength, 0, 3)];
            var pieces = t.ArmourPieces();
            if (a.Armour.Any(i => i is int k && pieces.FirstOrDefault(p => p.Index == k).Type == FuryGauntlets)) bonus += 5;
            return bonus;
        }

        /// <summary>The attacks a weapon has (modes 0-3), with their damage types; a bow has one.</summary>
        public static List<Attack> WeaponAttacks(CombatTables t, Weapon w, string name)
        {
            var list = new List<Attack>();
            for (int mode = 0; mode < (w.IsBow ? 1 : 4); mode++)
            {
                var row = t.AttackModeOf(w.Type, w.Class, mode);
                if (row is not AttackMode m || m.Flags == 0) continue;
                list.Add(new Attack($"{name}, attack {mode + 1} ({FlagNames(m.Flags)})", w.Min, w.Max, m.Flags, m.Special));
            }
            return list;
        }

        public static string FlagNames(int flags)
        {
            var parts = new List<string>();
            for (int b = 0; b < CombatTables.Bits; b++) if ((flags & (1 << b)) != 0) parts.Add(CombatTables.BitNames[b]);
            for (int b = CombatTables.Bits; b < 32; b++) if ((flags & (1 << b)) != 0) parts.Add("bit " + b);
            return parts.Count == 0 ? "none" : string.Join(" + ", parts);
        }

        /// <summary>The Avatar hitting a creature with an attack (melee adds the Avatar's bonus; a bow does not).</summary>
        public static HitsToKill AvatarHits(CombatTables t, AvatarSetup a, Attack attack, bool bow, NpcRecord target)
        {
            float m = CombatTables.Combine(t.MultipliersOf(target.Kind), attack.Flags);
            int bonus = bow ? 0 : AvatarBonus(t, a);
            var dmg = Odds.Uniform(attack.Min, attack.Max).Map(v => (int)(v * 1.0f * m) + bonus).Minus(CreatureDefence(target));
            int extra = attack.Special == 13 && target.UndeadOrDemon ? 10 : 0;
            if (extra != 0) dmg = dmg.Map(v => v + extra);
            if (target.Invulnerable) dmg = Odds.Exactly(0);
            string how = $"{attack.Label}: {attack.Min}-{attack.Max} x{m:0.###}" + (bonus != 0 ? $" +{bonus}" : "") +
                         $" - defence 0-{target.Defence}" + (extra != 0 ? " +10 (undead/demon)" : "") + (target.Invulnerable ? " (invulnerable)" : "");
            return Result(dmg, Math.Max(1, (int)target.MaxHp), how);
        }

        /// <summary>The best of a weapon's attacks against a creature (fewest expected hits), or the chosen one.</summary>
        public static HitsToKill AvatarHits(CombatTables t, AvatarSetup a, Weapon w, string name, NpcRecord target)
        {
            var attacks = WeaponAttacks(t, w, name);
            if (attacks.Count == 0) return Result(Odds.Exactly(0), Math.Max(1, target.MaxHp), name + ": no attack");
            if (a.Mode >= 0 && !w.IsBow)
            {
                var row = t.AttackModeOf(w.Type, w.Class, a.Mode);
                if (row is AttackMode m && m.Flags != 0) attacks = new List<Attack> { new Attack($"{name}, attack {a.Mode + 1} ({FlagNames(m.Flags)})", w.Min, w.Max, m.Flags, m.Special) };
            }
            return attacks.Select(x => AvatarHits(t, a, x, w.IsBow, target)).OrderBy(r => r.Expected).First();
        }

        /// <summary>A creature hitting the Avatar (kind 0: every multiplier 1, so any damage type counts once; none does nothing).</summary>
        public static HitsToKill CreatureHits(CombatTables t, AvatarSetup a, NpcRecord attacker, Attack attack)
        {
            float m = CombatTables.Combine(t.MultipliersOf(0), attack.Flags);
            var dmg = Odds.Uniform(attack.Min, attack.Max).Map(v => (int)(v * 1.0f * m));
            if (attacker.HitsHarder) dmg = dmg.Map(v => (int)(v * 1.1f));
            var def = AvatarDefence(t, a);
            dmg = dmg.Minus(def);
            if (attack.Special == 14) dmg = Odds.Exactly(0);      // drains mana instead of HP
            string how = $"{attacker.Name}, {attack.Label}: {attack.Min}-{attack.Max} x{m:0.###}" + (attacker.HitsHarder ? " x1.1" : "") +
                         $" - armour {def.Min}-{def.Max}" + (attack.Special == 14 ? " (drains mana, not HP)" : attack.Special == 4 ? " (also poisons: not counted)" : "");
            return Result(dmg, Math.Max(1, a.Hp), how);
        }
    }
}
