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
using U9.Formats.Terrain;
using U9Editor.Core;
using U9Editor.Render;

namespace U9Editor.App
{
    public partial class MainWindow : Window
    {
        readonly Settings settings = Settings.Load();
        GameFolder game;
        List<MapEntry> maps = new List<MapEntry>();
        TerrainMap terrain;
        MapEntry current;
        bool screenshotTaken;

        public MainWindow()
        {
            InitializeComponent();
            if (Program.Width > 0 && Program.Height > 0) { Width = Program.Width; Height = Program.Height; }
            NorthItem.IsChecked = settings.NorthIsRowZero;
            View.Hovered += h => StatusText.Text = Describe(h, "");
            View.Picked += h => StatusText.Text = Describe(h, "selected ");
            View.CameraChanged += () => Compass.RenderTransform = new Avalonia.Media.RotateTransform(-View.Camera.Yaw);
            View.FrameDrawn += OnFrameDrawn;
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, (s, e) => View.CameraKeyUp(e), RoutingStrategies.Tunnel);
            Opened += (s, e) => OpenAtStart();
            Closing += (s, e) => settings.Save();
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
        }

        void OpenGame(GameFolder g, int mapNumber)
        {
            game = g;
            settings.GameFolder = g.Root;
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
            View.FrameMap();
            MapInfo.Text = $"{terrain.Width}x{terrain.Height} points, {terrain.Chunks.Count} chunks, water at height {terrain.WaterHeight}, " +
                           $"waves {terrain.WaveAmplitude}, flags 0x{terrain.Flags:X}";
            StatusText.Text = "Left drag orbits, middle or Shift+drag pans, wheel zooms, W/A/S/D move, Q/E turn, click selects a point.";
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
        void OnColourHeight(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.Height);
        void OnColourTexture(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.Texture);
        void OnColourFlags(object sender, RoutedEventArgs e) => SetColour(ViewState.Colouring.Flags);
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
