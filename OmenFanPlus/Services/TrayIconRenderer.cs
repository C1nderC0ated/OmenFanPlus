using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace OmenFanPlus.Services
{
    /// <summary>Draws the tray icon that shows a temperature: white digits on a rounded badge in the app's purple.</summary>
    internal static class TrayIconRenderer
    {
        /// <summary>The purple of the app icon.</summary>
        public static readonly Color Accent = Color.FromArgb(150, 24, 235);

        /// <summary>A square icon of the given pixel size showing the temperature (°C, rounded).</summary>
        public static Bitmap Render(float temperature, int size)
        {
            if (size < 8)
                throw new ArgumentOutOfRangeException(nameof(size), size, "too small for digits");

            var text = Math.Clamp((int)Math.Round(temperature), 0, 999).ToString(CultureInfo.InvariantCulture);
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(bitmap);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (var path = RoundedRectangle(new RectangleF(0, 0, size, size), size / 4f))
                using (var brush = new SolidBrush(Accent))
                    g.FillPath(brush, path);

                using var font = FitFont(g, text, maxWidth: size - 1, maxHeight: size);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                // A hair up: digits sit visually low in their box.
                g.DrawString(text, font, Brushes.White, new RectangleF(0, -size * 0.04f, size, size), format);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        // The largest bold font (in pixels) at which the text fits the badge, with a hair of margin.
        private static Font FitFont(Graphics g, string text, float maxWidth, float maxHeight)
        {
            for (var px = maxHeight * 0.95f; px > 4; px -= 0.5f)
            {
                var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
                var measured = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
                if (measured.Width <= maxWidth && measured.Height <= maxHeight * 1.1f)
                    return font;
                font.Dispose();
            }
            return new Font("Segoe UI", 4, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        private static GraphicsPath RoundedRectangle(RectangleF r, float radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
