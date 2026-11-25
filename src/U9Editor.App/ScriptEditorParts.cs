using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using U9.Formats.Triggers.Language;

namespace U9Editor.App
{
    /// <summary>Picks a colour for the light or the dark look.</summary>
    static class Scheme
    {
        public static bool Dark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        public static IBrush Pick(string dark, string light) => new SolidColorBrush(Color.Parse(Dark ? dark : light));
    }

    /// <summary>
    /// The script editor's colours: keywords, classes (PascalCase), constants (UPPER_SNAKE), labels, numbers,
    /// comments, and the dimmed [A=..., L=...] fields the game ignores. Each line is coloured on its own.
    /// </summary>
    public sealed class ScriptColorizer : DocumentColorizingTransformer
    {
        static readonly HashSet<string> Words = new HashSet<string>(TriggerCompiler.Keywords.Concat(new[] { "move", "activate" }), StringComparer.Ordinal);

        /// <summary>What a token on a line is drawn as: keyword, class, constant, label, number, comment, ignored, op or name.</summary>
        public static string Classify(List<Token> line, int i, bool inSuffix)
        {
            var t = line[i];
            if (t.Kind == TokenKind.Comment) return "comment";
            if (inSuffix) return "ignored";
            if (t.Kind == TokenKind.Number || t.Kind == TokenKind.Time) return "number";
            if (t.Kind != TokenKind.Name) return "punct";
            bool labelDef = i == 0 && i + 1 < line.Count && (line[i + 1].Text == ":" || line[i + 1].Text == "#");
            if (labelDef) return "label";
            if (i > 0 && line[i - 1].Text == "goto") return "label";
            if (Words.Contains(t.Text)) return "keyword";
            if (t.Text.StartsWith("op_", StringComparison.Ordinal) || t.Text == "raw") return "op";
            if (char.IsUpper(t.Text[0]) && t.Text.Any(char.IsLower)) return "class";
            if (t.Text.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_')) return "constant";
            return "name";
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            string text = CurrentContext.Document.GetText(line);
            var tokens = TriggerLexer.Scan(text, keepComments: true).Where(t => t.Kind != TokenKind.End && t.Kind != TokenKind.Newline).ToList();
            bool suffix = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Text == "[" && i + 1 < tokens.Count && (tokens[i + 1].Text == "A" || tokens[i + 1].Text == "L") && i + 2 < tokens.Count && tokens[i + 2].Text == "=") suffix = true;
                string kind = Classify(tokens, i, suffix);
                if (t.Text == "]") suffix = false;
                IBrush brush = kind switch
                {
                    "keyword" => Scheme.Pick("#7FB6E0", "#2F6DA8"),
                    "class" => Scheme.Pick("#4EC9B0", "#1E7A6A"),
                    "constant" => Scheme.Pick("#C895F2", "#7A3DB8"),
                    "label" => Scheme.Pick("#D9A441", "#8A6210"),
                    "number" => Scheme.Pick("#A8C99A", "#2B7A31"),
                    "comment" => Scheme.Pick("#7C8A74", "#66705C"),
                    "ignored" => Scheme.Pick("#6E6E6E", "#9A9A9A"),
                    "op" => Scheme.Pick("#D99A7E", "#A3412A"),
                    _ => null,
                };
                if (brush == null) continue;
                bool italic = kind == "comment" || kind == "ignored";
                ChangeLinePart(line.Offset + t.Offset, line.Offset + t.Offset + t.Length, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(brush);
                    if (italic)
                    {
                        var tf = el.TextRunProperties.Typeface;
                        el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Italic, tf.Weight));
                    }
                });
            }
        }
    }

    /// <summary>An error or warning of the live check, kept on its place in the text as the text changes.</summary>
    public sealed class ScriptProblem : TextSegment
    {
        public bool Warning;
        public string Message;
        public int Line, Column;
        public override string ToString() => $"{(Warning ? "warning" : "error")} line {Line}: {Message}";
    }

    /// <summary>Wavy underlines under the check's errors (red) and warnings (amber).</summary>
    public sealed class ScriptProblemRenderer : IBackgroundRenderer
    {
        TextSegmentCollection<ScriptProblem> problems;
        TextDocument document;

        public KnownLayer Layer => KnownLayer.Selection;

        public void Set(TextDocument doc, IEnumerable<ScriptProblem> list)
        {
            if (doc != document || problems == null) { document = doc; problems = new TextSegmentCollection<ScriptProblem>(doc); }
            problems.Clear();
            foreach (var p in list) problems.Add(p);
        }

        public List<ScriptProblem> At(int offset) => problems == null ? new List<ScriptProblem>() : problems.FindSegmentsContaining(offset).ToList();

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (problems == null || problems.Count == 0 || !textView.VisualLinesValid) return;
            var visual = textView.VisualLines;
            if (visual.Count == 0) return;
            int first = visual.First().FirstDocumentLine.Offset, last = visual.Last().LastDocumentLine.EndOffset;
            var error = new Pen(Scheme.Pick("#E07060", "#C0392B"), 1.2);
            var warn = new Pen(Scheme.Pick("#D9A441", "#9A6A10"), 1.2);
            foreach (var p in problems.FindOverlappingSegments(first, last - first))
            {
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, p))
                {
                    double y = r.Bottom - 1.5, x = r.Left, end = Math.Max(r.Right, r.Left + 4);
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(new Point(x, y), false);
                        bool up = true;
                        for (x += 2; x <= end; x += 2, up = !up) ctx.LineTo(new Point(x, up ? y - 2 : y));
                        ctx.EndFigure(false);
                    }
                    dc.DrawGeometry(null, p.Warning ? warn : error, g);
                }
            }
        }
    }

    /// <summary>One entry of the completion list.</summary>
    public sealed class ScriptCompletion : ICompletionData
    {
        public ScriptCompletion(string text, string description, double priority = 0) { Text = text; Description = description; Priority = priority; }
        public IImage Image => null;
        public string Text { get; }
        public object Content => Text;
        public object Description { get; }
        public double Priority { get; }
        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            textArea.Document.Replace(completionSegment, Text);
    }

    /// <summary>What the completion offers at a place in a line, worked out from the text before the caret.</summary>
    public static class ScriptCompletionContext
    {
        public static readonly string[] Starters =
        {
            "if", "goto", "end", "each", "link", "range", "square_range", "options", "pick", "change_map", "movie", "global", "clock",
            "npc", "avatar", "once", "nop", "use_trigger", "raw",
        };

        /// <summary>The entries for the text before the caret on its line, and the labels and names of the script.</summary>
        public static List<ScriptCompletion> For(string before, TriggerNames names, IEnumerable<string> labels)
        {
            var list = new List<ScriptCompletion>();
            string b = before.TrimEnd();
            bool Ends(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(before, pattern + @"\w*$");
            void Add(IEnumerable<string> words, string what) { foreach (var w in words) list.Add(new ScriptCompletion(w, what)); }

            if (Ends(@"each\([^)]*\)\.")) Add(TriggerCompiler.Actions, "action on each object");
            else if (Ends(@"any\([^)]*\)\.")) Add(TriggerCompiler.Tests, "test on any object");
            else if (Ends(@"avatar\.")) Add(new[] { "quantity", "mana", "attribute" }, "the Avatar");
            else if (Ends(@"npc\[\d+\]\.")) Add(new[] { "schedule", "push_schedule" }, "NPC schedule");
            else if (Ends(@"class\s*==\s*") || Ends(@"\[A=")) { Add(new[] { "any" }, "every class"); foreach (var kv in names.Classes.OrderBy(k => k.Value)) list.Add(new ScriptCompletion(kv.Value, $"class {kv.Key}")); }
            else if (Ends(@"global\[")) foreach (var kv in names.Globals.OrderBy(k => k.Value)) list.Add(new ScriptCompletion(kv.Value, $"global {kv.Key}"));
            else if (Ends(@"move\([^)]*,\s*(\w+\s*\|\s*)*")) Add(TriggerNames.Groups["move"].Select(x => x.Name), "camera move part");
            else if (Ends(@"options\(\s*(\w+\s*\|\s*)*")) Add(TriggerNames.Groups["options"].Select(x => x.Name), "option");
            else if (Ends(@"run_script\(")) Add(TriggerNames.Groups["script"].Select(x => x.Name), "trigger script slot");
            else if (Ends(@"movie\(")) Add(TriggerNames.Movies, "movie");
            else if (Ends(@"(set_flags|clear_flags|toggle_flags|has_any_flags|has_all_flags)\(\s*(\w+\s*\|\s*)*")) foreach (var kv in names.Flags.OrderBy(k => k.Key)) list.Add(new ScriptCompletion(kv.Value, $"flag bit 0x{kv.Key:X}"));
            else if (Ends(@"goto\s+")) Add(labels, "label");
            else if (Ends(@"if\s*\(\s*")) Add(new[] { "any", "count", "count_flagged", "link", "global", "clock", "avatar" }, "test");
            else if (Ends(@"\(\s*") || Ends(@",\s*")) Add(new[] { "class", "link" }, "selection");
            else if (System.Text.RegularExpressions.Regex.IsMatch(before, @"^\s*(\w+\s*(#\s*\d+\s*)?:\s*)?\w*$")) Add(Starters, "statement");
            return list;
        }
    }
}
