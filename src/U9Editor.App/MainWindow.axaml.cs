using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using U9.Formats.Flx;
using U9.Formats.Models;
using U9.Formats.Objects;
using U9.Formats.Terrain;
using U9.Formats.Triggers;
using U9Editor.Core;
using U9Editor.Render;

namespace U9Editor.App
{
    public partial class MainWindow : Window
    {
        readonly Settings settings = Settings.Load();
        GameFolder game;
        FlxArchive bitmaps, appearances;
        TypeTable typeTable;
        ModelScene modelScene;
        TypeNames typeNames;
        MapLinks mapLinks;
        ObjectMap currentNonfixed;
        /// <summary>Editing sessions of the maps opened, kept while they have changes or undo steps.</summary>
        readonly Dictionary<int, MapEditor> editors = new Dictionary<int, MapEditor>();
        MapEditor editor;
        SceneObject selected;
        bool closeAnyway;
        (double X, double Row)? lookAfterLoad;

        sealed class LinkItem
        {
            public MapLink Link;
            public string Text;
            public override string ToString() => Text;
        }
        string objectStatus;
        List<MapEntry> maps = new List<MapEntry>();
        TerrainMap terrain;
        MapEntry current;
        bool screenshotTaken;

        public MainWindow()
        {
            InitializeComponent();
            if (Program.Width > 0 && Program.Height > 0) { Width = Program.Width; Height = Program.Height; }
            NorthItem.IsChecked = settings.NorthIsRowZero;
            View.Hovered += h => StatusText.Text = objectStatus ?? Describe(h, "");
            View.Picked += h => StatusText.Text = objectStatus ?? Describe(h, "selected ");
            View.ObjectHovered += o => objectStatus = o?.ToString();
            View.ObjectPicked += o => { objectStatus = o != null ? "selected " + o : null; Select(o); };
            View.ObjectDragging += (o, x, y, z) => StatusText.Text = $"moving {o.Name ?? "type " + o.Source.Type} to ({x / 128.0:F2}, {y / 128.0:F2}) z {z}   (Esc puts it back)";
            View.ObjectDragged += (o, x, y, z) => Edit(() => editor.Move(o, x, y, z));
            View.FollowTerrain = true;
            View.CameraChanged += () => Compass.RenderTransform = new Avalonia.Media.RotateTransform(-View.Camera.Yaw);
            View.FrameDrawn += OnFrameDrawn;
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => View.CameraKeyUp(e), RoutingStrategies.Tunnel);
            Opened += (s, e) => OpenAtStart();
            Closing += OnClosing;
            Closed += (s, e) => { bitmaps?.Dispose(); appearances?.Dispose(); };
        }

        void OpenAtStart()
        {
            var g = GameFolder.TryOpen(Program.GameDataAtStart)
                    ?? GameFolder.TryOpen(settings.GameFolder)
                    ?? GameFolder.FindNear(AppContext.BaseDirectory);
            if (g == null)
            {
                StatusText.Text = "Open the Ultima IX folder (File > Open game folder…): the one holding static/ and runtime/.";
                return;
            }
            OpenGame(g, Program.GameDataAtStart != null ? Program.MapAtStart : settings.LastMap);
            if (Program.TopDownAtStart) { TopDownItem.IsChecked = true; View.SetTopDown(true); View.FrameMap(); }
            if (!float.IsNaN(Program.LookX) && View.Raster != null)
            {
                var r = View.Raster;
                View.Camera.Target = new System.Numerics.Vector3(Program.LookX, MapSpace.WorldY(r.HeightAt((int)Program.LookX, (int)Program.LookRow)), View.Space.WorldZ(Program.LookRow));
            }
            if (!float.IsNaN(Program.Distance)) View.Camera.Distance = Program.Distance;
            if (!float.IsNaN(Program.Pitch)) View.Camera.Pitch = Program.Pitch;
            if (!float.IsNaN(Program.Yaw)) View.Camera.Yaw = Program.Yaw;
            View.Redraw();
        }

