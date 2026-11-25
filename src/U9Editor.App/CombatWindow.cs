using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Media;
using U9.Formats.Combat;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>
    /// Combat balance: how many hits the Avatar needs to kill each creature with each weapon, and how many each creature
    /// needs to kill the Avatar in each armour, worked out from the game's own tables; and the editing of those tables:
    /// weapons, armour and creatures' natural attacks and damage multipliers (u9.exe, kept as u9.exe.patches.json) and
    /// creatures' HP, defence and kind (runtime/NPC.FLX). Ctrl+S saves, Ctrl+Z / Ctrl+Y undo and redo.
    /// </summary>
    public sealed class CombatWindow : Window
    {
        readonly CombatEditing ed;
        readonly Func<int, string> names;
        readonly AvatarSetup avatar = new AvatarSetup();
        readonly Dictionary<int, string> attackChoice = new();   // NPC record → "n:6" / "w:5388" / "" (none)
        readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        readonly TextBlock detail = new TextBlock { Margin = new Thickness(6, 4), TextWrapping = TextWrapping.Wrap, FontSize = 12, MinHeight = 34 };
        readonly MatrixView killThem = new MatrixView(), killYou = new MatrixView { HighIsGood = true };
        HitsToKill[,] killThemCells = new HitsToKill[0, 0], killYouCells = new HitsToKill[0, 0];
        readonly TextBox creatureFilter = new TextBox { PlaceholderText = "Filter creatures by name", FontSize = 12 };
        readonly CheckBox hostileOnly = new CheckBox { Content = "Fighters only", IsChecked = true, FontSize = 12 };
        readonly CheckBox[] classShown = new CheckBox[5];
        readonly ComboBox[] slotBoxes = new ComboBox[6];
        readonly ComboBox shieldBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        readonly TextBox hpBox = new TextBox { FontSize = 12, Width = 70, HorizontalAlignment = HorizontalAlignment.Left };
        readonly ComboBox strengthBox = new ComboBox { FontSize = 12, ItemsSource = new[] { "0 (+0)", "1 (+2)", "2 (+4)", "3 (+8)" }, SelectedIndex = 0 };
        readonly ComboBox modeBox = new ComboBox { FontSize = 12, ItemsSource = new[] { "The best attack against each", "Attack 1", "Attack 2", "Attack 3", "Attack 4" }, SelectedIndex = 0 };
        readonly TabControl innerTabs = new TabControl();
        readonly List<Row> rows = new();      // every editable row, refreshed after undo/redo
        bool building, closeAnyway;
        public static string ShotPath;
        public static int ArmourAtStart = -1;

        public CombatWindow(CombatEditing editing, Func<int, string> typeNames, string startTab = null)
        {
            ed = editing;
            var own = ed.TypeNames;
            names = t => typeNames?.Invoke(t) ?? own?.Invoke(t);
            ed.TypeNames = names;
            Title = "Combat balance";
            Width = 1360; Height = 860;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            avatar.Hp = Math.Max(1, ed.Npcs[0].MaxHp);
            foreach (var n in ed.Npcs.Records())
            {
                var d = CreatureAttacks.DefaultsOf(n);
                if (d.Length > 0) attackChoice[n.Index] = d[0];
            }

            var tabs = new TabControl { Margin = new Thickness(4, 0) };
            tabs.Items.Add(new TabItem { Header = "Balance", Content = BalanceTab() });
            tabs.Items.Add(new TabItem { Header = "Weapons", Content = WeaponsTab() });
            tabs.Items.Add(new TabItem { Header = "Armour", Content = ArmourTab() });
            tabs.Items.Add(new TabItem { Header = "Creatures", Content = CreaturesTab() });
            tabs.Items.Add(new TabItem { Header = "Natural attacks", Content = NaturalTab() });
            tabs.Items.Add(new TabItem { Header = "Damage multipliers", Content = MultipliersTab() });
            foreach (TabItem t in tabs.Items) t.FontSize = 13;
            string[] tabKeys = { "balance", "weapons", "armour", "creatures", "natural", "multipliers" };
            if (startTab != null)
            {
                int k = Array.IndexOf(tabKeys, startTab.ToLowerInvariant() == "balance-you" ? "balance" : startTab.ToLowerInvariant());
                if (k >= 0) tabs.SelectedIndex = k;
                if (startTab.Equals("balance-you", StringComparison.OrdinalIgnoreCase)) innerTabs.SelectedIndex = 1;
            }
            if (ArmourAtStart >= 0 && ArmourAtStart <= 6) WearSet(ArmourAtStart);

            var save = new Button { Content = "Save", FontSize = 12 };
            save.Click += (s, e) => Save();
            var undo = new Button { Content = "Undo", FontSize = 12 };
            undo.Click += (s, e) => ed.Undo();
            var redo = new Button { Content = "Redo", FontSize = 12 };
            redo.Click += (s, e) => ed.Redo();
            var bar = new DockPanel { Margin = new Thickness(6, 6, 6, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            buttons.Children.Add(save); buttons.Children.Add(undo); buttons.Children.Add(redo);
            DockPanel.SetDock(buttons, Dock.Left);
            bar.Children.Add(buttons);
            bar.Children.Add(status);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(tabs);
            Content = root;

            ed.Changed += OnChanged;
            KeyDown += OnKey;
            Closing += OnClosing;
            Opened += (s, e) =>
            {
                Recompute();
                if (ShotPath != null) Avalonia.Threading.DispatcherTimer.RunOnce(() => { SaveShot(ShotPath); closeAnyway = true; Close(); }, TimeSpan.FromSeconds(1.5));
            };
            ShowStatus();
        }

        // ---------------------------------------------------------------- common

        void OnChanged()
        {
            foreach (var r in rows) r.Refresh();
            Recompute();
            ShowStatus();
        }

        void ShowStatus()
        {
            int patches = ed.ExePatchList().Count;
            string what = patches == 0 ? "u9.exe unchanged" : $"{patches} change{(patches == 1 ? "" : "s")} to u9.exe's tables (kept in {U9.Mods.ExePatchFile.Name}; the game sees them through a mod)";
            status.Text = (ed.HasChanges ? "Not saved. " : "") + what + ". NPC.FLX changes reach new games only (saves keep their own copy).";
            Title = "Combat balance" + (ed.HasChanges ? " *" : "");
        }

        void Save()
        {
            try
            {
                var written = ed.Save(KindName);
                status.Text = written.Count == 0 ? "Nothing to save." : "Saved " + string.Join(", ", written.Select(Path.GetFileName)) +
                    ". File > Export changes as mod puts them in a .u9mod; the U9 Mod Manager applies it (u9.exe itself is never changed).";
                Title = "Combat balance";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                status.Text = "Not saved: " + ex.Message;
            }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.Control) return;
            if (e.Key == Key.S) { Save(); e.Handled = true; }
            else if (e.Key == Key.Z && FocusManager?.GetFocusedElement() is not TextBox) { ed.Undo(); e.Handled = true; }
            else if (e.Key == Key.Y && FocusManager?.GetFocusedElement() is not TextBox) { ed.Redo(); e.Handled = true; }
        }

        async void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (closeAnyway || !ed.HasChanges) { ed.Changed -= OnChanged; return; }
            e.Cancel = true;
            var dlg = new Window { Title = "Combat balance", SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            string answer = null;
            var p = new StackPanel { Margin = new Thickness(14), Spacing = 10 };
            p.Children.Add(new TextBlock { Text = "Save the combat changes before closing?", FontSize = 13 });
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

        public bool HasChanges => ed.HasChanges;

        string KindName(int k)
        {
            var list = ed.Npcs.Records().Where(n => n.Kind == k && n.Name.Length > 0).Select(n => n.Name).Distinct().Take(3).ToList();
            return string.Join(", ", list);
        }

        string TypeName(int type) => names(type) ?? "type " + type;

        static TextBlock Label(string text, double size = 12, FontWeight w = FontWeight.Normal) =>
            new TextBlock { Text = text, FontSize = size, FontWeight = w, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

        // ---------------------------------------------------------------- balance

        Control BalanceTab()
        {
            var side = new StackPanel { Spacing = 6, Margin = new Thickness(6), Width = 290 };
            side.Children.Add(Label("The Avatar", 14, FontWeight.SemiBold));
            hpBox.Text = avatar.Hp.ToString();
            hpBox.LostFocus += (s, e) => ReadHp();
            hpBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) ReadHp(); };
            side.Children.Add(Pair("Hit points", hpBox));
            side.Children.Add(Label("The Avatar starts a new game with the HP of NPC.FLX record 0 and gains more with experience.", 11));
            strengthBox.SelectionChanged += (s, e) => { avatar.Strength = Math.Max(0, strengthBox.SelectedIndex); Recompute(); };
            side.Children.Add(Pair("Strength", strengthBox));
            modeBox.SelectionChanged += (s, e) => { avatar.Mode = modeBox.SelectedIndex - 1; Recompute(); };
            side.Children.Add(Pair("Attack", modeBox));

            side.Children.Add(Label("Armour", 13, FontWeight.SemiBold));
            var sets = new WrapPanel();
            string[] setNames = { "None", "Leather", "Chain", "Plate", "Bone", "Wyrmguard", "Blackrock" };
            for (int level = 0; level < setNames.Length; level++)
            {
                int l = level;
                var b = new Button { Content = setNames[level], FontSize = 11, Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 4, 4) };
                b.Click += (s, e) => WearSet(l);
                sets.Children.Add(b);
            }
            side.Children.Add(sets);
            var pieces = ed.Tables.ArmourPieces();
            for (int slot = 0; slot < 6; slot++)
            {
                var items = new List<string> { "(nothing)" };
                items.AddRange(pieces.Where(p => p.Slot == slot).OrderBy(p => p.Level).Select(p => $"{TypeName(p.Type)}"));
                var box = new ComboBox { ItemsSource = items, SelectedIndex = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
                int sl = slot;
                box.SelectionChanged += (s, e) =>
                {
                    if (building) return;
                    var list = ed.Tables.ArmourPieces().Where(p => p.Slot == sl).OrderBy(p => p.Level).ToList();
                    avatar.Armour[sl] = box.SelectedIndex > 0 ? list[box.SelectedIndex - 1].Index : null;
                    Recompute();
                };
                slotBoxes[slot] = box;
                side.Children.Add(Pair(CombatTables.SlotNames[slot], box));
            }
            var shields = new List<string> { "(no shield)" };
            shields.AddRange(Shields().Select(w => $"{TypeName(w.Type)} ({w.Min}-{w.Max})"));
            shieldBox.ItemsSource = shields;
            shieldBox.SelectedIndex = 0;
            shieldBox.SelectionChanged += (s, e) =>
            {
                var list = Shields();
                avatar.Shield = shieldBox.SelectedIndex > 0 ? list[shieldBox.SelectedIndex - 1].Index : null;
                Recompute();
            };
            side.Children.Add(Pair("Shield", shieldBox));

            side.Children.Add(Label("Shown", 13, FontWeight.SemiBold));
            creatureFilter.TextChanged += (s, e) => Recompute();
            side.Children.Add(creatureFilter);
            hostileOnly.IsCheckedChanged += (s, e) => Recompute();
            ToolTip.SetTip(hostileOnly, "NPC.FLX records with flag 0x08000000 at +0x48: the hostile creature and fighter templates (bandits, undead, beasts)");
            side.Children.Add(hostileOnly);
            var classes = new WrapPanel();
            string[] classLabels = { "Fists", "One-handed", "Two-handed", "Staves", "Bows" };
            for (int c = 0; c < 5; c++)
            {
                classShown[c] = new CheckBox { Content = classLabels[c], IsChecked = c > 0, FontSize = 12, Margin = new Thickness(0, 0, 8, 0) };
                classShown[c].IsCheckedChanged += (s, e) => Recompute();
                classes.Children.Add(classShown[c]);
            }
            side.Children.Add(classes);
            side.Children.Add(Label("Mean hits from the game's own rules: damage random(min, max) times the creature's multiplier for the attack's damage types, " +
                                    "plus strength, minus the defender's defence roll. Not counted: blocking, poison and other on-hit effects, spells, and the " +
                                    "per-object Min/Max Damage and Defense properties placed creatures may carry. Kind -1 creatures read multipliers from outside the table: shown as 1.", 11));

            var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };

            var inner = innerTabs;
            inner.Items.Add(new TabItem { Header = "Hits for you to kill them", Content = MatrixPane(killThem, true), FontSize = 13 });
            inner.Items.Add(new TabItem { Header = "Hits for them to kill you", Content = MatrixPane(killYou, false), FontSize = 13 });
            killThem.Hovered += (r, c) => ShowCell(killThemCells, r, c, true);
            killYou.Hovered += (r, c) => ShowCell(killYouCells, r, c, false);

            var right = new DockPanel();
            var detailBorder = new Border { Child = detail, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(detailBorder, Dock.Bottom);
            right.Children.Add(detailBorder);
            right.Children.Add(inner);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            Grid.SetColumn(right, 1);
            grid.Children.Add(sideScroll);
            grid.Children.Add(right);
            return grid;
        }

        static Control MatrixPane(MatrixView m, bool you)
        {
            var sv = new ScrollViewer { Content = m, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            m.Follow(sv);
            var help = Label(you
                ? "Mean number of hits for the Avatar to kill each creature (rows) with each weapon (columns), with the strength, armour and attack on the left. Green: few hits."
                : "Mean number of hits for each creature's attack (rows) to kill the Avatar in each armour set (columns), with the shield and HP on the left. Green: many. The framed column is the armour chosen on the left.", 11);
            help.Margin = new Thickness(4, 2, 4, 4);
            var dp = new DockPanel();
            DockPanel.SetDock(help, Dock.Top);
            dp.Children.Add(help);
            dp.Children.Add(sv);
            return dp;
        }

        static Control Pair(string label, Control c)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
            var t = Label(label);
            Grid.SetColumn(c, 1);
            g.Children.Add(t);
            g.Children.Add(c);
            return g;
        }

        void ReadHp()
        {
            if (int.TryParse(hpBox.Text, out int hp) && hp > 0) { avatar.Hp = Math.Min(hp, 65535); Recompute(); }
            hpBox.Text = avatar.Hp.ToString();
        }

        List<Weapon> Shields() => ed.Tables.Weapons().Where(w => w.IsShield && w.Max > 0).GroupBy(w => w.Type).Select(g => g.First()).ToList();

        void WearSet(int level)
        {
            building = true;
            var pieces = ed.Tables.ArmourPieces();
            for (int slot = 0; slot < 6; slot++)
            {
                var list = pieces.Where(p => p.Slot == slot).OrderBy(p => p.Level).ToList();
                int k = level == 0 ? -1 : list.FindIndex(p => p.Level == level);
                avatar.Armour[slot] = k >= 0 ? list[k].Index : null;
                slotBoxes[slot].SelectedIndex = k + 1;
            }
            building = false;
            Recompute();
        }

        List<NpcRecord> ShownCreatures()
        {
            string f = creatureFilter.Text?.Trim() ?? "";
            bool hostile = hostileOnly.IsChecked == true;
            return ed.Npcs.Records().Where(n => n.Name.Length > 0 && n.MaxHp > 1 && (!hostile || n.Fighter) &&
                                                (f.Length == 0 || n.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(n => (n.Name, n.MaxHp, n.Defence, n.Kind)).Select(g => g.First()).ToList();
        }

        List<(Weapon W, string Name)> ShownWeapons()
        {
            var list = new List<(Weapon, string)>();
            var seen = new HashSet<string>();
            foreach (var w in ed.Tables.Weapons())
            {
                if (w.Class > 4 || w.Max <= 0 || classShown[w.Class].IsChecked != true) continue;
                string n = TypeName(w.Type);
                if (w.Type == 4792) n += " (broken)";
                if (!seen.Add($"{n}|{w.Min}|{w.Max}")) continue;     // T2's twins of T1 records
                list.Add((w, n));
            }
            return list;
        }

        void Recompute()
        {
            if (killThem == null || classShown[4] == null) return;
            var creatures = ShownCreatures();
            var weapons = ShownWeapons();
            killThemCells = new HitsToKill[creatures.Count, weapons.Count];
            var vals = new double[creatures.Count, weapons.Count];
            for (int r = 0; r < creatures.Count; r++)
                for (int c = 0; c < weapons.Count; c++)
                {
                    var h = CombatMath.AvatarHits(ed.Tables, avatar, weapons[c].W, weapons[c].Name, creatures[r]);
                    killThemCells[r, c] = h;
                    vals[r, c] = h.Expected;
                }
            killThem.Rows = creatures.Select(n => $"{n.Name}  {n.MaxHp} HP, def {n.Defence}" + (n.Invulnerable ? ", invulnerable" : "")).ToArray();
            killThem.Columns = weapons.Select(w => $"{w.Name} {w.W.Min}-{w.W.Max}").ToArray();
            killThem.Values = vals;
            killThem.Refresh();

            // Creatures' attacks against armour sets: the chosen attack, or all of the kind's defaults.
            var attackRows = new List<(NpcRecord N, string Choice, Attack? A)>();
            foreach (var n in creatures)
            {
                var defaults = CreatureAttacks.DefaultsOf(n);
                var list = attackChoice.TryGetValue(n.Index, out var chosen) && !(defaults.Length > 0 && chosen == defaults[0]) ? new[] { chosen } : defaults.Length > 0 ? defaults : new[] { "" };
                foreach (var c in list) attackRows.Add((n, c, CreatureAttacks.Resolve(ed.Tables, c, names, n)));
            }
            var sets = new List<(string Name, AvatarSetup Setup)>();
            string[] setNames = { "No armour", "Leather", "Chain", "Plate", "Bone", "Wyrmguard", "Blackrock" };
            var pieces = ed.Tables.ArmourPieces();
            for (int level = 0; level < setNames.Length; level++)
            {
                var s = new AvatarSetup { Hp = avatar.Hp, Shield = avatar.Shield };
                for (int slot = 0; slot < 6; slot++) s.Armour[slot] = level == 0 ? null : pieces.Where(p => p.Slot == slot && p.Level == level).Select(p => (int?)p.Index).FirstOrDefault();
                sets.Add((setNames[level], s));
            }
            sets.Add(("Yours (left)", avatar));
            killYouCells = new HitsToKill[attackRows.Count, sets.Count];
            var v2 = new double[attackRows.Count, sets.Count];
            for (int r = 0; r < attackRows.Count; r++)
                for (int c = 0; c < sets.Count; c++)
                {
                    if (attackRows[r].A is not Attack a) { v2[r, c] = attackRows[r].Choice == "none" ? double.PositiveInfinity : double.NaN; continue; }
                    var h = CombatMath.CreatureHits(ed.Tables, sets[c].Setup, attackRows[r].N, a);
                    killYouCells[r, c] = h;
                    v2[r, c] = h.Expected;
                }
            killYou.Rows = attackRows.Select(x => $"{x.N.Name}: " + (x.A is Attack a ? $"{a.Label} {a.Min}-{a.Max}" :
                x.Choice == "none" ? "never attacks" : "attack not known (spells or effects; pick one on the Creatures tab)")).ToArray();
            killYou.Columns = sets.Select(s => s.Name).ToArray();
            killYou.Values = v2;
            killYou.MarkedColumn = sets.Count - 1;
            killYou.Refresh();
        }

        void ShowCell(HitsToKill[,] cells, int r, int c, bool you)
        {
            if (r < 0 || c < 0 || r >= cells.GetLength(0) || c >= cells.GetLength(1) || cells[r, c] == null) { detail.Text = ""; return; }
            var h = cells[r, c];
            string hits = h.Never ? "no hit can do damage" : $"{h.Expected:0.0} hits on average, {h.Best} at best";
            detail.Text = $"{h.How}\nDamage per hit {Math.Max(0, h.Damage.Min)}-{Math.Max(0, h.Damage.Max)}, {h.Damage.MeanLanded:0.0} on average, " +
                          $"{h.Damage.Lands:P0} of hits do any. {h.Hp} HP: {hits}.";
        }

        // ---------------------------------------------------------------- editable rows

        /// <summary>A row of an edit table: properties bound to text boxes; Refresh re-reads them all.</summary>
        abstract class Row : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string F(double v) => v.ToString("0.###", Inv);
        static bool TryNumber(string s, out double v) => double.TryParse(s?.Trim().TrimStart('x', '×'), NumberStyles.Float, Inv, out v);

        /// <summary>A table: header names with widths, then a virtualised list of rows made by <paramref name="cells"/>.</summary>
        Control Table(string help, (string Name, double Width)[] columns, IEnumerable<Row> items, Func<Row, int, Control> cells)
        {
            var list = items.ToList();
            rows.AddRange(list);
            string widths = string.Join(",", columns.Select(c => c.Width.ToString(Inv)));
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions(widths), Margin = new Thickness(8, 4, 8, 2) };
            for (int i = 0; i < columns.Length; i++)
            {
                var t = Label(columns[i].Name, 12, FontWeight.SemiBold);
                Grid.SetColumn(t, i);
                header.Children.Add(t);
            }
            var lb = new ListBox
            {
                ItemsSource = list,
                ItemTemplate = new FuncDataTemplate<Row>((row, scope) =>
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions(widths) };
                    for (int i = 0; i < columns.Length; i++)
                    {
                        var c = cells(null, i);
                        if (c == null) continue;
                        c.Margin = new Thickness(0, 0, 4, 0);
                        Grid.SetColumn(c, i);
                        g.Children.Add(c);
                    }
                    return g;
                }),
            };
            lb.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
            {
                Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(8, 1)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0) },
            });
            var dp = new DockPanel();
            var h = Label(help, 11);
            h.Margin = new Thickness(8, 4);
            DockPanel.SetDock(h, Dock.Top);
            DockPanel.SetDock(header, Dock.Top);
            dp.Children.Add(h);
            dp.Children.Add(header);
            dp.Children.Add(lb);
            return dp;
        }

        static TextBlock Bound(string path) { var t = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; t.Bind(TextBlock.TextProperty, new Binding(path)); return t; }

        static TextBox Edit(string path)
        {
            var t = new TextBox { FontSize = 12, Padding = new Thickness(4, 1), MinHeight = 22 };
            t.Bind(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            t.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                BindingOperations.GetBindingExpressionBase(t, TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            };
            return t;
        }

        // ---------------------------------------------------------------- weapons

        sealed class WeaponRow : Row
        {
            public CombatWindow W; public int Index; public string Name;
            Weapon Now => W.ed.Tables.WeaponAt(Index);
            public string Type => Now.Type.ToString();
            public string Class => CombatTables.ClassNames.ElementAtOrDefault(Now.Class) ?? Now.Class.ToString();
            public string Table => Index < CombatTables.WeaponT1Count ? "T1" : "T2";
            public string Min { get => Now.Min.ToString(); set => Put(W.ed.Tables.WeaponMin(Index, Name), value); }
            public string Max { get => Now.Max.ToString(); set => Put(W.ed.Tables.WeaponMax(Index, Name), value); }
            public string Reach { get => F(Now.Reach); set => Put(CombatTables.WeaponReach(Index, Name), value); }
            public string Radius { get => F(Now.Radius); set => Put(CombatTables.WeaponRadius(Index, Name), value); }
            public string Attacks
            {
                get
                {
                    var w = Now;
                    if (w.IsShield) return "blocks: a tenth of a roll from min to max comes off each hit";
                    if (w.IsLight) return "light";
                    var parts = new List<string>();
                    for (int m = 0; m < (w.IsBow ? 1 : 4); m++)
                        if (W.ed.Tables.AttackModeOf(w.Type, w.Class, m) is AttackMode a && a.Flags != 0) parts.Add($"{m + 1}: {CombatMath.FlagNames(a.Flags)}");
                    return string.Join("; ", parts);
                }
            }
            void Put(ExeStat s, string v) { if (TryNumber(v, out double d)) W.ed.Set(s, d); else Refresh(); }
        }

        Control WeaponsTab()
        {
            var items = ed.Tables.Weapons().Where(w => w.Type != 9999).Select(w => (Row)new WeaponRow { W = this, Index = w.Index, Name = TypeName(w.Type) + $" ({w.Type})" });
            return Table("Weapons, shields and lights of u9.exe's two weapon tables (T2 holds a second record for 13 types; the game takes T1's first). " +
                         "Damage is random(min, max); a shield's min and max are its block roll, of which a tenth is taken off each hit. Reach is the distance of the hit " +
                         "sphere in front (a bow's range); hit radius is that sphere's size. Damage types come from the attack-mode table (by type, else by class).",
                new[] { ("Weapon", 210.0), ("Type", 50.0), ("Class", 90.0), ("Table", 40.0), ("Min", 60.0), ("Max", 60.0), ("Reach", 64.0), ("Radius", 60.0), ("Attacks and damage types", 520.0) },
                items, (r, i) => i switch
                {
                    0 => Bound(nameof(WeaponRow.Name)), 1 => Bound(nameof(WeaponRow.Type)), 2 => Bound(nameof(WeaponRow.Class)), 3 => Bound(nameof(WeaponRow.Table)),
                    4 => Edit(nameof(WeaponRow.Min)), 5 => Edit(nameof(WeaponRow.Max)), 6 => Edit(nameof(WeaponRow.Reach)), 7 => Edit(nameof(WeaponRow.Radius)),
                    _ => Bound(nameof(WeaponRow.Attacks)),
                });
        }

        // ---------------------------------------------------------------- armour

        sealed class ArmourRow : Row
        {
            public CombatWindow W; public int Index; public string Name;
            ArmourPiece Now => W.ed.Tables.ArmourPieces().First(p => p.Index == Index);
            public string Slot => CombatTables.SlotNames.ElementAtOrDefault(Now.Slot) ?? Now.Slot.ToString();
            public string Level => Now.Level.ToString();
            public string Min { get => Now.Min.ToString(); set => Put(CombatTables.ArmourMin(Index, Name), value); }
            public string Max { get => Now.Max.ToString(); set => Put(CombatTables.ArmourMax(Index, Name), value); }
            void Put(ExeStat s, string v) { if (TryNumber(v, out double d) && d >= 0 && d <= 255) W.ed.Set(s, d); else Refresh(); }
        }

        Control ArmourTab()
        {
            var items = ed.Tables.ArmourPieces().OrderBy(p => p.Slot).ThenBy(p => p.Level).Select(p => (Row)new ArmourRow { W = this, Index = p.Index, Name = TypeName(p.Type) });
            return Table("The Avatar's armour (u9.exe): each worn piece rolls random(min, max) per hit and the sum comes off the damage. Values 0-255. " +
                         "Other characters do not wear these: their defence is NPC.FLX's (Creatures tab).",
                new[] { ("Piece", 240.0), ("Slot", 90.0), ("Level", 50.0), ("Min", 60.0), ("Max", 60.0) },
                items, (r, i) => i switch
                {
                    0 => Bound(nameof(ArmourRow.Name)), 1 => Bound(nameof(ArmourRow.Slot)), 2 => Bound(nameof(ArmourRow.Level)),
                    3 => Edit(nameof(ArmourRow.Min)), _ => Edit(nameof(ArmourRow.Max)),
                });
        }

        // ---------------------------------------------------------------- creatures

        sealed class CreatureRow : Row
        {
            public CombatWindow W; public int Index;
            NpcRecord Now => W.ed.Npcs[Index];
            public string Number => Index.ToString();
            public string Name => Now.Name;
            public string Hp { get => Now.MaxHp.ToString(); set => Put(NpcField.Hp, value); }
            public string Defence { get => Now.Defence.ToString(); set => Put(NpcField.Defence, value); }
            public string Kind { get => Now.Kind.ToString(); set => Put(NpcField.Kind, value); }
            public string KindOf => Now.Kind < 0 ? "none (multipliers read from outside the table)" : W.KindName(Now.Kind);
            public string Flags
            {
                get
                {
                    var n = Now; var f = new List<string>();
                    if (n.Fighter) f.Add("fighter");
                    if (n.Invulnerable) f.Add("invulnerable");
                    if (n.UndeadOrDemon) f.Add("undead/demon");
                    if (n.HitsHarder) f.Add("hits x1.1");
                    return string.Join(", ", f);
                }
            }
            public List<string> Choices => W.choiceLabels;
            public int Choice
            {
                get { var c = W.attackChoice.GetValueOrDefault(Index, ""); int k = W.choiceKeys.IndexOf(c); return k < 0 ? 0 : k; }
                set { if (value < 0) return; W.attackChoice[Index] = W.choiceKeys[value]; W.Recompute(); }
            }
            void Put(NpcField f, string v)
            {
                var (min, max) = NpcTable.RangeOf(f);
                if (int.TryParse(v?.Trim(), out int d) && d >= min && d <= max) W.ed.SetNpc(Index, f, d); else Refresh();
            }
        }

        List<string> choiceKeys, choiceLabels;

        Control CreaturesTab()
        {
            var all = CreatureAttacks.All(ed.Tables, names);
            choiceKeys = new List<string> { "" };
            choiceLabels = new List<string> { "(not known)" };
            choiceKeys.AddRange(all.Select(a => a.Choice));
            choiceLabels.AddRange(all.Select(a => a.Label));
            var items = ed.Npcs.Records().Where(n => n.Name.Length > 0).Select(n => (Row)new CreatureRow { W = this, Index = n.Index }).ToList();
            var filter = new TextBox { PlaceholderText = "Filter by name", FontSize = 12, Margin = new Thickness(8, 4), Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
            var table = Table("NPC.FLX's characters and creature templates (a creature's type number is its record number). HP sets current, maximum and base maximum. " +
                              "Defence: random(0, this) comes off each hit it takes. Kind: its row of damage multipliers (and AI class). Attack: what the balance uses for its " +
                              "hits on the Avatar (not stored in the game). New games only: saves keep their own copy.",
                new[] { ("#", 40.0), ("Name", 190.0), ("HP", 64.0), ("Defence", 64.0), ("Kind", 50.0), ("Kind's creatures", 230.0), ("Flags", 170.0), ("Attack (for the balance)", 330.0) },
                items, (r, i) => i switch
                {
                    0 => Bound(nameof(CreatureRow.Number)), 1 => Bound(nameof(CreatureRow.Name)), 2 => Edit(nameof(CreatureRow.Hp)), 3 => Edit(nameof(CreatureRow.Defence)),
                    4 => Edit(nameof(CreatureRow.Kind)), 5 => Bound(nameof(CreatureRow.KindOf)), 6 => Bound(nameof(CreatureRow.Flags)),
                    _ => ChoiceBox(),
                });
            var lb = table.GetLogicalDescendants().OfType<ListBox>().First();
            filter.TextChanged += (s, e) =>
            {
                string f = filter.Text?.Trim() ?? "";
                lb.ItemsSource = items.Where(x => f.Length == 0 || ((CreatureRow)x).Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            };
            var dp = new DockPanel();
            DockPanel.SetDock(filter, Dock.Top);
            dp.Children.Add(filter);
            dp.Children.Add(table);
            return dp;
        }

        static ComboBox ChoiceBox()
        {
            var c = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 22, Padding = new Thickness(4, 0) };
            c.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(CreatureRow.Choices)));
            c.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty, new Binding(nameof(CreatureRow.Choice)) { Mode = BindingMode.TwoWay });
            return c;
        }

        // ---------------------------------------------------------------- natural attacks

        sealed class NaturalRow : Row
        {
            public CombatWindow W; public int Index;
            NaturalAttack Now => W.ed.Tables.NaturalAttacks()[Index];
            public string Number => Index.ToString();
            public string Bone => Now.Bone;
            public string Min { get => Now.Min.ToString(); set => Put(CombatTables.NaturalMin(Index), value); }
            public string Max { get => Now.Max.ToString(); set => Put(CombatTables.NaturalMax(Index), value); }
            public string Types { get => CombatMath.FlagNames(Now.Flags); set { if (ParseFlags(value) is int f) W.ed.Set(CombatTables.NaturalFlags(Index), f); else Refresh(); } }
            public string Reach => F(Now.Reach);
            public string Users => string.Join(", ", W.ed.Npcs.Records().Where(n => W.attackChoice.GetValueOrDefault(n.Index) == $"n:{Index}" || CreatureAttacks.DefaultsOf(n).Contains($"n:{Index}")).Select(n => n.Name).Distinct());
            public string Note => Index <= 2 ? "Avatar's punch/kick/headbutt: the game works these out from strength, min and max unused" : Now.Special == 4 ? "poisons" : Now.Special == 12 ? "fire burst" : Now.Special == 14 ? "drains mana" : "";
            void Put(ExeStat s, string v) { if (TryNumber(v, out double d)) W.ed.Set(s, d); else Refresh(); }
        }

        /// <summary>Damage types from names ("slash + fire") or a number (0x41).</summary>
        static int? ParseFlags(string s)
        {
            s = s?.Trim() ?? "";
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[2..], NumberStyles.HexNumber, Inv, out int hex)) return hex;
            if (int.TryParse(s, out int n)) return n;
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
            int f = 0;
            foreach (var part in s.Split(new[] { '+', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int b = Array.FindIndex(CombatTables.BitNames, x => x.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (b < 0 && part.StartsWith("bit ", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[4..], out int bb) && bb < 32) b = bb;
                if (b < 0) return null;
                f |= 1 << b;
            }
            return f;
        }

        Control NaturalTab() => Table("Creatures' built-in attacks (u9.exe): bites, claws, breath. Damage types: names joined by + (" + string.Join(", ", CombatTables.BitNames) + ") or a number.",
            new[] { ("#", 36.0), ("Bone", 120.0), ("Min", 60.0), ("Max", 60.0), ("Damage types", 180.0), ("Reach", 50.0), ("Used by (for the balance)", 330.0), ("Note", 360.0) },
            Enumerable.Range(0, CombatTables.NaturalCount).Select(i => (Row)new NaturalRow { W = this, Index = i }),
            (r, i) => i switch
            {
                0 => Bound(nameof(NaturalRow.Number)), 1 => Bound(nameof(NaturalRow.Bone)), 2 => Edit(nameof(NaturalRow.Min)), 3 => Edit(nameof(NaturalRow.Max)),
                4 => Edit(nameof(NaturalRow.Types)), 5 => Bound(nameof(NaturalRow.Reach)), 6 => Bound(nameof(NaturalRow.Users)), _ => Bound(nameof(NaturalRow.Note)),
            });

        // ---------------------------------------------------------------- multipliers

        sealed class KindRow : Row
        {
            public CombatWindow W; public int Kind;
            public string Number => Kind.ToString();
            public string Who => W.KindName(Kind);
            public string this[int bit]
            {
                get => F(W.ed.Tables.Multiplier(Kind, bit));
                set { if (TryNumber(value, out double d) && d >= 0 && d < 1000) W.ed.SetMultiplier(Kind, bit, (float)d); else Refresh(); }
            }
        }

        Control MultipliersTab()
        {
            var cols = new List<(string, double)> { ("Kind", 40.0), ("Creatures", 240.0) };
            cols.AddRange(CombatTables.BitNames.Select(b => (b, 62.0)));
            return Table("Damage multipliers per creature kind and damage type (u9.exe start-up code at VA 0x44A040). An attack's factor is the product of the non-zero " +
                         "multipliers of its types; when they add up to 0 the creature takes nothing (0 = immune). Some values are pushed as a one-byte 0: changing one " +
                         "rewrites that code as a table (outside the code U9GL checks).",
                cols.ToArray(),
                Enumerable.Range(0, CombatTables.Kinds).Select(k => (Row)new KindRow { W = this, Kind = k }),
                (r, i) => i switch { 0 => Bound(nameof(KindRow.Number)), 1 => Bound(nameof(KindRow.Who)), _ => Edit($"[{i - 2}]") });
        }

        // ---------------------------------------------------------------- shots

        void SaveShot(string path)
        {
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(path);
        }
    }
}
