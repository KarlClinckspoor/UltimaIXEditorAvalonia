using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Interactivity;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>Prefabs: copying objects with their ground, pasting them on any map, and the library of saved ones.</summary>
    public partial class MainWindow
    {
        Prefab prefabClip;
        int prefabTurns;
        string prefabDeletePending;

        static string PrefabFolder => Path.Combine(Settings.Folder, "prefabs");

        sealed class PrefabItem
        {
            public string Path, Name, Info;
            public override string ToString() => $"{Name}  ({Info})";
        }

        IReadOnlyList<SceneObject> SelectionForCopy() =>
            View.Selection.Count > 0 ? View.Selection : selected != null ? new[] { selected } : Array.Empty<SceneObject>();

        void OnPrefabCopy(object sender, RoutedEventArgs e)
        {
            if (editor == null) return;
            var list = SelectionForCopy();
            if (list.Count == 0) { StatusText.Text = "Select the objects to copy first (Ctrl+click adds one, Ctrl+drag boxes them)."; return; }
            try
            {
                prefabClip = Prefab.Copy(editor, list, (int)(PrefabMargin.Value ?? 3), PrefabWithGround.IsChecked == true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException)
            {
                StatusText.Text = "Not copied: " + ex.Message;
                return;
            }
            ShowPrefabInfo();
            StatusText.Text = $"Copied {prefabClip.Describe()}. Ctrl+V pastes it, here or on another map.";
        }

        void ShowPrefabInfo()
        {
            PrefabInfo.Text = prefabClip == null ? "Nothing copied yet." : $"Copied: {prefabClip.Name}. {prefabClip.Describe()}.";
            if (prefabClip != null && string.IsNullOrWhiteSpace(PrefabName.Text)) PrefabName.Text = prefabClip.Name;
        }

        void OnPrefabPaste(object sender, RoutedEventArgs e)
        {
            if (editor == null) return;
            if (prefabClip == null) { StatusText.Text = "Nothing to paste: copy objects with Ctrl+C first, or pick a prefab in the library."; return; }
            if (View.Brush != null) { BrushToggle.IsChecked = false; OnBrushToggle(null, null); }
            if (View.Placing) View.Placing = false;
            prefabTurns = 0;
            ShowPasteFootprint();
            View.Focus();
        }

        void ShowPasteFootprint()
        {
            var p = prefabClip;
            bool swap = (prefabTurns & 1) != 0;
            View.Paste = (swap ? p.Height : p.Width, swap ? p.Width : p.Height, p.HasGround ? p.Margin : 0, prefabTurns & 3);
            StatusText.Text = $"Paste {p.Name}: click where it goes (its outline follows the mouse; the orange side is its north as copied). " +
                              "[ and ] turn it a quarter, Esc stops.";
        }

        void StopPasting()
        {
            View.Paste = null;
            StatusText.Text = "Pasting stopped.";
        }

        void TurnPaste(int quarters)
        {
            prefabTurns = ((prefabTurns + quarters) % 4 + 4) % 4;
            ShowPasteFootprint();
        }

        PasteOptions PasteOptionsFromPanel() => new PasteOptions
        {
            Ground = (PrefabGround)Math.Clamp(PrefabGroundMode.SelectedIndex, 0, 2),
            FitHeights = PrefabFit.IsChecked == true,
            BlendEdge = PrefabBlend.IsChecked == true,
            ObjectsFollowGround = PrefabFollow.IsChecked == true,
            NewLinks = PrefabNewLinks.IsChecked == true,
            CopyScripts = PrefabCopyScripts.IsChecked == true,
            ClearArea = PrefabClear.IsChecked == true,
        };

        void OnPasteAt(int x, int row)
        {
            if (editor == null || prefabClip == null) return;
            var p = prefabClip.Turned(prefabTurns);
            PasteResult r = null;
            Edit(() => r = editor.PastePrefab(p, x, row, PasteOptionsFromPanel()));
            if (r == null) return;
            editors[editor.MapNumber] = editor;
            // textures the map did not use yet need their frames in the texture array
            if (r.GroundPasted && textures != null && bitmaps != null &&
                r.Textures.Any(t => t.Texture * TerrainTextures.Frames + t.Frame < textures.Lookup.Length &&
                                    textures.Lookup[t.Texture * TerrainTextures.Frames + t.Frame] == 0 && !textures.Missing.Contains(t)))
            {
                textures = TerrainTextures.Build(editor.Raster, i => i < bitmaps.Count ? bitmaps.Read(i) : null);
                View.SetTextures(textures);
                FillPalette();
            }
            if (r.Objects.Count > 0) View.SetSelection(r.Objects, r.Objects[^1]);
            var parts = new List<string> { $"Pasted {r.Objects.Count} objects at ({x}, {row})" };
            if (r.GroundPasted) parts.Add(r.HeightOffset != 0 ? $"the ground {(r.HeightOffset > 0 ? "raised" : "lowered")} {Math.Abs(r.HeightOffset)} to fit" : "with its ground");
            if (r.LinkShift != 0) parts.Add($"links moved up by {r.LinkShift}");
            if (r.Slots.Count > 0) parts.Add($"{r.Slots.Count} script{(r.Slots.Count == 1 ? "" : "s")} copied to slot{(r.Slots.Count == 1 ? "" : "s")} {string.Join(", ", r.Slots.Values)}");
            if (r.Cleared > 0) parts.Add($"{r.Cleared} objects there deleted");
            if (r.Skipped > 0) parts.Add($"{r.Skipped} left out (outside the map, or of a file this map has not)");
            parts.AddRange(r.Notes);
            StatusText.Text = string.Join("; ", parts) + ". Ctrl+Z takes it back; click again for another, Esc stops.";
            UpdateTitle();
        }

        /// <summary>The --tab, --select, --prefab, --prefab-file, --paste and --paste-preview start options (for scripted checks).</summary>
        void PrefabsAtStart()
        {
            if (Program.TabAtStart != null)
                foreach (var item in SideTabs.Items.OfType<Avalonia.Controls.TabItem>())
                    if (string.Equals(item.Header as string, Program.TabAtStart, StringComparison.OrdinalIgnoreCase)) SideTabs.SelectedItem = item;
            if (editor == null) return;
            if (!float.IsNaN(Program.SelectX))
            {
                float sx = Program.SelectX * 128, sy = Program.SelectRow * 128;
                var near = editor.Objects.All.Where(o => !o.Source.Removed)
                    .OrderBy(o => (o.Source.MapX - sx) * (o.Source.MapX - sx) + (o.Source.MapY - sy) * (o.Source.MapY - sy)).FirstOrDefault();
                if (near != null) View.SelectObject(near);
            }
            if (Program.PrefabFileAtStart != null)
            {
                try { prefabClip = Prefab.Load(Program.PrefabFileAtStart); ShowPrefabInfo(); }
                catch (Exception ex) { StatusText.Text = "Could not read the prefab: " + ex.Message; return; }
            }
            else if (Program.PrefabAtStart is { Length: 4 } box)
            {
                var inside = editor.Objects.All.Where(o => !o.Source.Removed &&
                    o.Source.MapX >= box[0] * 128 && o.Source.MapX <= box[2] * 128 && o.Source.MapY >= box[1] * 128 && o.Source.MapY <= box[3] * 128).ToList();
                View.SetSelection(inside, inside.LastOrDefault());
                OnPrefabCopy(null, null);
            }
            if (prefabClip == null) return;
            if (Program.PasteAtStart is { Length: >= 2 } at)
            {
                prefabTurns = at.Length > 2 ? ((at[2] % 4) + 4) % 4 : 0;
                OnPasteAt(at[0], at[1]);
            }
            if (Program.PastePreviewAtStart is { Length: >= 2 } pv)
            {
                OnPrefabPaste(null, null);
                if (pv.Length > 2) TurnPaste(pv[2]);
                View.ShowPasteAt(pv[0], pv[1]);
            }
        }

        // ---- library

        void RefreshPrefabList()
        {
            var items = new List<PrefabItem>();
            if (Directory.Exists(PrefabFolder))
                foreach (var f in Directory.EnumerateFiles(PrefabFolder, "*" + Prefab.Extension).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var p = Prefab.Load(f);
                        items.Add(new PrefabItem { Path = f, Name = string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(f) : p.Name, Info = $"{p.Objects.Count} objects, {p.Width} x {p.Height}, map {p.SourceMap}" });
                    }
                    catch (Exception ex) { items.Add(new PrefabItem { Path = f, Name = Path.GetFileNameWithoutExtension(f), Info = "unreadable: " + ex.Message }); }
                }
            PrefabList.ItemsSource = items;
        }

        void OnPrefabSave(object sender, RoutedEventArgs e)
        {
            if (prefabClip == null) { StatusText.Text = "Nothing copied to save: select objects and press Ctrl+C first."; return; }
            string name = string.IsNullOrWhiteSpace(PrefabName.Text) ? prefabClip.Name : PrefabName.Text.Trim();
            prefabClip.Name = name;
            Directory.CreateDirectory(PrefabFolder);
            string stem = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
            if (stem.Length == 0) stem = "prefab";
            if (stem.Length > 60) stem = stem[..60];
            string path = Path.Combine(PrefabFolder, stem + Prefab.Extension);
            for (int k = 2; File.Exists(path); k++) path = Path.Combine(PrefabFolder, $"{stem} {k}{Prefab.Extension}");
            try { prefabClip.Save(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { StatusText.Text = "Not saved: " + ex.Message; return; }
            RefreshPrefabList();
            ShowPrefabInfo();
            StatusText.Text = $"Saved {name} to {path}.";
        }

        void OnPrefabUse(object sender, RoutedEventArgs e)
        {
            if (PrefabList.SelectedItem is not PrefabItem it) { StatusText.Text = "Pick a prefab in the library first."; return; }
            try { prefabClip = Prefab.Load(it.Path); }
            catch (Exception ex) { StatusText.Text = "Could not read it: " + ex.Message; return; }
            ShowPrefabInfo();
            OnPrefabPaste(null, null);
        }

        void OnPrefabDelete(object sender, RoutedEventArgs e)
        {
            if (PrefabList.SelectedItem is not PrefabItem it) { StatusText.Text = "Pick a prefab in the library first."; return; }
            if (prefabDeletePending != it.Path)
            {
                prefabDeletePending = it.Path;
                StatusText.Text = $"Press Delete again to delete {it.Name} ({Path.GetFileName(it.Path)}) from the library.";
                return;
            }
            prefabDeletePending = null;
            try { File.Delete(it.Path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { StatusText.Text = "Not deleted: " + ex.Message; return; }
            RefreshPrefabList();
            StatusText.Text = $"Deleted {it.Name} from the library.";
        }

        void OnPrefabFolder(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(PrefabFolder);
            try { Process.Start(new ProcessStartInfo { FileName = PrefabFolder, UseShellExecute = true }); }
            catch (Exception ex) { StatusText.Text = $"The library is {PrefabFolder} ({ex.Message})."; }
        }
    }
}
