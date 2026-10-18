using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenHubLight.Lib;
using OmenFanPlus.Models;
using OmenFanPlus.Properties;
using OmenFanPlus.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Timer = System.Windows.Forms.Timer;

namespace OmenFanPlus.Forms
{
    public partial class FormPopup : Form
    {
        private readonly FanModeController _fanModes;
        private Button? activeButton;
        private Timer focusTimer = new Timer();
        public bool AppIsExiting = false;
        private Timer updateTextTimer = new Timer();
        private FanCurveProfile _fanCurveProfile;
        private Timer _curveTimer = new Timer();
        private readonly int _bottomMargin;
        private bool? _manualLayout;
        private int _refreshBusy;
        private int _curveBusy;
        private readonly ToolTip _temperatureToolTip = new();
        private readonly ToolTip _modeToolTip = new() { AutoPopDelay = 15000 };

        internal FormPopup(FanModeController fanModes)
        {
            _fanModes = fanModes;
            InitializeComponent();

            _fanCurveProfile = FanCurveProfile.Load();
            _bottomMargin = ClientSize.Height - OffButton.Bottom;

            // The fan label doubles as a status line, so give it the width of the button row.
            fanList.AutoSize = false;
            fanList.SetBounds(DefaultButton.Left, fanList.Top, PerformanceButton.Right - DefaultButton.Left, fanList.Height);

            focusTimer.Tick += delegate
            {
                if (ApplicationIsActivated())
                    focusTimer.Stop();
                else
                    Activate();
            };
            focusTimer.Interval = 10;
            updateTextTimer.Interval = 500;
            updateTextTimer.Tick += UpdateTempInfo;

            _curveTimer.Interval = 2000;
            _curveTimer.Tick += CurveTimer_Tick;

            ApplyCapabilities();
            ShowSavedMode();
            UpdateFanSpeedText(new List<int>());
            if (Settings.Default.fanCustom && _fanModes.Capabilities.ManualFanControlSupported)
                StartCurveTimer();

            // Status changes arrive from timer and power-event threads; BeginInvoke needs a window handle.
            CreateHandle();
            _fanModes.StatusChanged += FanModes_StatusChanged;
        }

        private void FanModes_StatusChanged(object? sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            BeginInvoke(() =>
            {
                ApplyCapabilities();
                ShowSavedMode();
                if (!_fanModes.HasFanSpeeds)
                    fanList.Text = _fanModes.Status;

                // Capabilities can arrive late (BIOS retry after logon), so the curve may need starting now.
                if (Settings.Default.fanCustom && _fanModes.Capabilities.ManualFanControlSupported)
                    StartCurveTimer();
                else
                    StopCurveTimer();
            });
        }

        /// <summary>
        /// Only show the modes this laptop's BIOS supports. Boards without manual fan control (e.g. the
        /// OMEN 15-dc1xxx) get Default / Performance / Comfort, and the Off / Max / Custom row is hidden.
        /// </summary>
        private void ApplyCapabilities()
        {
            var caps = _fanModes.Capabilities;
            QuietButton.Text = FanModeController.DisplayName(caps.QuietOrCoolMode);
            // Until the probe is complete it isn't known whether this board takes Cool (2) or Quiet (3).
            QuietButton.Enabled = caps.ProbeComplete;
            SetModeToolTips(caps.QuietOrCoolMode);

            // Remember what was applied: a child's Visible reads false while the popup itself is hidden.
            var manual = caps.ManualFanControlSupported;
            if (_manualLayout == manual)
                return;
            _manualLayout = manual;

            SuspendLayout();
            // Keep the first row where it is when the form gets shorter.
            foreach (var button in new[] { QuietButton, DefaultButton, PerformanceButton, OffButton, MaxButton, CustomButton, CustomCogButton })
                button.Anchor = AnchorStyles.Top | AnchorStyles.Left;

            OffButton.Visible = MaxButton.Visible = CustomButton.Visible = CustomCogButton.Visible = manual;
            ClientSize = new Size(ClientSize.Width, (manual ? OffButton.Bottom : DefaultButton.Bottom) + _bottomMargin);
            ResumeLayout();
        }

        private void SetModeToolTips(PerformanceMode thirdMode)
        {
            // On the 2018-2019 OMENs HP showed its Cool profile as "Comfort": a lower-temperature mode that runs
            // the fans sooner, not a quiet one. Quiet (3) only exists on newer (Victus-style) boards.
            _modeToolTip.SetToolTip(QuietButton, thirdMode == PerformanceMode.Cool
                ? "Comfort (HP's Cool profile): lower CPU and GPU temperatures.\n" +
                  "The fans start earlier and run more; for the quietest fans, use Default."
                : "Quiet: HP's low-noise mode.");
            _modeToolTip.SetToolTip(DefaultButton, "Default: HP's balanced mode.");
            _modeToolTip.SetToolTip(PerformanceButton,
                "Performance: higher CPU and GPU power limits; the fans ramp up more under load.\n" +
                "The BIOS may refuse it on battery; it is sent again when the charger is plugged in.");
            _modeToolTip.SetToolTip(MaxButton, "Max: fans at full speed (re-sent every 90 seconds).");
            _modeToolTip.SetToolTip(OffButton, "Off: fans stopped until the BIOS takes over again (about 2 minutes).");
            _modeToolTip.SetToolTip(CustomButton, "Custom: your fan curve (edit it with the gear button).");
        }

