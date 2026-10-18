using System.Drawing.Drawing2D;

namespace OmenFanPlus.Forms
{
    /// <summary>Draws the tray icon's right-click menu in the app's dark colours.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public static readonly Color Background = Color.FromArgb(32, 32, 32);
        public static readonly Color Text = Color.FromArgb(230, 230, 230);
        private static readonly Color DisabledText = Color.FromArgb(128, 128, 128);
        private static readonly Color Hover = Color.FromArgb(64, 64, 64);
        private static readonly Color Border = Color.FromArgb(58, 58, 58);

        public DarkMenuRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        /// <summary>Applies the renderer and colours to a menu.</summary>
        public static void Apply(ToolStrip menu)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = Background;
            menu.ForeColor = Text;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : DisabledText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        // The default check glyph is black; draw a light one instead.
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            var g = e.Graphics;
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(e.Item.Enabled ? Text : DisabledText, Math.Max(1.5f, r.Height / 9f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.80f, r.Top + r.Height * 0.30f),
            });
            g.SmoothingMode = oldMode;
        }

        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Background;
            public override Color ImageMarginGradientBegin => Background;
            public override Color ImageMarginGradientMiddle => Background;
            public override Color ImageMarginGradientEnd => Background;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientMiddle => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Background;
            public override Color CheckBackground => Background;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }
    }
}
