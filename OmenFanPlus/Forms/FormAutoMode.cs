using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenFanPlus.Lib;
using OmenFanPlus.Properties;
using OmenFanPlus.Services;
using Timer = System.Windows.Forms.Timer;

namespace OmenFanPlus.Forms
{
    /// <summary>Thresholds for the Auto mode, with a live preview of what they would pick right now.</summary>
    internal sealed class FormAutoMode : Form
    {
        private static readonly Color Background = Color.FromArgb(23, 23, 23);
        private static readonly Color TextColor = Color.FromArgb(230, 230, 230);
        private static readonly Color Muted = Color.FromArgb(160, 160, 160);
        private static readonly Color Field = Color.FromArgb(64, 64, 64);

        private readonly FanModeController _fanModes;
        private readonly NumericUpDown _comfortBelow;
        private readonly NumericUpDown _performanceAbove;
        private readonly NumericUpDown _hysteresis;
        private readonly Label _preview;
        private readonly Label _error;
        private readonly Timer _previewTimer = new() { Interval = 2000 };
        private int _previewBusy;

        public FormAutoMode(FanModeController fanModes)
        {
            _fanModes = fanModes;
            var calmName = FanModeController.DisplayName(fanModes.Capabilities.QuietOrCoolMode);

            SuspendLayout();
            // Laid out in 96-dpi pixels; WinForms scales everything for the screen's scaling.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "OmenFanPlus - Auto mode";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            BackColor = Background;
            ForeColor = TextColor;
            ClientSize = new Size(440, 312);
            Font = new Font("Segoe UI", 9F);

            var intro = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(16, 12, 408, 72),
                ForeColor = Muted,
                Text = $"Auto picks the thermal profile from the hottest of CPU and GPU: {calmName} when cool, " +
                       "Default in between, Performance when hot. It steps up as soon as a threshold is reached and " +
                       "steps down once the temperature has fallen by the hysteresis. On battery it never picks Performance.",
            };
            Controls.Add(intro);

            _comfortBelow = AddField(96, $"{calmName} below", "°C", AutoProfileSelector.MinThreshold, AutoProfileSelector.MaxThreshold, Settings.Default.AutoComfortBelow);
            _performanceAbove = AddField(128, "Performance at or above", "°C", AutoProfileSelector.MinThreshold, AutoProfileSelector.MaxThreshold, Settings.Default.AutoPerformanceAbove);
            _hysteresis = AddField(160, "Hysteresis", "°C", AutoProfileSelector.MinHysteresis, AutoProfileSelector.MaxHysteresis, Settings.Default.AutoHysteresis);

            _error = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(16, 192, 408, 34),
                ForeColor = Color.FromArgb(255, 170, 60),
            };
            Controls.Add(_error);

            _preview = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(16, 232, 408, 22),
                ForeColor = TextColor,
                Text = "Now: reading...",
            };
            Controls.Add(_preview);

            var ok = MakeButton("OK", new Point(344, 266));
            ok.Click += (s, e) => Save();
            var cancel = MakeButton("Cancel", new Point(256, 266));
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            var defaults = MakeButton("Defaults", new Point(16, 266));
            defaults.Click += (s, e) =>
            {
                _comfortBelow.Value = 55;
                _performanceAbove.Value = 80;
                _hysteresis.Value = 5;
            };
            AcceptButton = ok;
            CancelButton = cancel;

            foreach (var field in new[] { _comfortBelow, _performanceAbove, _hysteresis })
                field.ValueChanged += (s, e) => CheckThresholds();
            CheckThresholds();

            _previewTimer.Tick += (s, e) => UpdatePreview();
            _previewTimer.Start();
            UpdatePreview();
            ResumeLayout(false);
            PerformLayout();
        }

        private NumericUpDown AddField(int top, string caption, string unit, int min, int max, int value)
        {
            Controls.Add(new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(16, top + 3, 220, 22),
                ForeColor = TextColor,
                Text = caption,
            });
            var field = new NumericUpDown
            {
                Bounds = new Rectangle(244, top, 70, 24),
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                BackColor = Field,
                ForeColor = TextColor,
                BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(field);
            Controls.Add(new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(320, top + 3, 40, 22),
                ForeColor = Muted,
                Text = unit,
            });
            return field;
        }

        private Button MakeButton(string text, Point location)
        {
            var button = new Button
            {
                Text = text,
                Location = location,
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                ForeColor = TextColor,
                BackColor = Field,
                FlatAppearance = { BorderSize = 0 },
            };
            Controls.Add(button);
            return button;
        }

        private bool CheckThresholds()
        {
            var valid = AutoProfileSelector.TryValidate((int)_comfortBelow.Value, (int)_performanceAbove.Value,
                (int)_hysteresis.Value, out var error);
            _error.Text = error ?? "";
            return valid;
        }

        private void UpdatePreview()
        {
            if (!CheckThresholds() || Interlocked.Exchange(ref _previewBusy, 1) == 1)
                return;

            var comfort = (int)_comfortBelow.Value;
            var performance = (int)_performanceAbove.Value;
            var hysteresis = (int)_hysteresis.Value;
            var calm = _fanModes.Capabilities.QuietOrCoolMode;

            Task.Run(() =>
            {
                try
                {
                    var t = TemperatureService.Read();
                    var onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
                    string text;
                    if (t.Cpu is null && t.Gpu is null)
                    {
                        text = "Now: no temperature reading";
                    }
                    else
                    {
                        var hottest = Math.Max(t.Cpu ?? float.MinValue, t.Gpu ?? float.MinValue);
                        var pick = new AutoProfileSelector(comfort, performance, hysteresis, calm).Band(hottest, onBattery);
                        text = $"Now: CPU {Fmt(t.Cpu)}, GPU {Fmt(t.Gpu)} → {FanModeController.DisplayName(pick)}" +
                               (onBattery ? " (on battery)" : "");
                    }

                    if (!IsDisposed && IsHandleCreated)
                        BeginInvoke(() => _preview.Text = text);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                }
                finally
                {
                    Interlocked.Exchange(ref _previewBusy, 0);
                }
            });
        }

        private static string Fmt(float? value) => value is float v ? $"{v:0} °C" : "n/a";

        private void Save()
        {
            if (!CheckThresholds())
                return;

            Settings.Default.AutoComfortBelow = (int)_comfortBelow.Value;
            Settings.Default.AutoPerformanceAbove = (int)_performanceAbove.Value;
            Settings.Default.AutoHysteresis = (int)_hysteresis.Value;
            FanModeController.TrySave();
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _previewTimer.Stop();
            _previewTimer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