        private void ShowSavedMode()
        {
            var s = Settings.Default;
            var manual = _fanModes.Capabilities.ManualFanControlSupported;

            if (manual && (s.fanMax || s.fanOff))
                ActivePerformanceModeChange(s.fanMax, s.fanOff);
            else if (manual && s.fanCustom)
                ActivePerformanceModeChange(isCustom: true);
            else
                ActivePerformanceModeChange(s.PerformanceMode);
        }

        public new void Show()
        {
            base.Show();
            focusTimer.Start();
            UpdateTempInfo(new object(), EventArgs.Empty);

            var screenBounds = Screen.FromPoint(Cursor.Position).Bounds; // FromPoint never returns null
            var xPos = (screenBounds.Width / 2) - (Width / 2) + screenBounds.X;
            var yPos = (screenBounds.Height + screenBounds.Top) - (Height + 50);

            Location = new Point(xPos, yPos);

            if (activeButton != null)
            {
                activeButton.Focus();
                activeButton.Select();
            }
            updateTextTimer.Start();
        }
        public new void Hide()
        {
            if (focusTimer.Enabled)
                focusTimer.Stop();
            updateTextTimer.Stop();
            base.Hide();
        }
        public static bool ApplicationIsActivated()
        {
            var activatedHandle = GetForegroundWindow();
            if (activatedHandle == IntPtr.Zero)
            {
                return false;       // No window is currently activated
            }

            var procId = Process.GetCurrentProcess().Id;
            int activeProcId;
            GetWindowThreadProcessId(activatedHandle, out activeProcId);

            return activeProcId == procId;
        }

        public void UpdateTempInfoText(TemperatureReading t)
        {
            cpuTempLabel.Text = t.Cpu is float cpu ? $"CPU: {cpu:0}℃" : "CPU: n/a";
            gpuTempLabel.Text = t.Gpu is float gpu ? $"GPU: {gpu:0}℃" : "GPU: n/a";

            var source = TemperatureService.Describe(t.CpuSource);
            if (_temperatureToolTip.GetToolTip(cpuTempLabel) != source)
                _temperatureToolTip.SetToolTip(cpuTempLabel, source);
        }

        private void QuietButton_Click(object sender, EventArgs e)
        {
            StopCurveTimer();
            _fanModes.ApplyThermalMode(_fanModes.Capabilities.QuietOrCoolMode);
            ShowSavedMode();
        }

        private void DefaultButton_Click(object sender, EventArgs e)
        {
            StopCurveTimer();
            _fanModes.ApplyThermalMode(PerformanceMode.Default);
            ShowSavedMode();
        }

        private void PerformanceButton_Click(object sender, EventArgs e)
        {
            StopCurveTimer();
            _fanModes.ApplyThermalMode(PerformanceMode.Performance);
            ShowSavedMode();
        }

