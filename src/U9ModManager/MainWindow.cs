using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using U9.Mods;

namespace U9ModManager
{
    /// <summary>
    /// The mod manager's window: a game folder, the library with the mods ticked to apply (in order), what applying them
    /// would do, the catalog, and U9GL.
    /// </summary>
    public sealed class MainWindow : Window
    {
        readonly string home;
        readonly ManagerSettings settings;
        readonly ModLibrary library;
        GameInstall game;
        List<ModLibrary.Item> items = new List<ModLibrary.Item>();
        List<string> chosen = new List<string>();
        List<CatalogEntry> catalog = new List<CatalogEntry>();
        ModLibrary.U9glRelease u9glLatest;
        bool busy;

        readonly ComboBox gameBox = new ComboBox { MinWidth = 420 };
        readonly TextBlock gameState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        readonly ListBox modList = new ListBox { MinHeight = 260 };
        readonly SelectableTextBlock details = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        readonly SelectableTextBlock plan = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12 };
        readonly TextBox catalogUrl = new TextBox { PlaceholderText = "https://.../catalog.json", MinWidth = 420 };
        readonly ListBox catalogList = new ListBox { MinHeight = 240 };
        readonly SelectableTextBlock catalogDetails = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        readonly SelectableTextBlock u9glText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        readonly CheckBox u9glSmall = new CheckBox { Content = "The small download (needs .NET 10 installed)" };
        readonly CheckBox laa = new CheckBox { Content = "Large address aware u9-mod.exe (more memory, as U9GL's u9-laa.exe)" };
        readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap };
        readonly Button applyButton = new Button { Content = "Apply" }, originalButton = new Button { Content = "The original game" }, playButton = new Button { Content = "Play" };

        public MainWindow()
        {
            home = Program.HomeAtStart ?? GameInstall.DefaultHome;
            settings = ManagerSettings.Load(home);
            library = new ModLibrary(home);
            Title = "U9 Mod Manager";
            Width = 1100; Height = 720;
            Content = Build();
            laa.IsChecked = settings.LargeAddressAware;
            laa.IsCheckedChanged += (s, e) => { settings.LargeAddressAware = laa.IsChecked == true; settings.Save(home); };
            catalogUrl.Text = settings.CatalogUrl;
            if (Program.GameAtStart != null) AddGame(Program.GameAtStart, save: false);
            gameBox.ItemsSource = settings.Games.ToList();
            gameBox.SelectionChanged += (s, e) => OpenGame(gameBox.SelectedItem as string);
            if (Program.GameAtStart != null) gameBox.SelectedItem = settings.Games.First(g => string.Equals(g, Path.GetFullPath(Program.GameAtStart), StringComparison.OrdinalIgnoreCase));
            else if (settings.Games.Count > 0) gameBox.SelectedIndex = 0;
            else ShowLibrary();
            if (Program.ShotPath != null) Opened += async (s, e) => { await Task.Delay(1500); SaveShot(Program.ShotPath); Close(); };
        }

        Control Build()
        {
            var add = new Button { Content = "Add game folder…" };
            add.Click += async (s, e) => await PickGame();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8) };
            top.Children.Add(new TextBlock { Text = "Game", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
            top.Children.Add(gameBox);
            top.Children.Add(add);
            var topPanel = new StackPanel();
            topPanel.Children.Add(top);
            topPanel.Children.Add(new Border { Child = gameState, Margin = new Thickness(8, 0, 8, 6) });

            var tabs = new TabControl { Margin = new Thickness(4, 0) };
            tabs.Items.Add(new TabItem { Header = "Mods", Content = ModsTab() });
            tabs.Items.Add(new TabItem { Header = "Catalog", Content = CatalogTab() });
            tabs.Items.Add(new TabItem { Header = "U9GL", Content = U9glTab() });

            var dock = new DockPanel();
            DockPanel.SetDock(topPanel, Dock.Top);
            DockPanel.SetDock(status, Dock.Bottom);
            dock.Children.Add(topPanel);
            dock.Children.Add(status);
            dock.Children.Add(tabs);
            return dock;
        }

        Control ModsTab()
        {
            var up = new Button { Content = "Up" };
            var down = new Button { Content = "Down" };
            var import = new Button { Content = "Import…" };
            var remove = new Button { Content = "Remove from library" };
            up.Click += (s, e) => Move(-1);
            down.Click += (s, e) => Move(+1);
            import.Click += async (s, e) => await PickImport();
            remove.Click += (s, e) => RemoveSelected();
            ToolTip.SetTip(up, "Ticked mods apply top first; where two change the same file their changes are merged");
            var left = new DockPanel { Margin = new Thickness(4) };
            var head = new TextBlock { Text = "Library: tick the mods to apply", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var b in new[] { up, down, import, remove }) { b.Margin = new Thickness(0, 0, 6, 4); buttons.Children.Add(b); }
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            left.Children.Add(head);
            left.Children.Add(buttons);
            left.Children.Add(modList);
            modList.SelectionChanged += (s, e) => ShowDetails();

            applyButton.Click += async (s, e) => await Apply(chosenMods: true);
            originalButton.Click += async (s, e) => await Apply(chosenMods: false);
            playButton.Click += (s, e) => Play();
            ToolTip.SetTip(applyButton, "Makes the game folder what the ticked mods give (all or nothing); the originals are kept in the manager's vault");
            ToolTip.SetTip(originalButton, "Puts every file the manager changed back as shipped, and removes u9-mod.exe");
            ToolTip.SetTip(playButton, "Starts u9-mod.exe when the active mods patch the executable, else u9.exe");
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
            actions.Children.Add(applyButton);
            actions.Children.Add(originalButton);
            actions.Children.Add(playButton);
            var right = new StackPanel { Spacing = 6, Margin = new Thickness(8, 4, 4, 4) };
            right.Children.Add(new TextBlock { Text = "The mod", FontWeight = FontWeight.SemiBold });
            right.Children.Add(details);
            right.Children.Add(new TextBlock { Text = "Applying the ticked mods", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            right.Children.Add(plan);
            right.Children.Add(laa);
            right.Children.Add(actions);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*") };
            Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(new ScrollViewer { Content = right, [Grid.ColumnProperty] = 1 });
            return grid;
        }

        Control CatalogTab()
        {
            var refresh = new Button { Content = "Refresh" };
            var download = new Button { Content = "Download into the library" };
            refresh.Click += async (s, e) => await RefreshCatalog();
            download.Click += async (s, e) => await DownloadSelected();
            catalogList.SelectionChanged += (s, e) =>
            {
                var c = catalogList.SelectedItem as CatalogEntry;
                catalogDetails.Text = c == null ? "" :
                    $"{c.Name} {c.Version} ({c.Id})\nby {string.Join(", ", c.Authors)}\n\n{c.Description}\n\n{c.Url}\n{(c.Size > 0 ? $"{c.Size / 1024.0:0} KB, " : "")}SHA-256 {c.Sha256}" +
                    (c.Homepage.Length > 0 ? "\n" + c.Homepage : "") +
                    (items.Any(i => i.Mod?.Id == c.Id) ? $"\n\nIn the library: {items.First(i => i.Mod?.Id == c.Id).Mod.Version}" : "");
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = "Catalog", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(catalogUrl);
            row.Children.Add(refresh);
            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(6) };
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock { Text = "A JSON list of mods (docs/mod-format.md); each download is checked against its SHA-256 and read whole before it enters the library.", Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("420,*") };
            grid.Children.Add(catalogList);
            var right = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0) };
            right.Children.Add(catalogDetails);
            right.Children.Add(download);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
            panel.Children.Add(grid);
            return panel;
        }

        Control U9glTab()
        {
            var check = new Button { Content = "Check for the latest" };
            var download = new Button { Content = "Download and open U9GL" };
            var open = new Button { Content = "Open U9GL" };
            check.Click += async (s, e) => await CheckU9gl();
            download.Click += async (s, e) => await DownloadU9gl();
            open.Click += (s, e) => OpenU9gl();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(check);
            row.Children.Add(download);
            row.Children.Add(open);
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(6) };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
                Text = "U9GL (theGreyWanderer-uc/tgwUltima9GL) is an OpenGL renderer, launcher and fixes for Ultima IX. The manager downloads its " +
                       "release, checks it against the SHA-256 the release lists, unpacks its u9gl folder into the game folder and opens U9GL.exe, " +
                       "whose Install puts dsound.dll in place (keeping its own backups). U9GL's dsound.dll also loads into u9-mod.exe. U9GL's launcher " +
                       "recognises the game by u9.exe, which the manager never changes.",
            });
            panel.Children.Add(u9glText);
            panel.Children.Add(u9glSmall);
            panel.Children.Add(row);
            return panel;
        }

        // ---- game folders

        async Task PickGame()
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "The Ultima IX folder (holding u9.exe)" });
            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            if (GamePaths.FindExe(path) == null) { Status($"{path} has no u9.exe."); return; }
            AddGame(path, save: true);
            gameBox.ItemsSource = settings.Games.ToList();
            gameBox.SelectedItem = settings.Games.First(g => string.Equals(g, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        }

        void AddGame(string path, bool save)
        {
            string full = Path.GetFullPath(path);
            if (!settings.Games.Contains(full, StringComparer.OrdinalIgnoreCase)) settings.Games.Insert(0, full);
            if (save) settings.Save(home);
        }

        void OpenGame(string path)
        {
            game = null;
            if (path == null) { ShowLibrary(); return; }
            try { game = new GameInstall(path, home); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Status("Cannot open the game folder: " + ex.Message); }
            var state = game?.Read();
            chosen = settings.Chosen.TryGetValue(Path.GetFullPath(path), out var c) ? c.ToList() : state?.Active.Select(a => a.Id).ToList() ?? new List<string>();
            ShowGameState();
            ShowLibrary();
            ShowU9gl();
        }

        void ShowGameState()
        {
            if (game == null) { gameState.Text = "No game folder: add the Ultima IX folder (the one holding u9.exe)."; return; }
            var s = game.Read();
            var drift = game.ChangedSince();
            string u9gl = ModLibrary.InstalledU9gl(game.Root);
            gameState.Text = (s.Active.Count == 0 ? "The original game." : "Active: " + string.Join(", ", s.Active.Select(a => $"{a.Name} {a.Version}")) + ".") +
                             (s.ExeCopy ? " Executable patches in u9-mod.exe." : "") +
                             $" u9.exe {game.ExeId.Size:N0} bytes, SHA-256 {game.ExeId.Sha256[..12]}…" +
                             (u9gl == null ? "  U9GL: not here." : $"  U9GL {u9gl}{(ModLibrary.U9glLoaderInstalled(game.Root) ? ", installed" : ", unpacked (install it in U9GL)")}.") +
                             (drift.Count > 0 ? $"\nChanged since the manager wrote them: {string.Join(", ", drift)}." : "");
        }

        // ---- the library

        void ShowLibrary()
        {
            items = library.Items();
            chosen = chosen.Where(id => items.Any(i => i.Mod?.Id == id)).ToList();
            var ordered = chosen.Select(id => items.First(i => i.Mod?.Id == id))
                                .Concat(items.Where(i => i.Mod == null || !chosen.Contains(i.Mod.Id)).OrderBy(i => i.Mod?.Name ?? i.Path, StringComparer.OrdinalIgnoreCase)).ToList();
            var keep = (modList.SelectedItem as Control)?.Tag as ModLibrary.Item;
            var rows = new List<Control>();
            foreach (var it in ordered)
            {
                var cb = new CheckBox { IsChecked = it.Mod != null && chosen.Contains(it.Mod.Id), IsEnabled = it.Mod != null, VerticalAlignment = VerticalAlignment.Center };
                var text = new TextBlock
                {
                    Text = it.Mod != null ? $"{it.Mod.Name} {it.Mod.Version}   {it.Mod.Summary()}" : $"{Path.GetFileName(it.Path)}: {it.Problem}",
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                cb.IsCheckedChanged += (s, e) =>
                {
                    if (it.Mod == null) return;
                    chosen.Remove(it.Mod.Id);
                    if (cb.IsChecked == true) chosen.Add(it.Mod.Id);
                    UpdatePlan();
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Tag = it };
                row.Children.Add(cb);
                row.Children.Add(text);
                rows.Add(row);
            }
            modList.ItemsSource = rows;
            if (keep != null) modList.SelectedItem = rows.FirstOrDefault(r => (r.Tag as ModLibrary.Item)?.Path == keep.Path);
            if (rows.Count == 0) details.Text = "The library is empty: Import a .u9mod file, or download one from the catalog.";
            UpdatePlan();
        }

        void ShowDetails()
        {
            var it = (modList.SelectedItem as Control)?.Tag as ModLibrary.Item;
            if (it?.Mod == null) { details.Text = it?.Problem ?? ""; return; }
            var m = it.Mod;
            var lines = new List<string>
            {
                $"{m.Name} {m.Version} ({m.Id})", m.Authors.Count > 0 ? "by " + string.Join(", ", m.Authors) : "", m.Description, "",
                $"Changes {m.Summary()}:",
            };
            lines.AddRange(m.Files.Select(f => $"  {f.Path}  ({f.Base.Size:N0} -> {f.Target.Size:N0} bytes)"));
            lines.AddRange(m.ExePatches.Select(p => $"  u9.exe at 0x{p.Offset:X}: {p.New.Length} bytes{(p.Note.Length > 0 ? "  " + p.Note : "")}"));
            if (m.MadeWith.Length > 0) lines.Add($"\nMade with {m.MadeWith}.");
            details.Text = string.Join("\n", lines.Where((l, i) => i > 2 || l.Length > 0));
        }

        List<ModPackage> ChosenMods() => chosen.Select(id => items.First(i => i.Mod?.Id == id).Mod).ToList();

        int planVersion;
        async void UpdatePlan()
        {
            settings.Chosen[game?.Root ?? ""] = chosen.ToList();
            if (game != null) settings.Save(home);
            applyButton.IsEnabled = originalButton.IsEnabled = playButton.IsEnabled = game != null && !busy;
            if (game == null) { plan.Text = ""; return; }
            int v = ++planVersion;
            var mods = ChosenMods();
            plan.Text = mods.Count == 0 ? "No mods ticked: applying gives the original game." : "Working out what the mods give…";
            if (mods.Count == 0) return;
            var g = game;
            StackResult r;
            try { r = await Task.Run(() => g.Plan(mods)); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { plan.Text = ex.Message; return; }
            if (v != planVersion) return;
            var lines = new List<string>();
            foreach (var kv in r.Files) lines.Add($"{kv.Key}: {(r.How.TryGetValue(kv.Key, out var how) ? how : "")}");
            if (r.ExePatches.Count > 0) lines.Add($"u9-mod.exe: {r.ExePatches.Count} patch{(r.ExePatches.Count == 1 ? "" : "es")}");
            foreach (var c in r.Clashes) lines.Add("REFUSED: " + c);
            plan.Text = string.Join("\n", lines);
            plan.Foreground = r.Ok ? Brushes.Black : Brushes.DarkRed;
        }

        void Move(int by)
        {
            var it = (modList.SelectedItem as Control)?.Tag as ModLibrary.Item;
            if (it?.Mod == null || !chosen.Contains(it.Mod.Id)) { Status("Tick a mod first; the ticked ones apply in their order, top first."); return; }
            int i = chosen.IndexOf(it.Mod.Id), j = Math.Clamp(i + by, 0, chosen.Count - 1);
            chosen.RemoveAt(i);
            chosen.Insert(j, it.Mod.Id);
            ShowLibrary();
        }

        async Task PickImport()
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import mods", AllowMultiple = true,
                FileTypeFilter = new[] { new FilePickerFileType("Ultima IX mods") { Patterns = new[] { "*" + ModPackage.Extension } } },
            });
            foreach (var f in files)
            {
                var path = f.TryGetLocalPath();
                if (path == null) continue;
                try { Status("Imported " + library.Import(path).Mod + "."); }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException) { Status($"{Path.GetFileName(path)} was not imported: {ex.Message}"); }
            }
            ShowLibrary();
        }

        async void RemoveSelected()
        {
            var it = (modList.SelectedItem as Control)?.Tag as ModLibrary.Item;
            if (it == null) return;
            string name = it.Mod?.ToString() ?? Path.GetFileName(it.Path);
            if (!await Ask($"Remove {name} from the library? Its file is deleted; a game it is applied to keeps it until you apply again.")) return;
            if (it.Mod != null) { library.Remove(it.Mod.Id); chosen.Remove(it.Mod.Id); } else File.Delete(it.Path);
            ShowLibrary();
            Status($"Removed {name}.");
        }

        // ---- applying and playing

        async Task Apply(bool chosenMods)
        {
            if (game == null || busy) return;
            var mods = chosenMods ? ChosenMods() : new List<ModPackage>();
            busy = true; UpdateButtons();
            Status(mods.Count == 0 ? "Putting the original game back…" : "Applying " + string.Join(", ", mods.Select(m => m.Name)) + "…");
            try
            {
                var g = game;
                bool laaOn = settings.LargeAddressAware;
                var r = await Task.Run(() => g.Apply(mods, false, laaOn));
                if (!r.Ok && g.ChangedSince().Count > 0 && r.Clashes.Count == 1 &&
                    await Ask(r.Clashes[0] + "\n\nOverwrite them?"))
                    r = await Task.Run(() => g.Apply(mods, true, laaOn));
                if (!chosenMods && r.Ok) chosen.Clear();
                Status(r.Ok ? (mods.Count == 0 ? "The original game is back." : $"Applied: {string.Join(", ", mods.Select(m => m.Name))}. {r.Files.Count} files{(r.ExePatches.Count > 0 ? ", u9-mod.exe" : "")}.")
                            : "Nothing was changed: " + string.Join(" ", r.Clashes));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Status("Stopped: " + ex.Message + " (anything half done is finished the next time the manager opens the folder).");
            }
            finally { busy = false; }
            ShowGameState();
            ShowLibrary();
        }

        void UpdateButtons() => applyButton.IsEnabled = originalButton.IsEnabled = playButton.IsEnabled = game != null && !busy;

        void Play()
        {
            if (game == null) return;
            try { game.Launch(); Status("Started the game."); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException) { Status("Could not start the game: " + ex.Message); }
        }

        // ---- catalog

        async Task RefreshCatalog()
        {
            string url = catalogUrl.Text?.Trim() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeFile)) { Status("The catalog is an https:// address of a JSON file."); return; }
            settings.CatalogUrl = url; settings.Save(home);
            Status("Reading the catalog…");
            try
            {
                catalog = u.Scheme == Uri.UriSchemeFile ? ModLibrary.ParseCatalog(File.ReadAllText(u.LocalPath)) : await library.FetchCatalog(url);
                catalogList.ItemsSource = catalog;
                Status($"The catalog lists {catalog.Count} mod{(catalog.Count == 1 ? "" : "s")}.");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is InvalidDataException || ex is System.Text.Json.JsonException || ex is IOException || ex is TaskCanceledException)
            {
                Status("Could not read the catalog: " + ex.Message);
            }
        }

        async Task DownloadSelected()
        {
            if (catalogList.SelectedItem is not CatalogEntry c) { Status("Pick a mod in the catalog first."); return; }
            Status($"Downloading {c.Name}…");
            try
            {
                var progress = new Progress<double>(p => Status($"Downloading {c.Name}: {p:P0}"));
                var it = await library.Download(c, progress);
                Status($"{it.Mod} is in the library; tick it on the Mods tab to apply it.");
                ShowLibrary();
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is InvalidDataException || ex is IOException || ex is TaskCanceledException)
            {
                Status($"{c.Name} was not downloaded: {ex.Message}");
            }
        }

        // ---- U9GL

        void ShowU9gl()
        {
            if (game == null) { u9glText.Text = "Add a game folder first."; return; }
            string have = ModLibrary.InstalledU9gl(game.Root);
            u9glText.Text = (have == null ? "U9GL is not in this game folder." : $"U9GL {have} is in the game folder; " +
                             (ModLibrary.U9glLoaderInstalled(game.Root) ? "its dsound.dll is installed." : "not installed yet (open U9GL and choose Install).")) +
                            (u9glLatest != null ? $"\nLatest release: {u9glLatest.Version} ({u9glLatest.ZipName})." : "");
        }

        async Task CheckU9gl()
        {
            try { u9glLatest = await library.LatestU9gl(u9glSmall.IsChecked == true); ShowU9gl(); Status($"U9GL's latest release is {u9glLatest.Version}."); }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is InvalidDataException || ex is TaskCanceledException) { Status("Could not reach U9GL's releases: " + ex.Message); }
        }

        async Task DownloadU9gl()
        {
            if (game == null) { Status("Add a game folder first."); return; }
            try
            {
                var rel = await library.LatestU9gl(u9glSmall.IsChecked == true);
                if (!await Ask($"Download U9GL {rel.Version} ({rel.ZipName}) from GitHub into {Path.Combine(game.Root, "u9gl")} and open its launcher?")) return;
                var progress = new Progress<double>(p => Status($"Downloading U9GL: {p:P0}"));
                string exe = await library.DownloadU9gl(rel, game.Root, progress);
                u9glLatest = rel;
                ShowU9gl(); ShowGameState();
                Status($"U9GL {rel.Version} unpacked; its launcher opens now (Install there puts dsound.dll in place).");
                OpenU9gl();
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException || ex is InvalidDataException || ex is IOException || ex is TaskCanceledException || ex is UnauthorizedAccessException)
            {
                Status("U9GL was not downloaded: " + ex.Message);
            }
        }

        void OpenU9gl()
        {
            if (game == null) return;
            string exe = Path.Combine(game.Root, "u9gl", "U9GL.exe");
            if (!File.Exists(exe)) { Status("U9GL is not in the game folder: download it first."); return; }
            try { Process.Start(new ProcessStartInfo { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception) { Status("Could not open U9GL: " + ex.Message); }
        }

        // ---- small things

        void Status(string text) => Dispatcher.UIThread.Post(() => status.Text = text);

        async Task<bool> Ask(string question)
        {
            var dlg = new Window { Title = "U9 Mod Manager", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            bool answer = false;
            var yes = new Button { Content = "Yes", IsDefault = true };
            var no = new Button { Content = "No", IsCancel = true };
            yes.Click += (s, e) => { answer = true; dlg.Close(); };
            no.Click += (s, e) => dlg.Close();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(yes); row.Children.Add(no);
            var panel = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(row);
            dlg.Content = panel;
            await dlg.ShowDialog(this);
            return answer;
        }

        void SaveShot(string path)
        {
            var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
            using var bmp = new RenderTargetBitmap(size);
            bmp.Render(this);
            bmp.Save(path);
        }
    }
}
