using OmenFanPlus.Models;
using OmenFanPlus.Services;
using OmenHubLight.Lib;
using System.Drawing.Drawing2D;

namespace OmenFanPlus.Forms
{
    public class FormFanCurve : Form
    {
        private FanCurveProfile _profile;
        private CurvePanel cpuPanel = null!;
        private CurvePanel gpuPanel = null!;
        private Button btnSave = null!;
        private Button btnCancel = null!;
        private Button btnReset = null!;
        private TabControl tabControl = null!;
        private System.Windows.Forms.Timer _liveTimer = null!;
        private readonly FanModeController _fanModes;

        public FanCurveProfile Result => _profile;

        internal FormFanCurve(FanCurveProfile profile, FanModeController fanModes)
        {
            _fanModes = fanModes;
            _profile = new FanCurveProfile
            {
                CpuCurve = profile.CpuCurve.Select(p => new FanCurvePoint(p.Temperature, p.FanSpeed)).ToList(),
                GpuCurve = profile.GpuCurve.Select(p => new FanCurvePoint(p.Temperature, p.FanSpeed)).ToList()
            };
            InitializeLayout();
            StartLiveUpdates();
        }

        private void StartLiveUpdates()
        {
            _liveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _liveTimer.Tick += (s, e) =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        var temperatures = TemperatureService.Read();
                        float cpuTemp = temperatures.Cpu ?? 0f;
                        float gpuTemp = temperatures.Gpu ?? 0f;

                        List<int> fanLevels = _fanModes.GetFanLevels();
                        int cpuFanPct = fanLevels.Count > 0 ? fanLevels[0] : 0;
                        int gpuFanPct = fanLevels.Count > 1 ? fanLevels[1] : cpuFanPct;

                        if (!IsDisposed)
                        {
                            Invoke(() =>
                            {
                                cpuPanel.SetLiveData(cpuTemp, cpuFanPct);
                                gpuPanel.SetLiveData(gpuTemp, gpuFanPct);
                            });
                        }
                    }
                    catch { }
                });
            };
            _liveTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _liveTimer?.Stop();
            _liveTimer?.Dispose();
            base.OnFormClosed(e);
        }

        private void InitializeLayout()
        {
            Text = "Fan Curve Editor";
            Size = new Size(460, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(23, 23, 23);
            ForeColor = Color.FromArgb(230, 230, 230);
            TopMost = true;

            tabControl = new TabControl
            {
                Dock = DockStyle.Top,
                Height = 380,
                BackColor = Color.FromArgb(23, 23, 23)
            };

            var cpuTab = new TabPage("CPU Fan") { BackColor = Color.FromArgb(30, 30, 30) };
            cpuPanel = new CurvePanel(_profile.CpuCurve) { Dock = DockStyle.Fill };
            cpuTab.Controls.Add(cpuPanel);

            var gpuTab = new TabPage("GPU Fan") { BackColor = Color.FromArgb(30, 30, 30) };
            gpuPanel = new CurvePanel(_profile.GpuCurve) { Dock = DockStyle.Fill };
            gpuTab.Controls.Add(gpuPanel);

            tabControl.TabPages.Add(cpuTab);
            tabControl.TabPages.Add(gpuTab);
            Controls.Add(tabControl);

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 45,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5),
                BackColor = Color.FromArgb(23, 23, 23)
            };

            btnSave = CreateButton("Save");
            btnSave.Click += (s, e) =>
            {
                _profile.CpuCurve = cpuPanel.GetPoints();
                _profile.GpuCurve = gpuPanel.GetPoints();
                DialogResult = DialogResult.OK;
                Close();
            };

            btnCancel = CreateButton("Cancel");
            btnCancel.Click += (s, e) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            btnReset = CreateButton("Reset");
            btnReset.Click += (s, e) =>
            {
                var def = FanCurveProfile.CreateDefault();
                cpuPanel.SetPoints(def.CpuCurve);
                gpuPanel.SetPoints(def.GpuCurve);
            };

            buttonPanel.Controls.Add(btnSave);
            buttonPanel.Controls.Add(btnCancel);
            buttonPanel.Controls.Add(btnReset);
            Controls.Add(buttonPanel);
        }

        private Button CreateButton(string text)
        {
            return new Button
            {
                Text = text,
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(230, 230, 230),
                BackColor = Color.FromArgb(64, 64, 64),
                FlatAppearance = { BorderSize = 0 }
            };
        }
    }

    internal class CurvePanel : Panel
    {
        private List<FanCurvePoint> _points;
        private int _dragIndex = -1;
        private const int GraphMargin = 40;
        private ToolTip _tooltip = new();
        private float _liveTemp = -1;
        private int _liveFanSpeed = -1;

        public CurvePanel(List<FanCurvePoint> points)
        {
            _points = points.OrderBy(p => p.Temperature).ToList();
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        public void SetLiveData(float temperature, int fanSpeed)
        {
            _liveTemp = temperature;
            _liveFanSpeed = fanSpeed;
            Invalidate();
        }

        public List<FanCurvePoint> GetPoints() => _points.OrderBy(p => p.Temperature).ToList();

        public void SetPoints(List<FanCurvePoint> points)
        {
            _points = points.OrderBy(p => p.Temperature).ToList();
            Invalidate();
        }

        private RectangleF GraphArea => new(
            GraphMargin, 10,
            Width - GraphMargin - 15,
            Height - GraphMargin - 15
        );

        private PointF TempSpeedToScreen(int temp, int speed)
        {
            var g = GraphArea;
            float x = g.X + (temp / 100f) * g.Width;
            float y = g.Y + g.Height - (speed / 100f) * g.Height;
            return new PointF(x, y);
        }

        private (int temp, int speed) ScreenToTempSpeed(float x, float y)
        {
            var g = GraphArea;
            int temp = (int)Math.Clamp((x - g.X) / g.Width * 100, 0, 100);
            int speed = (int)Math.Clamp((1 - (y - g.Y) / g.Height) * 100, 0, 100);
            return (temp, speed);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var area = GraphArea;

            using var gridPen = new Pen(Color.FromArgb(50, 255, 255, 255), 1);
            using var axisPen = new Pen(Color.FromArgb(120, 255, 255, 255), 1);
            using var curvePen = new Pen(Color.FromArgb(0, 180, 220), 2);
            using var fillBrush = new SolidBrush(Color.FromArgb(30, 0, 180, 220));
            using var pointBrush = new SolidBrush(Color.FromArgb(0, 200, 240));
            using var textBrush = new SolidBrush(Color.FromArgb(180, 180, 180));
            using var font = new Font("Segoe UI", 8f);

            // Grid lines
            for (int i = 0; i <= 10; i++)
            {
                float xPos = area.X + (i / 10f) * area.Width;
                float yPos = area.Y + (i / 10f) * area.Height;
                g.DrawLine(gridPen, xPos, area.Y, xPos, area.Bottom);
                g.DrawLine(gridPen, area.X, yPos, area.Right, yPos);
            }

            // Axis labels
            for (int i = 0; i <= 100; i += 20)
            {
                var pt = TempSpeedToScreen(i, 0);
                g.DrawString($"{i}°", font, textBrush, pt.X - 10, area.Bottom + 3);
                var pt2 = TempSpeedToScreen(0, i);
                g.DrawString($"{i}%", font, textBrush, 2, pt2.Y - 6);
            }

            g.DrawLine(axisPen, area.X, area.Bottom, area.Right, area.Bottom);
            g.DrawLine(axisPen, area.X, area.Y, area.X, area.Bottom);

            // Curve
            var sorted = _points.OrderBy(p => p.Temperature).ToList();
            if (sorted.Count >= 2)
            {
                var screenPts = sorted.Select(p => TempSpeedToScreen(p.Temperature, p.FanSpeed)).ToArray();

                // Fill under curve
                var fillPts = new List<PointF>(screenPts);
                fillPts.Add(new PointF(screenPts[^1].X, area.Bottom));
                fillPts.Add(new PointF(screenPts[0].X, area.Bottom));
                g.FillPolygon(fillBrush, fillPts.ToArray());

                g.DrawLines(curvePen, screenPts);
            }

            // Points
            foreach (var p in sorted)
            {
                var pt = TempSpeedToScreen(p.Temperature, p.FanSpeed);
                g.FillEllipse(pointBrush, pt.X - 5, pt.Y - 5, 10, 10);
            }

            // Live indicator
            if (_liveTemp >= 0)
            {
                int clampedTemp = (int)Math.Clamp(_liveTemp, 0, 100);
                int clampedFan = Math.Clamp(_liveFanSpeed, 0, 100);
                var livePt = TempSpeedToScreen(clampedTemp, clampedFan);

                using var livePenV = new Pen(Color.FromArgb(80, 255, 80, 80), 1) { DashStyle = DashStyle.Dash };
                using var livePenH = new Pen(Color.FromArgb(80, 80, 255, 80), 1) { DashStyle = DashStyle.Dash };
                // Vertical line at current temp
                g.DrawLine(livePenV, livePt.X, area.Y, livePt.X, area.Bottom);
                // Horizontal line at current fan speed
                g.DrawLine(livePenH, area.X, livePt.Y, area.Right, livePt.Y);

                // Dot at intersection
                using var liveDotBrush = new SolidBrush(Color.FromArgb(255, 220, 60));
                g.FillEllipse(liveDotBrush, livePt.X - 6, livePt.Y - 6, 12, 12);

                // Label
                using var liveFont = new Font("Segoe UI", 8f, FontStyle.Bold);
                using var liveLabelBrush = new SolidBrush(Color.FromArgb(255, 220, 60));
                string label = $"{_liveTemp:F0}° / {_liveFanSpeed}%";
                g.DrawString(label, liveFont, liveLabelBrush, livePt.X + 10, livePt.Y - 18);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                // Right-click on a point: delete it (keep min 2 points)
                int idx = HitTest(e.X, e.Y);
                if (idx >= 0 && _points.Count > 2)
                {
                    _points.RemoveAt(idx);
                    Invalidate();
                    return;
                }
            }

            if (e.Button == MouseButtons.Left)
            {
                int idx = HitTest(e.X, e.Y);
                if (idx >= 0)
                {
                    _dragIndex = idx;
                }
                else
                {
                    // Add new point
                    var (temp, speed) = ScreenToTempSpeed(e.X, e.Y);
                    _points.Add(new FanCurvePoint(temp, speed));
                    _points = _points.OrderBy(p => p.Temperature).ToList();
                    _dragIndex = _points.FindIndex(p => p.Temperature == temp && p.FanSpeed == speed);
                    Invalidate();
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragIndex >= 0 && e.Button == MouseButtons.Left)
            {
                var (temp, speed) = ScreenToTempSpeed(e.X, e.Y);
                _points[_dragIndex] = new FanCurvePoint(temp, speed);
                _tooltip.Show($"{temp}° → {speed}%", this, e.X + 15, e.Y - 15, 1000);
                Invalidate();
            }
            else
            {
                int idx = HitTest(e.X, e.Y);
                Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragIndex >= 0)
            {
                _points = _points.OrderBy(p => p.Temperature).ToList();
                _dragIndex = -1;
            }
        }

        private int HitTest(int x, int y)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                var pt = TempSpeedToScreen(_points[i].Temperature, _points[i].FanSpeed);
                if (Math.Abs(pt.X - x) < 8 && Math.Abs(pt.Y - y) < 8)
                    return i;
            }
            return -1;
        }
    }
}
