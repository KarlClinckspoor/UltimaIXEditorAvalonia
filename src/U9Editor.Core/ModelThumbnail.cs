using System;
using System.Collections.Generic;
using System.Numerics;
using U9.Formats.Bitmaps;
using U9.Formats.Models;

namespace U9Editor.Core
{
    /// <summary>
    /// A small picture of a model, drawn on the CPU (no GL needed): orthographic, seen from the south-east and above
    /// (models face south), textured and lit, on a transparent background. For lists of pieces.
    /// </summary>
    public static class ModelThumbnail
    {
        /// <summary>
        /// The model as <paramref name="size"/>² RGBA pixels (row 0 at the top), or null when the record is not a
        /// model or has nothing to draw. <paramref name="readBitmap"/> reads a bitmap16.flx record (null: faces
        /// in their colours); <paramref name="textures"/> may be shared between calls.
        /// </summary>
        public static byte[] Render(byte[] model, Func<int, byte[]> readBitmap, int size,
                                    Dictionary<(int, int), BitmapFrame> textures = null, float azimuth = 30, float elevation = 35)
        {
            if (model == null || !SappearModel.IsModel(model) || size < 4) return null;
            List<ModelTriangle> tris;
            try { tris = SappearModel.Triangles(model, 0); }
            catch (System.IO.InvalidDataException) { return null; }
            tris.RemoveAll(t => t.Invisible);
            if (tris.Count == 0) return null;
            textures ??= new Dictionary<(int, int), BitmapFrame>();

            // the camera: from the south-east, above
            float az = azimuth * MathF.PI / 180, el = elevation * MathF.PI / 180;
            var toEye = new Vector3(MathF.Cos(el) * MathF.Sin(az), -MathF.Cos(el) * MathF.Cos(az), MathF.Sin(el));
            var forward = -toEye;
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
            var up = Vector3.Cross(right, forward);
            var light = Vector3.Normalize(toEye + new Vector3(-0.3f, 0.2f, 0.6f));

            // fit the projected corners into the picture, with a small margin
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var t in tris)
                foreach (var p in new[] { t.P0, t.P1, t.P2 })
                {
                    float sx = Vector3.Dot(p, right), sy = Vector3.Dot(p, up);
                    minX = Math.Min(minX, sx); maxX = Math.Max(maxX, sx); minY = Math.Min(minY, sy); maxY = Math.Max(maxY, sy);
                }
            int s2 = size * 2;                                          // drawn at twice the size, then averaged down
            float span = Math.Max(maxX - minX, maxY - minY);
            if (span <= 0) return null;
            float scale = s2 * 0.9f / span, cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            Vector3 Screen(Vector3 p) => new Vector3(s2 / 2f + (Vector3.Dot(p, right) - cx) * scale, s2 / 2f - (Vector3.Dot(p, up) - cy) * scale, Vector3.Dot(p, forward));

            var colour = new byte[s2 * s2 * 4];
            var depth = new float[s2 * s2];
            Array.Fill(depth, float.MaxValue);
            foreach (var t in tris)
            {
                var a = Screen(t.P0); var b = Screen(t.P1); var c = Screen(t.P2);
                float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (Math.Abs(area) < 1e-6f) continue;
                var n = Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0);
                float shade = n.LengthSquared() > 0 ? 0.35f + 0.65f * Math.Abs(Vector3.Dot(Vector3.Normalize(n), light)) : 0.7f;
                BitmapFrame tex = null;
                if (t.Texture >= 0 && readBitmap != null && !textures.TryGetValue((t.Texture, t.Frame), out tex))
                {
                    try
                    {
                        var rec = readBitmap(t.Texture);
                        if (rec != null && Bitmap16.FrameCount(rec) > 0) tex = Bitmap16.ReadFrame(rec, Math.Min(t.Frame, Bitmap16.FrameCount(rec) - 1));
                    }
                    catch (System.IO.InvalidDataException) { tex = null; }
                    textures[(t.Texture, t.Frame)] = tex;
                }
                float fr = (t.Colour & 0xFF) / 255f, fg = ((t.Colour >> 8) & 0xFF) / 255f, fb = ((t.Colour >> 16) & 0xFF) / 255f;
                int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X)))), x1 = Math.Min(s2 - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y)))), y1 = Math.Min(s2 - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                        float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                        float w2 = 1 - w0 - w1;
                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                        float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                        int k = y * s2 + x;
                        if (z >= depth[k]) continue;
                        float r = fr, g = fg, bl = fb;
                        if (tex != null && tex.Width > 0)
                        {
                            var uv = w0 * t.T0 + w1 * t.T1 + w2 * t.T2;
                            int tw = tex.Width, th = tex.Rgba.Length / 4 / tw;
                            int tx = ((int)MathF.Floor(uv.X * tw) % tw + tw) % tw, ty = ((int)MathF.Floor(uv.Y * th) % th + th) % th;
                            int q = (ty * tw + tx) * 4;
                            if (tex.Rgba[q + 3] < 128) continue;          // see-through texels
                            r *= tex.Rgba[q] / 255f; g *= tex.Rgba[q + 1] / 255f; bl *= tex.Rgba[q + 2] / 255f;
                        }
                        depth[k] = z;
                        colour[k * 4] = (byte)Math.Clamp(r * shade * 255, 0, 255);
                        colour[k * 4 + 1] = (byte)Math.Clamp(g * shade * 255, 0, 255);
                        colour[k * 4 + 2] = (byte)Math.Clamp(bl * shade * 255, 0, 255);
                        colour[k * 4 + 3] = 255;
                    }
            }
            // average each 2x2 block (colour weighted by coverage)
            var o = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int r = 0, g = 0, b = 0, cover = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int k = ((2 * y + dy) * s2 + 2 * x + dx) * 4;
                            if (colour[k + 3] == 0) continue;
                            r += colour[k]; g += colour[k + 1]; b += colour[k + 2]; cover++;
                        }
                    if (cover == 0) continue;
                    int q = (y * size + x) * 4;
                    o[q] = (byte)(r / cover); o[q + 1] = (byte)(g / cover); o[q + 2] = (byte)(b / cover); o[q + 3] = (byte)(cover * 255 / 4);
                }
            return o;
        }
    }
}
