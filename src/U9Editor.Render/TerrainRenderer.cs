using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.OpenGL;
using U9Editor.Core;

namespace U9Editor.Render
{
    /// <summary>What the view should show and highlight this frame.</summary>
    public sealed class ViewState
    {
        public enum Colouring { Height = 0, TextureNumber = 1, Flags = 2, Textures = 3 }
        public Colouring Colour = Colouring.Textures;
        /// <summary>Debug: quarter turns for every quad's texture, and texture rows the other way.</summary>
        public int UvRotate;
        public bool UvFlipV;
        public bool ShowWater = true;
        public bool ShowHoles;
        public bool TileGrid;
        public int HoverX = -1, HoverRow = -1;
        public int SelectedX = -1, SelectedRow = -1;
        /// <summary>The terrain brush ring: centre in points and radius; radius 0 hides it.</summary>
        public float BrushX, BrushRow, BrushRadius;
        /// <summary>A prefab's footprint (first and last point, x and row; X1 &lt; X0 hides it), its edge in points and which side its north faces (0 north, 1 west, 2 south, 3 east).</summary>
        public float RegionX0, RegionRow0, RegionX1 = -1, RegionRow1, RegionMargin;
        public int RegionNorth;
        public bool ShowFixed = true, ShowNonfixed = true;
        /// <summary>Draw object models (and markers only for objects without one, plus link zones), or markers for all.</summary>
        public bool ShowModels = true;
        public bool ShowTerrain = true;
        /// <summary>Drop terrain quads with texture 29, which the game never shows.</summary>
        public bool HideUnseen = true;
        public int HoverObject = -1, SelectedObject = -1;
        /// <summary>Further selected objects (ids), drawn like the selected one; bump <see cref="GroupVersion"/> after a change.</summary>
        public IReadOnlyCollection<int> Group = Array.Empty<int>();
        public int GroupVersion;
    }

    /// <summary>
    /// Draws a map's terrain with OpenGL 3.3 / GLES 3.0. Knows nothing about the window toolkit: it gets a
    /// function loader, a framebuffer and a size. Like the UW editor's renderer, data handed over with the
    /// Set methods is uploaded at the next <see cref="Render"/>, when the context is current.
    /// <para>The map's raw points go up once as an R32UI texture; one grid of BlockSize x BlockSize quads is
    /// drawn once per block, and the shaders place, light and colour it from the texture. An edit re-sends
    /// only the rectangle that changed.</para>
    /// </summary>
    public sealed unsafe class TerrainRenderer : IDisposable
    {
        public const int BlockSize = 64;

        GL gl;
        uint terrainProg, waterProg, markerProg;
        uint markerVao, markerVbo;
        uint modelProg, modelVao, modelVbo, instVbo, modelTexA, modelTexB;
        int modelSplit = 2048;
        ModelScene pendingModels, models, pendingInstances;
        int uploadedGeometry = -1;
        bool modelsReady;
        readonly List<(int Slot, float[] Data)> pendingInstanceWrites = new List<(int, float[])>();
        readonly List<(int Slot, float[] Data)> pendingMarkerWrites = new List<(int, float[])>();
        int[] markerSlot = Array.Empty<int>();
        readonly List<(int First, int Count, int InstFirst, int InstCount)> modelDraws = new List<(int, int, int, int)>();
        int markerFixed, markerNonfixed;
        float[] pendingMarkers;
        int pendingFixed;
        bool gles;
        uint gridVao, gridVbo, gridEbo, waterVao, waterVbo;
        uint pointsTex, terrainTex, lookupTex;
        // one byte per object id (1: in the selected group), GroupWidth ids to a row
        const int GroupWidth = 1024;
        uint groupTex;
        int groupRows, uploadedGroupVersion = -1;
        bool haveTextures;
        TerrainTextures pendingTextures;
        uint gridIndexCount;
        int texWidth, texHeight;
        readonly Dictionary<(uint, string), int> uniforms = new Dictionary<(uint, string), int>();