        void OpenGame(GameFolder g, int mapNumber)
        {
            game = g;
            settings.GameFolder = g.Root;
            bitmaps?.Dispose();
            bitmaps = null;
            string bmpPath = Path.Combine(g.StaticFolder, "bitmap16.flx");
            if (File.Exists(bmpPath)) bitmaps = FlxArchive.Open(bmpPath);
            appearances?.Dispose();
            appearances = null;
            string sapPath = Directory.EnumerateFiles(g.StaticFolder, "sappear.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            if (sapPath != null) appearances = FlxArchive.Open(sapPath);
            string typesPath = Directory.EnumerateFiles(g.StaticFolder, "types.dat", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            typeTable = typesPath != null ? TypeTable.Load(typesPath) : null;
            string namesPath = Directory.EnumerateFiles(g.StaticFolder, "typename.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            typeNames = namesPath != null ? TypeNames.Load(namesPath) : null;
            string trigPath = Directory.EnumerateFiles(g.StaticFolder, "triggers.flx", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
            mapLinks = trigPath != null ? new MapLinks(TriggerScripts.Load(trigPath), n => LoadObjectFile("runtime", "nonfixed", n, ObjectFileKind.Nonfixed)) : null;
            maps = g.Maps();
            MapBox.ItemsSource = maps;
            var pick = maps.FirstOrDefault(m => m.Number == mapNumber) ?? maps.FirstOrDefault(m => m.Number == 9) ?? maps.FirstOrDefault();
            MapBox.SelectedItem = pick;
            Title = $"U9 Editor — {g.Root}";
        }

        void OnMapChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MapBox.SelectedItem is MapEntry m && m != current) LoadMap(m);
        }

        void LoadMap(MapEntry m)
        {
            try
            {
                terrain = TerrainMap.Load(m.TerrainPath);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            {
                StatusText.Text = $"Could not read {m.TerrainPath}: {ex.Message}";
                return;
            }
            current = m;
            settings.LastMap = m.Number;
            var raster = new TerrainRaster(terrain);
            var space = new MapSpace(terrain.Width, terrain.Height) { NorthIsRowZero = settings.NorthIsRowZero };
            View.SetMap(raster, space, MapSpace.WorldY(0) + terrain.WaterHeight / MapSpace.HeightUnitsPerPoint);
            var objects = LoadObjects(m.Number, space, raster);
            View.SetObjects(objects);
            ShowLinks(m, objects);
            modelScene = null;
            if (appearances != null && bitmaps != null && typeTable != null)
            {
                var sap = appearances;
                var bmp = bitmaps;
                modelScene = ModelScene.Build(objects, space, typeTable, i => i < sap.Count ? sap.Read(i) : null, i => i < bmp.Count ? bmp.Read(i) : null);
                View.SetModels(modelScene);
            }
            TerrainTextures textures = null;
            if (bitmaps != null)
            {
                textures = TerrainTextures.Build(raster, i => i < bitmaps.Count ? bitmaps.Read(i) : null);
                View.SetTextures(textures);
            }
            editor.Models = modelScene;
            Select(null);
            UpdateTitle();
            View.FrameMap();
            if (lookAfterLoad is { } look) { View.LookAt(look.X, look.Row, 30); lookAfterLoad = null; }
            MapInfo.Text = $"{terrain.Width}x{terrain.Height} points, {terrain.Chunks.Count} chunks, water at height {terrain.WaterHeight}, " +
                           $"waves {terrain.WaveAmplitude}, flags 0x{terrain.Flags:X}, " +
                           $"{textures?.Layers.Count ?? 0} texture frames, " +
                           $"{View.Objects?.All.Count(o => o.Kind == ObjectFileKind.Fixed) ?? 0} fixed and {View.Objects?.All.Count(o => o.Kind == ObjectFileKind.Nonfixed) ?? 0} nonfixed objects, " +
                           $"{modelScene?.Batches.Count ?? 0} models ({modelScene?.Triangles ?? 0} triangles)" + (textures?.Missing.Count > 0 ? $" ({textures.Missing.Count} missing)" : "");
            StatusText.Text = "Left drag orbits, middle or Shift+drag pans, wheel zooms, W/A/S/D move, Q/E turn, click selects a point.";
        }

        ObjectMap LoadObjectFile(string folder, string name, int number, ObjectFileKind kind)
        {
            string dir = Path.Combine(game.Root, folder);
            string f = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{name}.{number}", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault() : null;
            if (f == null) return null;
            try { return ObjectMap.Load(f, kind); }
            catch (InvalidDataException) { return null; }
        }

        SceneObjects LoadObjects(int number, MapSpace space, TerrainRaster raster)
        {
            if (editor != null) editor.Changed -= OnObjectsChanged;
            // a map with edits keeps them while other maps are looked at
            if (editors.TryGetValue(number, out var kept))
            {
                kept.Rebind(space, raster);
                editor = kept;
            }
            else
            {
                var fx = LoadObjectFile("static", "fixed", number, ObjectFileKind.Fixed);
                var nf = LoadObjectFile("runtime", "nonfixed", number, ObjectFileKind.Nonfixed);
                editor = new MapEditor(number, fx, nf, SceneObjects.Build(fx, nf, space, typeNames), space, raster);
            }
            editor.Changed += OnObjectsChanged;
            currentNonfixed = editor.Nonfixed;
            return editor.Objects;
        }

        // ---- editing

        void OnObjectsChanged(IReadOnlyList<SceneObject> touched, bool structural)
        {
            editors[editor.MapNumber] = editor;
            View.RefreshEdited(touched, structural);
            if (selected != null && selected.Source.Removed) { View.SelectObject(null); }
            else Select(selected);
            UpdateTitle();
        }

        /// <summary>Runs an edit, showing why if the files do not allow it.</summary>
        void Edit(Action a)
        {
            if (editor == null) return;
            try { a(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is InvalidDataException)
            {
                StatusText.Text = "Not done: " + ex.Message;
                if (selected != null) View.RefreshEdited(new[] { selected }, false);
            }
        }

        void Select(SceneObject o)
        {
            selected = o;
            bool on = o != null && editor != null;
            ObjectFields.IsEnabled = ObjectButtons.IsEnabled = on;
            if (!on)
            {
                ObjectTitle.Text = "Click an object to select it; drag it to move it.";
                EditX.Text = EditRow.Text = EditZ.Text = EditYaw.Text = "";
                ObjectDetails.Text = "";
                return;
            }
            var s = o.Source;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            ObjectTitle.Text = $"{(o.Kind == ObjectFileKind.Fixed ? "Fixed" : "Nonfixed")} #{o.Id}: {o.Name ?? "(no name)"}, type {s.Type}";
            EditX.Text = (s.MapX / 128.0).ToString("0.###", ci);
            EditRow.Text = (s.MapY / 128.0).ToString("0.###", ci);
            EditZ.Text = s.Z.ToString(ci);
            EditYaw.Text = MapEditor.Yaw(s.Rotation).ToString("0.#", ci);
            ObjectDetails.Text = $"flags 0x{s.Flags:X}" + (o.Kind == ObjectFileKind.Nonfixed ? $", link {s.Link}, model {s.Model}" : "") +
                                 (o.Model >= 0 ? $", draws model {o.Model}" : ", no model") + (o.IsLinkZone ? ", map change zone" : "") +
                                 $"\nchunk ({s.ChunkX / 4096}, {s.ChunkY / 4096}), file offset 0x{s.FileOffset:X}" +
                                 (s.Map.CanLeaveChunk(s) ? "" : "\nits properties chain on, so it stays in its chunk");
        }

        void UpdateTitle()
        {
            string dirty = editors.Values.Any(e => e.HasChanges) ? " *" : "";
            Title = $"U9 Editor — {game?.Root}{dirty}";
            UndoItem.Header = editor?.UndoDescription != null ? $"_Undo {editor.UndoDescription}" : "_Undo";
            RedoItem.Header = editor?.RedoDescription != null ? $"_Redo {editor.RedoDescription}" : "_Redo";
        }

        void OnObjectPanelKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && e.Source is TextBox) { OnApplyObject(null, null); e.Handled = true; }
        }

        void OnApplyObject(object sender, RoutedEventArgs e)
        {
            if (selected == null || editor == null) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var st = System.Globalization.NumberStyles.Float;
            if (!double.TryParse(EditX.Text, st, ci, out double x) || !double.TryParse(EditRow.Text, st, ci, out double row) ||
                !int.TryParse(EditZ.Text, System.Globalization.NumberStyles.Integer, ci, out int z) || !float.TryParse(EditYaw.Text, st, ci, out float yaw))
            {
                StatusText.Text = "Not done: X and Row are points (like 502.25), Z a whole number, Turn degrees.";
                return;
            }
            var s = selected.Source;
            float turn = yaw - MapEditor.Yaw(s.Rotation);
            System.Numerics.Quaternion? q = Math.Abs(turn) > 0.05f ? MapEditor.Turned(s.Rotation, turn) : null;
            var o = selected;
            Edit(() => editor.Move(o, (int)Math.Round(x * 128), (int)Math.Round(row * 128), z, q));
        }

        void OnDropToGround(object sender, RoutedEventArgs e)
        {
            if (selected == null) return;
            var o = selected;
            Edit(() => editor.Move(o, o.Source.MapX, o.Source.MapY, editor.GroundZ(o.Source.MapX, o.Source.MapY)));
        }

        void OnDuplicate(object sender, RoutedEventArgs e)
        {
            if (selected == null) return;
            var o = selected;
            SceneObject copy = null;
            // one point east, so the copy can be seen and dragged
            Edit(() => copy = editor.Duplicate(o, o.Source.MapX + 128, o.Source.MapY, o.Source.Z));
            if (copy != null) { View.SelectObject(copy); StatusText.Text = "Copied: the copy is selected, one point east."; }
        }

        void OnDelete(object sender, RoutedEventArgs e)
        {
            if (selected == null) return;
            var o = selected;
            Edit(() => editor.Remove(o));
            StatusText.Text = $"Deleted {o.Name ?? "type " + o.Source.Type}. Ctrl+Z brings it back.";
        }

        void Turn(float degrees)
        {
            if (selected == null) return;
            var o = selected;
            Edit(() => editor.Rotate(o, MapEditor.Turned(o.Source.Rotation, degrees)));
        }

        void Raise(int units)
        {
            if (selected == null) return;
            var o = selected;
            Edit(() => editor.Move(o, o.Source.MapX, o.Source.MapY, o.Source.Z + units));
        }

        static bool Fine => (lastModifiers & KeyModifiers.Shift) != 0;
        static KeyModifiers lastModifiers;
        void OnTurnLeft(object sender, RoutedEventArgs e) => Turn(Fine ? 1 : 15);
        void OnTurnRight(object sender, RoutedEventArgs e) => Turn(Fine ? -1 : -15);
        void OnRaise(object sender, RoutedEventArgs e) => Raise(Fine ? 2 : 16);
        void OnLower(object sender, RoutedEventArgs e) => Raise(Fine ? -2 : -16);
        void OnToggleFollow(object sender, RoutedEventArgs e) => View.FollowTerrain = FollowItem.IsChecked;

        void OnUndo(object sender, RoutedEventArgs e)
        {
            string d = editor?.Undo();
            StatusText.Text = d != null ? $"Undid {d}." : "Nothing to undo.";
        }

        void OnRedo(object sender, RoutedEventArgs e)
        {
            string d = editor?.Redo();
            StatusText.Text = d != null ? $"Redid {d}." : "Nothing to redo.";
        }

        string ObjectPath(string folder, string name, int number)
        {
            string dir = Path.Combine(game.Root, folder);
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, $"{name}.{number}", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault() : null;
        }

        void OnSave(object sender, RoutedEventArgs e)
        {
            var written = new List<string>();
            try
            {
                foreach (var ed in editors.Values.Where(x => x.HasChanges).ToList())
                    written.AddRange(ed.Save(ObjectPath("static", "fixed", ed.MapNumber), ObjectPath("runtime", "nonfixed", ed.MapNumber)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                StatusText.Text = "Could not save: " + ex.Message;
                return;
            }
            UpdateTitle();
            StatusText.Text = written.Count == 0 ? "No changes to save." :
                $"Saved {string.Join(", ", written.Select(Path.GetFileName))} (the first save keeps the original as .orig). Start a new game to see nonfixed changes.";
        }

        void OnClosing(object sender, WindowClosingEventArgs e)
        {
            if (!closeAnyway && editors.Values.Any(x => x.HasChanges))
            {
                e.Cancel = true;
                closeAnyway = true;
                StatusText.Text = "There are unsaved object changes: Ctrl+S saves them; close again to throw them away.";
                return;
            }
            settings.Save();
        }

        void ShowLinks(MapEntry m, SceneObjects objects)
        {
            var items = new List<LinkItem>();
            if (mapLinks != null && currentNonfixed != null)
            {
                mapLinks.Remember(m.Number, currentNonfixed);
                var zones = new HashSet<int>();
                foreach (var l in mapLinks.From(m.Number).OrderBy(l => l.DestinationMap).ThenBy(l => l.Zone.PointX))
                {
                    zones.Add(l.Zone.FileOffset);
                    string dest = maps.FirstOrDefault(e => e.Number == l.DestinationMap)?.Name ?? "?";
                    string where = l.Resolved ? $"({l.Arrival.PointX:F1}, {l.Arrival.PointRow:F1})" : $"link {l.DestinationLink} not found";
                    items.Add(new LinkItem
                    {
                        Link = l,
                        Text = $"({l.Zone.PointX:F1}, {l.Zone.PointRow:F1})  ->  map {l.DestinationMap} {dest}  {where}" +
                               (l.DestinationMap == m.Number ? "  [same map]" : ""),
                    });
                }
                foreach (var o in objects.All) o.IsLinkZone = o.Kind == ObjectFileKind.Nonfixed && zones.Contains(o.Source.FileOffset);
                View.RefreshObjects();
            }
            LinksList.ItemsSource = items;
            LinksHeader.Text = $"Map links ({items.Count}, {items.Count(i => i.Link.Resolved)} resolved)";
        }

        void OnLinkSelected(object sender, SelectionChangedEventArgs e)
        {
            if (LinksList.SelectedItem is not LinkItem li) return;
            View.LookAt(li.Link.Zone.PointX, li.Link.Zone.PointRow, 30);
            View.SelectObject(View.Objects?.All.FirstOrDefault(o => o.Source.FileOffset == li.Link.Zone.FileOffset && o.Kind == ObjectFileKind.Nonfixed));
            StatusText.Text = $"trigger {li.Link.TriggerSlot}: {li.Link.Ops}   destination link {li.Link.DestinationLink}, marker type {li.Link.MarkerType}, flags 0x{li.Link.Flags:X2}";
        }

        void OnLinkGo(object sender, Avalonia.Input.TappedEventArgs e)
        {
            if (LinksList.SelectedItem is not LinkItem li || !li.Link.Resolved) return;
            var a = li.Link.Arrival;
            if (li.Link.DestinationMap != current?.Number)
            {
                var target = maps.FirstOrDefault(x => x.Number == li.Link.DestinationMap);
                if (target == null) { StatusText.Text = $"Map {li.Link.DestinationMap} has no terrain to show."; return; }
                lookAfterLoad = (a.PointX, a.PointRow);
                MapBox.SelectedItem = target;      // loads it (OnMapChanged)
            }
            else View.LookAt(a.PointX, a.PointRow, 30);
            View.State.SelectedX = (int)a.PointX; View.State.SelectedRow = (int)a.PointRow;
            View.Redraw();
            StatusText.Text = $"Arrived from map {li.Link.SourceMap} at ({a.PointX:F2}, {a.PointRow:F2}), height {a.TerrainHeight:F1}";
        }

        string Describe(TerrainHit h, string prefix)
        {
            if (!h.Hit || terrain == null) return "";
            var p = View.Raster[h.X, h.Row];
            int tile = h.X / 16 + h.Row / 16 * terrain.TilesX;
            return $"{prefix}point ({h.X}, {h.Row})  height {p.Height}  texture {p.Texture}  frame {p.Frame}" +
                   $"{(p.Hole ? "  hole" : "")}{(p.Swap ? "  swap" : "")}{(p.Mirror ? "  mirror" : "")}{(p.Bit15 ? "  bit15" : "")}" +
                   $"   tile {h.X / 16},{h.Row / 16}  chunk {terrain.TileChunks[tile]}   raw 0x{p.Raw:X8}";
        }

        void OnFrameDrawn()
        {
            GlText.Text = (View.GlError != null ? "GL error: " + View.GlError + "   " : "") + $"{View.LastFrameMs:F1} ms   {View.GlInfo}";
            if (Program.ScreenshotPath != null && !screenshotTaken && terrain != null)
            {
                screenshotTaken = true;
                // one more frame, so the map uploaded in this one is surely drawn
                View.TakePicture((w, h, px) =>
                {
                    SavePng(Program.ScreenshotPath, w, h, px);
                    Close();
                });
            }
        }

        void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            lastModifiers = e.KeyModifiers;
            if (e.Source is TextBox || e.Source is ComboBox) return;
            if (EditKey(e)) { e.Handled = true; return; }
            if (e.Key == Key.Tab) { TopDownItem.IsChecked = !TopDownItem.IsChecked; OnToggleTopDown(null, null); e.Handled = true; return; }
            View.CameraKey(e);
        }

        bool EditKey(KeyEventArgs e)
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0, shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.Z: if (shift) OnRedo(null, null); else OnUndo(null, null); return true;
                    case Key.Y: OnRedo(null, null); return true;
                    case Key.S: OnSave(null, null); return true;
                    case Key.D: OnDuplicate(null, null); return true;
                }
                return false;
            }
            switch (e.Key)
            {
                case Key.Escape:
                    if (View.IsDraggingObject) { View.CancelDrag(); StatusText.Text = "Put back."; }
                    else View.SelectObject(null);
                    return true;
                case Key.Delete: OnDelete(null, null); return true;
                case Key.End: OnDropToGround(null, null); return true;
                case Key.OemOpenBrackets: OnTurnLeft(null, null); return true;
                case Key.OemCloseBrackets: OnTurnRight(null, null); return true;
                case Key.PageUp: OnRaise(null, null); return true;
                case Key.PageDown: OnLower(null, null); return true;
            }
            return false;
        }

