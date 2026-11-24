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
        ObjectTemplates templates;
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

        sealed class TypeItem
        {
            public int Type;
            public string Name;
            public int Here;
            public override string ToString() => $"{Type,5}  {Name ?? "(no name)"}" + (Here > 0 ? $"  ({Here} here)" : "");
        }
        List<TypeItem> allTypes = new List<TypeItem>();
        TriggerScripts triggerScripts;

        sealed class LinkItem
        {
            public MapLink Link;
            public string Text;
            public override string ToString() => Text;
        }
        string objectStatus;
        List<MapEntry> maps = new List<MapEntry>();
        TerrainMap terrain;
        TerrainTextures textures;
        MapEntry current;
        bool screenshotTaken;

        public MainWindow()
        {
            InitializeComponent();
            if (Program.Width > 0 && Program.Height > 0) { Width = Program.Width; Height = Program.Height; }
            NorthItem.IsChecked = settings.NorthIsRowZero;
            // the hover readout has its own place, so messages about edits stay until the next one
            View.Hovered += h => HoverText.Text = objectStatus ?? Describe(h, "");
            View.Picked += h => HoverText.Text = objectStatus ?? Describe(h, "selected ");
            View.ObjectHovered += o => objectStatus = o?.ToString();
            View.ObjectPicked += o => { objectStatus = o != null ? "selected " + o : null; Select(o); };
            View.ObjectDragging += (o, x, y, z) => StatusText.Text = $"moving {o.Name ?? "type " + o.Source.Type} to ({x / 128.0:F2}, {y / 128.0:F2}) z {z}   (Esc puts it back)";
            View.ObjectDragged += (o, x, y, z) => Edit(() => { editor.Move(o, x, y, z); StatusText.Text = Moved(o); });
            View.FollowTerrain = true;
            View.PlaceAt += OnPlaceAt;
            View.StrokeEnded += OnStrokeEnded;
            View.StrokeRefused += m => StatusText.Text = m;
            View.BrushSample += OnBrushSample;
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
            templates = new ObjectTemplates(g.Root);
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
            triggerScripts = trigPath != null ? TriggerScripts.Load(trigPath) : null;
            // the editors' own copies come first, so links see unsaved edits in other maps
            mapLinks = triggerScripts != null ? new MapLinks(triggerScripts, n => editors.TryGetValue(n, out var ed) ? ed.Nonfixed : LoadObjectFile("runtime", "nonfixed", n, ObjectFileKind.Nonfixed)) : null;
            allTypes = new List<TypeItem>();
            if (typeTable != null)
                for (int t = 0; t < typeTable.Count; t++)
                    if (t != TypeTable.PlaceholderType && (typeNames?[t] != null || typeTable[t].DefaultModel > 0))
                        allTypes.Add(new TypeItem { Type = t, Name = typeNames?[t] });
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
                // a map with unsaved terrain edits keeps them while other maps are looked at
                terrain = editors.TryGetValue(m.Number, out var keptEditor) && keptEditor.Terrain != null ? keptEditor.Terrain : TerrainMap.Load(m.TerrainPath);
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
            textures = null;
            if (bitmaps != null)
            {
                textures = TerrainTextures.Build(raster, i => i < bitmaps.Count ? bitmaps.Read(i) : null);
                View.SetTextures(textures);
            }
            editor.Models = modelScene;
            FillPalette();
            Select(null);
            UpdateTitle();
            RefreshTypeList();
            View.FrameMap();
            if (lookAfterLoad is { } look) { View.LookAt(look.X, look.Row, 30); lookAfterLoad = null; }
            MapInfo.Text = $"{terrain.Width}x{terrain.Height} points, {terrain.Chunks.Count} chunks, water at height {terrain.WaterHeight}, " +
                           $"waves {terrain.WaveAmplitude}, flags 0x{terrain.Flags:X}, " +
                           $"{textures?.Layers.Count ?? 0} texture frames, " +
                           $"{View.Objects?.All.Count(o => o.Kind == ObjectFileKind.Fixed) ?? 0} fixed and {View.Objects?.All.Count(o => o.Kind == ObjectFileKind.Nonfixed) ?? 0} nonfixed objects, " +
                           $"{modelScene?.Batches.Count ?? 0} models ({modelScene?.Triangles ?? 0} triangles)" + (textures?.Missing.Count > 0 ? $" ({textures.Missing.Count} missing)" : "");
            StatusText.Text = "Click selects, dragging the selection moves it. Drag orbits, Shift+drag pans, wheel zooms, W/A/S/D move.";
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
            if (editor != null) { editor.Changed -= OnObjectsChanged; editor.TerrainEdited -= OnTerrainEdited; }
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
            editor.Scripts = triggerScripts;
            editor.Types = typeTable;
            editor.Templates = templates;
            editor.Changed += OnObjectsChanged;
            editor.Terrain = terrain;
            editor.TerrainPath = current?.TerrainPath;
            editor.TerrainEdited += OnTerrainEdited;
            View.Editor = editor;       // without it the view cannot start a drag
            currentNonfixed = editor.Nonfixed;
            return editor.Objects;
        }

        // ---- editing

        void OnObjectsChanged(IReadOnlyList<SceneObject> touched, bool structural)
        {
            editors[editor.MapNumber] = editor;
            View.RefreshEdited(touched, structural);
            // zones, arrivals and scripts make the link list: rebuild it when one of them changed (undo included)
            if (current != null && (structural || touched.Any(o => o.IsLinkZone || o.IsArrival || o.Source.Type == 1111)))
            {
                var keep = LinksList.SelectedItem as LinkItem;
                ShowLinks(current, View.Objects);
                if (keep != null && LinksList.ItemsSource is List<LinkItem> items)
                    LinksList.SelectedItem = items.FirstOrDefault(i => i.Link.Zone == keep.Link.Zone);
            }
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
            // a zone picked in the view picks its link in the list too
            if (o.IsLinkZone && LinksList.ItemsSource is List<LinkItem> links && links.FirstOrDefault(l => l.Link.Zone == s) is LinkItem li && LinksList.SelectedItem != li)
            {
                LinksList.SelectedItem = li;
                LinksList.ScrollIntoView(li);
            }
            ObjectTitle.Text = $"{(o.Kind == ObjectFileKind.Fixed ? "Fixed" : "Nonfixed")} #{o.Id}: {o.Name ?? "(no name)"}, type {s.Type}";
            EditX.Text = (s.MapX / 128.0).ToString("0.###", ci);
            EditRow.Text = (s.MapY / 128.0).ToString("0.###", ci);
            EditZ.Text = s.Z.ToString(ci);
            EditYaw.Text = (Math.Round(MapEditor.Yaw(s.Rotation), 1) + 0.0).ToString("0.#", ci);     // + 0.0: no "-0"
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
            Edit(() => { editor.Move(o, (int)Math.Round(x * 128), (int)Math.Round(row * 128), z, q); StatusText.Text = Moved(o); });
        }

        static string Moved(SceneObject o) =>
            $"Moved {o.Name ?? "type " + o.Source.Type} to ({o.Source.MapX / 128.0:F2}, {o.Source.MapY / 128.0:F2}) z {o.Source.Z}. Ctrl+Z takes it back.";

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
            UpdateTitle();
        }

        void OnRedo(object sender, RoutedEventArgs e)
        {
            string d = editor?.Redo();
            StatusText.Text = d != null ? $"Redid {d}." : "Nothing to redo.";
            UpdateTitle();
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
                StatusText.Text = "There are unsaved changes: Ctrl+S saves them; close again to throw them away.";
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
                var zones = new HashSet<MapObject>();
                foreach (var l in mapLinks.From(m.Number).OrderBy(l => l.DestinationMap).ThenBy(l => l.Zone.PointX))
                {
                    zones.Add(l.Zone);
                    string dest = maps.FirstOrDefault(e => e.Number == l.DestinationMap)?.Name ?? "?";
                    string where = l.Resolved ? $"({l.Arrival.PointX:F1}, {l.Arrival.PointRow:F1})" : $"link {l.DestinationLink} not found";
                    items.Add(new LinkItem
                    {
                        Link = l,
                        Text = $"({l.Zone.PointX:F1}, {l.Zone.PointRow:F1})  ->  map {l.DestinationMap} {dest}  {where}" +
                               (l.DestinationMap == m.Number ? "  [same map]" : ""),
                    });
                }
                foreach (var o in objects.All) o.IsLinkZone = o.Kind == ObjectFileKind.Nonfixed && zones.Contains(o.Source);
                View.RefreshObjects();
            }
            LinksList.ItemsSource = items;
            LinksHeader.Text = $"Map links ({items.Count}, {items.Count(i => i.Link.Resolved)} resolved)";
        }

        void OnLinkSelected(object sender, SelectionChangedEventArgs e)
        {
            if (LinksList.SelectedItem is not LinkItem li) return;
            LinkMap.Text = li.Link.DestinationMap.ToString();
            LinkNumber.Text = li.Link.DestinationLink.ToString();
            LinkMarker.Text = li.Link.MarkerType.ToString();
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

        // ---- placing new objects

        void RefreshTypeList()
        {
            var here = new Dictionary<int, int>();
            if (editor != null)
                foreach (var o in editor.Objects.All)
                    if (!o.Source.Removed) here[o.Source.Type] = here.TryGetValue(o.Source.Type, out int n) ? n + 1 : 1;
            foreach (var t in allTypes) t.Here = here.TryGetValue(t.Type, out int n) ? n : 0;
            OnPlaceSearch(null, null);
        }

        void OnPlaceSearch(object sender, TextChangedEventArgs e)
        {
            string q = PlaceSearch.Text?.Trim() ?? "";
            IEnumerable<TypeItem> list = allTypes;
            if (q.Length > 0)
                list = int.TryParse(q, out int num)
                    ? allTypes.Where(t => t.Type.ToString().StartsWith(q, StringComparison.Ordinal))
                    : allTypes.Where(t => t.Name != null && t.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            // what the map already has first: those place with the flags the game gave them
            PlaceList.ItemsSource = list.OrderByDescending(t => t.Here > 0).ThenBy(t => t.Type).Take(2000).ToList();
        }

        void OnPlace(object sender, RoutedEventArgs e)
        {
            if (editor == null) return;
            if (PlaceList.SelectedItem is not TypeItem t) { StatusText.Text = "Pick a type in the list first."; return; }
            View.Placing = true;
            StatusText.Text = $"Click the ground to place {t.Name ?? "type " + t.Type}. Esc cancels.";
            View.Focus();
        }

        void OnPlaceAt(TerrainHit hit)
        {
            if (editor == null || PlaceList.SelectedItem is not TypeItem t) return;
            var kind = PlaceKind.SelectedIndex switch { 1 => ObjectFileKind.Fixed, 2 => ObjectFileKind.Nonfixed, _ => editor.KindFor(t.Type) };
            int mx = (int)Math.Round(hit.World.X * 128), my = (int)Math.Round(View.Space.Row(hit.World.Z) * 128);
            SceneObject placed = null;
            Edit(() => placed = editor.Place(kind, t.Type, mx, my, editor.GroundZ(mx, my)));
            if (placed == null) return;
            View.SelectObject(placed);
            RefreshTypeList();
            StatusText.Text = $"Placed {t.Name ?? "type " + t.Type} ({(kind == ObjectFileKind.Fixed ? "fixed" : "nonfixed")}) at ({mx / 128.0:F2}, {my / 128.0:F2}). Ctrl+Z takes it back.";
        }

        // ---- editing map links

        bool LinkFields(out int map, out int link, out int marker)
        {
            link = marker = 0;
            bool ok = int.TryParse(LinkMap.Text, out map) & int.TryParse(LinkNumber.Text, out link) & int.TryParse(LinkMarker.Text, out marker);
            if (!ok || map < 0 || map > 255 || link < 0 || link > 0xFFFF || marker < 0 || marker > 0xFFFF)
            {
                StatusText.Text = "Not done: To map is 0-255, Link and Marker 0-65535.";
                return false;
            }
            return true;
        }

        bool SelectedPoint(out int mx, out int my)
        {
            mx = my = 0;
            if (View.State.SelectedX < 0) { StatusText.Text = "Click a ground point first (the yellow square marks it)."; return false; }
            mx = View.State.SelectedX * 128 + 64;
            my = View.State.SelectedRow * 128 + 64;
            return true;
        }

        void AfterLinkEdit(SceneObject select)
        {
            if (current != null) ShowLinks(current, View.Objects);
            if (select != null) View.SelectObject(select);
        }

        void OnLinkFieldsKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && e.Source is TextBox) { OnRetarget(null, null); e.Handled = true; }
        }

        void OnRetarget(object sender, RoutedEventArgs e)
        {
            if (editor == null) return;
            if (LinksList.SelectedItem is not LinkItem li) { StatusText.Text = "Select a link in the list first."; return; }
            if (!LinkFields(out int map, out int link, out int marker)) return;
            var zone = View.Objects?.Of(li.Link.Zone);
            Edit(() =>
            {
                editor.RetargetLink(li.Link, map, link, marker);
                StatusText.Text = $"The link now goes to map {map}, link {link} (a new script slot; Ctrl+Z takes it back).";
            });
            AfterLinkEdit(zone);
        }

        void OnFreeLink(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(LinkMap.Text, out int map)) { StatusText.Text = "Type the destination map first."; return; }
            var nf = editors.TryGetValue(map, out var ed) ? ed.Nonfixed : current?.Number == map ? editor?.Nonfixed : LoadObjectFile("runtime", "nonfixed", map, ObjectFileKind.Nonfixed);
            LinkNumber.Text = MapEditor.FreeLinkIn(nf).ToString();
            StatusText.Text = $"Link {LinkNumber.Text} is free in map {map}. Put an arrival with it there (New arrival at point).";
        }

        void OnNewLink(object sender, RoutedEventArgs e)
        {
            if (editor == null || mapLinks == null) return;
            if (!LinkFields(out int map, out int link, out int marker) || !SelectedPoint(out int mx, out int my)) return;
            // the zone's size and kind come from a zone already in this map (the selected link's if any), else Britannia
            var template = (LinksList.SelectedItem as LinkItem)?.Link.Zone
                           ?? mapLinks.From(current.Number).Select(l => l.Zone).FirstOrDefault()
                           ?? mapLinks.From(9).Select(l => l.Zone).FirstOrDefault(z => z.Map.CanLeaveChunk(z));
            if (template == null) { StatusText.Text = "Not done: no map change zone to copy."; return; }
            var seg = template.Map.Data.AsSpan(template.Map.DataStart + template.PropertyOffset, 16).ToArray();
            SceneObject zone = null;
            Edit(() => zone = editor.NewLink(template.Raw, seg, mx, my, editor.GroundZ(mx, my), map, link, marker));
            if (zone == null) return;
            AfterLinkEdit(zone);
            StatusText.Text = $"New map change at ({mx / 128.0:F1}, {my / 128.0:F1}) to map {map}, link {link}. It needs an arrival with link {link} there.";
        }

        void OnNewArrival(object sender, RoutedEventArgs e)
        {
            if (editor == null) return;
            if (!int.TryParse(LinkNumber.Text, out int link) || !int.TryParse(LinkMarker.Text, out int marker)) { StatusText.Text = "Type the link number first."; return; }
            if (!SelectedPoint(out int mx, out int my)) return;
            SceneObject arrival = null;
            Edit(() => arrival = editor.NewArrival(link, mx, my, editor.GroundZ(mx, my), marker));
            if (arrival == null) return;
            AfterLinkEdit(arrival);
            StatusText.Text = $"New arrival (link {link}) at ({mx / 128.0:F1}, {my / 128.0:F1}). Links to this map with link {link} land here.";
        }


        string Describe(TerrainHit h, string prefix)
        {
            if (!h.Hit || terrain == null) return "";
            var p = View.Raster[h.X, h.Row];
            int tile = h.X / 16 + h.Row / 16 * terrain.TilesX;
            return $"{prefix}point ({h.X}, {h.Row})  height {p.Height}  texture {p.Texture}  frame {p.Frame}" +
                   $"{(p.Hole ? "  hole" : "")}  turn {p.Rotation}{(p.Bit15 ? "  bit15" : "")}" +
                   $"   tile {h.X / 16},{h.Row / 16}  chunk {terrain.TileChunks[tile]}   raw 0x{p.Raw:X8}";
        }

        void OnFrameDrawn()
        {
            GlText.Text = (View.GlError != null ? "GL error: " + View.GlError + "   " : "") + $"{View.LastFrameMs:F1} ms";
            ToolTip.SetTip(GlText, View.GlInfo);
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
            // saving works from anywhere, a panel field included; other keys belong to the field while typing
            if (e.Key == Key.S && (e.KeyModifiers & KeyModifiers.Control) != 0) { OnSave(null, null); e.Handled = true; return; }
            // fields and lists keep their own keys (End, Delete, Page Up...); the edit and camera keys belong to the view
            if (e.Source is TextBox || e.Source is ComboBox || e.Source is ListBox || e.Source is ListBoxItem) return;
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
                case Key.B:
                    BrushToggle.IsChecked = BrushToggle.IsChecked != true;
                    OnBrushToggle(null, null);
                    return true;
                case Key.Escape:
                    if (View.Brush != null) { BrushToggle.IsChecked = false; OnBrushToggle(null, null); }
                    else if (View.Placing) { View.Placing = false; StatusText.Text = "Not placed."; }
                    else if (View.IsDraggingObject) { View.CancelDrag(); StatusText.Text = "Put back."; }
                    else View.SelectObject(null);
                    return true;
                case Key.Delete: OnDelete(null, null); return true;
                case Key.End: OnDropToGround(null, null); return true;
                case Key.OemOpenBrackets when View.Brush != null: BrushRadius.Value = Math.Max(BrushRadius.Minimum, BrushRadius.Value / 1.25); return true;
                case Key.OemCloseBrackets when View.Brush != null: BrushRadius.Value = Math.Min(BrushRadius.Maximum, BrushRadius.Value * 1.25); return true;
                case Key.OemOpenBrackets: OnTurnLeft(null, null); return true;
                case Key.OemCloseBrackets: OnTurnRight(null, null); return true;
                case Key.PageUp: OnRaise(null, null); return true;
                case Key.PageDown: OnLower(null, null); return true;
            }
            return false;
        }

        // ---- terrain

        void OnTerrainEdited(int x, int row, int w, int h) => View.InvalidatePoints(x, row, w, h);

        void OnBrushToggle(object sender, RoutedEventArgs e)
        {
            bool on = BrushToggle.IsChecked == true;
            if (on && editor?.Terrain == null) { BrushToggle.IsChecked = false; StatusText.Text = "No map open."; return; }
            if (on) { View.SelectObject(null); SideTabs.SelectedIndex = 2; }
            View.Brush = on ? MakeBrush() : null;
            StatusText.Text = on ? $"Terrain brush on ({ToolName()}): drag on the ground. B or Esc turns it off." : "Terrain brush off.";
        }

        string ToolName() => (BrushTool.SelectedItem as ComboBoxItem)?.Content as string ?? "Raise";

        TerrainBrush MakeBrush()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            int tool = Math.Max(0, BrushTool.SelectedIndex);
            var b = new TerrainBrush
            {
                Tool = tool switch { 0 => TerrainTool.Raise, 1 => TerrainTool.Lower, 2 => TerrainTool.Smooth, 3 => TerrainTool.Flatten, 4 => TerrainTool.Paint, _ => TerrainTool.Hole },
                CutHole = tool != 6,
                Radius = (float)BrushRadius.Value,
                RandomTurn = BrushTurn.IsChecked == true,
                Blend = BrushBlend.IsChecked == true,
                ObjectsFollow = BrushFollow.IsChecked == true,
            };
            b.Strength = b.Tool is TerrainTool.Smooth or TerrainTool.Flatten ? (float)BrushStrength.Value / 100f : (float)Math.Round(BrushStrength.Value);
            if (int.TryParse(BrushFlatten.Text, System.Globalization.NumberStyles.Integer, ci, out int f)) b.FlattenTo = Math.Clamp(f, 0, U9.Formats.Terrain.TerrainPoint.MaxHeight);
            if (int.TryParse(BrushTexture.Text, System.Globalization.NumberStyles.Integer, ci, out int t)) b.Texture = Math.Clamp(t, 0, 1023);
            if (int.TryParse(BrushFrame.Text, System.Globalization.NumberStyles.Integer, ci, out int fr)) b.Frame = Math.Clamp(fr, 0, 63);
            return b;
        }

        void OnBrushChanged(object sender, RoutedEventArgs e)
        {
            if (View == null || BrushRadius == null || BrushTurn == null || BrushBlend == null || BlendInfo == null) return;      // during InitializeComponent
            if (View.Brush != null) View.Brush = MakeBrush();
            ShowBlendInfo();
        }

        void OnBrushSlider(object sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (BrushRadiusText == null || BrushStrengthText == null) return;
            BrushRadiusText.Text = BrushRadius.Value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            BrushStrengthText.Text = Math.Round(BrushStrength.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
            OnBrushChanged(null, null);
        }

        void OnBrushFieldKey(object sender, KeyEventArgs e) => OnBrushChanged(null, null);

        void OnBrushSample(TerrainHit h)
        {
            var p = editor.Raster[h.X, h.Row];
            int round = (int)MathF.Round(editor.Raster.HeightAt(h.World.X, View.Space.Row(h.World.Z)));
            switch (BrushTool.SelectedIndex)
            {
                case 3: BrushFlatten.Text = round.ToString(); StatusText.Text = $"Flattening to height {round}."; break;
                case 4:
                    // with blending, take the ground's material (a transition quad gives the side under the click)
                    int tex = p.Texture, frame = p.Frame;
                    if (BrushBlend.IsChecked == true && editor.Transitions.IsTransition(tex))
                    {
                        float fu = h.World.X - h.X, fv = View.Space.Row(h.World.Z) - h.Row;
                        tex = editor.Transitions.CornerMaterial(p, fu < 0.5f ? 0 : 1, fv < 0.5f ? 0 : 1);
                        frame = 0;
                    }
                    BrushTexture.Text = tex.ToString(); BrushFrame.Text = frame.ToString();
                    OnBrushChanged(null, null);
                    StatusText.Text = $"Painting texture {tex}, frame {frame}.";
                    break;
                default: StatusText.Text = $"Ground at ({h.X}, {h.Row}): height {round}, texture {p.Texture} frame {p.Frame}."; break;
            }
        }

        bool fillingPalette;

        /// <summary>Thumbnails of the map's plain ground textures (first frame), most used first.</summary>
        void FillPalette()
        {
            fillingPalette = true;
            try
            {
                var items = new List<Control>();
                if (textures != null && editor?.Raster != null)
                {
                    var tr = editor.Transitions;
                    var uses = new Dictionary<int, int>();
                    foreach (uint raw in editor.Raster.Points) { int t = (int)(raw >> 22); uses[t] = uses.GetValueOrDefault(t) + 1; }
                    foreach (var t in uses.Where(kv => !tr.IsTransition(kv.Key)).OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
                    {
                        int frame = tr.PlainFrames.TryGetValue(t, out var fr) && fr.Length > 0 ? fr[0] : 0;
                        int layer = textures.Lookup[t * TerrainTextures.Frames + frame];
                        if (layer == 0) continue;
                        var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(TerrainTextures.LayerSize, TerrainTextures.LayerSize), new Vector(96, 96),
                                                                             Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
                        using (var fb = bmp.Lock())
                        {
                            var src = textures.Layers[layer - 1];
                            int row = TerrainTextures.LayerSize * 4;
                            for (int y = 0; y < TerrainTextures.LayerSize; y++)
                                System.Runtime.InteropServices.Marshal.Copy(src, y * row, fb.Address + y * fb.RowBytes, row);
                        }
                        var tip = $"texture {t} ({uses[t]} quads)";
                        var partners = tr.Sets.Values.Where(s => s.A == t || s.B == t).Select(s => s.A == t ? s.B : s.A).Distinct().OrderBy(x => x).ToList();
                        if (partners.Count > 0) tip += $", blends into {string.Join(", ", partners)}";
                        var img = new Avalonia.Controls.Image { Source = bmp, Width = 34, Height = 34, Tag = t };
                        ToolTip.SetTip(img, tip);
                        items.Add(img);
                    }
                }
                TexturePalette.ItemsSource = items;
            }
            finally { fillingPalette = false; }
        }

        void OnPaletteSelected(object sender, SelectionChangedEventArgs e)
        {
            if (fillingPalette || TexturePalette.SelectedItem is not Avalonia.Controls.Image img || img.Tag is not int t) return;
            BrushTexture.Text = t.ToString();
            BrushFrame.Text = (editor?.Transitions.PlainFrames.TryGetValue(t, out var fr) == true && fr.Length > 0 ? fr[0] : 0).ToString();
            if (BrushTool.SelectedIndex != 4) BrushTool.SelectedIndex = 4;
            OnBrushChanged(null, null);
            StatusText.Text = $"Painting texture {t}.";
        }

        void ShowBlendInfo()
        {
            if (editor?.Raster == null || BrushTool.SelectedIndex != 4 || BrushBlend.IsChecked != true) { BlendInfo.Text = ""; return; }
            int t = MakeBrush().Texture;
            var tr = editor.Transitions;
            if (tr.IsTransition(t)) { BlendInfo.Text = $"Texture {t} is a transition set ({tr.Sets[t]}): paint one of the plain textures it blends."; return; }
            var partners = tr.Sets.Values.Where(s => s.A == t || s.B == t).Select(s => s.A == t ? s.B : s.A).Distinct().OrderBy(x => x).ToList();
            BlendInfo.Text = partners.Count == 0 ? $"This map has no transitions for texture {t}: its edges stay hard." :
                $"Texture {t} blends into {string.Join(", ", partners)} on this map; edges against others stay hard.";
        }

        void OnBrushPickTexture(object sender, RoutedEventArgs e)
        {
            if (View.State.SelectedX < 0 || editor?.Raster == null) { StatusText.Text = "Click a ground point first (with the brush off)."; return; }
            var p = editor.Raster[View.State.SelectedX, View.State.SelectedRow];
            BrushTexture.Text = p.Texture.ToString();
            BrushFrame.Text = p.Frame.ToString();
            BrushTool.SelectedIndex = 4;
            OnBrushChanged(null, null);
            StatusText.Text = $"Painting texture {p.Texture}, frame {p.Frame}.";
        }

        void OnStrokeEnded()
        {
            if (editor == null) return;
            editors[editor.MapNumber] = editor;
            // a texture the map did not use yet needs its frame in the texture array
            var b = View.Brush;
            if (b != null && b.Tool == TerrainTool.Paint && textures != null && bitmaps != null &&
                textures.Lookup[b.Texture * TerrainTextures.Frames + b.Frame] == 0 && !textures.Missing.Contains((b.Texture, b.Frame)))
            {
                textures = TerrainTextures.Build(editor.Raster, i => i < bitmaps.Count ? bitmaps.Read(i) : null);
                View.SetTextures(textures);
                FillPalette();
            }
            StatusText.Text = editor.UndoDescription != null ? $"Terrain: {editor.UndoDescription} (Ctrl+Z takes it back)." : "Nothing changed.";
            UpdateTitle();
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