        readonly object gate = new object();
        TerrainRaster pendingRaster;
        readonly List<(int X, int Y, int W, int H)> pendingRects = new List<(int, int, int, int)>();
        TerrainRaster raster;
        MapSpace space;
        float waterY;

        public string Info { get; private set; }
        public string Error { get; private set; }

        public void Init(Func<string, IntPtr> getProc, bool isGles)
        {
            gl = GL.GetApi(name => getProc(name));
            gles = isGles;
            string h = Shaders.Header(isGles);
            markerProg = Program(h + Shaders.MarkerVertex, h + Shaders.MarkerFragment);
            modelProg = Program(h + Shaders.ModelVertex, h + Shaders.ModelFragment);
            modelVao = gl.GenVertexArray();
            modelVbo = gl.GenBuffer();
            instVbo = gl.GenBuffer();
            modelTexA = gl.GenTexture();
            modelTexB = gl.GenTexture();
            gl.GetInteger(GetPName.MaxArrayTextureLayers, out int maxLayers);
            modelSplit = Math.Max(256, Math.Min(2048, maxLayers));
            markerVao = gl.GenVertexArray();
            markerVbo = gl.GenBuffer();
            gl.BindVertexArray(markerVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
            for (uint i = 0, off = 0; i < 3; off += i == 0 ? 3u : 1u, i++)
            {
                gl.EnableVertexAttribArray(i);
                gl.VertexAttribPointer(i, i == 0 ? 3 : 1, VertexAttribPointerType.Float, false, 20, (void*)(off * 4));
            }
            gl.BindVertexArray(0);
            terrainProg = Program(h + Shaders.TerrainVertex, h + Shaders.TerrainFragment);
            waterProg = Program(h + Shaders.WaterVertex, h + Shaders.WaterFragment);
            BuildGrid();
            waterVao = gl.GenVertexArray();
            waterVbo = gl.GenBuffer();
            gl.BindVertexArray(waterVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, waterVbo);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
            gl.BindVertexArray(0);
            pointsTex = gl.GenTexture();
            terrainTex = gl.GenTexture();
            lookupTex = gl.GenTexture();
            groupTex = gl.GenTexture();
            uploadedGroupVersion = -1;
            Info = $"{gl.GetStringS(StringName.Version)} — {gl.GetStringS(StringName.Renderer)}";
            // a new context starts empty: the map goes up again
            if (raster != null) lock (gate) { pendingRaster ??= raster; pendingTextures ??= textures; pendingMarkers ??= markers; pendingFixed = markersFixed; pendingModels ??= models; }
        }

        void BuildGrid()
        {
            int n = BlockSize + 1;
            var verts = new float[n * n * 2];
            for (int y = 0, i = 0; y < n; y++)
                for (int x = 0; x < n; x++) { verts[i++] = x; verts[i++] = y; }
            var idx = new uint[BlockSize * BlockSize * 6];
            int k = 0;
            for (int y = 0; y < BlockSize; y++)
                for (int x = 0; x < BlockSize; x++)
                {
                    uint a = (uint)(x + y * n), b = a + 1, c = a + (uint)n, d = c + 1;
                    idx[k++] = a; idx[k++] = b; idx[k++] = d;
                    idx[k++] = a; idx[k++] = d; idx[k++] = c;
                }
            gridIndexCount = (uint)idx.Length;
            gridVao = gl.GenVertexArray();
            gridVbo = gl.GenBuffer();
            gridEbo = gl.GenBuffer();
            gl.BindVertexArray(gridVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gridVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gridEbo);
            gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.StaticDraw);
            gl.BindVertexArray(0);
        }

        // ---- data, uploaded at the next frame

        /// <summary>Shows a new map (null for none).</summary>
        public void SetMap(TerrainRaster r, MapSpace s, float waterWorldY)
        {
            lock (gate)
            {
                raster = r;
                space = s;
                waterY = waterWorldY;
                pendingRaster = r;
                pendingRects.Clear();
            }
        }

        /// <summary>Moves the sea plane (world height).</summary>
        public void SetWater(float waterWorldY)
        {
            lock (gate) waterY = waterWorldY;
        }

