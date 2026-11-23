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
        public bool ShowFixed = true, ShowNonfixed = true;
        /// <summary>Draw object models (and markers only for objects without one, plus link zones), or markers for all.</summary>
        public bool ShowModels = true;
        public bool ShowTerrain = true;
        /// <summary>Drop terrain quads with texture 29, which the game never shows.</summary>
        public bool HideUnseen = true;
        public int HoverObject = -1, SelectedObject = -1;
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
        ModelScene pendingModels, models;
        bool modelsReady;
        readonly List<(int First, int Count, int InstFirst, int InstCount)> modelDraws = new List<(int, int, int, int)>();
        int markerFixed, markerNonfixed;
        float[] pendingMarkers;
        int pendingFixed;
        bool gles;
        uint gridVao, gridVbo, gridEbo, waterVao, waterVbo;
        uint pointsTex, terrainTex, lookupTex;
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

        TerrainTextures textures;
        float[] markers;
        int markersFixed;

        /// <summary>The objects' models (null for none).</summary>
        public void SetModels(ModelScene m)
        {
            lock (gate) { models = m; pendingModels = m; }
        }

        void UploadModels(ModelScene m)
        {
            modelsReady = false;
            modelDraws.Clear();
            if (m == null || m.Batches.Count == 0) return;
            gl.BindVertexArray(modelVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, modelVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, m.Vertices, BufferUsageARB.StaticDraw);
            int[] sizes = { 3, 3, 2, 1, 4 };
            for (uint i = 0, off = 0; i < sizes.Length; off += (uint)sizes[i], i++)
            {
                gl.EnableVertexAttribArray(i);
                gl.VertexAttribPointer(i, sizes[i], VertexAttribPointerType.Float, false, ModelScene.VertexFloats * 4, (void*)(off * 4));
            }
            var inst = new List<float>();
            foreach (var b in m.Batches)
            {
                modelDraws.Add((b.FirstVertex, b.VertexCount, inst.Count / ModelBatch.InstanceFloats, b.InstanceCount));
                inst.AddRange(b.Instances);
            }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, inst.ToArray(), BufferUsageARB.StaticDraw);
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
            if (objects != null)
            {
                f = new float[objects.All.Count * 5];
                int k = 0;
                foreach (var o in objects.All)
                {
                    if (o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed) nFixed++;
                    f[k++] = o.World.X; f[k++] = o.World.Y; f[k++] = o.World.Z;
                    f[k++] = o.Kind == U9.Formats.Objects.ObjectFileKind.Fixed ? 0 : o.IsLinkZone ? 2 : 1;
                    f[k++] = o.Id;
                }
            }
            lock (gate) { markers = f; markersFixed = nFixed; pendingMarkers = f; pendingFixed = nFixed; }
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
                if (pendingModels != null) { UploadModels(pendingModels); pendingModels = null; }
                if (pendingMarkers != null)
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
                    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, pendingMarkers, BufferUsageARB.StaticDraw);
                    markerFixed = pendingFixed;
                    markerNonfixed = pendingMarkers.Length / 5 - pendingFixed;
                    pendingMarkers = null;
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

                if (st.ShowModels && modelsReady && (st.ShowFixed || st.ShowNonfixed))
                {
                    gl.UseProgram(modelProg);
                    Matrix(modelProg, "uViewProj", vp);
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

        public void Dispose()
        {
            if (gl == null) return;
            gl.DeleteProgram(terrainProg); gl.DeleteProgram(waterProg); gl.DeleteProgram(markerProg);
            gl.DeleteVertexArray(markerVao); gl.DeleteBuffer(markerVbo);
            gl.DeleteProgram(modelProg); gl.DeleteVertexArray(modelVao); gl.DeleteBuffer(modelVbo); gl.DeleteBuffer(instVbo);
            gl.DeleteTexture(modelTexA); gl.DeleteTexture(modelTexB);
            modelsReady = false; modelDraws.Clear();
            markerFixed = markerNonfixed = 0;
            gl.DeleteVertexArray(gridVao); gl.DeleteBuffer(gridVbo); gl.DeleteBuffer(gridEbo);
            gl.DeleteVertexArray(waterVao); gl.DeleteBuffer(waterVbo);
            gl.DeleteTexture(pointsTex); gl.DeleteTexture(terrainTex); gl.DeleteTexture(lookupTex);
            haveTextures = false;
            uniforms.Clear();
            texWidth = texHeight = 0;
            gl = null;
        }
    }
}