        // ---- menu

        async void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the Ultima IX folder (the one holding static/ and runtime/)",
                AllowMultiple = false,
            });
            string path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (path == null) return;
            var g = GameFolder.TryOpen(path);
            if (g == null) { StatusText.Text = $"{path} has no static/terrain.* files."; return; }
            current = null;
            OpenGame(g, settings.LastMap);
        }

        async void OnSavePicture(object sender, RoutedEventArgs e)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save a picture of the view",
                DefaultExtension = "png",
                SuggestedFileName = current != null ? $"map{current.Number}.png" : "view.png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG picture") { Patterns = new[] { "*.png" } } },
            });
            string path = f?.TryGetLocalPath();
            if (path != null) View.TakePicture((w, h, px) => { SavePng(path, w, h, px); StatusText.Text = "Saved " + path; });
        }

        void SavePng(string path, int w, int h, byte[] px)
        {
            if (px == null) { StatusText.Text = "Could not read the frame: " + View.GlError; return; }
            using var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using (var fb = bmp.Lock())
            {
                // the frame comes bottom row first, and its alpha is whatever the scene left
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Array.Copy(px, (h - 1 - y) * w * 4, row, 0, row.Length);
                    for (int i = 3; i < row.Length; i += 4) row[i] = 255;
                    Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, row.Length);
                }
            }