        TerrainTextures textures;
        float[] markers;
        int markersFixed;

        /// <summary>The objects' models (null for none).</summary>
        public void SetModels(ModelScene m)
        {
            lock (gate) { models = m; pendingModels = m; }
        }

        /// <summary>The model instances changed (objects added, removed or regrouped): send them again.</summary>
        public void SetInstances(ModelScene m)
        {
            lock (gate)
            {
                models = m;
                pendingInstanceWrites.Clear();
                if (m != null && m.GeometryVersion != uploadedGeometry) pendingModels = m;
                else pendingInstances = m;
            }
        }

        /// <summary>One instance's placement changed (dragging): matrix and object id as stored in <see cref="ModelScene.Instances"/>.</summary>
        public void UpdateInstance(int slot, Matrix4x4 m, int objectId)
        {
            if (slot < 0) return;
            var f = new float[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44, objectId };
            lock (gate) pendingInstanceWrites.Add((slot, f));
        }

        /// <summary>One object's marker moved.</summary>
        public void UpdateMarker(int objectId, Vector3 world)
        {
            int slot = objectId >= 0 && objectId < markerSlot.Length ? markerSlot[objectId] : -1;
            if (slot < 0) return;
            lock (gate) pendingMarkerWrites.Add((slot, new[] { world.X, world.Y, world.Z }));
        }

        void UploadInstances(ModelScene m)
        {
            modelDraws.Clear();
            foreach (var b in m.Batches) modelDraws.Add((b.FirstVertex, b.VertexCount, b.InstanceFirst, b.InstanceCount));
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, m.Instances.Length > 0 ? m.Instances : new float[ModelBatch.InstanceFloats], BufferUsageARB.DynamicDraw);
        }

        void UploadModels(ModelScene m)
        {
            modelsReady = false;
            modelDraws.Clear();
            if (m == null) return;
            uploadedGeometry = m.GeometryVersion;
            gl.BindVertexArray(modelVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, modelVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, m.Vertices, BufferUsageARB.StaticDraw);
            int[] sizes = { 3, 3, 2, 1, 4 };
            for (uint i = 0, off = 0; i < sizes.Length; off += (uint)sizes[i], i++)
            {
                gl.EnableVertexAttribArray(i);
                gl.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, ModelScene.VertexFloats * 4, (void*)(off * 4));
            }
            UploadInstances(m);
            for (uint i = 5; i <= 9; i++) { gl.EnableVertexAttribArray(i); gl.VertexAttribDivisor(i, 1); }
            gl.BindVertexArray(0);

