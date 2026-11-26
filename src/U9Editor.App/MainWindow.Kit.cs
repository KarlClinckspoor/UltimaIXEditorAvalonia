using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using U9.Formats.Objects;
using U9Editor.Core;

namespace U9Editor.App
{
    /// <summary>
    /// The Kit tab: building with the pieces a map is made of. The original maps are sections each laid on its own
    /// turned grid, reusing a few dozen piece types, with walls strung end to end (KitCatalog). A picked piece follows the
    /// mouse as a ghost (a real object that is taken out again) and snaps where the source map puts that kind of piece
    /// next to one already here; Draw wall strings a pattern of pieces along clicked points.
    /// </summary>
    public partial class MainWindow
    {
        enum KitTool { None, Place, Wall, Floor, Room }

        /// <summary>The wall, floor and room tools collect points the same way.</summary>
        bool KitDrawing => kitTool == KitTool.Wall || kitTool == KitTool.Floor || kitTool == KitTool.Room;

        int KitTypeField(TextBox box) => int.TryParse(box.Text?.Trim(), out int t) && kit != null && kit.Pieces.ContainsKey(t) ? t : -1;

        void OnKitRoom(object sender, RoutedEventArgs e)
        {
            if (!KitReady()) return;
            if (KitPatternTypes() is not { } pattern) return;
            StopKit(null);
            kitTool = KitTool.Room;
            wallPoints.Clear();
            View.KitMode = true;
            View.Placing = true;
            ShowMode();
            string corner = KitTypeField(KitCorner) is int c and >= 0 ? $", corners {c}" : "";
            string floor = KitTypeField(KitFloor) is int f and >= 0 ? $", floor {f}" : "";
            StatusText.Text = $"Room (walls {string.Join(",", pattern)}{corner}{floor}): click the first side's two ends, then the depth; Enter builds, Esc cancels.";
            View.Focus();
        }

        sealed class KitItem
        {
            public KitPiece Piece;
            public override string ToString()
            {
                var s = Piece.Size;
                return FormattableString.Invariant($"{Piece.Label}  ×{Piece.Count}   {s.X:0}×{s.Y:0}×{s.Z:0}");
            }
        }

        const string GhostStep = "kit ghost", WallPreviewStep = "kit wall preview";

        readonly Dictionary<int, (KitCatalog Kit, ObjectMap Source)> kitCache = new();
        KitCatalog kit;
        ObjectMap kitSource;
        KitTool kitTool;
        float kitYaw;
        SceneObject kitGhost;
        readonly List<Vector2> wallPoints = new();
        int wallZ;
        bool wallPreview;
        List<KitPlacement> wallPlan;

        KitPiece KitPicked => ((KitList.SelectedItem as Control)?.Tag as KitItem)?.Piece;

        // pictures of the pieces' models, drawn on a background task (its own file handles) and kept per model
        const int ThumbSize = 48;
        readonly Dictionary<int, Avalonia.Media.Imaging.WriteableBitmap> kitThumbs = new();
        System.Threading.CancellationTokenSource kitThumbJob;

        static Avalonia.Media.Imaging.WriteableBitmap ToBitmap(byte[] rgba, int size)
        {
            var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new Avalonia.PixelSize(size, size), new Avalonia.Vector(96, 96),
                                                                 Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Unpremul);
            using var fb = bmp.Lock();
            for (int y = 0; y < size; y++) System.Runtime.InteropServices.Marshal.Copy(rgba, y * size * 4, fb.Address + y * fb.RowBytes, size * 4);
            return bmp;
        }

