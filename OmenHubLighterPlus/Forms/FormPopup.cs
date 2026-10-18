using Hp.Omen.OmenCommonLib;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenHubLight.Lib;
using OmenHubLighterPlus.Models;
using OmenHubLighterPlus.Properties;
using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Timer = System.Windows.Forms.Timer;

namespace OmenHubLighterPlus.Forms
{
    public partial class FormPopup : Form
    {
        private Button? activeButton;
        private Timer focusTimer = new Timer();
        public bool AppIsExiting = false;
        private OmenHsaClient hsaClient = new();
        private Timer updateTextTimer = new Timer();
        private FanCurveProfile _fanCurveProfile;
        private Timer _curveTimer = new Timer();

        public FormPopup()
        {
            InitializeComponent();

            _fanCurveProfile = FanCurveProfile.Load();

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

            if(Settings.Default.fanOff || Settings.Default.fanMax)
            {
                ActivePerformanceModeChange(Settings.Default.fanMax, Settings.Default.fanOff);
            }else if(Settings.Default.fanCustom)
            {
                ActivePerformanceModeChange(isCustom: true);
                StartCurveTimer();
            }else
            {
                ActivePerformanceModeChange(Settings.Default.PerformanceMode);
            }

            _curveTimer.Interval = 2000;
            _curveTimer.Tick += CurveTimer_Tick;

            Settings.Default.PropertyChanged += Settings_PropertyChanged;
        }

