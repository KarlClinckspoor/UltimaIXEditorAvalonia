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
    /// Mouse: left click selects, left or right drag orbits, middle or Shift+left drag pans, wheel zooms.
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

        public void SetObjects(SceneObjects o)
        {
            objects = o;
            State.HoverObject = State.SelectedObject = -1;
            renderer.SetObjects(o);
            Redraw();
        }

        /// <summary>The object marker under a screen point, if any is in front of the terrain.</summary>
        public SceneObject PickObjectAt(Point p, TerrainHit terrainHit)
        {
            if (objects == null) return null;
            float depth = terrainHit.Hit ? System.Numerics.Vector3.Distance(Camera.Eye, terrainHit.World) : float.PositiveInfinity;
            var vp = Camera.View * Camera.Projection((float)(Bounds.Width / Math.Max(1, Bounds.Height)));
            return objects.Pick(vp, Camera.Eye, (float)p.X, (float)p.Y, (float)Bounds.Width, (float)Bounds.Height, 7f, depth,
                o => o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed ? State.ShowFixed : State.ShowNonfixed);
        }

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
            bool click = anyDown && !dragging && leftDown && !panning;
            var at = e.GetPosition(this);
            anyDown = dragging = panning = leftDown = false;
            e.Pointer.Capture(null);
            if (click)
            {
                var hit = PickAt(at);
                var ob = PickObjectAt(at, hit);
                State.SelectedObject = ob?.Id ?? -1;
                ObjectPicked?.Invoke(ob);
                State.SelectedX = hit.Hit ? hit.X : -1;
                State.SelectedRow = hit.Hit ? hit.Row : -1;
                Redraw();
                Picked?.Invoke(hit);
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            anyDown = dragging = panning = leftDown = false;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
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