            int size = ModelScene.LayerSize;
            for (int part = 0; part < 2; part++)
            {
                int first = part * modelSplit, n = Math.Max(0, Math.Min(modelSplit, m.Layers.Count - first));
                gl.BindTexture(TextureTarget.Texture2DArray, part == 0 ? modelTexA : modelTexB);
                gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8, (uint)size, (uint)size, (uint)Math.Max(1, n), 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
                for (int i = 0; i < n; i++)
                    fixed (byte* p = m.Layers[first + i])
                        gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, i, (uint)size, (uint)size, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                gl.GenerateMipmap(TextureTarget.Texture2DArray);
                gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            }
            modelsReady = true;
        }

        /// <summary>The objects to mark: fixed ones first, then nonfixed (null for none).</summary>
        public void SetObjects(SceneObjects objects)
        {
            float[] f = new float[0];
            int nFixed = 0;
            var slots = Array.Empty<int>();
            if (objects != null)
            {
                slots = new int[objects.All.Count];
                Array.Fill(slots, -1);
                var shown = new List<SceneObject>();
                foreach (var o in objects.All) if (!o.Source.Removed && o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed) shown.Add(o);
                nFixed = shown.Count;
                foreach (var o in objects.All) if (!o.Source.Removed && o.Kind != U9.Formats.Objects.ObjectFileKind.Fixed) shown.Add(o);
                f = new float[shown.Count * 5];
                int k = 0;
                foreach (var o in shown)
                {
                    slots[o.Id] = k / 5;
                    f[k++] = o.World.X; f[k++] = o.World.Y; f[k++] = o.World.Z;
                    f[k++] = o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed ? 0 : o.IsLinkZone ? 2 : o.IsArrival ? 3 : 1;
                    f[k++] = o.Id;
                }
            }
            lock (gate) { markers = f; markersFixed = nFixed; pendingMarkers = f; pendingFixed = nFixed; markerSlot = slots; pendingMarkerWrites.Clear(); }
        }

        /// <summary>The terrain textures for the map shown (null for none).</summary>
        public void SetTextures(TerrainTextures t)
        {
            lock (gate) { textures = t; pendingTextures = t; }
        }

        void UploadTextures(TerrainTextures t)
        {
            haveTextures = false;
            if (t == null || t.Layers.Count == 0) return;
            int n = t.Layers.Count, size = TerrainTextures.LayerSize;
            gl.BindTexture(TextureTarget.Texture2DArray, terrainTex);
            gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8, (uint)size, (uint)size, (uint)n, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            for (int i = 0; i < n; i++)
                fixed (byte* p = t.Layers[i])
                    gl.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, i, (uint)size, (uint)size, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            gl.GenerateMipmap(TextureTarget.Texture2DArray);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            gl.BindTexture(TextureTarget.Texture2D, lookupTex);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            fixed (ushort* p = t.Lookup)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R16ui, TerrainTextures.Frames, TerrainTextures.Textures, 0, PixelFormat.RedInteger, PixelType.UnsignedShort, p);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            haveTextures = true;
        }

        /// <summary>Points in the rectangle changed in the raster: send them again.</summary>
        public void Invalidate(int x, int row, int w, int h)
        {
            lock (gate) pendingRects.Add((x, row, w, h));
        }

        void Upload()
        {
            TerrainRaster full;
            TerrainTextures tex;
            (int X, int Y, int W, int H)[] rects;
            lock (gate)
            {
                full = pendingRaster;
                pendingRaster = null;
                tex = pendingTextures;
                pendingTextures = null;
                if (pendingModels != null) { UploadModels(pendingModels); pendingModels = null; pendingInstances = null; }
                if (pendingInstances != null) { if (modelsReady) UploadInstances(pendingInstances); pendingInstances = null; }
                if (pendingInstanceWrites.Count > 0 && modelsReady)
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
                    foreach (var w in pendingInstanceWrites)
                        gl.BufferSubData<float>(BufferTargetARB.ArrayBuffer, (nint)(w.Slot * ModelBatch.InstanceFloats * 4), w.Data);
                }
                pendingInstanceWrites.Clear();
                if (pendingMarkers != null)
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
                    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, pendingMarkers, BufferUsageARB.StaticDraw);
                    markerFixed = pendingFixed;
                    markerNonfixed = pendingMarkers.Length / 5 - pendingFixed;
                    pendingMarkers = null;
                }
                if (pendingMarkerWrites.Count > 0)
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
                    foreach (var w in pendingMarkerWrites)
                        gl.BufferSubData<float>(BufferTargetARB.ArrayBuffer, (nint)(w.Slot * 5 * 4), w.Data);
                    pendingMarkerWrites.Clear();
                }
                rects = pendingRects.ToArray();
                pendingRects.Clear();
            }
            if (tex != null) UploadTextures(tex);
            gl.BindTexture(TextureTarget.Texture2D, pointsTex);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            if (full != null && full.Width > 0)
            {
                texWidth = full.Width;
                texHeight = full.Height;
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                fixed (uint* p = full.Points)
                    gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32ui, (uint)full.Width, (uint)full.Height, 0, PixelFormat.RedInteger, PixelType.UnsignedInt, p);
                UploadWater(full);
            }
            var r = raster;
            if (r != null && texWidth == r.Width)
                foreach (var rc in rects)
                {
                    int x0 = Math.Max(0, rc.X), y0 = Math.Max(0, rc.Y);
                    int x1 = Math.Min(r.Width, rc.X + rc.W), y1 = Math.Min(r.Height, rc.Y + rc.H);
                    if (x1 <= x0 || y1 <= y0) continue;
                    var sub = new uint[(x1 - x0) * (y1 - y0)];
                    for (int y = y0; y < y1; y++) Array.Copy(r.Points, x0 + y * r.Width, sub, (y - y0) * (x1 - x0), x1 - x0);
                    fixed (uint* p = sub)
                        gl.TexSubImage2D(TextureTarget.Texture2D, 0, x0, y0, (uint)(x1 - x0), (uint)(y1 - y0), PixelFormat.RedInteger, PixelType.UnsignedInt, p);
                }
        }

        void UploadWater(TerrainRaster r)
        {
            // the water reaches well past the map's edge, as the game's sea does
            float m = Math.Max(r.Width, r.Height) * 8;
            float x0 = -m, x1 = r.Width + m, z0 = -m, z1 = r.Height + m;
            var v = new[] { x0, z0, x1, z0, x1, z1, x0, z0, x1, z1, x0, z1 };
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, waterVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, v, BufferUsageARB.StaticDraw);
        }

        public void Render(int framebuffer, int width, int height, OrbitCamera cam, ViewState st)
        {
            try
            {
                Upload();
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)framebuffer);
                gl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
                gl.ClearColor(0.10f, 0.17f, 0.27f, 1f);
                gl.ClearDepth(1);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                gl.Enable(EnableCap.DepthTest);
                gl.DepthFunc(DepthFunction.Lequal);
                gl.DepthMask(true);
                gl.Disable(EnableCap.CullFace);
                gl.Disable(EnableCap.Blend);

                var r = raster;
                var s = space;
                if (r == null || s == null || texWidth == 0) return;
                var vp = cam.View * cam.Projection(width / (float)Math.Max(1, height));

                gl.UseProgram(terrainProg);
                Matrix(terrainProg, "uViewProj", vp);
                gl.Uniform2(U(terrainProg, "uSize"), r.Width, r.Height);
                gl.Uniform1(U(terrainProg, "uNorthRow0"), s.NorthIsRowZero ? 1 : 0);
                gl.Uniform1(U(terrainProg, "uHeightScale"), 1f / MapSpace.HeightUnitsPerPoint);
                gl.Uniform1(U(terrainProg, "uColourMode"), (int)st.Colour);
                gl.Uniform1(U(terrainProg, "uWaterY"), waterY);
                gl.Uniform1(U(terrainProg, "uMaxY"), MapSpace.WorldY(r.MaxHeight));
                gl.Uniform1(U(terrainProg, "uHoleMode"), st.ShowHoles ? 1 : 0);
                gl.Uniform1(U(terrainProg, "uTileGrid"), st.TileGrid ? 1 : 0);
                gl.Uniform2(U(terrainProg, "uHover"), st.HoverX, st.HoverRow);
                gl.Uniform2(U(terrainProg, "uSelected"), st.SelectedX, st.SelectedRow);
                gl.Uniform3(U(terrainProg, "uBrush"), st.BrushX, st.BrushRow, st.BrushRadius);
                gl.Uniform4(U(terrainProg, "uRegion"), st.RegionX0, st.RegionRow0, st.RegionX1, st.RegionRow1);
                gl.Uniform1(U(terrainProg, "uRegionMargin"), st.RegionMargin);
                gl.Uniform1(U(terrainProg, "uRegionNorth"), st.RegionNorth);
                gl.Uniform1(U(terrainProg, "uHaveTextures"), haveTextures ? 1 : 0);
                gl.Uniform1(U(terrainProg, "uUvRotate"), st.UvRotate & 3);
                gl.Uniform1(U(terrainProg, "uUvFlipV"), st.UvFlipV ? 1 : 0);
                gl.Uniform1(U(terrainProg, "uHideUnseen"), st.HideUnseen ? 1 : 0);
                gl.Uniform1(U(terrainProg, "uShowTerrain"), st.ShowTerrain ? 1 : 0);
                gl.ActiveTexture(TextureUnit.Texture1);
                gl.BindTexture(TextureTarget.Texture2DArray, terrainTex);
                gl.Uniform1(U(terrainProg, "uTerrainTex"), 1);
                gl.ActiveTexture(TextureUnit.Texture2);
                gl.BindTexture(TextureTarget.Texture2D, lookupTex);
                gl.Uniform1(U(terrainProg, "uLookup"), 2);
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, pointsTex);
                gl.Uniform1(U(terrainProg, "uPoints"), 0);
                gl.BindVertexArray(gridVao);
                int originLoc = U(terrainProg, "uOrigin");
                for (int by = 0; by < r.Height - 1; by += BlockSize)
                    for (int bx = 0; bx < r.Width - 1; bx += BlockSize)
                    {
                        gl.Uniform2(originLoc, bx, by);
                        gl.DrawElements(PrimitiveType.Triangles, gridIndexCount, DrawElementsType.UnsignedInt, (void*)0);
                    }

                UploadGroup(st);
                gl.ActiveTexture(TextureUnit.Texture5);
                gl.BindTexture(TextureTarget.Texture2D, groupTex);
                gl.ActiveTexture(TextureUnit.Texture0);

                if (st.ShowModels && modelsReady && (st.ShowFixed || st.ShowNonfixed))
                {
                    gl.UseProgram(modelProg);
                    Matrix(modelProg, "uViewProj", vp);
                    gl.Uniform1(U(modelProg, "uGroup"), 5);
                    gl.Uniform1(U(modelProg, "uGroupRows"), groupRows);
                    gl.Uniform1(U(modelProg, "uSelected"), st.SelectedObject);
                    gl.Uniform1(U(modelProg, "uHover"), st.HoverObject);
                    gl.Uniform1(U(modelProg, "uSplit"), modelSplit);
                    gl.ActiveTexture(TextureUnit.Texture3);
                    gl.BindTexture(TextureTarget.Texture2DArray, modelTexA);
                    gl.Uniform1(U(modelProg, "uTexA"), 3);
                    gl.ActiveTexture(TextureUnit.Texture4);
                    gl.BindTexture(TextureTarget.Texture2DArray, modelTexB);
                    gl.Uniform1(U(modelProg, "uTexB"), 4);
                    gl.BindVertexArray(modelVao);
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
                    const uint stride = ModelBatch.InstanceFloats * 4;
                    foreach (var d in modelDraws)
                    {
                        long b = (long)d.InstFirst * stride;
                        for (uint c = 0; c < 4; c++) gl.VertexAttribPointer(5 + c, 4, VertexAttribPointerType.Float, false, stride, (void*)(b + c * 16));
                        gl.VertexAttribPointer(9, 1, VertexAttribPointerType.Float, false, stride, (void*)(b + 64));
                        gl.DrawArraysInstanced(PrimitiveType.Triangles, d.First, (uint)d.Count, (uint)d.InstCount);
                    }
                    gl.BindVertexArray(0);
                }

                bool onlyZones = st.ShowModels && modelsReady;
                if ((st.ShowFixed && markerFixed > 0 && !onlyZones) || (st.ShowNonfixed && markerNonfixed > 0))
                {
                    if (!gles) gl.Enable((EnableCap)0x8642);     // GL_PROGRAM_POINT_SIZE (always on in GLES)
                    gl.UseProgram(markerProg);
                    Matrix(markerProg, "uViewProj", vp);
                    gl.Uniform1(U(markerProg, "uPointScale"), (float)height * 0.6f);
                    gl.Uniform1(U(markerProg, "uHover"), st.HoverObject);
                    gl.Uniform1(U(markerProg, "uSelected"), st.SelectedObject);
                    gl.Uniform1(U(markerProg, "uGroup"), 5);
                    gl.Uniform1(U(markerProg, "uGroupRows"), groupRows);
                    gl.Uniform1(U(markerProg, "uOnlyZones"), onlyZones ? 1 : 0);
                    gl.BindVertexArray(markerVao);
                    if (st.ShowFixed && markerFixed > 0 && !onlyZones) gl.DrawArrays(PrimitiveType.Points, 0, (uint)markerFixed);
                    if (st.ShowNonfixed && markerNonfixed > 0) gl.DrawArrays(PrimitiveType.Points, markerFixed, (uint)markerNonfixed);
                }

                if (st.ShowWater)
                {
                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    gl.DepthMask(false);
                    gl.UseProgram(waterProg);
                    Matrix(waterProg, "uViewProj", vp);
                    gl.Uniform1(U(waterProg, "uWaterY"), waterY);
                    gl.BindVertexArray(waterVao);
                    gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
                    gl.DepthMask(true);
                    gl.Disable(EnableCap.Blend);
                }
                gl.BindVertexArray(0);
                gl.UseProgram(0);
                Error = null;
            }
            catch (Exception e)
            {
                Error = e.Message;
            }
        }

        int U(uint prog, string name)
        {
            if (!uniforms.TryGetValue((prog, name), out int loc))
                uniforms[(prog, name)] = loc = gl.GetUniformLocation(prog, name);
            return loc;
        }

        void Matrix(uint prog, string name, Matrix4x4 m) => gl.UniformMatrix4(U(prog, name), 1, false, (float*)&m);

        uint Program(string vs, string fs)
        {
            uint v = Shader(ShaderType.VertexShader, vs), f = Shader(ShaderType.FragmentShader, fs);
            uint p = gl.CreateProgram();
            gl.AttachShader(p, v); gl.AttachShader(p, f);
            gl.LinkProgram(p);
            gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(p));
            gl.DeleteShader(v); gl.DeleteShader(f);
            return p;
        }

        uint Shader(ShaderType type, string src)
        {
            uint s = gl.CreateShader(type);
            gl.ShaderSource(s, src);
            gl.CompileShader(s);
            gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(type + " failed: " + gl.GetShaderInfoLog(s));
            return s;
        }

        void UploadGroup(ViewState st)
        {
            if (st.GroupVersion == uploadedGroupVersion) return;
            uploadedGroupVersion = st.GroupVersion;
            var ids = st.Group;
            int max = 0;
            foreach (int id in ids) max = Math.Max(max, id);
            groupRows = ids.Count == 0 ? 0 : max / GroupWidth + 1;
            var bytes = new byte[GroupWidth * Math.Max(1, groupRows)];
            foreach (int id in ids) if (id >= 0) bytes[id] = 1;
            gl.BindTexture(TextureTarget.Texture2D, groupTex);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            fixed (byte* p = bytes)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8ui, GroupWidth, (uint)Math.Max(1, groupRows), 0, PixelFormat.RedInteger, PixelType.UnsignedByte, p);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }

        public void Dispose()
        {
            if (gl == null) return;
            gl.DeleteProgram(terrainProg); gl.DeleteProgram(waterProg); gl.DeleteProgram(markerProg);
            gl.DeleteVertexArray(markerVao); gl.DeleteBuffer(markerVbo);
            gl.DeleteProgram(modelProg); gl.DeleteVertexArray(modelVao); gl.DeleteBuffer(modelVbo); gl.DeleteBuffer(instVbo);
            gl.DeleteTexture(modelTexA); gl.DeleteTexture(modelTexB);
            modelsReady = false; modelDraws.Clear(); uploadedGeometry = -1;
            markerFixed = markerNonfixed = 0;
            gl.DeleteVertexArray(gridVao); gl.DeleteBuffer(gridVbo); gl.DeleteBuffer(gridEbo);
            gl.DeleteVertexArray(waterVao); gl.DeleteBuffer(waterVbo);
            gl.DeleteTexture(pointsTex); gl.DeleteTexture(terrainTex); gl.DeleteTexture(lookupTex); gl.DeleteTexture(groupTex);
            haveTextures = false;
            uniforms.Clear();
            texWidth = texHeight = 0;
            gl = null;
        }
    }
}
