using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using U9.Formats.Text;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>
    /// The dialogue editor: every conversation line of static/text.flx, grouped by the conversation file it came
    /// from, with its voice clip (sound/Speech.flx, the record of the same number). Edits are undo steps; Ctrl+S saves
    /// text.flx (keeping text.flx.orig).
    /// </summary>
    public sealed class DialogueWindow : Window
    {
        readonly DialogueEditing ed;
        readonly ListBox convList = new ListBox { FontSize = 12 };
        readonly TextBox convFilter = new TextBox { PlaceholderText = "Filter conversations", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) };
        readonly TextBox search = new TextBox { PlaceholderText = "Find in every line (text, clip name or line number), Enter", FontSize = 12 };
        readonly ListBox lineList = new ListBox { FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New, monospace") };
        readonly ObservableCollection<LineItem> lines = new();
        readonly TextBlock header = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 13, Margin = new Thickness(0, 4) };
        readonly TextBlock info = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        readonly TextBox editor = new TextBox { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, FontSize = 14, MinHeight = 70 };
        readonly TextBlock warnings = new TextBlock { FontSize = 12, Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock preview = new TextBlock { FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New, monospace"), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.NoWrap };
        readonly Button fix = new Button { Content = "Fix characters", FontSize = 12, IsVisible = false };
        readonly TextBlock status = new TextBlock { FontSize = 12, Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap };
        int current = -1;
        bool closeAnyway;
        public static string ShotPath, FindAtStart;
        public static int LineAtStart = -1;

        sealed class LineItem
        {
            public int Line; public string Text; public bool WithConversation;
            public override string ToString() => Text;
        }

        public DialogueWindow(DialogueEditing editing)
        {
            ed = editing;
            Title = "Dialogue";
            Width = 1280; Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ed.ReadClipLengths();

            convList.ItemsSource = ed.Conversations;
            convList.SelectionChanged += (s, e) => { if (convList.SelectedItem is Conversation c) ShowConversation(c); };
            convFilter.TextChanged += (s, e) =>
            {
                string f = convFilter.Text?.Trim() ?? "";
                convList.ItemsSource = ed.Conversations.Where(c => f.Length == 0 || c.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            };
            search.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ShowFound(search.Text); e.Handled = true; } };
            lineList.ItemsSource = lines;
            lineList.SelectionChanged += (s, e) => { if (lineList.SelectedItem is LineItem li) Select(li.Line); };
            editor.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Apply(); e.Handled = true; }
                else if (e.Key == Key.Escape && current >= 0) { editor.Text = ed.Text[current]; e.Handled = true; }
            };
            editor.TextChanged += (s, e) => Check();
            ed.LineChanged += OnLineChanged;

            var left = new DockPanel { Width = 260, Margin = new Thickness(6) };
            var leftTitle = new TextBlock { Text = $"Conversations ({ed.Conversations.Count})", FontWeight = FontWeight.SemiBold, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(leftTitle, Dock.Top);
            DockPanel.SetDock(convFilter, Dock.Top);
            left.Children.Add(leftTitle);
            left.Children.Add(convFilter);
            left.Children.Add(convList);

            var set = new Button { Content = "Set (Enter)", FontSize = 12 };
            set.Click += (s, e) => Apply();
            var revert = new Button { Content = "Original", FontSize = 12 };
            ToolTip.SetTip(revert, "Put back the line as the game shipped it (text.flx.orig, or the file as loaded)");
            revert.Click += (s, e) => { if (current >= 0 && original.TryGetValue(current, out var o)) { editor.Text = o; Apply(); } };
            var play = new Button { Content = "▶ Play voice", FontSize = 12 };
            play.Click += (s, e) => PlayCurrent();
            var stop = new Button { Content = "■ Stop", FontSize = 12 };
            stop.Click += (s, e) => SoundPlayback.Stop();
            var saveWav = new Button { Content = "Save voice as WAV…", FontSize = 12 };
            saveWav.Click += async (s, e) => await SaveWav();
            var editButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            editButtons.Children.Add(set); editButtons.Children.Add(revert);
            editButtons.Children.Add(new Border { Width = 16 });
            var importWav = new Button { Content = "Replace voice from WAV…", FontSize = 12 };
            ToolTip.SetTip(importWav, "Stored as uncompressed PCM (22,050 Hz, 16-bit mono; converted when the WAV is not). The game's speech code accepts it, but no shipped clip uses it: try it in the game.");
            importWav.Click += async (s, e) => await ImportWav();
            fix.Click += (s, e) => { editor.Text = DialogueCheck.FixC1(editor.Text ?? ""); };
            ToolTip.SetTip(fix, "Replaces U+0080-U+009F (which the game shows as \"?\") with the Windows-1252 characters at those codes, e.g. U+0092 with ’");
            editButtons.Children.Add(play); editButtons.Children.Add(stop); editButtons.Children.Add(saveWav); editButtons.Children.Add(importWav);
            editButtons.Children.Add(new Border { Width = 16 });
            editButtons.Children.Add(fix);

            var bottom = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
            bottom.Children.Add(info);
            bottom.Children.Add(editor);
            bottom.Children.Add(editButtons);
            bottom.Children.Add(warnings);
            bottom.Children.Add(new TextBlock { Text = "As the game wraps it (subtitle box 600 px, font 0x263, 13 lines a page):", FontSize = 11, Foreground = Brushes.Gray });
            bottom.Children.Add(new ScrollViewer { Content = preview, MaxHeight = 120, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

            var right = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(search, Dock.Top);
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            right.Children.Add(search);
            right.Children.Add(header);
            right.Children.Add(bottom);
            right.Children.Add(lineList);

            var save = new Button { Content = "Save", FontSize = 12 };
            save.Click += (s, e) => Save();
            var undo = new Button { Content = "Undo", FontSize = 12 };
            undo.Click += (s, e) => Reveal(ed.Undo());
            var redo = new Button { Content = "Redo", FontSize = 12 };
            redo.Click += (s, e) => Reveal(ed.Redo());
            var bar = new DockPanel { Margin = new Thickness(6, 6, 6, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(save); buttons.Children.Add(undo); buttons.Children.Add(redo);
            var vowel = new Button { Content = "Vowel play…", FontSize = 12 };
            ToolTip.SetTip(vowel, "Shuffle the vowels of proper names in every line (an idea from Merrow, the Quest 64 randomizer)");
            vowel.Click += async (s, e) => await VowelPlayDialog();
            buttons.Children.Add(vowel);
            DockPanel.SetDock(buttons, Dock.Left);
            bar.Children.Add(buttons);
            bar.Children.Add(status);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(right);
            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(grid);
            Content = root;

            string origPath = ed.TextPath + ".orig";
            var origTable = File.Exists(origPath) ? TextTable.Load(origPath) : null;
            for (int i = 0; i < ed.Text.Count; i++) original[i] = origTable?[i] ?? ed.Text[i];

            KeyDown += OnKey;
            Closing += OnClosing;
            Opened += (s, e) =>
            {
                if (FindAtStart != null) { search.Text = FindAtStart; ShowFound(FindAtStart); }
                else if (LineAtStart >= 0) Reveal(LineAtStart);
                else if (ed.Conversations.Count > 0) convList.SelectedIndex = 0;
                if (LineAtStart >= 0 && FindAtStart != null) Select(LineAtStart);
                if (VowelAtStart) Avalonia.Threading.DispatcherTimer.RunOnce(async () => await VowelPlayDialog(), TimeSpan.FromSeconds(0.5));
                else if (ShotPath != null) Avalonia.Threading.DispatcherTimer.RunOnce(() => { SaveShot(ShotPath); closeAnyway = true; Close(); }, TimeSpan.FromSeconds(1.5));
            };
            ShowStatus();
        }

        readonly Dictionary<int, string> original = new();

        public bool HasChanges => ed.HasChanges;

        string Describe(int i)
        {
            string text = ed.Text[i] ?? "";
            var c = ed.ClipOf(i);
            double? sec = ed.SecondsOf(i);
            string who = c is SpeechClip k ? k.Speaker : "(no voice)";
            if (who.Length > 16) who = who[..15] + "…";
            string len = sec is double d ? $"{d,4:0.0}s" : "     ";
            string mark = (ed.Text[i] != original.GetValueOrDefault(i) ? "* " : "") + (ed.ClipReplaced(i) ? "[new voice] " : "");
            return $"{i,5}  {len}  {who,-16}  {mark}{text}";
        }

        void ShowConversation(Conversation c)
        {
            header.Text = $"{c.Name}: lines {c.First}-{c.Last}   ({c.Source})";
            lines.Clear();
            for (int i = c.First; i <= c.Last; i++) lines.Add(new LineItem { Line = i, Text = Describe(i) });
        }

        void ShowFound(string q)
        {
            var found = ed.Find(q).Take(2000).ToList();
            header.Text = found.Count == 0 ? $"Nothing has \"{q}\"." : $"{found.Count}{(found.Count == 2000 ? "+" : "")} lines with \"{q}\"";
            lines.Clear();
            foreach (int i in found) lines.Add(new LineItem { Line = i, WithConversation = true, Text = $"{ed.ConversationOf(i)?.Name,-12} " + Describe(i) });
        }

        /// <summary>Shows a line in its conversation and selects it.</summary>
        void Reveal(int line)
        {
            if (line < 0) return;
            var c = ed.ConversationOf(line);
            if (c != null && (lines.Count == 0 || !lines.Any(l => l.Line == line)))
            {
                convList.SelectedItem = c;
                ShowConversation(c);
            }
            var item = lines.FirstOrDefault(l => l.Line == line);
            if (item != null) { lineList.SelectedItem = item; lineList.ScrollIntoView(item); }
            Select(line);
        }

        void Select(int line)
        {
            current = line;
            var c = ed.ClipOf(line);
            string voice = c is SpeechClip k
                ? $"voice: {k.Name}, {ed.SecondsOf(line):0.0} s ({k.Rate} Hz, {(k.Channels == 1 ? "mono" : k.Channels + " channels")}, {(k.Codec == 2 ? "EA MicroTalk" : k.Codec == 0 ? "PCM" : "codec " + k.Codec)}, {k.DataLength:N0} bytes{(ed.ClipReplaced(line) ? ", replaced" : "")})"
                : "no voice clip: shown as text only";
            info.Text = $"Line {line} of {ed.ConversationOf(line)?.Name ?? "?"}. {voice}.";
            editor.Text = ed.Text[line] ?? "";
            Check();
        }

        void PlayCurrent()
        {
            if (current < 0) return;
            try
            {
                var wav = ed.ClipWav(current);
                if (wav == null) { status.Text = $"Line {current} has no voice clip."; return; }
                status.Text = SoundPlayback.Play(wav) ? $"Playing {ed.ClipOf(current)?.Name} ({ed.SecondsOf(current):0.0} s)." : "Sound playback works on Windows only: save the WAV instead.";
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { status.Text = "Cannot play: " + ex.Message; }
        }

        async System.Threading.Tasks.Task SaveWav()
        {
            if (current < 0 || ed.ClipOf(current) is not SpeechClip c) { status.Text = "This line has no voice clip."; return; }
            var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save the voice clip", DefaultExtension = "wav", SuggestedFileName = Path.ChangeExtension(c.Name, ".wav"),
                FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("WAV sound") { Patterns = new[] { "*.wav" } } },
            });
            string path = f?.TryGetLocalPath();
            if (path == null) return;
            try { File.WriteAllBytes(path, ed.ClipWav(current)); status.Text = "Saved " + path; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException) { status.Text = "Not saved: " + ex.Message; }
        }

        void Check()
        {
            if (current < 0) { warnings.Text = ""; preview.Text = ""; fix.IsVisible = false; return; }
            string t = (editor.Text ?? "").Replace("\r", "").Replace("\n", " ");
            var r = ed.Check(current, t);
            fix.IsVisible = DialogueCheck.FixC1(t) != t;
            if (r == null) { warnings.Text = $"{t.Length} characters. (No subtitle font found: bitmap16.flx is missing.)"; preview.Text = ""; return; }
            var parts = new List<string> { $"{t.Length} characters, {r.Bytes.Length} bytes for the game." };
            parts.AddRange(r.Errors.Select(x => "ERROR: " + x));
            parts.AddRange(r.Warnings);
            parts.AddRange(r.Notes);
            warnings.Foreground = r.Errors.Count > 0 ? Brushes.DarkRed : r.Warnings.Count > 0 ? Brushes.DarkOrange : Brushes.DimGray;
            warnings.Text = string.Join("  ", parts);
            preview.Text = string.Join("\n", r.Wrapped.Select((l, i) => (i > 0 && i % DialogueCheck.LinesPerPage == 0 ? "--- page " + (i / DialogueCheck.LinesPerPage + 1) + " ---\n" : "") + l));
        }

        async System.Threading.Tasks.Task ImportWav()
        {
            if (current < 0) return;
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = $"A WAV for line {current}", AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("WAV sound") { Patterns = new[] { "*.wav" } } },
            });
            string path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (path == null) return;
            try
            {
                ed.ImportWav(current, File.ReadAllBytes(path));
                status.Text = $"Line {current} now has the voice of {Path.GetFileName(path)} ({ed.SecondsOf(current):0.0} s, PCM). Untried in the game: check it there. Ctrl+S writes Speech.flx (140 MB; the first save keeps Speech.flx.orig).";
                Select(current);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { status.Text = "No voice imported: " + ex.Message; }
        }

        void Apply()
        {
            if (current < 0) return;
            string t = (editor.Text ?? "").Replace("\r", "").Replace("\n", " ");
            if (!ed.SetLine(current, t)) return;
            ShowStatus();
        }

        void OnLineChanged(int line)
        {
            for (int k = 0; k < lines.Count; k++)
                if (lines[k].Line == line)
                {
                    bool w = lines[k].WithConversation;
                    lines[k] = new LineItem { Line = line, WithConversation = w, Text = (w ? $"{ed.ConversationOf(line)?.Name,-12} " : "") + Describe(line) };
                }
            if (line == current) editor.Text = ed.Text[line];
            ShowStatus();
        }

        void ShowStatus()
        {
            int changed = Enumerable.Range(0, ed.Text.Count).Count(i => ed.Text[i] != original.GetValueOrDefault(i));
            status.Text = (ed.HasChanges ? "Not saved. " : "") + $"{ed.Text.Count:N0} lines, {ed.ClipCount:N0} voiced; {changed} differ from the shipped text.";
            Title = "Dialogue" + (ed.HasChanges ? " *" : "");
        }

        void Save()
        {
            try
            {
                var p = ed.Save();
                status.Text = p.Count == 0 ? "Nothing to save." : $"Saved {string.Join(", ", p.Select(Path.GetFileName))} (the first save keeps a .orig). File > Export changes as mod carries the changes.";
                Title = "Dialogue";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { status.Text = "Not saved: " + ex.Message; }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.Control) return;
            if (e.Key == Key.S) { Save(); e.Handled = true; }
            else if (e.Key == Key.F) { search.Focus(); search.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Z && FocusManager?.GetFocusedElement() is not TextBox) { Reveal(ed.Undo()); e.Handled = true; }
            else if (e.Key == Key.Y && FocusManager?.GetFocusedElement() is not TextBox) { Reveal(ed.Redo()); e.Handled = true; }
        }

        async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (closeAnyway || !ed.HasChanges) { SoundPlayback.Stop(); ed.LineChanged -= OnLineChanged; return; }
            e.Cancel = true;
            var dlg = new Window { Title = "Dialogue", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            string answer = null;
            var p = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
            p.Children.Add(new TextBlock { Text = "Save the dialogue changes before closing?", FontSize = 13 });
            var b = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var (label, key) in new[] { ("Save", "save"), ("Don't save", "drop"), ("Cancel", "") })
            {
                var btn = new Button { Content = label };
                btn.Click += (s, a) => { answer = key; dlg.Close(); };
                b.Children.Add(btn);
            }
            p.Children.Add(b);
            dlg.Content = p;
            await dlg.ShowDialog(this);
            if (answer == "save") { Save(); if (ed.HasChanges) return; }
            else if (answer == "drop") { while (ed.CanUndo) ed.Undo(); }
            else return;
            closeAnyway = true;
            Close();
        }

        public static bool VowelAtStart;

        /// <summary>Vowel play: shuffle the vowels of proper names in every line, as one undo step.</summary>
        async System.Threading.Tasks.Task VowelPlayDialog()
        {
            var dlg = new Window { Title = "Vowel play", Width = 760, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var seedBox = new TextBox { Text = new Random().Next(1, 100000).ToString(), Width = 90, FontSize = 12 };
            var keepFirst = new CheckBox { Content = "A name starting with a vowel keeps it", FontSize = 12 };
            var names = new TextBox
            {
                AcceptsReturn = true, FontSize = 12, Height = 150, TextWrapping = TextWrapping.Wrap,
                Text = string.Join(" ", VowelPlay.DefaultNames(ed.Text.Lines, ed.NpcNames)),
            };
            var report = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontSize = 12, FontFamily = new FontFamily("Consolas, Courier New, monospace") };
            VowelPlayResult last = null;
            void Preview()
            {
                int.TryParse(seedBox.Text, out int seed);
                var list = (names.Text ?? "").Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                last = VowelPlay.Preview(ed.Text, list, seed, keepFirst.IsChecked == true);
                report.Text = last.Report(false);
            }
            var preview = new Button { Content = "Preview", FontSize = 12 };
            preview.Click += (s, e) => Preview();
            var apply = new Button { Content = "Apply", FontSize = 12, IsDefault = true };
            apply.Click += (s, e) =>
            {
                Preview();
                if (last != null && ed.SetLines(last.Edits))
                {
                    status.Text = $"Vowel play (seed {last.Seed}) changed {last.Edits.Count} lines: one undo step. Ctrl+S saves text.flx.";
                    if (current >= 0) Select(current);
                    ShowStatus();
                }
                dlg.Close();
            };
            var cancel = new Button { Content = "Cancel", FontSize = 12, IsCancel = true };
            cancel.Click += (s, e) => dlg.Close();
            var top = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
            top.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 12,
                Text = "Each proper name gets its vowels drawn again (a, e, i, o, u, case kept), the same new spelling everywhere it appears in the dialogue, " +
                       "the journal and the replies; a new spelling is never a word the text already uses. The idea is Merrow's \"Vowel play\", from the " +
                       "Quest 64 randomizer by hangedmandesign (github.com/hangedmandesign/merrow). The voices still say the old names; the speaker names " +
                       "in NPC.FLX stay (the game finds lines by them). Names to change (edit freely):",
            });
            top.Children.Add(names);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = "Seed", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            row.Children.Add(seedBox);
            row.Children.Add(keepFirst);
            row.Children.Add(preview);
            row.Children.Add(apply);
            row.Children.Add(cancel);
            top.Children.Add(row);
            var dp = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            dp.Children.Add(top);
            dp.Children.Add(new Border { Child = report, Margin = new Thickness(10, 0, 10, 10) });
            dlg.Content = dp;
            dlg.Opened += (s, e) =>
            {
                Preview();
                if (ShotPath != null && VowelAtStart)
                    Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                    {
                        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)dlg.Bounds.Width, (int)dlg.Bounds.Height));
                        bmp.Render(dlg);
                        bmp.Save(ShotPath);
                        dlg.Close();
                        closeAnyway = true;
                        Close();
                    }, TimeSpan.FromSeconds(1));
            };
            await dlg.ShowDialog(this);
        }

        void SaveShot(string path)
        {
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height));
            bmp.Render(this);
            bmp.Save(path);
        }
    }
}
