using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using U9Editor.Core;
using U9Editor.Render;

namespace U9Editor.App
{
    /// <summary>
    /// The 3D view, after the UW editor's LevelView: hosts <see cref="TerrainRenderer"/> in Avalonia's OpenGL
    /// control and turns mouse and keys into camera moves and picks. It draws only when something changed.
    /// Mouse: left click selects, Ctrl+click adds or takes away an object, Ctrl+left drag selects the objects in a box,
    /// left drag on a selected object moves the selection, other left or right drags orbit, middle or Shift+left drag
    /// pans, wheel zooms.
    /// Keys: W/A/S/D or arrows move, Q/E turn, R/F raise and lower the point looked at, +/- zoom, Home frames the map.
    /// </summary>
    public sealed class MapView : OpenGlControlBase, Avalonia.Rendering.ICustomHitTest
    {
        readonly TerrainRenderer renderer = new TerrainRenderer();
        public readonly OrbitCamera Camera = new OrbitCamera();
        public readonly ViewState State = new ViewState();
        TerrainRaster raster;
        MapSpace space;
        bool glReady;
        Point pressAt, lastAt;
        bool dragging, panning, leftDown, anyDown;
        readonly Stopwatch frameClock = new Stopwatch();

        public event Action<TerrainHit> Picked;
        /// <summary>The object under the mouse changed, or one was clicked (null: none).</summary>
        public event Action<SceneObject> ObjectHovered, ObjectPicked;
        SceneObjects objects;
        public SceneObjects Objects => objects;
        public event Action<TerrainHit> Hovered;
        public event Action CameraChanged;
        public event Action FrameDrawn;

        public double LastFrameMs { get; private set; }
        public string GlInfo => renderer.Info;
        public string GlError { get; private set; }

        public MapView()
        {
            Focusable = true;
            ClipToBounds = true;
            LostFocus += (s, e) => held.Clear();
        }

        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        public void Redraw() => RequestNextFrameRendering();

        // ---- GL

        protected override void OnOpenGlInit(GlInterface gl)
        {
            try
            {
                renderer.Init(gl.GetProcAddress, GlVersion.Type == GlProfileType.OpenGLES);
                glReady = true;
            }
            catch (Exception e) { GlError = e.Message; }
        }

        protected override void OnOpenGlDeinit(GlInterface gl)
        {
            renderer.Dispose();
            glReady = false;
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            if (!glReady) return;
            frameClock.Restart();
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            int w = (int)Math.Max(1, Bounds.Width * scale), h = (int)Math.Max(1, Bounds.Height * scale);
            renderer.Render(fb, w, h, Camera, State);
            if (renderer.Error != null) GlError = renderer.Error;
            if (pictureWanted != null)
            {
                var done = pictureWanted;
                pictureWanted = null;
                var px = new byte[w * h * 4];
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    readPixels ??= System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<ReadPixelsFn>(gl.GetProcAddress("glReadPixels"));
                    readPixels(0, 0, w, h, 0x1908, 0x1401, pin.AddrOfPinnedObject());      // GL_RGBA, GL_UNSIGNED_BYTE
                }
                catch (Exception e) { px = null; GlError = e.Message; }
                finally { pin.Free(); }
                Dispatcher.UIThread.Post(() => done(w, h, px));
            }
            LastFrameMs = frameClock.Elapsed.TotalMilliseconds;
            Dispatcher.UIThread.Post(() => FrameDrawn?.Invoke());
        }

