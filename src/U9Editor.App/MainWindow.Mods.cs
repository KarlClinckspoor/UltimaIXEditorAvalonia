using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using U9.Mods;

namespace U9Editor.App
{
    /// <summary>File > Export changes as mod: a .u9mod of what the editor saved into the game folder (docs/mod-format.md).</summary>
    public partial class MainWindow
    {
        async void OnExportMod(object sender, RoutedEventArgs e)
        {
            if (game == null) return;
            if (editors.Values.Any(x => x.HasChanges))
            {
                StatusText.Text = "Save first (Ctrl+S): a mod is made from the saved files against their .orig copies.";
                return;
            }
            if (DialogueUnsaved)
            {
                StatusText.Text = "Save the dialogue window's changes first: the mod is made from saved files.";
                return;
            }
            if (CombatUnsaved)
            {
                StatusText.Text = "Save the combat balance window's changes first: the mod is made from saved files.";
                return;
            }
            var changed = ModMaker.ChangedByEditor(game.Root);
            bool exe = GamePaths.FindExe(game.Root) is string x && (File.Exists(x + ".orig") || File.Exists(ExePatchFile.PathIn(game.Root)));
            if (changed.Count == 0 && !exe)
            {
                StatusText.Text = "Nothing to export: no file of the game folder has a .orig copy (the editor keeps one the first time it saves a file), and there are no saved combat changes to u9.exe.";
                return;
            }
            var meta = await AskModDetails(changed.Count, exe);
            if (meta == null) return;
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the mod", DefaultExtension = "u9mod", SuggestedFileName = meta.Id + ModPackage.Extension,
                FileTypeChoices = new[] { new FilePickerFileType("Ultima IX mod") { Patterns = new[] { "*" + ModPackage.Extension } } },
            });
            string path = f?.TryGetLocalPath();
            if (path == null) return;
            StatusText.Text = "Making the mod…";
            string root = game.Root;
            try
            {
                var mod = await Task.Run(() => ModMaker.FromEditorCopies(root, meta));
                if (mod.Files.Count == 0 && !mod.ChangesExe) { StatusText.Text = "Nothing to export: every changed file is back as it was."; return; }
                await Task.Run(() => mod.Write(path));
                StatusText.Text = $"Saved {mod}: {mod.Summary()}, {new FileInfo(path).Length / 1024.0:0.#} KB, at {path}. The U9 Mod Manager applies it.";
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                StatusText.Text = "No mod made: " + ex.Message;
            }
        }

        /// <summary>The mod's id, name, version, author and description; null when cancelled.</summary>
        async Task<ModPackage> AskModDetails(int files, bool exe)
        {
            var dlg = new Window { Title = "Export changes as mod", Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
            TextBox Box(string text, string placeholder) => new TextBox { Text = text, PlaceholderText = placeholder, FontSize = 12 };
            var name = Box("", "what players see, e.g. Moonglow garden");
            var id = Box("", "lower case, e.g. karl.moonglow-garden");
            var version = Box("1.0.0", "");
            var author = Box(Environment.UserName, "");
            var desc = new TextBox { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Height = 80, FontSize = 12, PlaceholderText = "what it changes" };
            name.TextChanged += (s, e) =>
            {
                if (id.Tag as string == "typed") return;
                var slug = new string((name.Text ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
                while (slug.Contains("--")) slug = slug.Replace("--", "-");
                id.Text = slug.Length > 0 ? slug[..Math.Min(slug.Length, 64)] : "";
            };
            id.KeyDown += (s, e) => id.Tag = "typed";
            var problem = new TextBlock { Foreground = Avalonia.Media.Brushes.DarkRed, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
            ModPackage result = null;
            var ok = new Button { Content = "Make the mod", IsDefault = true };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            ok.Click += (s, e) =>
            {
                var m = new ModPackage
                {
                    Id = (id.Text ?? "").Trim(), Name = (name.Text ?? "").Trim(), Version = (version.Text ?? "").Trim(), Description = desc.Text ?? "",
                    MadeWith = "U9 Editor " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? ""),
                };
                if (!string.IsNullOrWhiteSpace(author.Text)) m.Authors.Add(author.Text.Trim());
                if (m.Name.Length == 0) { problem.Text = "Give the mod a name."; return; }
                if (!ModPackage.IsValidId(m.Id)) { problem.Text = "The id is 3 to 64 lower-case letters, digits, '.', '_' or '-'."; return; }
                result = m;
                dlg.Close();
            };
            cancel.Click += (s, e) => dlg.Close();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
            void Row(int r, string label, Control c)
            {
                var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4), FontSize = 12 };
                Grid.SetRow(t, r); Grid.SetRow(c, r); Grid.SetColumn(c, 1);
                c.Margin = new Thickness(0, 3);
                grid.Children.Add(t); grid.Children.Add(c);
            }
            Row(0, "Name", name); Row(1, "Id", id); Row(2, "Version", version); Row(3, "Author", author); Row(4, "Description", desc);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12,
                Text = $"The mod holds the differences of {files} file{(files == 1 ? "" : "s")} against their .orig copies (the originals the editor kept), " +
                       (exe ? "and the changes to u9.exe's tables as patches (which the mod manager writes into a copy, u9-mod.exe), " : "") + "never the files themselves.",
            });
            panel.Children.Add(grid);
            panel.Children.Add(problem);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            await dlg.ShowDialog(this);
            return result;
        }
    }
}
