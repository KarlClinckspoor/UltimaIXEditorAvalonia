using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Formats.Triggers;
using U9.Formats.Triggers.Language;
using Xunit;

namespace U9.Formats.Tests
{
    public class TriggerLanguageTests
    {
        readonly ITestOutputHelper output;
        public TriggerLanguageTests(ITestOutputHelper o) { output = o; }

        static string NamesFile()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                string f = Path.Combine(d.FullName, "names", "trigger-names.txt");
                if (File.Exists(f)) return f;
            }
            return null;
        }

        static TriggerNames Names(string root) =>
            TriggerNames.Build(TypeNames.Load(Path.Combine(root, "static", "TYPENAME.FLX")), TypeTable.Load(Path.Combine(root, "static", "TYPES.DAT")), NamesFile());

        [Fact]
        public void EveryShippedScriptPrintsAndCompilesBack()
        {
            string root = GameData.Require();
            var scripts = TriggerScripts.Load(Path.Combine(root, "static", "TRIGGERS.FLX"));
            var names = Names(root);
            Assert.Empty(names.Problems);
            var forms = new Dictionary<string, int>();
            var rawCmds = new Dictionary<int, int>();
            var failures = new List<string>();
            int count = 0;
            for (int slot = 1; slot < scripts.Count; slot++)
            {
                if (!scripts.IsUsed(slot)) continue;
                count++;
                var ops = scripts[slot];
                var lines = TriggerPrinter.Lines(ops, names, slot);
                foreach (var l in lines) forms[l.Form] = forms.GetValueOrDefault(l.Form) + 1;
                foreach (var (l, op) in lines.Zip(ops))
                    if (l.Form != "named") rawCmds[op.Command] = rawCmds.GetValueOrDefault(op.Command) + 1;
                string text = string.Join("\n", lines.Select(l => l.Text));
                var r = TriggerCompiler.Compile(text, names, slot);
                if (!r.Ok || !r.Ops.SequenceEqual(ops))
                    failures.Add($"script {slot}:\n{text}\n{string.Join("\n", r.Diagnostics)}\n" +
                                 (r.Ok ? $"got {string.Join(" ", r.Ops)}\nwas {string.Join(" ", ops)}" : ""));
            }
            output.WriteLine($"{count} scripts; forms: {string.Join(", ", forms.Select(k => $"{k.Key} {k.Value}"))}");
            output.WriteLine("commands not in a named form: " + string.Join(", ", rawCmds.OrderByDescending(k => k.Value).Select(k => $"0x{k.Key:X2} {k.Value}")));
            foreach (var f in failures.Take(5)) output.WriteLine(f);
            Assert.True(failures.Count == 0, $"{failures.Count} scripts do not round-trip; first:\n{failures.FirstOrDefault()}");
        }

        static TriggerCompileResult C(string text) => TriggerCompiler.Compile(text, new TriggerNames());

        [Fact]
        public void SemicolonsAndNewlinesBothEndStatements()
        {
            var a = C("range = 10\nlink = 5\n");
            var b = C("range = 10; link = 5;");
            Assert.True(a.Ok && b.Ok);
            Assert.Equal(a.Ops, b.Ops);
            Assert.Equal(3, a.Ops.Length);
        }

        [Fact]
        public void TestsEncodeTheirLabelAndComparison()
        {
            var r = C("if (any(class == 1111, link).variable > 3) goto done\neach(class == 1111, link).variable += 1\nend\ndone:\n");
            Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
            // label 1 in bits 1-3, 3 in bits 4-11, '>' leaves bit 0 clear
            Assert.Equal(new TriggerOp(0x101A, 1111, (3 << 4) | (1 << 1)), r.Ops[0]);
            Assert.Equal(new TriggerOp(0x1016, 0, 1), r.Ops[3]);
            Assert.Equal("done", r.LabelNames[1]);
        }

        [Fact]
        public void ErrorsPointAtTheirPlace()
        {
            var r = C("range = 10\nif (any(class == 1111, link).variable == 3) end\n");
            Assert.False(r.Ok);
            var d = Assert.Single(r.Diagnostics, x => !x.Warning);
            Assert.Equal(2, d.Line);
            Assert.Contains("< or >", d.Message);
        }

        [Fact]
        public void TestsCannotReachLabelsAboveSeven()
        {
            var r = C("if (clock < 6:00) goto L9\nL9:\n");
            Assert.Contains(r.Diagnostics, d => !d.Warning && d.Message.Contains("1 to 7"));
        }

        [Fact]
        public void AnyClassNeedsALink()
        {
            Assert.False(C("each(class == any).activate(1)").Ok);
            Assert.True(C("each(class == any, link - 2).activate(1)").Ok);
        }

        [Fact]
        public void CameraMovesPrintWithTheirParts()
        {
            var op = new TriggerOp(0x1001, 2218, 0x8000 | 0x0800 | 0x2000 | 4);
            var line = TriggerPrinter.Statement(op, new TriggerNames());
            Assert.Equal("each(class == 2218, link).move(2s, GLIDE | THEN_SCRIPT1 | STRAIGHT)", line.Text);
        }

        [Fact]
        public void IgnoredFieldsKeepTheirValues()
        {
            var op = new TriggerOp(0x1111, 1134, 21);      // link = 21, with A and a link offset the game ignores
            var line = TriggerPrinter.Statement(op, new TriggerNames());
            Assert.Equal("link = 21 [A=1134, L=+1]", line.Text);
            Assert.Equal(op, C(line.Text).Ops[0]);
        }
    }
}