        Action<int, int, byte[]> pictureWanted;
        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi)]
        delegate void ReadPixelsFn(int x, int y, int width, int height, int format, int type, IntPtr data);
        ReadPixelsFn readPixels;

        /// <summary>Hands over the next frame as RGBA bytes, bottom row first (null if it could not be read).</summary>
        public void TakePicture(Action<int, int, byte[]> done)
        {
            pictureWanted = done;
            Redraw();
        }

        // ---- what to draw

        public TerrainRaster Raster => raster;
        public MapSpace Space => space;

        public void SetMap(TerrainRaster r, MapSpace s, float waterWorldY)
        {
            raster = r;
            space = s;
            State.HoverX = State.HoverRow = State.SelectedX = State.SelectedRow = -1;
            renderer.SetMap(r, s, waterWorldY);
            Redraw();
        }

        public void SetWater(float waterWorldY)
        {
            renderer.SetWater(waterWorldY);
            Redraw();
        }

        public void SetObjects(SceneObjects o)
        {
            objects = o;
            State.HoverObject = State.SelectedObject = -1;
            renderer.SetObjects(o);
            Redraw();
        }

        bool KindShown(SceneObject o) => o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed ? State.ShowFixed : State.ShowNonfixed;
        bool ModelsDrawn => State.ShowModels && models != null;

        /// <summary>
        /// The object under a screen point, if any is in front of the terrain: the first model the ray meets, or a
        /// marker within a few pixels (markers are drawn for every object with models off, else for map-change zones).
        /// </summary>
        public SceneObject PickObjectAt(Point p, TerrainHit terrainHit)
        {
            if (objects == null) return null;
            Camera.Ray((float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height, out var origin, out var dir);
            float terrainT = terrainHit.Hit && State.ShowTerrain ? Vector3.Dot(terrainHit.World - origin, dir) : float.PositiveInfinity;
            SceneObject model = null;
            float modelT = terrainT;
            if (ModelsDrawn) model = objects.PickModel(models, space, origin, dir, terrainT, KindShown, out modelT);
            float depth = terrainHit.Hit ? Vector3.Distance(Camera.Eye, terrainHit.World) : float.PositiveInfinity;
            var vp = Camera.View * Camera.Projection((float)(Bounds.Width / Math.Max(1, Bounds.Height)));
            var marker = objects.Pick(vp, Camera.Eye, (float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height, 7f, depth,
                o => KindShown(o) && (!ModelsDrawn || o.IsLinkZone || o.IsArrival));
            if (marker == null) return model;
            if (model == null) return marker;
            return Vector3.Dot(marker.World - origin, dir) <= modelT + 0.5f ? marker : model;
        }

        ModelScene models;

        public void SetModels(ModelScene m)
        {
            models = m;
            renderer.SetModels(m);
            Redraw();
        }

        // ---- editing

        /// <summary>The next click on the ground places an object (<see cref="PlaceAt"/>) instead of selecting.</summary>
        public bool Placing { get; set; }
        public event Action<TerrainHit> PlaceAt;

        // ---- terrain brush

        TerrainBrush brush;
        /// <summary>While set, a left drag on the ground edits the terrain with it (through <see cref="Editor"/>).</summary>
        public TerrainBrush Brush
        {
            get => brush;
            set { brush = value; if (value == null) { EndStroke(); State.BrushRadius = 0; } else if (State.BrushRadius > 0) State.BrushRadius = value.Radius; Redraw(); }
        }
        /// <summary>A stroke ended (it is on the editor's undo stack).</summary>
        public event Action StrokeEnded;
        /// <summary>Ctrl+click with the brush on: the ground point to take a height or texture from.</summary>
        public event Action<TerrainHit> BrushSample;
        /// <summary>A stroke could not start; the message says why.</summary>
        public event Action<string> StrokeRefused;
        bool stroking;
        float lastDabX, lastDabRow;
        DispatcherTimer dabTimer;

        bool PointOf(Point p, out float x, out float row)
        {
            var hit = PickAt(p);
            x = row = 0;
            if (!hit.Hit) return false;
            x = hit.World.X;
            row = space.Row(hit.World.Z);
            return true;
        }

        void StartStroke(Point p)
        {
            if (Editor?.Terrain == null) { StrokeRefused?.Invoke("This map's terrain cannot be edited here."); return; }
            if (!PointOf(p, out float x, out float row)) return;
            Editor.BeginStroke(brush);
            stroking = true;
            DabAt(x, row);
            // the height tools keep working while the mouse is held still
            if (brush.Tool is TerrainTool.Raise or TerrainTool.Lower or TerrainTool.Smooth or TerrainTool.Flatten or TerrainTool.Roughen)
            {
                dabTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Input, (s, e) => { if (stroking) DabAt(lastDabX, lastDabRow); });
                dabTimer.Start();
            }
        }

        void DabAt(float x, float row)
        {
            Editor.Dab(x, row);
            lastDabX = x; lastDabRow = row;
            State.BrushX = x; State.BrushRow = row;
        }

        void StrokeTo(Point p)
        {
            if (!PointOf(p, out float x, out float row)) return;
            State.BrushX = x; State.BrushRow = row;
            // dabs a quarter of the radius apart along the way
            float step = Math.Max(0.25f, brush.Radius * 0.25f);
            float dx = x - lastDabX, dr = row - lastDabRow, len = MathF.Sqrt(dx * dx + dr * dr);
            int n = (int)(len / step);
            for (int i = 1; i <= n; i++) DabAt(lastDabX + dx * step / len, lastDabRow + dr * step / len);
            Redraw();
        }

        void EndStroke()
        {
            dabTimer?.Stop();
            if (!stroking) return;
            stroking = false;
            Editor?.EndStroke();
            StrokeEnded?.Invoke();
        }

        /// <summary>The map's editing session (null: nothing can be moved).</summary>
        public MapEditor Editor { get; set; }
        /// <summary>A dragged object keeps its height above the ground; off, it keeps its height.</summary>
        public bool FollowTerrain { get; set; } = true;
        /// <summary>An object was dragged and let go: object, new position across the map and height (object units).</summary>
        public event Action<SceneObject, int, int, int> ObjectDragged;
        /// <summary>While dragging: object, position and height it would get.</summary>
        public event Action<SceneObject, int, int, int> ObjectDragging;

        SceneObject dragObject;
        Vector3 dragGrab, dragStartWorld;
        int dragX, dragY, dragZ, dragGroundOffset;
        // a drag of a selection of several objects: all move by the grabbed one's offset
        List<SceneObject> dragGroup;
        /// <summary>A selection of several objects was dragged and let go: how far, in object units east and north.</summary>
        public event Action<IReadOnlyList<SceneObject>, int, int> GroupDragged;

        /// <summary>Objects changed in the editor: move their markers and models, or send all again.</summary>
        public void RefreshEdited(IReadOnlyList<SceneObject> touched, bool structural)
        {
            if (structural)
            {
                renderer.SetObjects(objects);
                renderer.SetInstances(models);
            }
            else
                foreach (var o in touched)
                {
                    if (o.Source.Removed) continue;
                    renderer.UpdateMarker(o.Id, o.World);
                    if (models != null) renderer.UpdateInstance(models.SlotOf(o.Id), ModelScene.Placement(o.Source, space), o.Id);
                }
            Redraw();
        }

        /// <summary>Draws an object at a position it does not have yet (object units across the map), as a drag shows it.</summary>
        public void Preview(SceneObject o, float mapX, float mapY, float z, Quaternion rotation)
        {
            renderer.UpdateMarker(o.Id, new Vector3(mapX / MapSpace.ObjectUnitsPerPoint, MapSpace.WorldYFromObject(z), space.WorldZ(mapY / MapSpace.ObjectUnitsPerPoint)));
            if (models != null) renderer.UpdateInstance(models.SlotOf(o.Id), ModelScene.Placement(mapX, mapY, z, rotation, space), o.Id);
            Redraw();
        }

        bool RayToHeight(Point p, float y, out Vector3 at)
        {
            Camera.Ray((float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height, out var o, out var d);
            at = default;
            if (Math.Abs(d.Y) < 1e-4f) return false;
            float t = (y - o.Y) / d.Y;
            if (t < 0) return false;
            at = o + d * t;
            return true;
        }

        void StartObjectDrag(Point p)
        {
            dragObject = null;
            if (Editor == null || objects == null || State.SelectedObject < 0) return;
            var hit = PickAt(p);
            var ob = PickObjectAt(p, hit);
            if (ob != null && selection.Count > 1 && selection.Contains(ob)) { }      // any of the selection can be grabbed
            else if (ob == null || ob.Id != State.SelectedObject)
            {
                // grabbing needs no exact hit: anywhere inside the selection's bounding sphere, or near its marker
                ob = State.SelectedObject < objects.All.Count ? objects.All[State.SelectedObject] : null;
                if (ob == null || ob.Source.Removed || !NearSelection(ob, p)) return;
            }
            if (!RayToHeight(p, ob.World.Y, out dragGrab)) return;
            dragObject = ob;
            dragGroup = selection.Count > 1 && selection.Contains(ob) ? new List<SceneObject>(selection) : null;
            dragStartWorld = ob.World;
            dragX = ob.Source.MapX; dragY = ob.Source.MapY; dragZ = ob.Source.Z;
            dragGroundOffset = ob.Source.Z - Editor.GroundZ(dragX, dragY);
        }

        bool NearSelection(SceneObject o, Point p)
        {
            Camera.Ray((float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height, out var origin, out var dir);
            if (o.Model >= 0 && ModelsDrawn)
            {
                var oc = o.BoundCentre - origin;
                float along = Vector3.Dot(oc, dir);
                if (along > 0 && (oc - dir * along).LengthSquared() <= o.BoundRadius * o.BoundRadius) return true;
            }
            var vp = Camera.View * Camera.Projection((float)(Bounds.Width / Math.Max(1, Bounds.Height)));
            var c = Vector4.Transform(new Vector4(o.World, 1), vp);
            if (c.W <= 0.01f) return false;
            float sx = (c.X / c.W * 0.5f + 0.5f) * (float)Bounds.Width, sy = (0.5f - c.Y / c.W * 0.5f) * (float)Bounds.Height;
            return (sx - p.X) * (sx - p.X) + (sy - p.Y) * (sy - p.Y) <= 12 * 12;
        }

        void DragObjectTo(Point p)
        {
            if (!RayToHeight(p, dragStartWorld.Y, out var at)) return;
            var w = dragStartWorld + new Vector3(at.X - dragGrab.X, 0, at.Z - dragGrab.Z);
            int mx = Math.Clamp((int)MathF.Round(w.X * MapSpace.ObjectUnitsPerPoint), 0, space.Width * (int)MapSpace.ObjectUnitsPerPoint - 1);
            int my = Math.Clamp((int)MathF.Round(space.Row(w.Z) * MapSpace.ObjectUnitsPerPoint), 0, space.Height * (int)MapSpace.ObjectUnitsPerPoint - 1);
            int z = FollowTerrain ? Math.Clamp(Editor.GroundZ(mx, my) + dragGroundOffset, 0, 0xFFFF) : dragObject.Source.Z;
            dragX = mx; dragY = my; dragZ = z;
            if (dragGroup != null)
            {
                foreach (var (o, x, y, gz) in Editor.GroupMoveTargets(dragGroup, mx - dragObject.Source.MapX, my - dragObject.Source.MapY, 0, FollowTerrain))
                    Preview(o, x, y, gz, o.Source.Rotation);
            }
            else Preview(dragObject, mx, my, z, dragObject.Source.Rotation);
            ObjectDragging?.Invoke(dragObject, mx, my, z);
        }

        /// <summary>Stops a drag and puts the object back where it was.</summary>
        public void CancelDrag()
        {
            if (dragObject == null) return;
            var o = dragObject;
            var g = dragGroup;
            dragObject = null;
            dragGroup = null;
            anyDown = dragging = false;
            RefreshEdited(g ?? (IReadOnlyList<SceneObject>)new[] { o }, false);
        }

        public bool IsDraggingObject => dragObject != null && dragging;

        public void SetTextures(TerrainTextures t)
        {
            renderer.SetTextures(t);
            Redraw();
        }

        /// <summary>Points in a rectangle changed in the raster.</summary>
        public void InvalidatePoints(int x, int row, int w, int h)
        {
            renderer.Invalidate(x, row, w, h);
            Redraw();
        }

        /// <summary>Puts the camera over the middle of the map, looking north, far enough out to see all of it.</summary>
        public void FrameMap()
        {
            if (raster == null) return;
            Camera.Target = space.Centre(MapSpace.WorldY(raster.HeightAt(raster.Width / 2, raster.Height / 2)));
            Camera.Yaw = 0;
            float size = Math.Max(raster.Width, raster.Height);
            if (Camera.Orthographic) { Camera.Pitch = 89; Camera.Distance = size * 0.9f; }
            else { Camera.Pitch = 55; Camera.Distance = size * 1.1f; }
            CameraMoved();
        }

        /// <summary>Looks at a point of the map from the given distance, keeping the camera's angles.</summary>
        public void LookAt(double x, double row, float distance)
        {
            if (raster == null) return;
            Camera.Target = new System.Numerics.Vector3((float)x, MapSpace.WorldY(raster.HeightAt((int)x, (int)row)), space.WorldZ((float)row));
            Camera.Distance = distance;
            CameraMoved();
        }

        /// <summary>Selects an object as a click would (the selection becomes just it, or nothing).</summary>
        public void SelectObject(SceneObject o)
        {
            selection.Clear();
            if (o != null) selection.Add(o);
            SelectionUpdated(o);
            ObjectPicked?.Invoke(o);
        }

        readonly List<SceneObject> selection = new List<SceneObject>();
        /// <summary>Every selected object; the last one picked is <see cref="ViewState.SelectedObject"/> and shown in the panel.</summary>
        public IReadOnlyList<SceneObject> Selection => selection;
        /// <summary>The selection changed (also when it is one object picked by a click).</summary>
        public event Action SelectionChanged;
        /// <summary>The selection box while Ctrl+dragging (view coordinates), or null when it is gone.</summary>
        public event Action<Rect?> BoxChanged;

        /// <summary>Makes these the selection; <paramref name="primary"/> (one of them) is the one the panel shows.</summary>
        public void SetSelection(IEnumerable<SceneObject> objs, SceneObject primary)
        {
            selection.Clear();
            foreach (var o in objs) if (o != null && !selection.Contains(o)) selection.Add(o);
            if (primary != null && !selection.Contains(primary)) selection.Add(primary);
            primary ??= selection.Count > 0 ? selection[^1] : null;
            SelectionUpdated(primary);
            ObjectPicked?.Invoke(primary);
        }

        /// <summary>Drops deleted objects from the selection (after an edit or undo).</summary>
        public void PruneSelection()
        {
            if (selection.RemoveAll(o => o.Source.Removed) == 0) return;
            var primary = selection.Count == 0 ? null : selection.Find(o => o.Id == State.SelectedObject) ?? selection[^1];
            SelectionUpdated(primary);
            ObjectPicked?.Invoke(primary);
        }

        void SelectionUpdated(SceneObject primary)
        {
            State.SelectedObject = primary?.Id ?? -1;
            var ids = new List<int>(selection.Count);
            foreach (var o in selection) ids.Add(o.Id);
            State.Group = selection.Count > 1 ? ids : Array.Empty<int>();
            State.GroupVersion++;
            SelectionChanged?.Invoke();
            Redraw();
        }

        void ToggleInSelection(SceneObject o)
        {
            if (selection.Remove(o))
            {
                var primary = o.Id == State.SelectedObject ? (selection.Count > 0 ? selection[^1] : null) : selection.Find(s => s.Id == State.SelectedObject);
                SelectionUpdated(primary);
                ObjectPicked?.Invoke(primary);
            }
            else
            {
                selection.Add(o);
                SelectionUpdated(o);
                ObjectPicked?.Invoke(o);
            }
        }

        // ---- box selection (Ctrl+left drag)

        bool boxing;

        Rect BoxRect(Point a, Point b) => new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        /// <summary>The objects drawn inside a rectangle of the view (their marker, or their model's centre).</summary>
        public List<SceneObject> ObjectsInBox(Rect box)
        {
            var found = new List<SceneObject>();
            if (objects == null) return found;
            var vp = Camera.View * Camera.Projection((float)(Bounds.Width / Math.Max(1, Bounds.Height)));
            bool models = ModelsDrawn;
            foreach (var o in objects.All)
            {
                if (o.Hidden || o.Source.Removed) continue;
                if (o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed ? !State.ShowFixed : !State.ShowNonfixed) continue;
                var at = models && o.Model >= 0 ? o.BoundCentre : o.World;
                var c = Vector4.Transform(new Vector4(at, 1), vp);
                if (c.W <= 0.01f) continue;
                double sx = (c.X / c.W * 0.5f + 0.5f) * Bounds.Width, sy = (0.5f - c.Y / c.W * 0.5f) * Bounds.Height;
                if (box.Contains(new Point(sx, sy))) found.Add(o);
            }
            return found;
        }

        /// <summary>The objects changed how they are drawn (link zones found, say).</summary>
        public void RefreshObjects()
        {
            renderer.SetObjects(objects);
            Redraw();
        }

        public void SetTopDown(bool on)
        {
            Camera.Orthographic = on;
            if (on) { Camera.Pitch = 89; Camera.Yaw = 0; }
            else Camera.Pitch = 55;
            CameraMoved();
        }

        void CameraMoved()
        {
            CameraChanged?.Invoke();
            Redraw();
        }

        public TerrainHit PickAt(Point p)
        {
            if (raster == null) return TerrainHit.None;
            Camera.Ray((float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height, out var o, out var d);
            return TerrainPicker.Pick(raster, space, o, d);
        }

        // ---- mouse

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            pressAt = lastAt = pt.Position;
            dragging = false;
            anyDown = true;
            leftDown = pt.Properties.IsLeftButtonPressed;
            panning = pt.Properties.IsMiddleButtonPressed || (leftDown && (e.KeyModifiers & KeyModifiers.Shift) != 0);
            dragObject = null;
            if (leftDown && !panning && brush != null && (e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                var h = PickAt(pressAt);
                if (h.Hit) BrushSample?.Invoke(h);
                anyDown = leftDown = false;
                e.Handled = true;
                return;
            }
            boxing = false;
            if (leftDown && !panning && brush != null) StartStroke(pressAt);
            else if (leftDown && !panning && !Placing && (e.KeyModifiers & KeyModifiers.Control) != 0) boxing = true;
            else if (leftDown && !panning) StartObjectDrag(pressAt);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            if (anyDown)
            {
                var d = p - lastAt;
                if (!dragging && Math.Abs(p.X - pressAt.X) + Math.Abs(p.Y - pressAt.Y) > 4) dragging = true;
                if (stroking)
                {
                    StrokeTo(p);
                    return;
                }
                if (dragging && dragObject != null)
                {
                    DragObjectTo(p);
                    return;
                }
                if (boxing)
                {
                    if (dragging) BoxChanged?.Invoke(BoxRect(pressAt, p));
                    return;
                }
                if (dragging)
                {
                    if (panning || Camera.Orthographic) Camera.Pan((float)d.X, (float)d.Y, (float)Bounds.Height);
                    else Camera.Orbit((float)d.X * 0.35f, (float)d.Y * 0.3f);
                    lastAt = p;
                    CameraMoved();
                }
                return;
            }
            var hit = PickAt(p);
            if (brush != null)
            {
                State.BrushRadius = hit.Hit ? brush.Radius : 0;
                if (hit.Hit) { State.BrushX = hit.World.X; State.BrushRow = space.Row(hit.World.Z); }
                Redraw();
                Hovered?.Invoke(hit);
                return;
            }
            var ob = PickObjectAt(p, hit);
            int hid = ob?.Id ?? -1;
            if (hid != State.HoverObject) { State.HoverObject = hid; Redraw(); }
            ObjectHovered?.Invoke(ob);
            int hx = hit.Hit ? hit.X : -1, hr = hit.Hit ? hit.Row : -1;
            if (hx != State.HoverX || hr != State.HoverRow)
            {
                State.HoverX = hx; State.HoverRow = hr;
                Redraw();
            }
            Hovered?.Invoke(hit);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (stroking)
            {
                EndStroke();
                anyDown = dragging = panning = leftDown = false;
                e.Pointer.Capture(null);
                return;
            }
            bool click = anyDown && !dragging && leftDown && !panning;
            var at = e.GetPosition(this);
            var dropped = dragging ? dragObject : null;
            var droppedGroup = dragGroup;
            bool boxed = boxing && dragging;
            bool ctrlClick = boxing && click;
            anyDown = dragging = panning = leftDown = boxing = false;
            dragObject = null;
            dragGroup = null;
            e.Pointer.Capture(null);
            if (boxed)
            {
                BoxChanged?.Invoke(null);
                var inBox = ObjectsInBox(BoxRect(pressAt, at));
                var keepPrimary = selection.Find(s => s.Id == State.SelectedObject);
                foreach (var o in inBox) if (!selection.Contains(o)) selection.Add(o);
                var primary = keepPrimary ?? (selection.Count > 0 ? selection[^1] : null);
                SelectionUpdated(primary);
                ObjectPicked?.Invoke(primary);
                return;
            }
            if (ctrlClick)
            {
                var ob = PickObjectAt(at, PickAt(at));
                if (ob != null) ToggleInSelection(ob);
                return;
            }
            if (dropped != null)
            {
                if (droppedGroup != null) GroupDragged?.Invoke(droppedGroup, dragX - dropped.Source.MapX, dragY - dropped.Source.MapY);
                else ObjectDragged?.Invoke(dropped, dragX, dragY, dragZ);
                return;
            }
            if (click && Placing)
            {
                var ph = PickAt(at);
                if (ph.Hit) { Placing = false; PlaceAt?.Invoke(ph); }
                return;
            }
            if (click)
            {
                var hit = PickAt(at);
                var ob = PickObjectAt(at, hit);
                State.SelectedX = hit.Hit ? hit.X : -1;
                State.SelectedRow = hit.Hit ? hit.Row : -1;
                SelectObject(ob);
                Picked?.Invoke(hit);
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndStroke();
            if (dragObject != null && dragging) CancelDrag();
            dragObject = null;
            dragGroup = null;
            if (boxing) { boxing = false; BoxChanged?.Invoke(null); }
            anyDown = dragging = panning = leftDown = false;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (State.BrushRadius > 0 && !stroking) { State.BrushRadius = 0; Redraw(); }
            if (State.HoverX >= 0)
            {
                State.HoverX = State.HoverRow = -1;
                Hovered?.Invoke(TerrainHit.None);
                Redraw();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            e.Handled = true;
            Camera.Zoom((float)Math.Pow(0.88, e.Delta.Y));
            CameraMoved();
        }

        // ---- keys: held keys move smoothly, once per frame

        readonly HashSet<Key> held = new HashSet<Key>();
        bool shiftHeld;
        DispatcherTimer motionTimer;
        readonly Stopwatch motionClock = new Stopwatch();

        static bool IsMotionKey(Key k) =>
            k is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right or Key.Q or Key.E or Key.R or Key.F;

        /// <summary>Handles a camera key; marks it handled. The window sends its keys here too.</summary>
        public void CameraKey(KeyEventArgs e)
        {
            if (IsMotionKey(e.Key))
            {
                held.Add(e.Key);
                shiftHeld = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                StartMotion();
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.Add: case Key.OemPlus: Camera.Zoom(0.8f); break;
                case Key.Subtract: case Key.OemMinus: Camera.Zoom(1.25f); break;
                case Key.Home: FrameMap(); break;
                default: return;
            }
            CameraMoved();
            e.Handled = true;
        }

        public void CameraKeyUp(KeyEventArgs e)
        {
            if (held.Remove(e.Key)) e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (!e.Handled) CameraKey(e); }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); CameraKeyUp(e); }

        void StartMotion()
        {
            if (motionTimer == null)
            {
                motionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (s, a) => MotionTick());
            }
            if (!motionTimer.IsEnabled) { motionClock.Restart(); motionTimer.Start(); }
        }

        void MotionTick()
        {
            if (held.Count == 0) { motionTimer.Stop(); return; }
            float dt = (float)Math.Min(0.1, motionClock.Elapsed.TotalSeconds);
            motionClock.Restart();
            // speed grows with the distance, so crossing Britannia and nudging inside a house both feel right
            float speed = Math.Max(4f, Camera.Distance * 0.6f) * (shiftHeld ? 3f : 1f) * dt;
            float fwd = 0, right = 0, turn = 0, up = 0;
            if (held.Contains(Key.W) || held.Contains(Key.Up)) fwd += 1;
            if (held.Contains(Key.S) || held.Contains(Key.Down)) fwd -= 1;
            if (held.Contains(Key.D) || held.Contains(Key.Right)) right += 1;
            if (held.Contains(Key.A) || held.Contains(Key.Left)) right -= 1;
            if (held.Contains(Key.E)) turn += 1;
            if (held.Contains(Key.Q)) turn -= 1;
            if (held.Contains(Key.R)) up += 1;
            if (held.Contains(Key.F)) up -= 1;
            Camera.Move(fwd * speed, right * speed);
            Camera.Target += new Vector3(0, up * speed * 0.5f, 0);
            if (turn != 0) Camera.Orbit(turn * 110f * dt, 0);
            CameraMoved();
        }
    }
}