        private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "PerformanceMode")
            {
                ActivePerformanceModeChange(Settings.Default.PerformanceMode);
            }
            else if (e.PropertyName == "fanMax" || e.PropertyName == "fanOff")
            {
                ActivePerformanceModeChange(Settings.Default.fanMax, Settings.Default.fanOff);
            }
            else if (e.PropertyName == "fanCustom" && Settings.Default.fanCustom)
            {
                ActivePerformanceModeChange(isCustom: true);
            }
        }

        public new void Show()
        {
            base.Show();
            focusTimer.Start();
            UpdateTempInfo(new object(), EventArgs.Empty);

            var screenBounds = (Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen).Bounds;
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

        public void UpdateTempInfoText(float cpuTemperatures, float gpuTemperatures)
        {
            cpuTempLabel.Text = $"CPU: {cpuTemperatures}℃";
            gpuTempLabel.Text = $"GPU: {gpuTemperatures}℃";
        }
        private void QuietButton_Click(object sender, EventArgs e)
        {
            hsaClient.SetSwFanControlLevel(0, 0);
            hsaClient.SetMaxFan(MaxFanMode.Off);
            hsaClient.SetFanMode(PerformanceMode.Quiet);
            Settings.Default.fanMax = false;
            Settings.Default.fanOff = false;
            Settings.Default.fanCustom = false;
            Settings.Default.PerformanceMode = PerformanceMode.Quiet;
            Settings.Default.Save();
            StopCurveTimer();
        }

        private void DefaultButton_Click(object sender, EventArgs e)
        {
            hsaClient.SetSwFanControlLevel(0, 0);
            hsaClient.SetMaxFan(MaxFanMode.Off);
            hsaClient.SetFanMode(PerformanceMode.Default);
            Settings.Default.fanMax = false;
            Settings.Default.fanOff = false;
            Settings.Default.fanCustom = false;
            Settings.Default.PerformanceMode = PerformanceMode.Default;
            Settings.Default.Save();
            StopCurveTimer();
        }

        private void PerformanceButton_Click(object sender, EventArgs e)
        {
            hsaClient.SetSwFanControlLevel(0, 0);
            hsaClient.SetMaxFan(MaxFanMode.Off);
            hsaClient.SetFanMode(PerformanceMode.Performance);
            Settings.Default.fanMax = false;
            Settings.Default.fanOff = false;
            Settings.Default.fanCustom = false;
            Settings.Default.PerformanceMode = PerformanceMode.Performance;
            Settings.Default.Save();
            StopCurveTimer();
        }

        private void OffButton_Click(object sender, EventArgs e)
        {
            DialogResult res = MessageBox.Show("This will cause your temperatures to rise. Make sure your laptop does not overheat", "Be careful", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            if (res == DialogResult.OK)
            {
                hsaClient.SetMaxFan(MaxFanMode.Off);
                hsaClient.SetSwFanControlLevel(1, 1);
                Settings.Default.fanMax = false;
                Settings.Default.fanOff = true;
                Settings.Default.fanCustom = false;
                Settings.Default.Save();
                StopCurveTimer();
            }
        }

        private void MaxButton_Click(object sender, EventArgs e)
        {
            hsaClient.SetFanMode(PerformanceMode.Performance);
            hsaClient.SetSwFanControlLevel(100, 100);
            Settings.Default.fanMax = true;
            Settings.Default.fanOff = false;
            Settings.Default.fanCustom = false;
            Settings.Default.Save();
            StopCurveTimer();
        }

        private void CustomButton_Click(object sender, EventArgs e)
        {
            hsaClient.SetMaxFan(MaxFanMode.Off);
            Settings.Default.fanMax = false;
            Settings.Default.fanOff = false;
            Settings.Default.fanCustom = true;
            Settings.Default.Save();
            ActivePerformanceModeChange(isCustom: true);
            StartCurveTimer();
        }

        private void CustomCogButton_Click(object sender, EventArgs e)
        {
            using var editor = new FormFanCurve(_fanCurveProfile);
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
            if (!Settings.Default.fanCustom)
            {
                StopCurveTimer();
                return;
            }

            Task.Run(() =>
            {
                foreach (var h in CpuInfo.GetCpuHardwares()) h.Update();
                foreach (var h in GpuInfo.GetGpuHardwares()) h.Update();

                float cpuTemp = CpuInfo.GetCpuHardwares()
                    .Select(x => x.GetTempSensors().Max(s => s.Value))
                    .Max() ?? 0f;
                float gpuTemp = GpuInfo.GetGpuHardwares()
                    .Select(x => x.GetTempSensors().Max(s => s.Value))
                    .Max() ?? 0f;

                int cpuFan = _fanCurveProfile.GetFanSpeed(_fanCurveProfile.CpuCurve, cpuTemp);
                int gpuFan = _fanCurveProfile.GetFanSpeed(_fanCurveProfile.GpuCurve, gpuTemp);

                hsaClient.SetSwFanControlLevel(Math.Max(cpuFan, 1), Math.Max(gpuFan, 1));
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

        public void UpdateFanSpeedText(List<int> speeds)
        {
            List<string> fanSpeeds = new List<string>();
            for (int i = 0; i < speeds.Count; i++)
            {
                int value = speeds[i];
                fanSpeeds.Add($"Fan {i + 1}: {value}%");
            }
            fanList.Text = String.Join("\n", fanSpeeds);
        }

        private void FormPopup_Leave(object sender, EventArgs e)
        {
            Hide();
        }

        private void UpdateTempInfo(object? sender, EventArgs a)
        {
            Task.Run(() =>
            {
                List<int> levels = hsaClient.GetFanLevel().ConvertAll<int>((byte a) => (int)a);

                Invoke(UpdateFanSpeedText, levels);
            });


            Task.Run(() =>
            {
                if (AppIsExiting) return;
                foreach (var h in CpuInfo.GetCpuHardwares())
                {
                    h.Update();
                }

                foreach (var h in GpuInfo.GetGpuHardwares())
                {
                    h.Update();
                }

                var cpuTemperatures = CpuInfo.GetCpuHardwares()
                    .Select(x => x.GetTempSensors().Max(x => x.Value))
                    .Max() ?? 0f;
                var gpuTemperatures = GpuInfo.GetGpuHardwares()
                    .Select(x => x.GetTempSensors().Max(x => x.Value))
                    .Max() ?? 0f;

                Invoke(UpdateTempInfoText, cpuTemperatures, gpuTemperatures);
            });
        }

        public delegate void ShowFormDelegate();

        public void OmenKeyPressed()
        {
            if (Visible)
            {
                Hide();
            }
            else
            {
                List<int> levels = hsaClient.GetFanLevel().ConvertAll<int>((byte a) => (int)a);

                Show();
                Invoke(UpdateFanSpeedText, levels);
            };
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);
    }
}
