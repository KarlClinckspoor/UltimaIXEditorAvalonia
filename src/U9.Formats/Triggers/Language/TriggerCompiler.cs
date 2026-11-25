using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace U9.Formats.Triggers.Language
{
    /// <summary>An error or warning of the compiler, at a place in the text (Line and Column from 1).</summary>
    public sealed class TriggerDiagnostic
    {
        public int Line, Column, Offset, Length;
        public string Message;
        public bool Warning;
        public override string ToString() => $"{Line}:{Column}: {(Warning ? "warning" : "error")}: {Message}";
    }

    public sealed class TriggerCompileResult
    {
        /// <summary>The ops, with the end op (unless the text says no_end); null when there are errors.</summary>
        public TriggerOp[] Ops;
        public readonly List<TriggerDiagnostic> Diagnostics = new List<TriggerDiagnostic>();
        /// <summary>The names of the labels the text named, by label number.</summary>
        public readonly Dictionary<int, string> LabelNames = new Dictionary<int, string>();
        /// <summary>The text line each op came from (the end op: 0).</summary>
        public readonly List<int> OpLines = new List<int>();
        public bool Ok => Ops != null;
    }

    /// <summary>
    /// Compiles the trigger language (docs/trigger-language.md) into a script's ops. One statement makes one op;
    /// statements end at a newline or ';'.
    /// </summary>
    public static class TriggerCompiler
    {
        /// <summary>The commands u9.exe's interpreter does itself (0x30 only with A = 0xD7); the rest go to objects.</summary>
        public static readonly HashSet<int> Interpreted = new HashSet<int>
        {
            0x00, 0x0D, 0x0E, 0x11, 0x12, 0x13, 0x14, 0x16, 0x17, 0x1F, 0x21, 0x2A, 0x2C, 0x2F, 0x31, 0x33, 0x34, 0x35, 0x36,
            0x37, 0x3A, 0x3E, 0x3F, 0x4B, 0x4C, 0x4D, 0x52, 0x53, 0x58, 0x5B, 0x5C, 0x5E, 0x5F, 0x60, 0x61, 0x62, 0x63, 0x64,
        };

        /// <summary>The tests that jump to label B >> 13 when they pass.</summary>
        public static readonly HashSet<int> Label13 = new HashSet<int> { 0x0E, 0x29, 0x3A, 0x3B, 0x45, 0x46, 0x47, 0x48, 0x4B, 0x53, 0x58, 0x5A, 0x5B, 0x61, 0x64 };

        public static readonly string[] Compares = { "==", "!=", ">", "<", ">=", "<=" };

        public const int MaxOps = 84;

        public static TriggerCompileResult Compile(string text, TriggerNames names, int slot = 0)
        {
            var c = new Compiler(TriggerLexer.Scan(text), names ?? new TriggerNames(), slot);
            return c.Run();
        }

        sealed class ParseError : Exception
        {
            public readonly Token At;
            public ParseError(Token at, string message) : base(message) { At = at; }
        }

        enum Jump { None, Goto, L13, L1A, Cmp, Tint }

        sealed class Stmt
        {
            public int Cmd, A, Lf = 0x10, B;
            public bool Interp, ASet, LfSet;
            public Jump Jump;
            public Token LabelRef;          // null: end
            public int Line;
        }

        sealed class LabelDef { public string Name; public int? Pinned; public Token At; public int Number; }

        sealed class Compiler
        {
            readonly List<Token> t;
            readonly TriggerNames names;
            readonly int slot;
            int p;
            readonly TriggerCompileResult r = new TriggerCompileResult();
            readonly List<Stmt> stmts = new List<Stmt>();
            readonly List<(LabelDef Def, int Index)> labelDefs = new List<(LabelDef, int)>();
            bool noEnd;
            static readonly Regex Numbered = new Regex(@"^L(\d+)$", RegexOptions.CultureInvariant);

            public Compiler(List<Token> tokens, TriggerNames n, int s) { t = tokens; names = n; slot = s; }

            Token Peek(int k = 0) => t[Math.Min(p + k, t.Count - 1)];
            Token Next() => t[Math.Min(p++, t.Count - 1)];
            bool Is(string s, int k = 0) => Peek(k).Kind != TokenKind.Comment && Peek(k).Text == s && Peek(k).Kind != TokenKind.Newline;
            bool Accept(string s) { if (Is(s)) { p++; return true; } return false; }
            Token Expect(string s)
            {
                if (!Is(s)) throw new ParseError(Peek(), $"expected '{s}'{Got()}");
                return Next();
            }
            string Got() => Peek().Kind == TokenKind.Newline || Peek().Kind == TokenKind.End ? " before the end of the line" : $", found '{Peek().Text}'";
            bool AtStatementEnd => Peek().Kind == TokenKind.Newline || Peek().Kind == TokenKind.End || Is(";");

            void Warn(Token at, string msg) => r.Diagnostics.Add(new TriggerDiagnostic { Line = at.Line, Column = at.Column, Offset = at.Offset, Length = Math.Max(1, at.Length), Message = msg, Warning = true });
            void Error(Token at, string msg) => r.Diagnostics.Add(new TriggerDiagnostic { Line = at.Line, Column = at.Column, Offset = at.Offset, Length = Math.Max(1, at.Length), Message = msg });

            public TriggerCompileResult Run()
            {
                while (Peek().Kind != TokenKind.End)
                {
                    if (Peek().Kind == TokenKind.Newline || Is(";")) { p++; continue; }
                    try
                    {
                        Statement();
                        if (!AtStatementEnd) throw new ParseError(Peek(), $"unexpected '{Peek().Text}' after the statement");
                    }
                    catch (ParseError e)
                    {
                        Error(e.At, e.Message);
                        while (!AtStatementEnd) p++;
                    }
                }
                ResolveLabels();
                CheckFlow();
                if (stmts.Count > MaxOps) Error(t[0], $"a script holds at most {MaxOps} ops (the game reads 85 with the end); this one has {stmts.Count}");
                if (r.Diagnostics.Any(d => !d.Warning)) return r;
                var ops = new List<TriggerOp>();
                foreach (var s in stmts)
                {
                    ops.Add(new TriggerOp((ushort)((s.Lf << 8) | s.Cmd), (ushort)s.A, (ushort)s.B));
                    r.OpLines.Add(s.Line);
                }
                if (!noEnd) { ops.Add(new TriggerOp(0x1000 | TriggerOp.End, 0, 0)); r.OpLines.Add(0); }
                r.Ops = ops.ToArray();
                return r;
            }

            // ---- statements

            void Statement()
            {
                var first = Peek();
                // label: NAME [#n] ':'
                if (first.Kind == TokenKind.Name && (Is(":", 1) || (Is("#", 1) && Is(":", 3))) && !Keywords.Contains(first.Text))
                {
                    p++;
                    int? pinned = null;
                    var m = Numbered.Match(first.Text);
                    if (m.Success) pinned = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (Accept("#"))
                    {
                        if (m.Success) throw new ParseError(first, "an L<n> label has its number already");
                        pinned = Int(0, 0xFFFF, "a label number");
                    }
                    Expect(":");
                    if (!m.Success && first.Text.Any(char.IsUpper)) Warn(first, "label names are lower_snake");
                    var def = new LabelDef { Name = m.Success ? null : first.Text, Pinned = pinned, At = first };
                    if (def.Name != null && labelDefs.Any(l => l.Def.Name == def.Name)) throw new ParseError(first, $"label '{def.Name}' is defined twice");
                    var st = Interp(0x16, first);
                    labelDefs.Add((def, stmts.Count));
                    Suffix(st);
                    Add(st);
                    if (!AtStatementEnd) Statement();
                    return;
                }
                if (first.Kind != TokenKind.Name) throw new ParseError(first, $"a statement cannot start with '{first.Text}'");
                Stmt s;
                switch (first.Text)
                {
                    case "if": s = If(); break;
                    case "goto": p++; s = Interp(0x17, first); s.Jump = Jump.Goto; s.LabelRef = LabelRef(); break;
                    case "end": p++; s = Interp(0x17, first); s.Jump = Jump.Goto; break;
                    case "nop": p++; s = Interp(0x00, first); if (Accept("(")) { s.B = Int(0, 0xFFFF); Expect(")"); } break;
                    case "once": p++; s = Interp(0x13, first); break;
                    case "no_end":
                        p++;
                        noEnd = true;
                        Warn(first, "no end op is added: unless one of the ops is an end, the game runs on into whatever its buffer held");
                        if (!AtStatementEnd) throw new ParseError(Peek(), "no_end stands alone");
                        while (Peek().Kind == TokenKind.Newline || Is(";")) p++;
                        if (Peek().Kind != TokenKind.End) throw new ParseError(Peek(), "no_end must be the last statement");
                        return;
                    case "link": s = LinkStatement(); break;
                    case "range": case "square_range": s = Range(); break;
                    case "options": p++; s = Interp(0x14, first); Expect("("); s.B = Bits("options", 0xFFFF); Expect(")"); break;
                    case "pick":
                        p++; s = Interp(0x21, first); Expect("(");
                        var nt = Peek(); s.B = Int(0, 0xFFFF);
                        if (s.B < 1 || s.B > 1023) Warn(nt, "the game picks only 1 to 1023 objects; with this number the op does nothing");
                        Expect(","); Selection(s, true); s.ASet = s.LfSet = true; Expect(")");
                        break;
                    case "change_map": s = ChangeMap(); break;
                    case "movie": p++; s = Interp(0x37, first); Expect("("); s.B = Const("movie", 0xFFFF); Expect(")"); break;
                    case "use_trigger": p++; s = Interp(0x30, first); s.A = 0xD7; s.ASet = true; Expect("("); s.B = Int(0, 0xFFFF); Expect(")"); break;
                    case "global": s = GlobalSet(); break;
                    case "clock": s = ClockSet(); break;
                    case "npc": s = Npc(); break;
                    case "avatar": s = AvatarSet(); break;
                    case "raw":
                        p++; Expect("(");
                        int op = Int(0, 0xFFFF); Expect(","); int a = Int(0, 0xFFFF); Expect(","); int b = Int(0, 0xFFFF); Expect(")");
                        s = new Stmt { Cmd = op & 0xFF, Lf = op >> 8, A = a, B = b, Line = first.Line };
                        Add(s);
                        return;
                    case "each": s = Each(); break;
                    default:
                        if (OpName(first.Text, out int cmd))
                        {
                            p++;
                            if (!Interpreted.Contains(cmd)) throw new ParseError(first, $"command 0x{cmd:X2} goes to objects: write each(class == ..., link).{first.Text}(...)");
                            s = Interp(cmd, first); Expect("("); s.B = Int(0, 0xFFFF); Expect(")");
                            break;
                        }
                        throw new ParseError(first, $"unknown statement '{first.Text}'");
                }
                if (s.Interp) Suffix(s);
                Add(s);
            }

            void Add(Stmt s) => stmts.Add(s);

            Stmt Interp(int cmd, Token at) => new Stmt { Cmd = cmd, Interp = true, Line = at.Line };

            void Suffix(Stmt s)
            {
                if (!Accept("[")) return;
                do
                {
                    var key = Next();
                    Expect("=");
                    if (key.Text == "A")
                    {
                        if (s.ASet) throw new ParseError(key, "this statement sets A itself");
                        s.A = ClassOrNumber(0xFFFF); s.ASet = true;
                    }
                    else if (key.Text == "L")
                    {
                        if (s.LfSet) throw new ParseError(key, "this statement sets the link field itself");
                        if (Accept("any")) s.Lf = 0;
                        else
                        {
                            int sign = Accept("-") ? -1 : (Accept("+") ? 1 : 0);
                            if (sign == 0) throw new ParseError(Peek(), "expected any, +n or -n");
                            s.Lf = 0x10 + sign * Int(0, 15);
                        }
                        s.LfSet = true;
                    }
                    else throw new ParseError(key, "expected A= or L= (fields the game ignores for this statement)");
                } while (Accept(","));
                Expect("]");
            }

            Stmt LinkStatement()
            {
                var at = Next();
                var s = Interp(0x11, at);
                if (Accept("=")) { s.B = Int(0, 0xFFFF); }
                else if (Accept("+=")) { s.Cmd = 0x12; s.B = Int(0, 0x7FFF); }
                else if (Accept("-=")) { s.Cmd = 0x12; s.B = 0x8000 | Int(0, 0x7FFF); }
                else throw new ParseError(Peek(), "expected =, += or -= after link");
                return s;
            }

            Stmt Range()
            {
                var at = Next();
                var s = Interp(0x2C, at);
                Expect("=");
                if (Accept("default")) { s.B = 0; return s; }
                var n = Peek();
                var (v, unit) = Number(n);
                if (unit == "u")
                {
                    if (v != Math.Floor(v) || v < 0 || v > 0x7FFF) throw new ParseError(n, "object units run from 0 to 32767");
                    s.B = 0x8000 | (int)v;
                    p++;
                    return s;
                }
                if (unit != "" && unit != "pt") throw new ParseError(n, "a range is in points (10, 10pt) or object units (1280u)");
                if (v != Math.Floor(v)) throw new ParseError(n, "the range in points is a whole number; write it in object units (448u) for fractions");
                if (v < 1 || v > 0x7FFF) throw new ParseError(n, "points run from 1 to 32767 (0 is range = default)");
                s.B = (int)v;
                p++;
                return s;
            }

            Stmt ChangeMap()
            {
                var at = Next();
                var s = Interp(0x1F, at);
                s.Lf = 0; s.LfSet = true;
                Expect("(");
                int map = Int(0, 0xFF, "a map number");
                int flags = 0;
                while (Accept(","))
                {
                    if (Is("link")) s.Lf = LinkPart();
                    else if (Is("flags") && Is(":", 1)) { p += 2; flags = Int(0, 0xFF); }
                    else throw new ParseError(Peek(), "expected link ± n or flags: n");
                }
                Expect(")");
                s.B = map | (flags << 8);
                return s;
            }

            Stmt GlobalSet()
            {
                var at = Next();
                var s = Interp(0x0D, at);
                int g = GlobalIndex();
                Expect("=");
                s.B = g | (Int(0, 0x7F, "a global's value") << 9);
                return s;
            }

            int GlobalIndex()
            {
                Expect("[");
                var n = Peek();
                int g;
                if (n.Kind == TokenKind.Name)
                {
                    if (!names.TryGlobal(n.Text, out g)) throw new ParseError(n, $"unknown global '{n.Text}'");
                    p++;
                }
                else g = Int(0, 0x1FF, "a global number");
                Expect("]");
                return g;
            }

            Stmt ClockSet()
            {
                var at = Next();
                var s = Interp(0x3F, at);
                bool add;
                if (Accept("=")) add = false;
                else if (Accept("+=")) add = true;
                else throw new ParseError(Peek(), "expected = or += after clock");
                s.B = Minutes(0x7FFF) | (add ? 0x8000 : 0);
                return s;
            }

            Stmt Npc()
            {
                var at = Next();
                var s = Interp(0x31, at);
                Expect("[");
                s.A = Int(0, 0xFFFF, "an NPC number"); s.ASet = true;
                Expect("]");
                Expect(".");
                if (Accept("schedule")) { Expect("="); s.B = Int(0, 0xFF); }
                else if (Accept("push_schedule")) { Expect("("); s.B = 0x100 | Int(0, 0xFF); Expect(")"); }
                else throw new ParseError(Peek(), "expected schedule = n or push_schedule(n)");
                return s;
            }

            Stmt AvatarSet()
            {
                var at = Next();
                Expect(".");
                if (Accept("mana"))
                {
                    if (Accept("=")) { var s = Interp(0x52, at); s.B = Int(0, 0xFFF); return s; }
                    if (Accept("+=")) { var s = Interp(0x5F, at); s.B = 0x8000 | Int(0, 0xFF); return s; }
                    if (Accept("-=")) { var s = Interp(0x5F, at); s.B = Int(0, 0xFF); return s; }
                    throw new ParseError(Peek(), "expected =, += or -=");
                }
                if (Accept("attribute"))
                {
                    var s = Interp(0x62, at);
                    Expect("["); int a = Int(0, 15, "an attribute number"); Expect("]");
                    if (Accept("+=")) { ExpectOne(); s.B = a; }
                    else if (Accept("-=")) { ExpectOne(); s.B = a | 0x100; }
                    else if (Accept("=")) s.B = a | (Int(0, 15) << 4) | 0x200;
                    else throw new ParseError(Peek(), "expected += 1, -= 1 or = n");
                    return s;
                }
                throw new ParseError(Peek(), "expected avatar.mana or avatar.attribute[n]");
            }

            void ExpectOne()
            {
                var n = Peek();
                if (Int(0, 0xFFFF) != 1) throw new ParseError(n, "the game only steps by 1 here");
            }

            Stmt Each()
            {
                var at = Next();
                var s = new Stmt { Line = at.Line };
                Expect("(");
                Selection(s, false);
                Expect(")");
                Expect(".");
                var m = Next();
                if (m.Kind != TokenKind.Name) throw new ParseError(m, "expected an action after each(...).");
                switch (m.Text)
                {
                    case "activate": s.Cmd = 0x01; Expect("("); s.B = Int(0, 0xFFFF); Expect(")"); break;
                    case "move":
                    {
                        s.Cmd = 0x01; Expect("(");
                        var tt = Peek();
                        var (v, unit) = Number(tt);
                        if (unit != "s") throw new ParseError(tt, "the time of a move is in seconds: 2.5s");
                        double half = v * 2;
                        if (half != Math.Floor(half) || half > 0x7FF) throw new ParseError(tt, "a move takes half seconds, 0 to 1023.5s");
                        p++;
                        int flags = 0;
                        if (Accept(",")) flags = Bits("move", 0xF800);
                        Expect(")");
                        s.B = (int)half | flags;
                        break;
                    }
                    case "run_script": s.Cmd = 0x28; Expect("("); s.B = Const("script", 3); Expect(")"); break;
                    case "link":
                        if (Accept("=")) { s.Cmd = 0x02; s.B = Int(0, 0xFFFF); }
                        else if (Accept("+=")) { s.Cmd = 0x57; s.B = Int(0, 0xFFF); }
                        else if (Accept("-=")) { s.Cmd = 0x57; s.B = 0x4000 | Int(0, 0xFFF); }
                        else throw new ParseError(Peek(), "expected =, += or -=");
                        break;
                    case "type": s.Cmd = 0x04; Expect("="); s.B = Int(0, 0xFFFF); break;
                    case "variable":
                        if (Accept("+="))
                        {
                            ExpectOne(); s.Cmd = 0x19;
                        }
                        else
                        {
                            Expect("=");
                            if (Accept("random")) { s.Cmd = 0x27; Expect("("); s.B = Int(0, 0xFFFF); Expect(")"); }
                            else { s.Cmd = 0x18; s.B = Int(0, 0xFFFF); }
                        }
                        break;
                    case "translucency":
                        if (Accept("=")) { s.Cmd = 0x41; s.B = Int(0, 0xFF); }
                        else if (Accept("+=")) { s.Cmd = 0x43; s.B = Int(0, 0xFF); }
                        else if (Accept("-=")) { s.Cmd = 0x43; s.B = 0x8000 | Int(0, 0xFF); }
                        else throw new ParseError(Peek(), "expected =, += or -=");
                        break;
                    case "hit_points": case "quantity": case "owner": case "hint_level":
                    {
                        s.Cmd = 0x03; Expect("=");
                        int idx = m.Text == "quantity" ? 0 : m.Text == "hit_points" ? 1 : m.Text == "owner" ? 2 : 3;
                        s.B = idx | (Int(0, 0xFFF) << 4);
                        break;
                    }
                    case "tint": s.Cmd = 0x4E; Expect("="); s.B = Rgb(); break;
                    case "fade_tint": s.Cmd = 0x4E; Expect("("); s.B = 0x8000 | Rgb(); Expect(")"); break;
                    case "set_flags": s.Cmd = 0x06; Expect("("); s.B = Flags(0xFFFF); Expect(")"); break;
                    case "clear_flags": s.Cmd = 0x07; Expect("("); s.B = Flags(0xFFFF); Expect(")"); break;
                    case "toggle_flags": s.Cmd = 0x08; Expect("("); s.B = Flags(0xFFFF); Expect(")"); break;
                    case "activity": s.Cmd = 0x40; Expect("("); s.B = Int(0, 0x7F); Expect(","); s.B |= Int(0, 0x1FF) << 7; Expect(")"); break;
                    case "special": s.Cmd = 0x3D; Expect("("); s.B = Int(0, 0xFFFF); Expect(")"); break;
                    default:
                        if (ScaleAxes(m.Text) is int ex)
                        {
                            if (Accept("=")) { s.Cmd = 0x42; s.B = Scale() | ex; }
                            else if (Accept("+=")) { s.Cmd = 0x44; s.B = Scale() | ex; }
                            else if (Accept("-=")) { s.Cmd = 0x44; s.B = 0x8000 | Scale() | ex; }
                            else throw new ParseError(Peek(), "expected =, += or -=");
                            break;
                        }
                        if (OpName(m.Text, out int cmd))
                        {
                            if (Interpreted.Contains(cmd)) throw new ParseError(m, $"command 0x{cmd:X2} is done by the interpreter: write {m.Text}(...) on its own");
                            s.Cmd = cmd; Expect("("); s.B = Int(0, 0xFFFF); Expect(")");
                            break;
                        }
                        throw new ParseError(m, $"unknown action '{m.Text}'");
                }
                return s;
            }

            /// <summary>The excluded-axis bits of op 42/44 for a scale property name (scale, scale_x, scale_xy ...), or null.</summary>
            static int? ScaleAxes(string name)
            {
                if (name == "scale") return 0;
                if (!name.StartsWith("scale_", StringComparison.Ordinal)) return null;
                string ax = name.Substring(6);
                if (ax.Length == 0 || ax.Length > 2 || ax.Any(c => c < 'x' || c > 'z') || ax.Distinct().Count() != ax.Length) return null;
                int ex = 0x7000;
                foreach (char c in ax) ex &= ~(0x1000 << (c - 'x'));
                return ex;
            }

            int Scale()
            {
                var n = Peek();
                var (v, unit) = Number(n);
                if (unit != "") throw new ParseError(n, "a scale has no unit");
                double h = Math.Round(v * 100);
                if (Math.Abs(h - v * 100) > 1e-6 || h < 0 || h > 0xFFF) throw new ParseError(n, "a scale runs from 0 to 40.95 in steps of 0.01");
                p++;
                return (int)h;
            }

            int Rgb()
            {
                Expect("rgb"); Expect("(");
                int a = Int(0, 31); Expect(","); int b = Int(0, 31); Expect(","); int c = Int(0, 31);
                Expect(")");
                return (a << 10) | (b << 5) | c;
            }

            // ---- tests

            Stmt If()
            {
                var at = Next();
                Expect("(");
                var s = Test(at);
                Expect(")");
                if (Accept("goto")) s.LabelRef = LabelRef();
                else if (!Accept("end")) throw new ParseError(Peek(), "expected goto label or end after the test");
                return s;
            }

            Stmt Test(Token at)
            {
                var f = Peek();
                Stmt s;
                switch (f.Text)
                {
                    case "any":
                    {
                        p++;
                        s = new Stmt { Line = at.Line };
                        Expect("("); Selection(s, false); Expect(")");
                        Expect(".");
                        var m = Next();
                        switch (m.Text)
                        {
                            case "has_any_flags": s.Cmd = 0x29; s.Jump = Jump.L13; Expect("("); s.B = Flags(0x1FFF); Expect(")"); return s;
                            case "has_all_flags": s.Cmd = 0x3B; s.Jump = Jump.L13; Expect("("); s.B = Flags(0x1FFF); Expect(")"); return s;
                            case "variable":
                            {
                                s.Cmd = 0x1A; s.Jump = Jump.L1A;
                                bool lt = TwoWay();
                                s.B = (Int(0, 0xFF) << 4) | (lt ? 1 : 0);
                                return s;
                            }
                            case "translucency": s.Cmd = 0x45; s.Jump = Jump.L13; s.B = Limit(0xFF); return s;
                            case "hit_points": s.Cmd = 0x5A; s.Jump = Jump.L13; s.B = Limit(0xFFF); return s;
                            case "scale_x": case "scale_y": case "scale_z":
                            {
                                s.Cmd = 0x46 + (m.Text[6] - 'x'); s.Jump = Jump.L13;
                                bool lt = TwoWay();
                                s.B = Scale() | (lt ? 0x1000 : 0);
                                return s;
                            }
                            case "tint":
                                s.Cmd = 0x4F; s.Jump = Jump.Tint;
                                Expect("!=");
                                s.B = Rgb();
                                return s;
                        }
                        throw new ParseError(m, $"unknown test '{m.Text}' (variable, translucency, hit_points, scale_x/y/z, tint, has_any_flags, has_all_flags)");
                    }
                    case "count":
                    case "count_flagged":
                        p++;
                        s = new Stmt { Line = at.Line, Cmd = f.Text == "count" ? 0x15 : 0x2D, Jump = Jump.Cmp };
                        Expect("("); Selection(s, false); Expect(")");
                        s.B = Compare(0xFF);
                        return s;
                    case "link":
                        p++;
                        s = Interp(0x2F, at); s.Jump = Jump.Cmp; s.LfSet = true;
                        s.Lf = 0;
                        if (Is("+") || Is("-"))
                        {
                            int sign = Accept("-") ? -1 : (Accept("+") ? 1 : 0);
                            s.Lf = 0x10 + sign * Int(0, 15);
                        }
                        s.B = Compare(0xFF);
                        return s;
                    case "global":
                    {
                        p++;
                        s = Interp(0x0E, at); s.Jump = Jump.L13;
                        int g = GlobalIndex();
                        bool lt = TwoWay();
                        int v = Int(0, 15, "a global compares with 0 to 15");
                        if (v >= 8) { s.Cmd = 0x5B; v -= 8; }
                        s.B = g | (v << 9) | (lt ? 0x1000 : 0);
                        return s;
                    }
                    case "clock":
                    {
                        p++;
                        s = Interp(0x3A, at); s.Jump = Jump.L13;
                        bool lt = TwoWay();
                        s.B = Minutes(0xFFF) | (lt ? 0x1000 : 0);
                        return s;
                    }
                    case "avatar":
                    {
                        p++;
                        Expect(".");
                        var m = Next();
                        if (m.Text == "quantity")
                        {
                            s = Interp(0x4B, at); s.Jump = Jump.L13;
                            Expect("("); Selection(s, true); s.ASet = s.LfSet = true; Expect(")");
                            s.B = Limit(0xFFF);
                            return s;
                        }
                        if (m.Text == "mana") { s = Interp(0x53, at); s.Jump = Jump.L13; s.B = Limit(0xFFF); return s; }
                        if (m.Text == "attribute")
                        {
                            s = Interp(0x61, at); s.Jump = Jump.L13;
                            Expect("["); int a = Int(0, 15, "an attribute number"); Expect("]");
                            bool lt = TwoWay();
                            s.B = a | (Int(0, 15) << 4) | (lt ? 0x1000 : 0);
                            return s;
                        }
                        throw new ParseError(m, "expected avatar.quantity(...), avatar.mana or avatar.attribute[n]");
                    }
                    default:
                        if (f.Kind == TokenKind.Name && OpName(f.Text, out int cmd))
                        {
                            p++;
                            if (!Label13.Contains(cmd) || !Interpreted.Contains(cmd)) throw new ParseError(f, $"command 0x{cmd:X2} is not a test written this way");
                            s = Interp(cmd, at); s.Jump = Jump.L13;
                            Expect("("); s.B = Int(0, 0x1FFF); Expect(")");
                            return s;
                        }
                        throw new ParseError(f, $"unknown test '{f.Text}'");
                }
            }

            bool TwoWay()
            {
                if (Accept("<")) return true;
                if (Accept(">")) return false;
                throw new ParseError(Peek(), $"this test compares with < or > only{Got()}");
            }

            int Limit(int max)
            {
                bool lt = TwoWay();
                return Int(0, max) | (lt ? 0x1000 : 0);
            }

            int Compare(int max)
            {
                var c = Peek();
                int k = Array.IndexOf(Compares, c.Text);
                if (k < 0 || c.Kind != TokenKind.Punct) throw new ParseError(c, $"expected a comparison (== != < > <= >=){Got()}");
                p++;
                return k | (Int(0, max) << 8);
            }

            // ---- pieces

            /// <summary>class == X [, link ± n]: sets A (low 13 bits) and the link field (0 when no link is written).</summary>
            void Selection(Stmt s, bool linkMeansCurrent)
            {
                var ct = Expect("class");
                Expect("==");
                var at = Peek();
                if (Accept("any")) s.A = 0x1FFF;
                else s.A = ClassOrNumber(0x1FFE);
                s.Lf = 0;
                if (Accept(",")) s.Lf = LinkPart();
                if (s.A == 0x1FFF && s.Lf == 0 && !linkMeansCurrent) throw new ParseError(at, "class == any with no link reaches nothing in the game: give a link");
            }

            int LinkPart()
            {
                Expect("link");
                if (Accept("+")) return 0x10 + Int(0, 15, "a link offset");
                if (Accept("-")) return 0x10 - Int(0, 15, "a link offset");
                return 0x10;
            }

            int ClassOrNumber(int max)
            {
                var n = Peek();
                if (n.Kind == TokenKind.Name)
                {
                    if (!names.TryClass(n.Text, out int cls)) throw new ParseError(n, $"unknown class '{n.Text}'");
                    p++;
                    return cls;
                }
                int v = Int(0, max);
                if (v < 0x1FFF && !names.IsClass(v) && names.ClassOf(v) != v)
                    Warn(n, $"type {v} is not a class: the game reaches the types of class {names.ClassOf(v)} (TYPES.DAT usecode), so write class == {names.ClassOf(v)}");
                return v;
            }

            Token LabelRef()
            {
                var n = Next();
                if (n.Kind != TokenKind.Name || Keywords.Contains(n.Text)) throw new ParseError(n, "expected a label name");
                return n;
            }

            int Bits(string group, int max) => FlagExpr(max, n =>
            {
                foreach (var (name, v) in TriggerNames.Groups[group]) if (name == n) return v;
                return null;
            });

            int Flags(int max) => FlagExpr(max, n => names.TryFlag(n, out int b) ? b : (int?)null);

            int FlagExpr(int max, Func<string, int?> lookup)
            {
                int v = 0;
                do
                {
                    var n = Peek();
                    if (n.Kind == TokenKind.Name)
                    {
                        int? x = lookup(n.Text);
                        if (x == null) throw new ParseError(n, $"unknown constant '{n.Text}' here");
                        v |= x.Value;
                        p++;
                    }
                    else v |= Int(0, max);
                } while (Accept("|"));
                if ((v & ~max) != 0) throw new ParseError(Peek(), $"bits 0x{v & ~max:X} do not fit here");
                return v;
            }

            int Const(string group, int max)
            {
                var n = Peek();
                if (n.Kind == TokenKind.Name)
                {
                    foreach (var (name, v) in TriggerNames.Groups[group]) if (name == n.Text) { p++; return v; }
                    throw new ParseError(n, $"unknown constant '{n.Text}' here");
                }
                return Int(0, max);
            }

            int Minutes(int max)
            {
                var n = Peek();
                if (n.Kind == TokenKind.Time)
                {
                    var parts = n.Text.Split(':');
                    int h = int.Parse(parts[0], CultureInfo.InvariantCulture), m = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (m > 59) throw new ParseError(n, "minutes run from 00 to 59");
                    int v = h * 60 + m;
                    if (v > max) throw new ParseError(n, $"too late: at most {max} minutes");
                    p++;
                    return v;
                }
                return Int(0, max, "minutes since midnight");
            }

            (double Value, string Unit) Number(Token n)
            {
                if (n.Kind != TokenKind.Number) throw new ParseError(n, $"expected a number{Got()}");
                string s = n.Text;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return (long.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), "");
                int u = s.Length;
                while (u > 0 && char.IsLetter(s[u - 1])) u--;
                return (double.Parse(s.Substring(0, u), CultureInfo.InvariantCulture), s.Substring(u));
            }

            int Int(int min, int max, string what = null)
            {
                var n = Peek();
                if (n.Kind != TokenKind.Number) throw new ParseError(n, $"expected {(what ?? "a number")}{Got()}");
                var (v, unit) = Number(n);
                if (unit != "") throw new ParseError(n, $"no unit here ({unit})");
                if (v != Math.Floor(v)) throw new ParseError(n, "expected a whole number");
                if (v < min || v > max) throw new ParseError(n, $"{(what ?? "the number")} must be {min} to {max}");
                p++;
                return (int)v;
            }

            static bool OpName(string s, out int cmd)
            {
                cmd = 0;
                return s.Length == 5 && s.StartsWith("op_", StringComparison.Ordinal) &&
                       int.TryParse(s.Substring(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out cmd);
            }

            // ---- labels and flow

            void ResolveLabels()
            {
                var used = new HashSet<int>();
                foreach (var (d, _) in labelDefs) if (d.Pinned is int n) { if (!used.Add(n)) Error(d.At, $"label number {n} is used twice"); }
                foreach (var s in stmts)
                    if (s.LabelRef != null && Numbered.Match(s.LabelRef.Text) is var m && m.Success) used.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                // unnumbered labels: numbers 1 to 7 first if a test jumps to them
                var tested = new HashSet<string>(stmts.Where(s => s.LabelRef != null && s.Jump != Jump.Goto).Select(s => s.LabelRef.Text));
                foreach (var (d, _) in labelDefs.OrderBy(l => tested.Contains(l.Def.Name ?? "") ? 0 : 1))
                {
                    if (d.Pinned is int n) { d.Number = n; continue; }
                    int k = 1;
                    while (used.Contains(k)) k++;
                    d.Number = k; used.Add(k);
                }
                foreach (var (d, i) in labelDefs)
                {
                    stmts[i].B = d.Number;
                    if (d.Name != null) r.LabelNames[d.Number] = d.Name;
                }
                foreach (var s in stmts)
                {
                    if (s.Jump == Jump.None) continue;
                    int n = 0;
                    if (s.LabelRef != null)
                    {
                        var m = Numbered.Match(s.LabelRef.Text);
                        var def = labelDefs.FirstOrDefault(l => l.Def.Name == s.LabelRef.Text || (m.Success && l.Def.Number == int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))).Def;
                        if (def == null && !m.Success) { Error(s.LabelRef, $"no label '{s.LabelRef.Text}'"); continue; }
                        n = def?.Number ?? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        if (def == null) Warn(s.LabelRef, $"the script has no label {n}: the jump ends the script");
                    }
                    switch (s.Jump)
                    {
                        case Jump.Goto: s.B = n; break;
                        case Jump.L13: Small(s, n); s.B |= n << 13; break;
                        case Jump.L1A: Small(s, n); s.B |= n << 1; break;
                        case Jump.Cmp: Small(s, n); s.B |= n << 5; break;
                        case Jump.Tint:
                            if (n == 1) s.B |= 0x8000;
                            else if (n != 0) Error(s.LabelRef, "a tint test can only jump to label 1 (or end)");
                            break;
                    }
                }
            }

            void Small(Stmt s, int n)
            {
                if (n > 7) Error(s.LabelRef, $"a test can only jump to labels 1 to 7; this one is {n}");
            }

            void CheckFlow()
            {
                for (int i = 0; i + 1 < stmts.Count; i++)
                {
                    var s = stmts[i];
                    bool stops = (s.Cmd == 0x1F && s.Interp) || (s.Cmd == 0x3D && s.B == 21 && !s.Interp);
                    if (stops && stmts[i + 1].Cmd != 0x16)
                        Warn(t.FirstOrDefault(x => x.Line == stmts[i + 1].Line && x.Kind != TokenKind.Newline) ?? t[0],
                             s.Cmd == 0x1F ? "change_map ends the script when it finds the arrival: this never runs then" : "special(21) ends the calling script: this does not run");
                }
            }
        }

        /// <summary>Words that are not labels or names of things.</summary>
        public static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "goto", "end", "nop", "once", "no_end", "link", "range", "square_range", "default", "options", "pick", "change_map",
            "flags", "movie", "use_trigger", "global", "clock", "npc", "avatar", "raw", "each", "any", "count", "count_flagged",
            "class", "rgb", "random",
        };

        /// <summary>The actions after each(...). and the tests after any(...)., for completion.</summary>
        public static readonly string[] Actions =
        {
            "activate", "move", "run_script", "link", "type", "variable", "translucency", "scale", "scale_x", "scale_y", "scale_z",
            "scale_xy", "scale_xz", "scale_yz", "hit_points", "quantity", "owner", "hint_level", "tint", "fade_tint", "set_flags",
            "clear_flags", "toggle_flags", "activity", "special",
        };

        public static readonly string[] Tests =
        {
            "variable", "translucency", "hit_points", "scale_x", "scale_y", "scale_z", "tint", "has_any_flags", "has_all_flags",
        };
    }
}