        private void OffButton_Click(object sender, EventArgs e)
        {
            DialogResult res = MessageBox.Show("This will cause your temperatures to rise. Make sure your laptop does not overheat", "Be careful", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            if (res == DialogResult.OK)
            {
                StopCurveTimer();
                _fanModes.ApplyOff();
                ShowSavedMode();
            }
        }

        private void MaxButton_Click(object sender, EventArgs e)
        {
            StopCurveTimer();
            _fanModes.ApplyMax();
            ShowSavedMode();
        }

        private void CustomButton_Click(object sender, EventArgs e)
        {
            _fanModes.ApplyCustom();
            ShowSavedMode();
            StartCurveTimer();
        }

        private void CustomCogButton_Click(object sender, EventArgs e)
        {
            if (!_fanModes.Capabilities.ManualFanControlSupported)
                return;

            using var editor = new FormFanCurve(_fanCurveProfile, _fanModes);
            if (editor.ShowDialog() == DialogResult.OK)
            {
                _fanCurveProfile = editor.Result;
                _fanCurveProfile.Save();
            }
        }

        private void StartCurveTimer()
        {
            if (!_curveTimer.Enabled)
                _curveTimer.Start();
        }

        private void StopCurveTimer()
        {
            if (_curveTimer.Enabled)
                _curveTimer.Stop();
        }

        private void CurveTimer_Tick(object? sender, EventArgs e)
        {
            if (!Settings.Default.fanCustom || !_fanModes.Capabilities.ManualFanControlSupported)
            {
                StopCurveTimer();
                return;
            }

            if (Interlocked.Exchange(ref _curveBusy, 1) == 1)
                return;

            var profile = _fanCurveProfile;
            Task.Run(() =>
            {
                try
                {
                    // While the popup is hidden the GPU is asked less often, so a hybrid-graphics dGPU can sleep.
                    var t = TemperatureService.Read(Visible
                        ? TemperatureService.GpuIntervalVisible
                        : TemperatureService.GpuIntervalBackground);

                    // A missing reading must not be treated as 0 °C (that would pick the curve's slowest point).
                    // The GPU shares heat pipes with the CPU, so fall back to the CPU temperature, then to full speed.
                    int cpuFan = t.Cpu is float cpu ? profile.GetFanSpeed(profile.CpuCurve, cpu) : 100;
                    float? gpuTemp = t.Gpu ?? t.Cpu;
                    int gpuFan = gpuTemp is float gpu ? profile.GetFanSpeed(profile.GpuCurve, gpu) : 100;

                    _fanModes.SetFanLevels(Math.Max(cpuFan, 1), Math.Max(gpuFan, 1));
                }
                catch (Exception ex)
                {
                    // Don't leave the fans at the last curve level when the temperature can't be read.
                    AppLog.Error("Custom fan curve update failed; running the fans at full speed", ex);
                    _fanModes.SetFanLevels(100, 100);
                }
                finally
                {
                    Interlocked.Exchange(ref _curveBusy, 0);
                }
            });
        }

        private readonly Color activeColor = Color.FromArgb(153, 153, 153);
        private readonly Color inactiveColor = Color.FromArgb(64, 64, 64);
        private readonly Color activeTextColor = Color.FromArgb(31, 31, 31);
        private readonly Color inactiveTextColor = Color.FromArgb(230, 230, 230);
        private void ResetAllButtons()
        {
            QuietButton.BackColor = inactiveColor;
            QuietButton.ForeColor = inactiveTextColor;
            DefaultButton.BackColor = inactiveColor;
            DefaultButton.ForeColor = inactiveTextColor;
            PerformanceButton.BackColor = inactiveColor;
            PerformanceButton.ForeColor = inactiveTextColor;
            OffButton.BackColor = inactiveColor;
            OffButton.ForeColor = inactiveTextColor;
            MaxButton.BackColor = inactiveColor;
            MaxButton.ForeColor = inactiveTextColor;
            CustomButton.BackColor = inactiveColor;
            CustomButton.ForeColor = inactiveTextColor;
        }
        public void ActivePerformanceModeChange(bool isMax, bool isOff)
        {
            ResetAllButtons();
            activeButton = null;

            if (isMax)
            {
                MaxButton.BackColor = activeColor;
                MaxButton.ForeColor = activeTextColor;
                activeButton = MaxButton;
            }
            else if (isOff)
            {
                OffButton.BackColor = activeColor;
                OffButton.ForeColor = activeTextColor;
                activeButton = OffButton;
            }
        }
        public void ActivePerformanceModeChange(bool isCustom)
        {
            ResetAllButtons();
            activeButton = null;

            if (isCustom)
            {
                CustomButton.BackColor = activeColor;
                CustomButton.ForeColor = activeTextColor;
                activeButton = CustomButton;
            }
        }
        public void ActivePerformanceModeChange(PerformanceMode m)
        {
            ResetAllButtons();
            activeButton = null;
            switch (m)
            {
                case PerformanceMode.Quiet:
                case PerformanceMode.Cool:
                    QuietButton.BackColor = activeColor;
                    QuietButton.ForeColor = activeTextColor;
                    activeButton = QuietButton;
                    break;
                case PerformanceMode.Default:
                    DefaultButton.BackColor = activeColor;
                    DefaultButton.ForeColor = activeTextColor;
                    activeButton = DefaultButton;
                    break;
                case PerformanceMode.Performance:
                    PerformanceButton.BackColor = activeColor;
                    PerformanceButton.ForeColor = activeTextColor;
                    activeButton = PerformanceButton;
                    break;
            }
        }

        public void UpdateFanSpeedText(List<int> speedsRpm)
        {
            if (!_fanModes.HasFanSpeeds)
            {
                fanList.Text = _fanModes.Status;
                return;
            }

            fanList.Text = speedsRpm.Count == 0
                ? "Fan speed n/a"
                : string.Join("\n", speedsRpm.Select((rpm, i) => $"Fan {i + 1}: {rpm} rpm"));
        }

        private void FormPopup_Leave(object sender, EventArgs e)
        {
            Hide();
        }

        private void UpdateTempInfo(object? sender, EventArgs a)
        {
            if (AppIsExiting || Interlocked.Exchange(ref _refreshBusy, 1) == 1)
                return;

            Task.Run(() =>
            {
                try
                {
                    var speeds = _fanModes.GetFanSpeedsRpm();
                    var t = TemperatureService.Read();

                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(() =>
                        {
                            UpdateFanSpeedText(speeds);
                            UpdateTempInfoText(t);
                        });
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("Reading temperatures failed", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshBusy, 0);
                }
            });
        }

        public delegate void ShowFormDelegate();

        public void OmenKeyPressed()
        {
            if (Visible)
                Hide();
            else
                Show();
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    }
}
