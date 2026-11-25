using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using U9.Formats.Triggers;
using U9.Formats.Triggers.Language;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>What the script editor needs from the main window.</summary>
    public interface IScriptHost
    {
        TriggerScripts Scripts { get; }
        TriggerNames Names { get; }
        MapEditor Editor { get; }
        /// <summary>The selected object's four script slots (OO1, OO2, SF1, SF2; 0 where none), or null.</summary>
        int[] SelectedObjectSlots();
        /// <summary>For each script, how many objects of the open map run it (counted in one pass).</summary>
        Dictionary<int, int> ScriptUsers();
        /// <summary>Runs a script edit as one undo step of the open map.</summary>
        bool RunScriptEdit(Action a);
        /// <summary>Saves the user's names file after a rename.</summary>
        void SaveNames();
    }

    /// <summary>
    /// The trigger script editor: the scripts of TRIGGERS.FLX in the trigger language (docs/trigger-language.md),
    /// with a live check, completion, find and replace, renaming (F2), and lists of the globals and symbols.
    /// New, changed and deleted scripts go through the open map's undo steps and are saved with Ctrl+S there.
    /// </summary>
    public sealed class ScriptEditorWindow : Window
    {
        readonly IScriptHost host;
        readonly TextEditor ed;
        readonly ScriptColorizer colorizer = new ScriptColorizer();
        readonly ScriptProblemRenderer problems = new ScriptProblemRenderer();
        readonly ListBox scriptList = new ListBox();
        readonly TextBox filter = new TextBox { PlaceholderText = "Filter: script number", Margin = new Thickness(0, 0, 0, 4) };
        readonly CheckBox onlyMap = new CheckBox { Content = "Only scripts on this map", Margin = new Thickness(0, 0, 0, 4) };
        readonly ListBox problemList = new ListBox { Height = 96 };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(4, 2), TextWrapping = TextWrapping.Wrap };
        readonly TextBlock header = new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 4, 4) };
        readonly Border sidePanel = new Border { Width = 270, IsVisible = false, BorderThickness = new Thickness(1, 0, 0, 0) };
        readonly TextBlock sideTitle = new TextBlock { FontWeight = FontWeight.SemiBold, Margin = new Thickness(4) };
        readonly TextBlock sideHint = new TextBlock { FontSize = 11, Margin = new Thickness(4, 0, 4, 4), TextWrapping = TextWrapping.Wrap };
        readonly ListBox sideList = new ListBox();
        readonly DispatcherTimer checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        CompletionWindow completion;
        int slot;
        bool dirty, loading;
        Dictionary<int, int> users = new Dictionary<int, int>();
        int UsersOnMap(int s) => users.TryGetValue(s, out int n) ? n : 0;
        string side;         // "globals", "symbols" or null
        TriggerCompileResult lastCheck;

        sealed class ScriptItem
        {
            public int Slot, Ops, Users;
            public bool OfSelected;
            public override string ToString() =>
                $"{Slot,5}   {Ops,2} op{(Ops == 1 ? " " : "s")}{(Users > 0 ? $"   {Users} on map" : "")}{(OfSelected ? "   ◆" : "")}";
        }

        sealed class SideItem
        {
            public string Text, Insert, Kind;
            public int Number, Line;
            public override string ToString() => Text;
        }

        public ScriptEditorWindow(IScriptHost host)
        {
            this.host = host;
            Title = "Trigger scripts";
            Width = 1180; Height = 760;
            MinWidth = 700; MinHeight = 400;

            ed = new TextEditor
            {
                ShowLineNumbers = true,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 13,
                Document = new TextDocument(),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            ed.Options.ConvertTabsToSpaces = true;
            ed.Options.IndentationSize = 4;
            ed.TextArea.TextView.LineTransformers.Add(colorizer);
            ed.TextArea.TextView.BackgroundRenderers.Add(problems);
            ed.TextChanged += (o, e) => { if (loading) return; dirty = true; UpdateHeader(); checkTimer.Stop(); checkTimer.Start(); };
            ed.TextArea.TextEntered += OnTextEntered;
            ed.TextArea.Caret.PositionChanged += (o, e) => ShowProblemAtCaret();
            ActualThemeVariantChanged += (o, e) => ed.TextArea.TextView.Redraw();
            checkTimer.Tick += (o, e) => { checkTimer.Stop(); Check(); };

            // ---- tool bar
            Button B(string text, string tip, EventHandler<RoutedEventArgs> click)
            {
                var b = new Button { Content = text, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2) };
                ToolTip.SetTip(b, tip);
                b.Click += click;
                return b;
            }
            Control Sep() => new Border { Width = 1, Margin = new Thickness(4, 2, 8, 2), Background = Scheme.Pick("#555555", "#B0B0B0") };
            var bar = new WrapPanel { Margin = new Thickness(4) };
            bar.Children.Add(B("New", "A new, empty script in the first free slot after the shipped ones (one undo step of the open map)", (o, e) => NewScript()));
            bar.Children.Add(B("Delete", "Frees the script's slot (one undo step); objects that run it then run nothing", async (o, e) => await DeleteScript()));
            bar.Children.Add(Sep());
            bar.Children.Add(B("Apply", "Compiles the text into the script (Ctrl+Enter; one undo step, Ctrl+S in the main window saves TRIGGERS.FLX)", (o, e) => Apply()));
            bar.Children.Add(B("Revert", "Shows the script as it is now, dropping the changes in the text", (o, e) => Load(slot)));
            bar.Children.Add(Sep());
            bar.Children.Add(B("Find", "Find in the script (Ctrl+F; F3 next)", (o, e) => OpenSearch(false)));
            bar.Children.Add(B("Replace", "Find and replace (Ctrl+H)", (o, e) => OpenSearch(true)));
            bar.Children.Add(B("Rename", "Renames the label, class, global or flag at the caret (F2); classes, globals and flags are renamed in every script", async (o, e) => await Rename()));
            bar.Children.Add(Sep());
            bar.Children.Add(B("Globals", "The globals the scripts set and test, and their names", (o, e) => ToggleSide("globals")));
            bar.Children.Add(B("Symbols", "The labels, classes and constants of this script, and every named class", (o, e) => ToggleSide("symbols")));
            bar.Children.Add(B("Check", "Checks the text now (it is also checked as you type)", (o, e) => Check()));
            bar.Children.Add(Sep());
            bar.Children.Add(B("Selected object", "Shows the first script of the object selected in the main window", (o, e) => ShowSelectedObjectScript()));

            // ---- script list
            filter.TextChanged += (o, e) => FillList();
            onlyMap.IsCheckedChanged += (o, e) => FillList();
            scriptList.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
            scriptList.FontSize = 12;
            scriptList.SelectionChanged += async (o, e) =>
            {
                if (loading || scriptList.SelectedItem is not ScriptItem it || it.Slot == slot) return;
                if (dirty && !await Confirm($"Script {slot} has changes that are not applied. Drop them?")) { SelectInList(slot); return; }
                Load(it.Slot);
            };
            var left = new DockPanel { Width = 240, Margin = new Thickness(4, 0, 4, 4) };
            var leftTop = new StackPanel();
            leftTop.Children.Add(filter);
            leftTop.Children.Add(onlyMap);
            DockPanel.SetDock(leftTop, Dock.Top);
            left.Children.Add(leftTop);
            left.Children.Add(scriptList);

            // ---- side panel
            sideList.FontSize = 12;
            sideList.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
            sideList.DoubleTapped += (o, e) => UseSideItem();
            sideList.KeyDown += async (o, e) =>
            {
                if (e.Key == Key.Enter) { UseSideItem(); e.Handled = true; }
                else if (e.Key == Key.F2) { e.Handled = true; await RenameSideItem(); }
            };
            var sideDock = new DockPanel();
            var sideTop = new StackPanel();
            sideTop.Children.Add(sideTitle);
            sideTop.Children.Add(sideHint);
            DockPanel.SetDock(sideTop, Dock.Top);
            sideDock.Children.Add(sideTop);
            sideDock.Children.Add(sideList);
            sidePanel.Child = sideDock;
            sidePanel.BorderBrush = Scheme.Pick("#555555", "#B0B0B0");

            // ---- problems
            problemList.FontSize = 12;
            problemList.SelectionChanged += (o, e) =>
            {
                if (problemList.SelectedItem is ScriptProblem p && p.StartOffset <= ed.Document.TextLength)
                {
                    ed.TextArea.Caret.Offset = p.StartOffset;
                    ed.TextArea.Caret.BringCaretToView();
                    ed.TextArea.Focus();
                }
            };

            var center = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            center.Children.Add(header);
            var bottom = new StackPanel();
            bottom.Children.Add(problemList);
            bottom.Children.Add(status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            center.Children.Add(bottom);
            DockPanel.SetDock(sidePanel, Dock.Right);
            center.Children.Add(sidePanel);
            center.Children.Add(ed);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            DockPanel.SetDock(left, Dock.Left);
            root.Children.Add(left);
            root.Children.Add(center);
            Content = root;

            KeyDown += OnKeyDown;
            Closing += async (o, e) =>
            {
                if (!dirty || closeAnyway) return;
                e.Cancel = true;
                if (await Confirm($"Script {slot} has changes that are not applied. Close anyway?")) { closeAnyway = true; Close(); }
            };
            FillList();
            UpdateHeader();
        }

        bool closeAnyway;

        /// <summary>Puts text in the editor as if typed (for the --scripts-text start option), and checks it.</summary>
        public void SetText(string text)
        {
            ed.Document.Text = text;
            dirty = true;
            UpdateHeader();
            Check();
            if (side != null) FillSide();
        }

        public void ShowSide(string which) { side = null; ToggleSide(which); }

        /// <summary>Saves the window as a PNG (for the --scripts-shot start option).</summary>
        public void SaveShot(string path)
        {
            double scale = RenderScaling;
            var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            rtb.Render(this);
            rtb.Save(path);
            closeAnyway = true;
            Close();
        }

        // ---- list and loading

        void FillList()
        {
            var scripts = host.Scripts;
            if (scripts == null) { scriptList.ItemsSource = null; status.Text = "No TRIGGERS.FLX in this game folder."; return; }
            var sel = host.SelectedObjectSlots() ?? Array.Empty<int>();
            users = host.ScriptUsers() ?? new Dictionary<int, int>();
            string f = filter.Text?.Trim() ?? "";
            var items = new List<ScriptItem>();
            for (int s = 1; s < scripts.Count; s++)
            {
                if (!scripts.IsUsed(s)) continue;
                if (f.Length > 0 && !s.ToString(CultureInfo.InvariantCulture).Contains(f, StringComparison.Ordinal)) continue;
                int n = UsersOnMap(s);
                if (onlyMap.IsChecked == true && n == 0) continue;
                items.Add(new ScriptItem { Slot = s, Ops = scripts[s].Count(o => o.Command != TriggerOp.End), Users = n, OfSelected = sel.Contains(s) });
            }
            loading = true;
            scriptList.ItemsSource = items;
            SelectInList(slot);
            loading = false;
            status.Text = $"{items.Count} scripts listed. ◆ marks the selected object's scripts.";
        }

        void SelectInList(int s)
        {
            bool was = loading;
            loading = true;
            scriptList.SelectedItem = (scriptList.ItemsSource as List<ScriptItem>)?.FirstOrDefault(i => i.Slot == s);
            if (scriptList.SelectedItem != null) scriptList.ScrollIntoView(scriptList.SelectedItem);
            loading = was;
        }

        /// <summary>Shows a script (printed from its ops) and selects it in the list.</summary>
        public void Load(int s)
        {
            if (host.Scripts == null) return;
            slot = s;
            loading = true;
            ed.Document.Text = s > 0 && host.Scripts.IsUsed(s) ? TriggerPrinter.Print(host.Scripts[s], host.Names, s) : "";
            ed.Document.UndoStack.ClearAll();
            loading = false;
            dirty = false;
            SelectInList(s);
            UpdateHeader();
            Check();
            if (side != null) FillSide();
        }

        /// <summary>Opens the script, asking first if the text has unapplied changes.</summary>
        public async void Open(int s)
        {
            if (s == slot && !dirty) { Activate(); return; }
            if (dirty && !await Confirm($"Script {slot} has changes that are not applied. Drop them?")) return;
            Load(s);
            Activate();
        }

        /// <summary>The scripts changed outside the editor (an undo, another edit): refreshes the list and, when unchanged, the text.</summary>
        public void ScriptsChanged()
        {
            FillList();
            if (dirty || slot <= 0) return;
            // the text already says what the script holds (just applied): keep it, with its comments and layout
            var now = host.Scripts.IsUsed(slot) ? host.Scripts[slot] : null;
            if (now != null && lastCheck?.Ops != null && lastCheck.Ops.SequenceEqual(now)) { UpdateHeader(); return; }
            Load(slot);
        }

        void UpdateHeader()
        {
            if (slot <= 0) { header.Text = "Pick a script on the left, or make a new one."; return; }
            int n = UsersOnMap(slot);
            header.Text = $"Script {slot}{(dirty ? " (changed, not applied)" : "")}: run by {n} object{(n == 1 ? "" : "s")} on this map";
        }

        void ShowSelectedObjectScript()
        {
            var s = host.SelectedObjectSlots();
            int first = s?.FirstOrDefault(x => x > 0) ?? 0;
            if (first == 0) { status.Text = "The selected object runs no trigger script."; return; }
            Open(first);
            status.Text = $"The selected object runs OO1 {s[0]}, OO2 {s[1]}, SF1 {s[2]}, SF2 {s[3]} (0: none).";
        }

        // ---- check, apply, new, delete

        TriggerCompileResult Check()
        {
            var r = TriggerCompiler.Compile(ed.Document.Text, host.Names, slot);
            lastCheck = r;
            var list = new List<ScriptProblem>();
            foreach (var d in r.Diagnostics)
            {
                int start = Math.Min(d.Offset, ed.Document.TextLength);
                int len = Math.Min(d.Length, ed.Document.TextLength - start);
                list.Add(new ScriptProblem { StartOffset = start, Length = Math.Max(0, len), Warning = d.Warning, Message = d.Message, Line = d.Line, Column = d.Column });
            }
            problems.Set(ed.Document, list);
            ed.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
            problemList.ItemsSource = list;
            int errors = list.Count(p => !p.Warning), warnings = list.Count - errors;
            status.Text = r.Ok
                ? $"OK: {r.Ops.Count(o => o.Command != TriggerOp.End)} ops{(warnings > 0 ? $", {warnings} warning{(warnings == 1 ? "" : "s")}" : "")}." + (dirty ? " Ctrl+Enter applies it." : "")
                : $"{errors} error{(errors == 1 ? "" : "s")}{(warnings > 0 ? $", {warnings} warning{(warnings == 1 ? "" : "s")}" : "")}: not applied.";
            return r;
        }

        void ShowProblemAtCaret()
        {
            var at = problems.At(ed.TextArea.Caret.Offset);
            if (at.Count > 0) status.Text = string.Join("  ", at.Select(p => p.ToString()));
        }

        void Apply()
        {
            if (slot <= 0) { status.Text = "Pick or make a script first."; return; }
            if (host.Editor == null) { status.Text = "Open a map first: script edits are undo steps of the open map."; return; }
            var r = Check();
            if (!r.Ok) { status.Text = "Not applied: " + status.Text; return; }
            int s = slot;
            if (!host.RunScriptEdit(() => host.Editor.SetScriptOps(s, r.Ops))) return;
            host.Names.ClearLabels(s);
            foreach (var kv in r.LabelNames) host.Names.SetLabel(s, kv.Key, kv.Value);
            host.SaveNames();
            dirty = false;
            UpdateHeader();
            FillList();
            status.Text = $"Script {s}: {r.Ops.Count(o => o.Command != TriggerOp.End)} ops applied. Ctrl+Z in the main window takes it back; Ctrl+S there saves TRIGGERS.FLX.";
        }

        async void NewScript()
        {
            if (host.Scripts == null || host.Editor == null) { status.Text = "Open a map first: new scripts are undo steps of the open map."; return; }
            if (dirty && !await Confirm($"Script {slot} has changes that are not applied. Drop them?")) return;
            int s = host.Scripts.FreeSlot();
            if (!host.RunScriptEdit(() => host.Editor.SetScriptOps(s, new[] { new TriggerOp(0x1000 | TriggerOp.End, 0, 0) }))) return;
            filter.Text = "";
            FillList();
            Load(s);
            status.Text = $"Script {s} made. Write its statements and Apply; give it to an object as its OO or SF Trigger in the main window's properties.";
            ed.TextArea.Focus();
        }

        async Task DeleteScript()
        {
            if (slot <= 0 || host.Editor == null) { status.Text = "Pick a script, with a map open."; return; }
            int n = UsersOnMap(slot);
            string who = n > 0 ? $"{n} object{(n == 1 ? "" : "s")} on this map run{(n == 1 ? "s" : "")} it (other maps are not checked)." : "No object on this map runs it (other maps are not checked).";
            if (!await Confirm($"Delete script {slot}? {who}")) return;
            int s = slot;
            if (!host.RunScriptEdit(() => host.Editor.SetScriptOps(s, Array.Empty<TriggerOp>()))) return;
            slot = 0;
            dirty = false;
            loading = true; ed.Document.Text = ""; loading = false;
            FillList();
            UpdateHeader();
            status.Text = $"Script {s} deleted. Ctrl+Z in the main window brings it back.";
        }

        // ---- search

        void OpenSearch(bool replace)
        {
            var sp = ed.SearchPanel;
            if (sp == null) return;
            sp.IsReplaceMode = replace;
            sp.Open();
            if (!ed.TextArea.Selection.IsEmpty && !ed.TextArea.Selection.IsMultiline) sp.SearchPattern = ed.TextArea.Selection.GetText();
            Dispatcher.UIThread.Post(() => sp.Reactivate(), DispatcherPriority.Input);
        }

        // ---- keys

        async void OnKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            if (ctrl && e.Key == Key.Enter) { e.Handled = true; Apply(); }
            else if (ctrl && e.Key == Key.H) { e.Handled = true; OpenSearch(true); }
            else if (ctrl && e.Key == Key.Space) { e.Handled = true; ShowCompletion(true); }
            else if (e.Key == Key.F2 && !sideList.IsFocused) { e.Handled = true; await Rename(); }
            else if (e.Key == Key.F8) { e.Handled = true; NextProblem(); }
        }

        void NextProblem()
        {
            if (problemList.ItemsSource is not List<ScriptProblem> list || list.Count == 0) return;
            int at = ed.TextArea.Caret.Offset;
            var next = list.FirstOrDefault(p => p.StartOffset > at) ?? list[0];
            problemList.SelectedItem = null;
            problemList.SelectedItem = next;
        }

        // ---- completion

        void OnTextEntered(object sender, TextInputEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text) || completion != null) return;
            char c = e.Text[0];
            if (c == '.' || c == '[' || c == '(' || c == '|' || c == ' ' && Regex.IsMatch(LineBeforeCaret(), @"(==|goto|\|)\s$")) ShowCompletion(false);
            else if (char.IsLetter(c) || c == '_')
            {
                var m = Regex.Match(LineBeforeCaret(), @"\w+$");
                if (m.Success && m.Length == 2) ShowCompletion(false);
            }
        }

        string LineBeforeCaret()
        {
            var line = ed.Document.GetLineByOffset(ed.TextArea.Caret.Offset);
            return ed.Document.GetText(line.Offset, ed.TextArea.Caret.Offset - line.Offset);
        }

        IEnumerable<string> LabelsInText() =>
            ed.Document.Lines.Select(l => Regex.Match(ed.Document.GetText(l), @"^\s*(\w+)\s*(#\s*\d+\s*)?:")).Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct();

        void ShowCompletion(bool forced)
        {
            string before = LineBeforeCaret();
            var items = ScriptCompletionContext.For(before, host.Names, LabelsInText());
            if (items.Count == 0) { if (forced) status.Text = "Nothing to complete here."; return; }
            var word = Regex.Match(before, @"\w+$");
            completion = new CompletionWindow(ed.TextArea) { MinWidth = 260 };
            completion.StartOffset = ed.TextArea.Caret.Offset - (word.Success ? word.Length : 0);
            foreach (var i in items) completion.CompletionList.CompletionData.Add(i);
            completion.Closed += (o, e) => completion = null;
            completion.Show();
            if (word.Success) completion.CompletionList.SelectItem(word.Value);
        }

        // ---- rename (F2)

        /// <summary>The token under the caret, with the tokens of its line.</summary>
        (Token Token, List<Token> Line, int LineOffset) TokenAtCaret()
        {
            var line = ed.Document.GetLineByOffset(ed.TextArea.Caret.Offset);
            var tokens = TriggerLexer.Scan(ed.Document.GetText(line)).Where(t => t.Kind != TokenKind.End && t.Kind != TokenKind.Newline).ToList();
            int col = ed.TextArea.Caret.Offset - line.Offset;
            var t = tokens.FirstOrDefault(x => col >= x.Offset && col <= x.Offset + x.Length && (x.Kind == TokenKind.Name || x.Kind == TokenKind.Number));
            return (t, tokens, line.Offset);
        }

        async Task Rename()
        {
            var (t, line, _) = TokenAtCaret();
            if (t == null) { status.Text = "Put the caret on a label, class, global or flag to rename it."; return; }
            int i = line.IndexOf(t);
            string prev = i > 0 ? line[i - 1].Text : "", prev2 = i > 1 ? line[i - 2].Text : "";
            var names = host.Names;
            // a class: after "class ==" or "A=", by name or number
            if ((prev == "==" && prev2 == "class") || (prev == "=" && prev2 == "A"))
            {
                int cls;
                if (t.Kind == TokenKind.Number) cls = ParseNumber(t.Text);
                else if (!names.TryClass(t.Text, out cls)) { status.Text = $"'{t.Text}' is not a known class."; return; }
                string now = names.ClassName(cls);
                string typesText = string.Join(", ", names.TypesOf(cls).Take(12));
                string n = await Ask($"Name class {cls} (types {typesText}). PascalCase; empty takes the name away.", now ?? "");
                if (n == null) return;
                try
                {
                    if (n.Length == 0) { status.Text = "Taking names away is done in the names file for now."; return; }
                    names.SetClass(cls, n);
                }
                catch (ArgumentException ex) { status.Text = ex.Message; return; }
                ReplaceTokens(x => (x.Kind == TokenKind.Name && x.Text == (now ?? "\0")) || (x.Kind == TokenKind.Number && IsClassPosition(x) && ParseNumber(x.Text) == cls), n);
                host.SaveNames();
                status.Text = $"Class {cls} is {n} in every script now (names file saved).";
                return;
            }
            // a global: inside global[...]
            if (prev == "[" && prev2 == "global")
            {
                int g;
                if (t.Kind == TokenKind.Number) g = ParseNumber(t.Text);
                else if (!names.TryGlobal(t.Text, out g)) { status.Text = $"'{t.Text}' is not a known global."; return; }
                string now = names.GlobalName(g);
                string n = await Ask($"Name global {g}. UPPER_SNAKE.", now ?? "");
                if (string.IsNullOrEmpty(n)) return;
                try { names.SetGlobal(g, n); } catch (ArgumentException ex) { status.Text = ex.Message; return; }
                ReplaceTokens(x => (x.Kind == TokenKind.Name && x.Text == (now ?? "\0")) || (x.Kind == TokenKind.Number && IsGlobalPosition(x) && ParseNumber(x.Text) == g), n);
                host.SaveNames();
                status.Text = $"Global {g} is {n} in every script now (names file saved).";
                return;
            }
            // a flag constant
            if (t.Kind == TokenKind.Name && names.TryFlag(t.Text, out int bit))
            {
                string n = await Ask($"Name flag bit 0x{bit:X} (object flag 0x{TriggerNames.FlagMap.First(f => f.Bit == bit).ObjectFlag:X}). UPPER_SNAKE.", t.Text);
                if (string.IsNullOrEmpty(n) || n == t.Text) return;
                try { names.SetFlag(bit, n); } catch (ArgumentException ex) { status.Text = ex.Message; return; }
                string old = t.Text;
                ReplaceTokens(x => x.Kind == TokenKind.Name && x.Text == old, n);
                host.SaveNames();
                status.Text = $"Flag 0x{bit:X} is {n} in every script now (names file saved).";
                return;
            }
            // a label: defined in this text
            if (t.Kind == TokenKind.Name && LabelsInText().Contains(t.Text))
            {
                string n = await Ask($"Rename label {t.Text} (lower_snake). Apply stores the name.", Regex.IsMatch(t.Text, @"^L\d+$") ? "" : t.Text);
                if (string.IsNullOrEmpty(n) || n == t.Text) return;
                if (!Regex.IsMatch(n, @"^[a-z_][a-z0-9_]*$") || TriggerCompiler.Keywords.Contains(n)) { status.Text = "A label name is lower_snake and not a keyword."; return; }
                string old = t.Text;
                var m = Regex.Match(old, @"^L(\d+)$");
                // L3: keeps its number as done #3:
                ReplaceTokens(x => x.Kind == TokenKind.Name && x.Text == old, n, m.Success ? m.Groups[1].Value : null);
                status.Text = $"Label {old} is {n}; Apply stores the name with the script.";
                return;
            }
            status.Text = "F2 renames labels, classes (after class == or A=), globals (in global[...]) and flags.";
        }

        static int ParseNumber(string s) =>
            s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(s, CultureInfo.InvariantCulture);

        List<Token> allTokens;
        bool IsClassPosition(Token x)
        {
            int i = allTokens.IndexOf(x);
            return i >= 2 && ((allTokens[i - 1].Text == "==" && allTokens[i - 2].Text == "class") || (allTokens[i - 1].Text == "=" && allTokens[i - 2].Text == "A"));
        }
        bool IsGlobalPosition(Token x)
        {
            int i = allTokens.IndexOf(x);
            return i >= 2 && allTokens[i - 1].Text == "[" && allTokens[i - 2].Text == "global";
        }

        /// <summary>Replaces the matching tokens of the whole text (one undo step of the text). A numbered label (L3) given a name keeps its number: L3: becomes name #3:.</summary>
        void ReplaceTokens(Func<Token, bool> match, string with, string keepLabelNumber = null)
        {
            allTokens = TriggerLexer.Scan(ed.Document.Text);
            var hits = allTokens.Where(x => x.Kind == TokenKind.Name || x.Kind == TokenKind.Number).Where(match).ToList();
            ed.Document.BeginUpdate();
            for (int k = hits.Count - 1; k >= 0; k--)
            {
                var h = hits[k];
                int i = allTokens.IndexOf(h);
                bool def = keepLabelNumber != null && i + 1 < allTokens.Count && allTokens[i + 1].Text == ":";
                ed.Document.Replace(h.Offset, h.Length, def ? $"{with} #{keepLabelNumber}" : with);
            }
            ed.Document.EndUpdate();
        }

        // ---- side panels: globals and symbols

        void ToggleSide(string which)
        {
            side = side == which ? null : which;
            sidePanel.IsVisible = side != null;
            if (side != null) FillSide();
        }

        void FillSide()
        {
            var items = new List<SideItem>();
            var names = host.Names;
            if (side == "globals")
            {
                sideTitle.Text = "Globals";
                sideHint.Text = "The game's global flags (0 to 511) the scripts set (global[n] = v) and test. Double-click inserts it; F2 names it.";
                var sets = new Dictionary<int, HashSet<int>>();
                var tests = new Dictionary<int, HashSet<int>>();
                var sc = host.Scripts;
                if (sc != null)
                    for (int s = 1; s < sc.Count; s++)
                        foreach (var op in sc[s])
                        {
                            var d = op.Command == 0x0D ? sets : op.Command == 0x0E || op.Command == 0x5B ? tests : null;
                            if (d == null) continue;
                            int g = op.B & 0x1FF;
                            if (!d.TryGetValue(g, out var h)) d[g] = h = new HashSet<int>();
                            h.Add(s);
                        }
                foreach (int g in sets.Keys.Union(tests.Keys).Union(names.Globals.Select(k => k.Key)).OrderBy(x => x))
                {
                    string nm = names.GlobalName(g);
                    int ns = sets.TryGetValue(g, out var a) ? a.Count : 0, nt = tests.TryGetValue(g, out var b) ? b.Count : 0;
                    items.Add(new SideItem { Kind = "global", Number = g, Insert = $"global[{nm ?? g.ToString(CultureInfo.InvariantCulture)}]",
                                             Text = $"{g,3}  {nm ?? "(unnamed)"}   set in {ns}, tested in {nt}" });
                }
            }
            else if (side == "symbols")
            {
                sideTitle.Text = "Symbols";
                sideHint.Text = "This script's labels, classes and constants, then every named class. Double-click goes to a label or inserts the name; F2 renames a class.";
                var tokens = TriggerLexer.Scan(ed.Document.Text);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t.Kind == TokenKind.Name && (i == 0 || tokens[i - 1].Kind == TokenKind.Newline || tokens[i - 1].Text == ";") && i + 1 < tokens.Count && (tokens[i + 1].Text == ":" || tokens[i + 1].Text == "#"))
                        items.Add(new SideItem { Kind = "label", Text = $"label  {t.Text}   line {t.Line}", Line = t.Line, Insert = t.Text });
                }
                var classes = new SortedSet<int>();
                var consts = new SortedSet<string>(StringComparer.Ordinal);
                for (int i = 2; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    bool clsPos = (tokens[i - 1].Text == "==" && tokens[i - 2].Text == "class") || (tokens[i - 1].Text == "=" && tokens[i - 2].Text == "A");
                    if (clsPos && t.Kind == TokenKind.Name && names.TryClass(t.Text, out int c)) classes.Add(c);
                    else if (clsPos && t.Kind == TokenKind.Number) classes.Add(ParseNumber(t.Text));
                }
                foreach (var t in tokens) if (t.Kind == TokenKind.Name && t.Text.Length > 1 && t.Text.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_') && t.Text != "A" && t.Text != "L" && !Regex.IsMatch(t.Text, @"^L\d+$")) consts.Add(t.Text);
                foreach (int c in classes)
                    items.Add(new SideItem { Kind = "class", Number = c, Insert = names.ClassName(c) ?? c.ToString(CultureInfo.InvariantCulture),
                                             Text = $"class  {names.ClassName(c) ?? "(unnamed)"} = {c}   types {string.Join(", ", names.TypesOf(c).Take(6))}{(names.TypesOf(c).Skip(6).Any() ? " ..." : "")}" });
                foreach (string k in consts) items.Add(new SideItem { Kind = "constant", Text = $"const  {k}", Insert = k });
                items.Add(new SideItem { Kind = "", Text = "— named classes —" });
                foreach (var kv in names.Classes.OrderBy(k => k.Value, StringComparer.Ordinal))
                    items.Add(new SideItem { Kind = "class", Number = kv.Key, Insert = kv.Value, Text = $"{kv.Value} = {kv.Key}" });
            }
            sideList.ItemsSource = items;
        }

        void UseSideItem()
        {
            if (sideList.SelectedItem is not SideItem it || it.Insert == null) return;
            if (it.Kind == "label" && it.Line > 0)
            {
                ed.TextArea.Caret.Line = it.Line;
                ed.TextArea.Caret.Column = 1;
                ed.TextArea.Caret.BringCaretToView();
            }
            else ed.Document.Insert(ed.TextArea.Caret.Offset, it.Insert);
            ed.TextArea.Focus();
        }

        async Task RenameSideItem()
        {
            if (sideList.SelectedItem is not SideItem it) return;
            var names = host.Names;
            try
            {
                if (it.Kind == "global")
                {
                    string old = names.GlobalName(it.Number);
                    string n = await Ask($"Name global {it.Number}. UPPER_SNAKE.", old ?? "");
                    if (string.IsNullOrEmpty(n)) return;
                    names.SetGlobal(it.Number, n);
                    allTokens = TriggerLexer.Scan(ed.Document.Text);
                    ReplaceTokens(x => (x.Kind == TokenKind.Name && x.Text == (old ?? "\0")) || (x.Kind == TokenKind.Number && IsGlobalPosition(x) && ParseNumber(x.Text) == it.Number), n);
                }
                else if (it.Kind == "class")
                {
                    string old = names.ClassName(it.Number);
                    string n = await Ask($"Name class {it.Number}. PascalCase.", old ?? "");
                    if (string.IsNullOrEmpty(n)) return;
                    names.SetClass(it.Number, n);
                    allTokens = TriggerLexer.Scan(ed.Document.Text);
                    ReplaceTokens(x => (x.Kind == TokenKind.Name && x.Text == (old ?? "\0")) || (x.Kind == TokenKind.Number && IsClassPosition(x) && ParseNumber(x.Text) == it.Number), n);
                }
                else return;
            }
            catch (ArgumentException ex) { status.Text = ex.Message; return; }
            host.SaveNames();
            FillSide();
        }

        // ---- small dialogs

        async Task<bool> Confirm(string question)
        {
            var dlg = new Window { Title = "Trigger scripts", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            bool answer = false;
            var yes = new Button { Content = "Yes", Margin = new Thickness(0, 0, 6, 0), MinWidth = 70 };
            var no = new Button { Content = "No", MinWidth = 70, IsDefault = true };
            yes.Click += (o, e) => { answer = true; dlg.Close(); };
            no.Click += (o, e) => dlg.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(yes); buttons.Children.Add(no);
            var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 460 };
            panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(buttons);
            dlg.Content = panel;
            await dlg.ShowDialog(this);
            return answer;
        }

        async Task<string> Ask(string question, string start)
        {
            var dlg = new Window { Title = "Rename", SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            string answer = null;
            var box = new TextBox { Text = start, Width = 320, Margin = new Thickness(0, 8, 0, 0) };
            var ok = new Button { Content = "OK", Margin = new Thickness(0, 0, 6, 0), MinWidth = 70, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 70, IsCancel = true };
            ok.Click += (o, e) => { answer = box.Text?.Trim() ?? ""; dlg.Close(); };
            cancel.Click += (o, e) => dlg.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14), MaxWidth = 460 };
            panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(box);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Opened += (o, e) => { box.Focus(); box.SelectAll(); };
            await dlg.ShowDialog(this);
            return answer;
        }
    }
}