#pragma warning disable CS0618 // the UW editor saves the same way; the replacement needs encoder options we do not use
            bmp.Save(path);
#pragma warning restore CS0618
        }

        void OnExit(object sender, RoutedEventArgs e) => Close();

        void SetColour(ViewState.Colouring c) { View.State.Colour = c; View.Redraw(); }
        void OnColourTextures(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.Textures);
        void OnUvRotate(object sender, RoutedEventArgs e) { View.State.UvRotate = (View.State.UvRotate + 1) & 3; StatusText.Text = $"Texture quarter turns: {View.State.UvRotate}"; View.Redraw(); }
        void OnUvFlip(object sender, RoutedEventArgs e) { View.State.UvFlipV = FlipVItem.IsChecked; View.Redraw(); }
        void OnColourHeight(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.Height);
        void OnColourTexture(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.TextureNumber);
        void OnColourFlags(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.Flags);
        void OnToggleTerrain(object sender, RoutedEventArgs e) { View.State.ShowTerrain = TerrainItem.IsChecked; View.Redraw(); }
        void OnToggleUnseen(object sender, RoutedEventArgs e) { View.State.HideUnseen = UnseenItem.IsChecked; View.Redraw(); }
        void OnToggleModels(object sender, RoutedEventArgs e) { View.State.ShowModels = ModelsItem.IsChecked; View.Redraw(); }
        void OnToggleFixed(object sender, RoutedEventArgs e) { View.State.ShowFixed = FixedItem.IsChecked; View.Redraw(); }
        void OnToggleNonfixed(object sender, RoutedEventArgs e) { View.State.ShowNonfixed = NonfixedItem.IsChecked; View.Redraw(); }
        void OnToggleWater(object sender, RoutedEventArgs e) { View.State.ShowWater = WaterItem.IsChecked; View.Redraw(); }
        void OnToggleHoles(object sender, RoutedEventArgs e) { View.State.ShowHoles = HolesItem.IsChecked; View.Redraw(); }
        void OnToggleGrid(object sender, RoutedEventArgs e) { View.State.TileGrid = GridItem.IsChecked; View.Redraw(); }
        void OnToggleTopDown(object sender, RoutedEventArgs e) => View.SetTopDown(TopDownItem.IsChecked);
        void OnFrame(object sender, RoutedEventArgs e) => View.FrameMap();

        void OnToggleNorth(object sender, RoutedEventArgs e)
        {
            settings.NorthIsRowZero = NorthItem.IsChecked;
            if (current != null) { var m = current; current = null; LoadMap(m); }
        }
    }
}
