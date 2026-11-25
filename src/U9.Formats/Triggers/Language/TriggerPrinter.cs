using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace U9.Formats.Triggers.Language
{
    /// <summary>
    /// Prints a script's ops in the trigger language, one statement a line. Every statement is compiled back
    /// as it is printed and kept only if it gives the same op; otherwise a plainer form is used, down to
    /// raw(op, A, B), so printing and compiling always gives the same bytes.
    /// </summary>
    public static class TriggerPrinter
    {
        public sealed class Line
        {
            public string Text;
            /// <summary>Which form it took: "named", "generic" (op_XX) or "raw".</summary>
            public string Form;
        }

        public static string Print(IReadOnlyList<TriggerOp> ops, TriggerNames names, int slot = 0) =>
            string.Join("\n", Lines(ops, names, slot).Select(l => l.Text)) + "\n";

        public static List<Line> Lines(IReadOnlyList<TriggerOp> ops, TriggerNames names, int slot = 0)
        {
            names ??= new TriggerNames();
            var lines = new List<Line>();
            int n = ops.Count;
            bool canonicalEnd = n > 0 && ops[n - 1].Op == (0x1000 | TriggerOp.End) && ops[n - 1].A == 0 && ops[n - 1].B == 0
                                && !ops.Take(n - 1).Any(o => o.Command == TriggerOp.End);
            int shown = canonicalEnd ? n - 1 : n;
            bool picked = false;
            for (int i = 0; i < shown; i++)
            {
                var op = ops[i];
                var line = Statement(op, names, slot);
                bool dispatched = !TriggerCompiler.Interpreted.Contains(op.Command) || op.Command == 0x16 || (op.Command == 0x30 && op.A != 0xD7);
                if (picked && dispatched && line.Form != "raw") { line.Text += "    // works on the picked objects, whatever it names"; picked = false; }
                else if (picked && dispatched) picked = false;
                if (op.Command == 0x21) picked = true;
                lines.Add(line);
            }
            if (!canonicalEnd) lines.Add(new Line { Text = "no_end", Form = "named" });
            return lines;
        }

        /// <summary>One op as a statement (no line break), in the plainest form that compiles back to it.</summary>
        public static Line Statement(TriggerOp op, TriggerNames names, int slot = 0)
        {
            names ??= new TriggerNames();
            if (op.Command != TriggerOp.End)
            {
                foreach (var (form, numeric, named) in Candidates(op, names, slot))
                    if (numeric != null && Check(numeric, op)) return new Line { Text = named ?? numeric, Form = form };
            }
            return new Line { Text = $"raw(0x{op.Op:X4}, {op.A}, {op.B})", Form = "raw" };
        }

        static bool Check(string text, TriggerOp op)
        {
            var r = TriggerCompiler.Compile(text, Verifier, 0);
            return r.Ok && r.Ops.Length == 2 && r.Ops[0].Op == op.Op && r.Ops[0].A == op.A && r.Ops[0].B == op.B;
        }

        // Statements are checked with numbers in place of names (labels L<n>, classes as numbers), so the check
        // does not depend on the names; the named text maps one to one onto the numeric one.
        static readonly TriggerNames Verifier = new TriggerNames();

        static IEnumerable<(string Form, string Numeric, string Named)> Candidates(TriggerOp op, TriggerNames names, int slot)
        {
            int cmd = op.Command, A = op.A, B = op.B, lf = op.Op >> 8;
            if (lf > 0x1F) yield break;
            bool interp = TriggerCompiler.Interpreted.Contains(cmd) || (cmd == 0x30 && A == 0xD7);
            var P = new Printer(names, slot);
            var V = new Printer(null, slot);
            if (interp)
            {
                var vi = Interp(V, cmd, A, B, lf).ToList();
                var pi = Interp(P, cmd, A, B, lf).ToList();
                for (int k = 0; k < vi.Count; k++)
                    yield return ("named", vi[k].Text + V.Suffix(A, lf, vi[k].AUsed, vi[k].LfUsed), pi[k].Text + P.Suffix(A, lf, pi[k].AUsed, pi[k].LfUsed));
                yield return ("generic", $"op_{cmd:x2}({V.Num(B)})" + V.Suffix(A, lf, false, false), $"op_{cmd:x2}({P.Num(B)})" + P.Suffix(A, lf, false, false));
                yield break;
            }
            if (A >> 13 != 0) yield break;
            var vs = Obj(V, cmd, A, B, lf).ToList();
            var ps = Obj(P, cmd, A, B, lf).ToList();
            for (int k = 0; k < vs.Count; k++) yield return ("named", vs[k], ps[k]);
            yield return ("generic", $"each({V.Sel(A, lf)}).op_{cmd:x2}({V.Num(B)})", $"each({P.Sel(A, lf)}).op_{cmd:x2}({P.Num(B)})");
        }

        static string Tail(Printer p, int label) => label == 0 ? "end" : "goto " + p.Label(label);
        static string Lt(int b, int bit) => (b & bit) != 0 ? "<" : ">";

        static IEnumerable<(string Text, bool AUsed, bool LfUsed)> Interp(Printer p, int cmd, int A, int B, int lf)
        {
            switch (cmd)
            {
                case 0x00: yield return (B == 0 ? "nop" : $"nop({p.Num(B)})", false, false); break;
                case 0x0D: yield return ($"global[{p.Global(B & 0x1FF)}] = {B >> 9}", false, false); break;
                case 0x0E: case 0x5B:
                    yield return ($"if (global[{p.Global(B & 0x1FF)}] {Lt(B, 0x1000)} {((B >> 9) & 7) + (cmd == 0x5B ? 8 : 0)}) {Tail(p, B >> 13)}", false, false);
                    break;
                case 0x11: yield return ($"link = {B}", false, false); break;
                case 0x12: yield return (B < 0x8000 ? $"link += {B}" : $"link -= {B & 0x7FFF}", false, false); break;
                case 0x13: yield return ("once", false, false); break;
                case 0x14: yield return ($"options({p.Bits("options", B)})", false, false); break;
                case 0x16: yield return (p.LabelDef(B), false, false); break;
                case 0x17: yield return (B == 0 ? "end" : "goto " + p.Label(B), false, false); break;
                case 0x1F:
                {
                    var sb = new StringBuilder($"change_map({B & 0xFF}");
                    if (lf != 0) sb.Append(", ").Append(p.Link(lf));
                    if (B >> 8 != 0) sb.Append($", flags: 0x{B >> 8:X}");
                    yield return (sb.Append(')').ToString(), false, true);
                    break;
                }
                case 0x21: yield return ($"pick({B}, {p.Sel(A, lf)})", true, true); break;
                case 0x2C:
                    yield return (B == 0 ? "range = default" : (B & 0x8000) != 0 ? $"range = {B & 0x7FFF}u" : $"range = {B}", false, false);
                    break;
                case 0x2F:
                    if ((B & 7) <= 5)
                    {
                        string l = lf == 0 ? "link" : lf >= 0x10 ? $"link + {lf - 0x10}" : $"link - {0x10 - lf}";
                        yield return ($"if ({l} {TriggerCompiler.Compares[B & 7]} {B >> 8}) {Tail(p, (B >> 5) & 7)}", false, true);
                    }
                    break;
                case 0x30: yield return ($"use_trigger({p.Num(B)})", true, false); break;
                case 0x31:
                    yield return ((B & 0x100) != 0 ? $"npc[{A}].push_schedule({B & 0xFF})" : $"npc[{A}].schedule = {B & 0xFF}", true, false);
                    break;
                case 0x37: yield return ($"movie({(B < TriggerNames.Movies.Length ? TriggerNames.Movies[B] : p.Num(B))})", false, false); break;
                case 0x3A: yield return ($"if (clock {Lt(B, 0x1000)} {Printer.Clock(B & 0xFFF)}) {Tail(p, B >> 13)}", false, false); break;
                case 0x3F: yield return ($"clock {((B & 0x8000) != 0 ? "+=" : "=")} {Printer.Clock(B & 0x7FFF)}", false, false); break;
                case 0x4B: yield return ($"if (avatar.quantity({p.Sel(A, lf)}) {Lt(B, 0x1000)} {B & 0xFFF}) {Tail(p, B >> 13)}", true, true); break;
                case 0x52: yield return ($"avatar.mana = {B}", false, false); break;
                case 0x53: yield return ($"if (avatar.mana {Lt(B, 0x1000)} {B & 0xFFF}) {Tail(p, B >> 13)}", false, false); break;
                case 0x5F: yield return ((B & 0x8000) != 0 ? $"avatar.mana += {B & 0xFF}" : $"avatar.mana -= {B & 0xFF}", false, false); break;
                case 0x61: yield return ($"if (avatar.attribute[{B & 0xF}] {Lt(B, 0x1000)} {(B >> 4) & 0xF}) {Tail(p, B >> 13)}", false, false); break;
                case 0x62:
                {
                    int mode = (B >> 8) & 0xF, a = B & 0xF;
                    if (mode == 0) yield return ($"avatar.attribute[{a}] += 1", false, false);
                    else if (mode == 1) yield return ($"avatar.attribute[{a}] -= 1", false, false);
                    else if (mode == 2) yield return ($"avatar.attribute[{a}] = {(B >> 4) & 0xF}", false, false);
                    break;
                }
                case 0x58: case 0x64: yield return ($"if (op_{cmd:x2}({p.Num(B & 0x1FFF)})) {Tail(p, B >> 13)}", false, false); break;
            }
        }

        static IEnumerable<string> Obj(Printer p, int cmd, int A, int B, int lf)
        {
            string e = $"each({p.Sel(A, lf)}).", any = $"any({p.Sel(A, lf)}).";
            switch (cmd)
            {
                case 0x01:
                    if (A == 2218)
                    {
                        string flags = (B & 0xF800) != 0 ? ", " + p.Bits("move", B & 0xF800) : "";
                        yield return e + "move(" + Printer.Seconds(B & 0x7FF) + flags + ")";
                    }
                    yield return e + $"activate({p.Num(B)})";
                    break;
                case 0x02: yield return e + $"link = {B}"; break;
                case 0x57: yield return e + ((B & 0x4000) != 0 ? $"link -= {B & 0xFFF}" : $"link += {B & 0xFFF}"); break;
                case 0x03:
                {
                    string[] props = { "quantity", "hit_points", "owner", "hint_level" };
                    if ((B & 0xF) < 4) yield return e + $"{props[B & 0xF]} = {B >> 4}";
                    break;
                }
                case 0x04: yield return e + $"type = {B}"; break;
                case 0x06: yield return e + $"set_flags({p.Flags(B)})"; break;
                case 0x07: yield return e + $"clear_flags({p.Flags(B)})"; break;
                case 0x08: yield return e + $"toggle_flags({p.Flags(B)})"; break;
                case 0x15: case 0x2D:
                    if ((B & 7) <= 5)
                        yield return $"if ({(cmd == 0x15 ? "count" : "count_flagged")}({p.Sel(A, lf)}) {TriggerCompiler.Compares[B & 7]} {B >> 8}) {Tail(p, (B >> 5) & 7)}";
                    break;
                case 0x18: yield return e + $"variable = {B}"; break;
                case 0x19: yield return e + "variable += 1"; break;
                case 0x27: yield return e + $"variable = random({B})"; break;
                case 0x1A: yield return $"if ({any}variable {Lt(B, 1)} {(B >> 4) & 0xFF}) {Tail(p, (B >> 1) & 7)}"; break;
                case 0x28: yield return e + $"run_script({TriggerNames.Groups["script"][B & 3].Name})"; break;
                case 0x29: yield return $"if ({any}has_any_flags({p.Flags(B & 0x1FFF)})) {Tail(p, B >> 13)}"; break;
                case 0x3B: yield return $"if ({any}has_all_flags({p.Flags(B & 0x1FFF)})) {Tail(p, B >> 13)}"; break;
                case 0x3D: yield return e + $"special({B})"; break;
                case 0x40: yield return e + $"activity({B & 0x7F}, {B >> 7})"; break;
                case 0x41: yield return e + $"translucency = {B}"; break;
                case 0x43: yield return e + ((B & 0x8000) != 0 ? $"translucency -= {B & 0xFF}" : $"translucency += {B & 0xFF}"); break;
                case 0x45: yield return $"if ({any}translucency {Lt(B, 0x1000)} {B & 0xFF}) {Tail(p, B >> 13)}"; break;
                case 0x42: case 0x44:
                {
                    string prop = ScaleName(B & 0x7000);
                    if (prop == null) break;
                    string opr = cmd == 0x42 ? "=" : (B & 0x8000) != 0 ? "-=" : "+=";
                    yield return e + $"{prop} {opr} {Printer.Scale(B & 0xFFF)}";
                    break;
                }
                case 0x46: case 0x47: case 0x48:
                    yield return $"if ({any}scale_{(char)('x' + cmd - 0x46)} {Lt(B, 0x1000)} {Printer.Scale(B & 0xFFF)}) {Tail(p, B >> 13)}";
                    break;
                case 0x4E: yield return e + ((B & 0x8000) != 0 ? $"fade_tint({Printer.Rgb(B)})" : $"tint = {Printer.Rgb(B)}"); break;
                case 0x4F: yield return $"if ({any}tint != {Printer.Rgb(B)}) {((B & 0x8000) != 0 ? "goto " + p.Label(1) : "end")}"; break;
                case 0x5A: yield return $"if ({any}hit_points {Lt(B, 0x1000)} {B & 0xFFF}) {Tail(p, B >> 13)}"; break;
            }
        }

        static string ScaleName(int excluded)
        {
            if (excluded == 0) return "scale";
            if (excluded == 0x7000) return null;
            var sb = new StringBuilder("scale_");
            for (int k = 0; k < 3; k++) if ((excluded & (0x1000 << k)) == 0) sb.Append((char)('x' + k));
            return sb.ToString();
        }

        /// <summary>Writes the parts of statements, with names (names set) or numbers only (names null).</summary>
        sealed class Printer
        {
            readonly TriggerNames names;
            readonly int slot;
            public Printer(TriggerNames n, int s) { names = n; slot = s; }

            public string Num(int v) => v < 256 ? v.ToString(CultureInfo.InvariantCulture) : "0x" + v.ToString("X", CultureInfo.InvariantCulture);

            public string Class(int a) => a == 0x1FFF ? "any" : names?.ClassName(a) ?? a.ToString(CultureInfo.InvariantCulture);

            public string Link(int lf) => lf == 0x10 ? "link" : lf > 0x10 ? $"link + {lf - 0x10}" : $"link - {0x10 - lf}";

            public string Sel(int a, int lf) => $"class == {Class(a & 0x1FFF)}" + (lf == 0 ? "" : ", " + Link(lf));

            public string Global(int g) => names?.GlobalName(g) ?? g.ToString(CultureInfo.InvariantCulture);

            public string Label(int n) => names?.LabelName(slot, n) ?? $"L{n}";

            public string LabelDef(int n) => names?.LabelName(slot, n) is string s ? $"{s} #{n}:" : $"L{n}:";

            public string Suffix(int a, int lf, bool aUsed, bool lfUsed)
            {
                var items = new List<string>();
                if (!aUsed && a != 0) items.Add("A=" + (a < 0x1FFF && names?.ClassName(a) is string s ? s : a.ToString(CultureInfo.InvariantCulture)));
                if (!lfUsed && lf != 0x10) items.Add(lf == 0 ? "L=any" : lf > 0x10 ? $"L=+{lf - 0x10}" : $"L=-{0x10 - lf}");
                return items.Count == 0 ? "" : " [" + string.Join(", ", items) + "]";
            }

            public string Bits(string group, int v)
            {
                var parts = new List<string>();
                int rest = v;
                foreach (var (name, bit) in TriggerNames.Groups[group])
                    if (bit != 0 && (rest & bit) == bit) { parts.Add(name); rest &= ~bit; }
                if (rest != 0 || parts.Count == 0) parts.Add(Num(rest));
                return string.Join(" | ", parts);
            }

            public string Flags(int v)
            {
                var parts = new List<string>();
                int rest = v;
                for (int bit = 1; bit <= 0x1000; bit <<= 1)
                    if ((rest & bit) != 0 && names?.FlagName(bit) is string s) { parts.Add(s); rest &= ~bit; }
                if (rest != 0 || parts.Count == 0) parts.Add("0x" + rest.ToString("X", CultureInfo.InvariantCulture));
                return string.Join(" | ", parts);
            }

            public static string Seconds(int half) => (half / 2).ToString(CultureInfo.InvariantCulture) + (half % 2 == 1 ? ".5s" : "s");

            public static string Scale(int v) => (v / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

            public static string Clock(int minutes) => minutes < 24 * 60 ? $"{minutes / 60}:{minutes % 60:00}" : minutes.ToString(CultureInfo.InvariantCulture);

            public static string Rgb(int b) => $"rgb({(b >> 10) & 31}, {(b >> 5) & 31}, {b & 31})";
        }
    }
}
