using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace U9Editor.App
{
    /// <summary>
    /// A table of numbers drawn as a heat map, with row names on the left and column names turned upright on top; both
    /// stay in view while it scrolls (put it in a ScrollViewer and call <see cref="Follow"/>). Raises
    /// <see cref="Hovered"/> with the cell under the mouse.
    /// </summary>
    public sealed class MatrixView : Control
    {
        public string[] Rows = Array.Empty<string>();
        public string[] Columns = Array.Empty<string>();
        /// <summary>The values; NaN draws an empty cell, +inf "never".</summary>
        public double[,] Values = new double[0, 0];
        /// <summary>True when a high number is good for the player (the hits a creature needs to kill the Avatar).</summary>
        public bool HighIsGood;
        public int MarkedColumn = -1;
        public event Action<int, int> Hovered;

        const double CellW = 40, CellH = 19;
        double rowHeaderW = 160, colHeaderH = 90;
        Vector offset;
        int hoverR = -1, hoverC = -1;
        static readonly Typeface Face = new Typeface("Segoe UI");

        public MatrixView() { ClipToBounds = true; }

        /// <summary>Keeps the headers in view as the ScrollViewer moves.</summary>
        public void Follow(ScrollViewer sv) => sv.ScrollChanged += (s, e) => { offset = sv.Offset; InvalidateVisual(); };

        FormattedText Text(string s, double size, IBrush brush, FontWeight w = FontWeight.Normal) =>
            new FormattedText(s ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Face.FontFamily, FontStyle.Normal, w), size, brush);

        /// <summary>Call after changing the rows, columns or values.</summary>
        public void Refresh()
        {
            rowHeaderW = 60;
            foreach (var r in Rows) rowHeaderW = Math.Max(rowHeaderW, Text(r, 11, Brushes.Black).Width + 12);
            colHeaderH = 40;
            foreach (var c in Columns) colHeaderH = Math.Max(colHeaderH, Text(c, 11, Brushes.Black).Width + 12);
            rowHeaderW = Math.Min(rowHeaderW, 320);
            colHeaderH = Math.Min(colHeaderH, 170);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new Size(rowHeaderW + CellW * Columns.Length + 2, colHeaderH + CellH * Rows.Length + 2);

        /// <summary>Green for good, red for bad: hits on a log scale from 1 to 20.</summary>
        public static Color ColourOf(double hits, bool highIsGood)
        {
            if (double.IsNaN(hits)) return Color.FromRgb(245, 245, 245);
            double t = double.IsInfinity(hits) ? 1 : Math.Clamp(Math.Log(Math.Max(1, hits)) / Math.Log(20), 0, 1);
            if (highIsGood) t = 1 - t;
            // hue 120 (green) .. 0 (red), pale enough for black text
            double h = 120 * (1 - t) / 60.0;
            int i = (int)Math.Floor(h);
            double f = h - i, v = 0.95, s = 0.45;
            double p = v * (1 - s), q = v * (1 - s * f), u = v * (1 - s * (1 - f));
            var (r, g, b) = i switch { 0 => (v, u, p), 1 => (q, v, p), _ => (p, v, u) };
            return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        public static string Format(double hits) =>
            double.IsNaN(hits) ? "" : double.IsInfinity(hits) ? "never" : hits >= 100 ? ">99" : hits < 10 ? hits.ToString("0.0") : hits.ToString("0");

        public override void Render(DrawingContext g)
        {
            var bounds = new Rect(Bounds.Size);
            g.FillRectangle(Brushes.White, bounds);
            int nr = Rows.Length, nc = Columns.Length;
            if (nr == 0 || nc == 0)
            {
                g.DrawText(Text("Nothing to show: check the filters on the left.", 12, Brushes.Gray), new Point(10, 10));
                return;
            }
            var grid = new Pen(new SolidColorBrush(Color.FromRgb(220, 220, 220)), 1);
            for (int r = 0; r < nr; r++)
            {
                double y = colHeaderH + r * CellH;
                if (y + CellH < offset.Y + colHeaderH || y > offset.Y + Bounds.Height + 40) continue;
                for (int c = 0; c < nc; c++)
                {
                    double x = rowHeaderW + c * CellW;
                    double v = Values[r, c];
                    var cell = new Rect(x, y, CellW, CellH);
                    g.FillRectangle(new SolidColorBrush(ColourOf(v, HighIsGood)), cell);
                    g.DrawRectangle(grid, cell);
                    var t = Text(Format(v), 11, Brushes.Black, c == MarkedColumn ? FontWeight.Bold : FontWeight.Normal);
                    g.DrawText(t, new Point(x + (CellW - t.Width) / 2, y + (CellH - t.Height) / 2));
                }
            }
            if (MarkedColumn >= 0 && MarkedColumn < nc)
                g.DrawRectangle(new Pen(Brushes.Black, 2), new Rect(rowHeaderW + MarkedColumn * CellW, colHeaderH, CellW, CellH * nr));
            if (hoverR >= 0 && hoverC >= 0)
                g.DrawRectangle(new Pen(Brushes.RoyalBlue, 2), new Rect(rowHeaderW + hoverC * CellW, colHeaderH + hoverR * CellH, CellW, CellH));

            // Row names, kept at the left edge of the view.
            double ox = offset.X, oy = offset.Y;
            g.FillRectangle(Brushes.White, new Rect(ox, colHeaderH, rowHeaderW, CellH * nr));
            for (int r = 0; r < nr; r++)
            {
                double y = colHeaderH + r * CellH;
                var t = Text(Rows[r], 11, r == hoverR ? Brushes.RoyalBlue : Brushes.Black);
                using (g.PushClip(new Rect(ox, y, rowHeaderW - 4, CellH)))
                    g.DrawText(t, new Point(ox + 4, y + (CellH - t.Height) / 2));
            }
            // Column names, upright, kept at the top.
            g.FillRectangle(Brushes.White, new Rect(rowHeaderW, oy, CellW * nc, colHeaderH));
            for (int c = 0; c < nc; c++)
            {
                double x = rowHeaderW + c * CellW;
                var t = Text(Columns[c], 11, c == hoverC ? Brushes.RoyalBlue : Brushes.Black, c == MarkedColumn ? FontWeight.Bold : FontWeight.Normal);
                var m = Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(x + (CellW - t.Height) / 2, oy + colHeaderH - 4);
                using (g.PushTransform(m)) g.DrawText(t, new Point(0, 0));
            }
            g.FillRectangle(Brushes.White, new Rect(ox, oy, rowHeaderW, colHeaderH));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            int c = p.X < rowHeaderW + offset.X ? -1 : (int)((p.X - rowHeaderW) / CellW);
            int r = p.Y < colHeaderH + offset.Y ? -1 : (int)((p.Y - colHeaderH) / CellH);
            if (c >= Columns.Length) c = -1;
            if (r >= Rows.Length) r = -1;
            if (r == hoverR && c == hoverC) return;
            hoverR = r; hoverC = c;
            InvalidateVisual();
            Hovered?.Invoke(r, c);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            hoverR = hoverC = -1;
            InvalidateVisual();
        }
    }
}