        /// <summary>Draws the missing pictures of the listed pieces in the background and puts each in its row.</summary>
        void StartKitThumbs(List<(KitPiece Piece, Image Picture)> rows)
        {
            kitThumbJob?.Cancel();
            var todo = rows.Where(r => !kitThumbs.ContainsKey(r.Piece.Model) && r.Piece.Model > 0).ToList();
            if (todo.Count == 0 || game == null) return;
            var job = kitThumbJob = new System.Threading.CancellationTokenSource();
            string sapPath = System.IO.Directory.EnumerateFiles(game.StaticFolder, "sappear.flx", new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive }).FirstOrDefault();
            string bmpPath = System.IO.Directory.EnumerateFiles(game.StaticFolder, "bitmap16.flx", new System.IO.EnumerationOptions { MatchCasing = System.IO.MatchCasing.CaseInsensitive }).FirstOrDefault();
            if (sapPath == null) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                using var sap = U9.Formats.Flx.FlxArchive.Open(sapPath);
                using var bmps = bmpPath != null ? U9.Formats.Flx.FlxArchive.Open(bmpPath) : null;
                var textures = new Dictionary<(int, int), U9.Formats.Bitmaps.BitmapFrame>();
                foreach (var (piece, picture) in todo)
                {
                    if (job.IsCancellationRequested) return;
                    byte[] px = null;
                    try { px = ModelThumbnail.Render(piece.Model < sap.Count ? sap.Read(piece.Model) : null, b => bmps != null && b < bmps.Count ? bmps.Read(b) : null, ThumbSize, textures); }
                    catch (Exception ex) when (ex is System.IO.IOException || ex is System.IO.InvalidDataException) { }
                    if (px == null) continue;
                    int model = piece.Model;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (!kitThumbs.TryGetValue(model, out var bmp)) kitThumbs[model] = bmp = ToBitmap(px, ThumbSize);
                        picture.Source = bmp;
                    });
                }
            });
        }
        float KitAngleStep => KitAngle.SelectedIndex switch { 1 => 15, 2 => 45, 3 => 90, _ => 0 };

        void InitKit()
        {
            View.KitHover += OnKitHover;
            View.KitClick += OnKitClick;
            View.ModeChanged += () => { if (kitTool != KitTool.None && View.Mode != ViewMode.Place) StopKit("Kit building stopped."); };
            View.DragSnap = KitDragSnap;
            // the source map's kit is read when the tab is first shown (Britannia's takes a second)
            SideTabs.SelectionChanged += (s, e) =>
            {
                if (SideTabs.SelectedItem == KitTab && KitSource.SelectedItem == null && current != null) KitSource.SelectedItem = current;
            };
        }

        /// <summary>--kit, --kit-piece, --kit-height, --kit-wall.</summary>
        void KitAtStart()
        {
            if (Program.KitAtStart < 0) return;
            SideTabs.SelectedItem = KitTab;
            KitSource.SelectedItem = maps.FirstOrDefault(m => m.Number == Program.KitAtStart);
            if (kit == null) return;
            if (Program.KitPieceAtStart >= 0)
                KitList.SelectedItem = (KitList.ItemsSource as IEnumerable<Control>)?.FirstOrDefault(c => (c.Tag as KitItem)?.Piece.Type == Program.KitPieceAtStart);
            if (Program.KitHeightAtStart >= 0) { KitHeight.Text = Program.KitHeightAtStart.ToString(CultureInfo.InvariantCulture); View.WorkHeight = Program.KitHeightAtStart; }
            if (Program.KitCornerAtStart >= 0) KitCorner.Text = Program.KitCornerAtStart.ToString(CultureInfo.InvariantCulture);
            if (Program.KitPatternAtStart != null) KitPattern.Text = Program.KitPatternAtStart;
            if (Program.KitWallAtStart != null && editor != null)
            {
                var pts = Program.KitWallAtStart.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Split(',')).Where(p => p.Length == 2)
                    .Select(p => new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture))).ToList();
                OnKitWall(null, null);
                if (kitTool != KitTool.Wall) return;
                foreach (var p in pts)
                {
                    float z = KitFixedHeight ?? editor.GroundZ(p.X, p.Y);
                    OnKitClick(new MapView.KitPoint(p.X, p.Y, z, null));
                }
                FinishWall();
                StopKit(null);
            }
            if (Program.KitFloorAtStart != null && editor != null)
            {
                OnKitFloor(null, null);
                if (kitTool != KitTool.Floor) return;
                foreach (var p in Points(Program.KitFloorAtStart))
                    OnKitClick(new MapView.KitPoint(p.X, p.Y, KitFixedHeight ?? editor.GroundZ(p.X, p.Y), null));
                FinishWall();
                StopKit(null);
            }
            if (Program.KitRoomAtStart != null && editor != null)
            {
                if (Program.KitFloorTypeAtStart >= 0) KitFloor.Text = Program.KitFloorTypeAtStart.ToString(CultureInfo.InvariantCulture);
                OnKitRoom(null, null);
                if (kitTool != KitTool.Room) return;
                foreach (var p in Points(Program.KitRoomAtStart))
                    OnKitClick(new MapView.KitPoint(p.X, p.Y, KitFixedHeight ?? editor.GroundZ(p.X, p.Y), null));
                FinishWall();
                StopKit(null);
            }
            if (Program.KitPlaceAtStart != null && editor != null)
            {
                var pts = Points(Program.KitPlaceAtStart);
                OnKitPlace(null, null);
                if (kitTool != KitTool.Place) return;
                for (int i = 0; i < pts.Count; i++)
                {
                    var kp = new MapView.KitPoint(pts[i].X, pts[i].Y, KitFixedHeight ?? editor.GroundZ(pts[i].X, pts[i].Y), null);
                    OnKitHover(kp);
                    if (i < pts.Count - 1) OnKitClick(kp);
                }
                StatusText.Text = HoverText.Text;       // the ghost's spot and what it snapped to, for the shot
            }
        }

        static List<Vector2> Points(string s) =>
            s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(',')).Where(p => p.Length == 2)
             .Select(p => new Vector2(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture))).ToList();

        /// <summary>The game was opened: the map list for the source box (the current map picked when the tab is first used).</summary>
        void KitGameOpened()
        {
            StopKit(null);
            kitCache.Clear();
            kitThumbJob?.Cancel();
            kitThumbs.Clear();
            kit = null;
            kitSource = null;
            KitSource.ItemsSource = maps;
            KitList.ItemsSource = null;
            KitSummary.Text = "";
        }

        /// <summary>Another map was opened in the view: whatever was being built there is taken out.</summary>
        void KitMapLeaving() => StopKit(null);

        void OnKitSource(object sender, SelectionChangedEventArgs e)
        {
            if (KitSource.SelectedItem is not MapEntry m || game == null) return;
            try
            {
                if (!kitCache.TryGetValue(m.Number, out var entry))
                {
                    // the map's own editor when it is open (unsaved edits count), else its file
                    var fixedFile = editors.TryGetValue(m.Number, out var ed) && ed.Fixed != null ? ed.Fixed : LoadObjectFile("static", "fixed", m.Number, ObjectFileKind.Fixed);
                    if (fixedFile == null) { KitSummary.Text = $"Map {m.Number} has no fixed objects."; return; }
                    var built = KitCatalog.Build(m.Number, fixedFile.Objects, typeTable,
                                                 r => appearances != null && r < appearances.Count ? appearances.Read(r) : null, t => typeNames?[t]);
                    entry = kitCache[m.Number] = (built, fixedFile);
                }
                (kit, kitSource) = entry;
                KitSummary.Text = $"{kit.Pieces.Values.Sum(p => p.Count)} pieces of {kit.Pieces.Count} kinds, {kit.Joins.Count} joins; joins on a {kit.JoinStep}-unit grid.";
                OnKitSearch(null, null);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is System.IO.InvalidDataException)
            {
                KitSummary.Text = "Could not read that map: " + ex.Message;
            }
        }

        void OnKitSearch(object sender, TextChangedEventArgs e)
        {
            if (kit == null) { KitList.ItemsSource = null; return; }
            string q = KitSearch.Text?.Trim() ?? "";
            var list = kit.ByUse.Where(p => p.Size.X > 0);
            if (q.Length > 0)
                list = int.TryParse(q, out _) ? list.Where(p => p.Type.ToString().StartsWith(q, StringComparison.Ordinal))
                                              : list.Where(p => p.Name != null && p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            // each row: the model's picture (drawn in the background) and the piece's name, use and size
            var rows = new List<(KitPiece, Image)>();
            var controls = new List<Control>();
            foreach (var p in list.Take(600))
            {
                var picture = new Image { Width = ThumbSize, Height = ThumbSize, Source = kitThumbs.GetValueOrDefault(p.Model) };
                var item = new KitItem { Piece = p };
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Tag = item };
                row.Children.Add(picture);
                row.Children.Add(new TextBlock { Text = item.ToString(), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                controls.Add(row);
                rows.Add((p, picture));
            }
            KitList.ItemsSource = controls;
            StartKitThumbs(rows);
        }

        void OnKitPicked(object sender, SelectionChangedEventArgs e)
        {
            var p = KitPicked;
            if (p == null) { KitJoins.Text = ""; return; }
            var joins = p.Joins.Take(5).Select(j => FormattableString.Invariant(
                $"{(kit.Pieces.TryGetValue(j.To, out var q) ? q.Label : "type " + j.To)} at ({j.Offset.X:0}, {j.Offset.Y:0}, {j.Offset.Z:0}){(j.Turn != 0 ? $" turned {j.Turn}°" : "")} ×{j.Count}"));
            KitJoins.Text = p.Joins.Count == 0 ? "No joins found for this piece: it snaps to the grid of the piece nearest to it." : "Joins: " + string.Join("; ", joins);
            // a wall pattern: the piece itself when it follows itself in a run, else it and the piece that usually follows it
            if (kit.StraightStep(p.Type, p.Type) != null) KitPattern.Text = p.Type.ToString(CultureInfo.InvariantCulture);
            else
            {
                var next = p.Joins.Where(j => j.Turn == 0 && j.To != p.Type && j.Reach > 1 && Math.Abs(j.Offset.Z) < 0.25f * j.Reach).Select(j => j.To).FirstOrDefault(-1);
                KitPattern.Text = next >= 0 ? $"{p.Type},{next}" : p.Type.ToString(CultureInfo.InvariantCulture);
            }
            // a piece picked while placing: the ghost changes to it
            if (kitTool == KitTool.Place) { DropKitGhost(); StatusText.Text = $"Placing {p.Label}: click to put it down, [ ] turn it, Esc stops."; }
        }

        bool KitReady()
        {
            if (editor == null) { StatusText.Text = "Open a map first."; return false; }
            if (kit == null) { StatusText.Text = "Pick the map whose pieces to build with (Kit tab, Pieces of)."; return false; }
            if (editor.Fixed == null) { StatusText.Text = "This map has no fixed object file to build in."; return false; }
            return true;
        }

        void OnKitPlace(object sender, RoutedEventArgs e)
        {
            if (!KitReady()) return;
            if (KitPicked == null) { StatusText.Text = "Pick a piece in the Kit list first."; return; }
            StopKit(null);
            kitTool = KitTool.Place;
            kitYaw = 0;
            View.KitMode = true;
            View.Placing = true;
            ShowMode();
            StatusText.Text = $"Placing {KitPicked.Label}: it follows the mouse; click to put it down, [ ] turn it, Esc stops.";
            View.Focus();
        }

        void OnKitWall(object sender, RoutedEventArgs e)
        {
            if (!KitReady()) return;
            if (KitPatternTypes() is not { } pattern) return;
            StopKit(null);
            kitTool = KitTool.Wall;
            wallPoints.Clear();
            View.KitMode = true;
            View.Placing = true;
            ShowMode();
            StatusText.Text = $"Draw wall ({string.Join(", ", pattern)}): click its points; Enter builds it, Backspace takes the last point back, Esc cancels.";
            View.Focus();
        }

        void OnKitFloor(object sender, RoutedEventArgs e)
        {
            if (!KitReady()) return;
            if (KitPicked == null) { StatusText.Text = "Pick the floor piece in the Kit list first (a tile, such as Shame's 663)."; return; }
            StopKit(null);
            kitTool = KitTool.Floor;
            wallPoints.Clear();
            View.KitMode = true;
            View.Placing = true;
            ShowMode();
            StatusText.Text = $"Fill floor with {KitPicked.Label}: click the area's corners; Enter fills it, Backspace takes the last point back, Esc cancels.";
            View.Focus();
        }

        List<int> KitPatternTypes()
        {
            var parts = (KitPattern.Text ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<int>();
            foreach (var s in parts)
            {
                if (!int.TryParse(s, out int t) || !kit.Pieces.ContainsKey(t)) { StatusText.Text = $"Wall pattern: {s} is not a piece of map {kit.Map}'s kit."; return null; }
                list.Add(t);
            }
            if (list.Count == 0) { StatusText.Text = "Wall pattern: give the types the wall cycles through (e.g. 666,664), or pick a piece."; return null; }
            return list;
        }

        /// <summary>The build height typed in the tab (object units), or null for "whatever is under the mouse".</summary>
        int? KitFixedHeight => int.TryParse(KitHeight.Text?.Trim(), out int z) && z >= 0 ? z : null;

        void OnKitHeight(object sender, RoutedEventArgs e) => View.WorkHeight = KitFixedHeight;
        void OnKitHeightKey(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            View.WorkHeight = KitFixedHeight;
            StatusText.Text = View.WorkHeight is { } z ? $"Building at height {z}: the mouse also meets a level plane there." : "Building on whatever is under the mouse.";
            View.Focus();
            e.Handled = true;
        }

        void OnKitTakeHeight(object sender, RoutedEventArgs e)
        {
            if (selected == null) { StatusText.Text = "Select an object to take its height."; return; }
            KitHeight.Text = selected.Source.Z.ToString(CultureInfo.InvariantCulture);
            View.WorkHeight = selected.Source.Z;
            StatusText.Text = $"Building at height {selected.Source.Z} (that of {selected.Name ?? "type " + selected.Source.Type}).";
        }

        void OnKitShow(object sender, RoutedEventArgs e)
        {
            var p = KitPicked;
            if (p == null || editor == null) { StatusText.Text = "Pick a piece first."; return; }
            var o = editor.Objects.All.FirstOrDefault(x => !x.Source.Removed && x.Source.Type == p.Type && x != kitGhost);
            if (o == null) { StatusText.Text = $"This map has no {p.Label}" + (current?.Number != kit.Map ? $"; open map {kit.Map} to see the source's." : "."); return; }
            View.Camera.Target = o.World;
            View.Camera.Distance = Math.Max(6f, p.Size.Length() / MapSpace.ObjectUnitsPerPoint * 3f);
            View.SelectObject(o);
            View.Redraw();
            StatusText.Text = $"{p.Label} at ({o.Source.PointX:F2}, {o.Source.PointRow:F2}) z {o.Source.Z}, turned {MapEditor.Yaw(o.Source.Rotation):0.#}°.";
        }

        // ---- the mouse while building

        /// <summary>Where the picked piece would go for a point under the mouse: snapped when it can be.</summary>
        (KitPlacement Placement, string How) KitSpot(MapView.KitPoint p, KitPiece piece, float yaw)
        {
            float z = KitFixedHeight ?? p.Z;
            var cursor = new Vector3(p.MapX, p.MapY, z);
            if (KitSnap.IsChecked == true)
            {
                var placed = editor.Fixed.Objects.Where(o => kitGhost == null || o != kitGhost.Source);
                float reach = Math.Max(96f, 0.6f * Math.Max(piece.Size.X, piece.Size.Y));
                if (KitBuilding.Snap(kit, piece, placed, cursor, yaw, reach) is { } s) return (s.Placement, s.How);
            }
            return (new KitPlacement(piece.Type, cursor, yaw), "free");
        }

        /// <summary>
        /// Moving one piece of the kit (Move mode, with the Kit tab showing and Snap to pieces on): it snaps to the joins
        /// of the pieces round it, as when placing. Other objects, and everything while another tab shows, move freely.
        /// </summary>
        (int X, int Y, int Z, Quaternion Rotation)? KitDragSnap(SceneObject o, int x, int y, int z)
        {
            if (kit == null || editor == null || SideTabs.SelectedItem != KitTab || KitSnap.IsChecked != true
                || o.Source.Map != editor.Fixed || !kit.Pieces.TryGetValue(o.Source.Type, out var piece))
                return null;
            float yaw = KitCatalog.UprightYaw(o.Source.Rotation) ?? 0;
            float reach = Math.Max(96f, 0.6f * Math.Max(piece.Size.X, piece.Size.Y));
            var placed = editor.Fixed.Objects.Where(m => m != o.Source && (kitGhost == null || m != kitGhost.Source));
            if (KitBuilding.Snap(kit, piece, placed, new Vector3(x, y, z), yaw, reach) is not { } s) return null;
            var p = s.Placement;
            return (p.MapX, p.MapY, p.Z, p.Rotation);
        }

        void OnKitHover(MapView.KitPoint? point)
        {
            if (editor == null || kit == null || point is not { } p) return;
            if (kitTool == KitTool.Place && KitPicked is { } piece)
            {
                var (spot, how) = KitSpot(p, piece, kitYaw);
                Edit(() =>
                {
                    if (kitGhost == null || kitGhost.Source.Removed || kitGhost.Source.Type != piece.Type)
                    {
                        DropKitGhost();
                        kitGhost = editor.PlaceKit(new[] { spot }, kitSource, ObjectFileKind.Fixed, GhostStep).FirstOrDefault();
                        View.KitIgnore = kitGhost;
                    }
                    else View.Preview(kitGhost, spot.MapX, spot.MapY, spot.Z, spot.Rotation);
                });
                HoverText.Text = FormattableString.Invariant($"{piece.Label} at ({spot.MapX / 128.0:F2}, {spot.MapY / 128.0:F2}) z {spot.Z}, turned {spot.Yaw:0.#}° ({how})");
            }
            else if (KitDrawing)
            {
                string what = kitTool == KitTool.Floor ? "floor" : "wall";
                if (wallPoints.Count == 0) { HoverText.Text = FormattableString.Invariant($"{what} starts at ({p.MapX / 128.0:F2}, {p.MapY / 128.0:F2}) z {KitFixedHeight ?? (int)p.Z}"); return; }
                var d = new Vector2(p.MapX, p.MapY) - wallPoints[^1];
                HoverText.Text = FormattableString.Invariant($"next edge {d.Length():0} units at {MathF.Atan2(d.Y, d.X) * 180 / MathF.PI:0.#}°   ({wallPoints.Count} points; Enter builds the {what})");
                KitHoverPreview(new Vector2(p.MapX, p.MapY));
            }
        }

        void OnKitClick(MapView.KitPoint? point)
        {
            if (editor == null || kit == null) return;
            if (point is not { } p) { StatusText.Text = "Nothing under the mouse to build on: set a build height to build in empty space."; return; }
            if (kitTool == KitTool.Place && KitPicked is { } piece)
            {
                var (spot, how) = KitSpot(p, piece, kitYaw);
                DropKitGhost();
                List<SceneObject> made = null;
                Edit(() => made = editor.PlaceKit(new[] { spot }, kitSource));
                if (made is { Count: > 0 })
                {
                    View.SelectObject(made[0]);
                    kitYaw = spot.Yaw;      // the next piece starts turned like this one
                    StatusText.Text = FormattableString.Invariant($"Placed {piece.Label} at ({spot.MapX / 128.0:F2}, {spot.MapY / 128.0:F2}) z {spot.Z}, turned {spot.Yaw:0.#}° ({how}). Ctrl+Z takes it back.");
                    UpdateTitle();
                }
            }
            else if (KitDrawing)
            {
                var pattern = KitDrawPattern();
                if (pattern == null) return;
                var at = new Vector2(p.MapX, p.MapY);
                if (wallPoints.Count == 0)
                {
                    wallZ = KitFixedHeight ?? (int)MathF.Round(p.Z);
                    // the wall's first piece where the kit would put it next to a piece already here
                    if (kitTool == KitTool.Wall && KitSnap.IsChecked == true
                        && KitBuilding.Snap(kit, kit.Pieces[pattern[0]], editor.Fixed.Objects, new Vector3(at, wallZ), 0, Math.Max(96f, 0.6f * kit.Pieces[pattern[0]].Size.X)) is { } s)
                    {
                        at = new Vector2(s.Placement.Position.X, s.Placement.Position.Y);
                        wallZ = s.Placement.Z;
                    }
                }
                if (kitTool == KitTool.Room && wallPoints.Count == 3) wallPoints[2] = at;      // a fourth click moves the depth
                else wallPoints.Add(at);
                kitHoverPoint = kitHoverShown = null;       // the next mouse move previews from the new point
                RefreshWallPreview(pattern);
            }
        }

        /// <summary>The types the drawing tool builds with: the wall pattern, or the picked piece for a floor.</summary>
        List<int> KitDrawPattern()
        {
            if (kitTool != KitTool.Floor) return KitPatternTypes();
            if (KitPicked is { } p) return new List<int> { p.Type };
            StatusText.Text = "Pick the floor piece in the Kit list first.";
            return null;
        }

        /// <summary>
        /// Shows the wall, floor or room the points make (with <paramref name="hover"/>, the point under the mouse, as
        /// a tentative next one) as a preview edit, replacing the last preview.
        /// </summary>
        void RefreshWallPreview(List<int> pattern, Vector2? hover = null)
        {
            DropWallPreview();
            wallPlan = null;
            bool floor = kitTool == KitTool.Floor, room = kitTool == KitTool.Room;
            int need = floor || room ? 3 : 2;
            var pts = new List<Vector2>(wallPoints);
            if (hover is { } h) { if (room && pts.Count == 3) pts[2] = h; else pts.Add(h); }
            if (pts.Count >= need)
                Edit(() =>
                {
                    var plan = KitPlan(pattern, pts);
                    if (hover == null) wallPlan = plan;
                    if (plan.Count > 0) { wallPreviewObjects = editor.PlaceKit(plan, kitSource, ObjectFileKind.Fixed, WallPreviewStep); wallPreview = true; }
                });
            if (hover != null) return;        // the hover readout says the rest
            string what = floor ? "Floor" : room ? "Room" : "Wall";
            StatusText.Text = wallPoints.Count < need
                ? $"{what}: click the next point (Esc cancels)."
                : $"{what}: {wallPoints.Count} points, {wallPlan?.Count ?? 0} pieces at height {wallZ}. Click on, Enter builds, Backspace takes the last point back, Esc cancels.";
        }

        /// <summary>What the drawing tool builds from these points (enough of them assumed).</summary>
        List<KitPlacement> KitPlan(List<int> pattern, List<Vector2> pts)
        {
            int cornerType = KitTypeField(KitCorner);
            bool flip = KitFlip.IsChecked == true;
            return kitTool switch
            {
                KitTool.Floor => KitBuilding.Fill(kit, pattern[0], pts, wallZ, KitAngleStep),
                KitTool.Room => KitBuilding.Room(kit, KitTypeField(KitFloor), pattern, cornerType, pts[0], pts[1], pts[2], wallZ, flip),
                _ => KitBuilding.Wall(kit, pattern, pts, wallZ, flip, KitAngleStep, cornerType),
            };
        }

        // the preview follows the mouse, at most five times a second (each one regroups the model instances)
        Vector2? kitHoverPoint, kitHoverShown;
        Avalonia.Threading.DispatcherTimer kitHoverTimer;

        void KitHoverPreview(Vector2 at)
        {
            if (wallPoints.Count == 0) return;
            kitHoverPoint = at;
            if (kitHoverTimer == null)
            {
                kitHoverTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                kitHoverTimer.Tick += (s, e) =>
                {
                    if (!KitDrawing || wallPoints.Count == 0 || kitHoverPoint == null) { kitHoverTimer.Stop(); return; }
                    if (kitHoverPoint == kitHoverShown) return;
                    kitHoverShown = kitHoverPoint;
                    if (KitDrawPattern() is { } pattern) RefreshWallPreview(pattern, kitHoverPoint);
                };
            }
            if (!kitHoverTimer.IsEnabled) kitHoverTimer.Start();
        }

        void FinishWall()
        {
            // the clicked points only (the preview may show the mouse's point too)
            var pattern = KitDrawPattern();
            int need = kitTool == KitTool.Wall ? 2 : 3;
            List<KitPlacement> plan = null;
            if (pattern != null && wallPoints.Count >= need)
            {
                try { plan = KitPlan(pattern, wallPoints); }
                catch (ArgumentException ex) { StatusText.Text = "Not done: " + ex.Message; }
            }
            string what = kitTool switch { KitTool.Floor => "floor", KitTool.Room => "room", _ => "wall" };
            DropWallPreview();
            wallPoints.Clear();
            wallPlan = null;
            kitHoverPoint = kitHoverShown = null;
            if (plan is { Count: > 0 })
            {
                Edit(() => editor.PlaceKit(plan, kitSource, ObjectFileKind.Fixed, $"{what} of {plan.Count} pieces"));
                StatusText.Text = $"Built a {what} of {plan.Count} pieces. Ctrl+Z takes it back; click to start another.";
                UpdateTitle();
            }
            else StatusText.Text = what == "wall" ? "No wall: it needs two points." : $"No {what}: it needs three clicks.";
        }

        /// <summary>
        /// Takes the ghost out: undone and forgotten for redo while it is the newest edit; deleted when another edit
        /// came after it (a panel button while placing), so it never stays behind as a real object.
        /// </summary>
        void DropKitGhost()
        {
            if (kitGhost == null) return;
            if (editor != null)
            {
                if (editor.UndoDescription == GhostStep) { editor.Undo(); editor.ForgetRedo(); }
                else if (!kitGhost.Source.Removed) Edit(() => editor.Remove(kitGhost));
            }
            kitGhost = null;
            View.KitIgnore = null;
        }

        List<SceneObject> wallPreviewObjects;

        void DropWallPreview()
        {
            if (!wallPreview) return;
            if (editor != null)
            {
                if (editor.UndoDescription == WallPreviewStep) { editor.Undo(); editor.ForgetRedo(); }
                else if (wallPreviewObjects?.Where(o => !o.Source.Removed).ToList() is { Count: > 0 } left) Edit(() => editor.RemoveGroup(left));
            }
            wallPreview = false;
            wallPreviewObjects = null;
        }

        /// <summary>
        /// Before undo, redo or save: the ghost and a wall preview come out (they are temporary edits); a wall being
        /// drawn keeps its points and shows again on the next click.
        /// </summary>
        void DropKitTemporaries()
        {
            DropKitGhost();
            DropWallPreview();
        }

        /// <summary>Ends any kit building: ghost and wall preview taken out, Place mode left (when <paramref name="message"/> is given).</summary>
        void StopKit(string message)
        {
            if (kitTool == KitTool.None && kitGhost == null && !wallPreview) return;
            DropKitGhost();
            DropWallPreview();
            wallPoints.Clear();
            wallPlan = null;
            var was = kitTool;
            kitTool = KitTool.None;
            View.KitMode = false;
            if (was != KitTool.None && View.Mode == ViewMode.Place) View.Placing = false;
            ShowMode();
            if (message != null) StatusText.Text = message;
        }

        /// <summary>Keys while building; true when handled.</summary>
        bool KitKey(Key key)
        {
            if (kitTool == KitTool.None) return false;
            switch (key)
            {
                case Key.Escape:
                    StopKit(kitTool switch { KitTool.Wall => "Wall cancelled.", KitTool.Floor => "Floor cancelled.", KitTool.Room => "Room cancelled.", _ => "Placing stopped." });
                    return true;
                case Key.Enter when KitDrawing:
                    FinishWall();
                    return true;
                case Key.Back when KitDrawing && wallPoints.Count > 0:
                    wallPoints.RemoveAt(wallPoints.Count - 1);
                    kitHoverPoint = kitHoverShown = null;
                    if (KitDrawPattern() is { } pattern) RefreshWallPreview(pattern);
                    return true;
                case Key.OemOpenBrackets when kitTool == KitTool.Place:
                case Key.OemCloseBrackets when kitTool == KitTool.Place:
                    float step = KitAngleStep > 0 ? KitAngleStep : 15;
                    kitYaw = KitBuilding.Wrap(kitYaw + (key == Key.OemOpenBrackets ? step : -step));
                    if (kitGhost != null) View.Preview(kitGhost, kitGhost.Source.MapX, kitGhost.Source.MapY, kitGhost.Source.Z, KitBuilding.Upright(kitYaw));
                    StatusText.Text = $"Turned to {kitYaw:0.#}° (when it does not snap to a join).";
                    return true;
            }
            return false;
        }
    }
}
