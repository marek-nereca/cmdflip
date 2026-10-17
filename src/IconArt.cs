using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Cmdflip
{
    // Draws the app icon: a rounded tile with a ⌘ (place of interest sign) built from plain
    // geometry, so no font is involved. Used for the tray icon and by tools\make-icon.ps1.
    public static class IconArt
    {
        public static Bitmap Render(int size, bool enabled)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                var back = enabled ? Color.FromArgb(255, 32, 32, 36) : Color.FromArgb(255, 120, 120, 120);
                using (var brush = new SolidBrush(back))
                using (var tile = RoundedRect(new RectangleF(0, 0, size, size), size * 0.22f))
                    g.FillPath(brush, tile);

                // Stroke gets relatively thicker at small sizes so the loops stay readable.
                float stroke = Math.Max(size * 0.08f, size <= 24 ? 1.6f : 1f);
                float halfExtent = size * 0.31f - stroke / 2;
                using (var pen = new Pen(Color.White, stroke) { LineJoin = LineJoin.Round })
                using (var glyph = CommandSign(size / 2f, size / 2f, halfExtent))
                    g.DrawPath(pen, glyph);
            }
            return bmp;
        }

        // ⌘ centred on (cx, cy). The inner square has half-size a; each corner loop is a circle of
        // radius r tangent to the extended sides. The whole sign is one closed stroke: four straight
        // sides joined by four 270° arcs. Overall half-extent = a + 2r.
        static GraphicsPath CommandSign(float cx, float cy, float halfExtent)
        {
            float r = halfExtent / 3f;
            float a = r;
            float o = a + r; // distance from centre to each loop's centre (per axis)
            float d = 2 * r;
            var p = new GraphicsPath();
            // GDI+ angles run clockwise from +x; negative sweeps go counter-clockwise on screen.
            p.AddArc(cx + o - r, cy - o - r, d, d, 90, -270);   // top-right loop: bottom -> left
            p.AddArc(cx + o - r, cy + o - r, d, d, 180, -270);  // bottom-right loop: left -> top
            p.AddArc(cx - o - r, cy + o - r, d, d, 270, -270);  // bottom-left loop: top -> right
            p.AddArc(cx - o - r, cy - o - r, d, d, 0, -270);    // top-left loop: right -> bottom
            p.CloseFigure();
            return p;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
