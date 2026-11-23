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
        FlxArchive bitmaps;
        TypeNames typeNames;
        MapLinks mapLinks;
        ObjectMap currentNonfixed;
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
            View.ObjectPicked += o => objectStatus = o != null ? "selected " + o : null;
            View.CameraChanged += () => Compass.RenderTransform = new Avalonia.Media.RotateTransform(-View.Camera.Yaw);
            View.FrameDrawn += OnFrameDrawn;
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => View.CameraKeyUp(e), RoutingStrategies.Tunnel);
            Opened += (s, e) => OpenAtStart();
            Closing += (s, e) => settings.Save();
            Closed += (s, e) => bitmaps?.Dispose();
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
            var objects = LoadObjects(m.Number, space);
            View.SetObjects(objects);
            ShowLinks(m, objects);
            TerrainTextures textures = null;
            if (bitmaps != null)
            {
                textures = TerrainTextures.Build(raster, i => i < bitmaps.Count ? bitmaps.Read(i) : null);
                View.SetTextures(textures);
            }
            View.FrameMap();
            if (lookAfterLoad is { } look) { View.LookAt(look.X, look.Row, 30); lookAfterLoad = null; }
            MapInfo.Text = $"{terrain.Width}x{terrain.Height} points, {terrain.Chunks.Count} chunks, water at height {terrain.WaterHeight}, " +
                           $"waves {terrain.WaveAmplitude}, flags 0x{terrain.Flags:X}, " +
                           $"{textures?.Layers.Count ?? 0} texture frames, " +
                           $"{View.Objects?.All.Count(o => o.Kind == ObjectFileKind.Fixed) ?? 0} fixed and {View.Objects?.All.Count(o => o.Kind == ObjectFileKind.Nonfixed) ?? 0} nonfixed objects" + (textures?.Missing.Count > 0 ? $" ({textures.Missing.Count} missing)" : "");
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

        SceneObjects LoadObjects(int number, MapSpace space)
        {
            currentNonfixed = LoadObjectFile("runtime", "nonfixed", number, ObjectFileKind.Nonfixed);
            return SceneObjects.Build(LoadObjectFile("static", "fixed", number, ObjectFileKind.Fixed), currentNonfixed, space, typeNames);
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
            if (e.Source is TextBox || e.Source is ComboBox) return;
            if (e.Key == Key.Tab) { TopDownItem.IsChecked = !TopDownItem.IsChecked; OnToggleTopDown(null, null); e.Handled = true; return; }
            View.CameraKey(e);
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
