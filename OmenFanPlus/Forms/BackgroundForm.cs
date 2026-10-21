using Hp.Ohl.WmiService.Models;
using OmenFanPlus.Lib;
using OmenFanPlus.Properties;
using OmenFanPlus.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsInput;
using WindowsInput.Events;

namespace OmenFanPlus.Forms
{
    public partial class BackgroundForm : Form
    {

        private FormNotify formNotify = new();
        private readonly FanModeController _fanModes;
        private readonly FormPopup popup;
        private readonly UpdateCheck _updates = new();

        // The tray icon showing the CPU temperature: redrawn every few seconds from a thread-pool read.
        private static readonly TimeSpan TrayRefresh = TimeSpan.FromSeconds(3);
        private readonly System.Windows.Forms.Timer _trayTimer = new();
        private readonly Icon? _baseIcon;
        private IntPtr _trayIconHandle;
        private int _trayBusy;
        private TemperatureReading? _lastReading;

        internal BackgroundForm(FanModeController fanModes)
        {
            _fanModes = fanModes;
            InitializeComponent();
            _baseIcon = TaskbarNotifyIcon.Icon;
            popup = new FormPopup(fanModes);
            Visible = false;
            AddMenuItems();
            DarkMenuRenderer.Apply(contextMenuStrip1);

            _updates.UpdateAvailable += (sender, message) => Balloon("Update available", message, ToolTipIcon.Info);
            _updates.InstallProgress += (sender, message) => Balloon("OmenFanPlus update", message, ToolTipIcon.Info);
            FormClosed += (sender, e) =>
            {
                _updates.Dispose();
                _trayTimer.Stop();
                ShowBaseIcon();
            };

            // What the update script did while the app was closed, shown once the tray icon is up.
            var lastUpdate = UpdateRunner.TakeResult();
            if (lastUpdate != null)
            {
                AppLog.Info($"Last update: {lastUpdate.ReplaceLineEndings(" | ")}");
                var showResult = new System.Windows.Forms.Timer { Interval = 3000 };
                showResult.Tick += (sender, e) =>
                {
                    showResult.Dispose();
                    TaskbarNotifyIcon.ShowBalloonTip(15000, "OmenFanPlus update", lastUpdate, ToolTipIcon.Info);
                };
                showResult.Start();
            }

            UpdateTrayText();
            _fanModes.StatusChanged += (sender, e) =>
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(UpdateTrayText);
            };

            _trayTimer.Interval = (int)TrayRefresh.TotalMilliseconds;
            _trayTimer.Tick += (sender, e) => RefreshTray();
            if (Settings.Default.TrayShowsTemperature)
            {
                // The first reading arrives through BeginInvoke, which needs the window handle to exist already.
                CreateHandle();
                _trayTimer.Start();
                RefreshTray();
            }

            Shown += (sender, e) =>
            {
                Hide();
                ShowInTaskbar = false;
            };

            TaskbarQuit.Click += (sender, e) =>
            {
                Program.Quit();
            };
            TaskbarChangeOmenKey.Click += (sender, e) =>
            {
                // Modal, so two copies of the dialog can't overwrite each other's settings.
                using var hotkey = new FormHotkey();
                hotkey.ShowDialog();
            };

            TaskbarRunOnStartup.Checked = Settings.Default.RunAtStartup;
            if (!StartupTask.IsInstalledCopy(Application.ExecutablePath))
            {
                // Only the copy installed by install.ps1 may start elevated at logon.
                TaskbarRunOnStartup.Enabled = false;
                TaskbarRunOnStartup.Text = "Run on startup (installed copy only)";
            }

            TaskbarRunOnStartup.CheckedChanged += (sender, e) =>
            {
                Settings.Default.RunAtStartup = TaskbarRunOnStartup.Checked;
                Settings.Default.Save();
            };

            Settings.Default.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(Settings.Default.RunAtStartup))
                {
                    TaskbarRunOnStartup.Checked = Settings.Default.RunAtStartup;
                    SetRunOnStartup(Settings.Default.RunAtStartup);
                };
            };
        }

        private void AddMenuItems()
        {
            // --- next to "Remap Omen key to..." ---------------------------------------------------------------

            // Takes precedence over the remap dialog's options while it is on.
            var cycleModes = new ToolStripMenuItem("OMEN key cycles fan modes")
            {
                CheckOnClick = true,
                Checked = Settings.Default.OmenKeyCyclesModes,
                ToolTipText = "The OMEN key switches Comfort > Default > Performance > Auto and shows which, instead of opening the popup.",
            };
            cycleModes.CheckedChanged += (sender, e) =>
            {
                Settings.Default.OmenKeyCyclesModes = cycleModes.Checked;
                FanModeController.TrySave();
            };

            var powerModes = new ToolStripMenuItem("Charger and battery modes...");
            powerModes.Click += (sender, e) =>
            {
                using var dialog = new FormPowerModes(_fanModes);
                if (dialog.ShowDialog() == DialogResult.OK && dialog.ApplyNow)
                    _fanModes.ApplyPowerSourceModes();
            };

            var trayTemperature = new ToolStripMenuItem("Show temperature in tray icon")
            {
                CheckOnClick = true,
                Checked = Settings.Default.TrayShowsTemperature,
                ToolTipText = "The tray icon shows the CPU temperature; hover it for CPU and GPU.",
            };
            trayTemperature.CheckedChanged += (sender, e) =>
            {
                Settings.Default.TrayShowsTemperature = trayTemperature.Checked;
                FanModeController.TrySave();
                if (trayTemperature.Checked)
                {
                    _trayTimer.Start();
                    RefreshTray();
                }
                else
                {
                    _trayTimer.Stop();
                    ShowBaseIcon();
                    UpdateTrayText();
                }
            };

            var afterRemap = contextMenuStrip1.Items.IndexOf(TaskbarChangeOmenKey) + 1;
            contextMenuStrip1.Items.Insert(afterRemap, trayTemperature);
            contextMenuStrip1.Items.Insert(afterRemap, powerModes);
            contextMenuStrip1.Items.Insert(afterRemap, cycleModes);

            // --- before "Quit" ------------------------------------------------------------------------------

            var checkUpdates = new ToolStripMenuItem("Check for updates...");
            checkUpdates.Click += async (sender, e) =>
            {
                checkUpdates.Enabled = false;
                try
                {
                    await CheckForUpdatesAsync();
                }
                finally
                {
                    checkUpdates.Enabled = true;
                }
            };

            // The daily check contacts builds.dotnet.microsoft.com and api.github.com/github.com; this turns it off.
            var autoCheck = new ToolStripMenuItem("Check for updates automatically")
            {
                CheckOnClick = true,
                Checked = Settings.Default.CheckForUpdates,
            };
            autoCheck.CheckedChanged += (sender, e) =>
            {
                Settings.Default.CheckForUpdates = autoCheck.Checked;
                Settings.Default.Save();
            };

            // Off by default: with it on, the daily check also downloads, verifies and installs what it finds,
            // closing and restarting the app to do so.
            var autoInstall = new ToolStripMenuItem("Install updates automatically")
            {
                CheckOnClick = true,
                Checked = Settings.Default.AutoInstallUpdates,
                ToolTipText = "Downloads .NET runtime and PawnIO updates from their publishers, checks their hashes and " +
                              "signatures, then closes OmenFanPlus, installs them and starts it again. Needs the automatic " +
                              "check and the installed copy in Program Files.",
            };
            autoInstall.CheckedChanged += (sender, e) =>
            {
                Settings.Default.AutoInstallUpdates = autoInstall.Checked;
                if (autoInstall.Checked)
                    autoCheck.Checked = true; // saves CheckForUpdates through its own handler
                Settings.Default.Save();
            };

            var openLog = new ToolStripMenuItem("Open log folder");
            openLog.Click += (sender, e) =>
            {
                Directory.CreateDirectory(AppLog.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Folder}\"") { UseShellExecute = true });
            };

            var quitIndex = contextMenuStrip1.Items.IndexOf(TaskbarQuit);
            contextMenuStrip1.Items.Insert(quitIndex, openLog);
            contextMenuStrip1.Items.Insert(quitIndex, autoInstall);
            contextMenuStrip1.Items.Insert(quitIndex, autoCheck);
            contextMenuStrip1.Items.Insert(quitIndex, checkUpdates);
        }

        private async Task CheckForUpdatesAsync()
        {
            var report = await _updates.RunAsync(notify: false);
            if (report.Available.Count == 0 || _updates.IsInstalling || !DependencyInstaller.CanInstall)
            {
                MessageBox.Show(report.Text, "OmenFanPlus updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var names = string.Join(" and ", report.Available.Select(u => $"{u.DisplayName} {u.Version}"));
            var answer = MessageBox.Show(
                report.Text + Environment.NewLine +
                $"Install {names} now?" + Environment.NewLine + Environment.NewLine +
                "OmenFanPlus downloads it from the publisher, checks it against the published hash (and the signature, " +
                "for installers), then closes, installs it and starts again. Nothing else is touched, and Windows is not restarted.",
                "OmenFanPlus updates", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;

            var error = await _updates.InstallAsync(report.Available);
            if (error != null)
                MessageBox.Show(error, "OmenFanPlus updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void Balloon(string title, string message, ToolTipIcon icon)
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => TaskbarNotifyIcon.ShowBalloonTip(10000, title, message, icon));
        }

        // ---- the tray icon and its tooltip ---------------------------------------------------------------

        private void RefreshTray()
        {
            if (Interlocked.Exchange(ref _trayBusy, 1) == 1)
                return;

            Task.Run(() =>
            {
                try
                {
                    // The background GPU interval: an NVAPI query every few seconds can keep a hybrid dGPU awake.
                    var reading = TemperatureService.Read(TemperatureService.GpuIntervalBackground);
                    if (IsHandleCreated && !IsDisposed)
                    {
                        BeginInvoke(() =>
                        {
                            _lastReading = reading;
                            UpdateTrayIcon(reading);
                            UpdateTrayText();
                        });
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // The app is closing.
                }
                catch (Exception ex)
                {
                    AppLog.Error("Reading temperatures for the tray icon failed", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _trayBusy, 0);
                }
            });
        }

        private void UpdateTrayIcon(TemperatureReading reading)
        {
            if (!Settings.Default.TrayShowsTemperature || reading.Cpu is not float cpu)
            {
                ShowBaseIcon();
                return;
            }

            try
            {
                // The shell's small icon size follows the display scaling, so the digits stay sharp at 125 %+.
                var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
                using var bitmap = TrayIconRenderer.Render(cpu, size);
                var handle = bitmap.GetHicon();
                using (var icon = Icon.FromHandle(handle))
                    TaskbarNotifyIcon.Icon = icon; // the shell keeps its own copy
                ReleaseTrayIconHandle();
                _trayIconHandle = handle;
            }
            catch (Exception ex)
            {
                AppLog.Error("Drawing the tray icon failed", ex);
                ShowBaseIcon();
            }
        }

        private void ShowBaseIcon()
        {
            if (!ReferenceEquals(TaskbarNotifyIcon.Icon, _baseIcon))
                TaskbarNotifyIcon.Icon = _baseIcon;
            ReleaseTrayIconHandle();
        }

        // Bitmap.GetHicon hands out a GDI handle the caller must destroy, once the shell no longer shows it.
        private void ReleaseTrayIconHandle()
        {
            if (_trayIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        private void UpdateTrayText()
        {
            TaskbarNotifyIcon.Text = TrayText(_fanModes.Status,
                Settings.Default.TrayShowsTemperature ? _lastReading : null);
        }

        /// <summary>The tooltip: the temperatures (when shown) on one line, the fan-mode status on the next.</summary>
        internal static string TrayText(string status, TemperatureReading? reading)
        {
            var lines = new List<string>();
            if (reading is { } r && (r.Cpu != null || r.Gpu != null))
                lines.Add($"CPU {Format(r.Cpu)}  GPU {Format(r.Gpu)}");
            lines.Add(string.IsNullOrEmpty(status) ? "OmenFanPlus" : $"OmenFanPlus - {status}");

            // NotifyIcon.Text is limited to 127 characters.
            var text = string.Join('\n', lines);
            return text.Length > 127 ? text[..127] : text;

            static string Format(float? value) => value is float v ? $"{v:0} °C" : "n/a";
        }

        // ---- HP BIOS events (the OMEN key, the Windows-key and touchpad toggles) --------------------------

        // Runs on the WMI event thread. An exception escaping an async void handler ends the process, so nothing
        // here may throw.
        public async void WmiEventWatcher_HpBiosEventArrived(object sender, HpBiosEventArgs e)
        {
            try
            {
                await HandleBiosEventAsync(e);
            }
            catch (Exception ex)
            {
                AppLog.Error("Handling an HP BIOS event failed", ex);
            }
        }

        private async Task HandleBiosEventAsync(HpBiosEventArgs e)
        {
            if (e.eventPayload is OmenKeyPressedPayload)
            {
                try
                {
                    if (Settings.Default.OmenKeyCyclesModes)
                    {
                        // A BIOS call, so not on the UI thread; the notice and the popup's buttons follow on it.
                        var name = _fanModes.CycleMode();
                        if (IsHandleCreated && !IsDisposed)
                            Invoke(() => formNotify.NotifyText(name));
                    }
                    else if (Settings.Default.UseOmenAlternateKey)
                    {
                        Keys key = Settings.Default.AlternateKey;
                        Keys noModifiersKey = ((Keys)((int)key & ~-65536));

                        bool hasCtrl = key.HasFlag(Keys.Control);
                        bool hasShift = key.HasFlag(Keys.Shift);
                        bool hasAlt = key.HasFlag(Keys.Alt);

                        List<KeyCode> keysToPress = new();

                        if (hasCtrl)
                            keysToPress.Add(KeyCode.Control);
                        if (hasShift)
                            keysToPress.Add(KeyCode.Shift);
                        if (hasAlt)
                            keysToPress.Add(KeyCode.Alt);

                        keysToPress.Add((KeyCode)noModifiersKey);
                        await Simulate.Events()
                            .ClickChord(keysToPress).Wait(10)
                            .Invoke();
                    }
                    else if (Settings.Default.UseOmenKeyExecute)
                    {
                        Process p = new Process();
                        ProcessStartInfo pi = new ProcessStartInfo();
                        pi.UseShellExecute = true;
                        pi.FileName = Settings.Default.ExecutePath;
                        pi.Arguments = Settings.Default.ExecuteArguments;
                        p.StartInfo = pi;
                        try
                        {
                            p.Start();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("Starting the OMEN-key program failed", ex);
                            if (IsHandleCreated && !IsDisposed)
                                BeginInvoke(() => TaskbarNotifyIcon.ShowBalloonTip(5000, "OmenFanPlus",
                                    $"Couldn't start the program for the OMEN key: {ex.Message}", ToolTipIcon.Warning));
                        }
                    }
                    else if (IsHandleCreated && !IsDisposed)
                    {
                        Invoke(popup.OmenKeyPressed);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // The app is closing.
                }
            }
            else if (e.eventPayload is WinKeyLockPayload WinkeyPayload)
            {
                ShowNotify(ButtonType.Winkey, WinkeyPayload.IsWinKeyEnabled);
            }
            else if (e.eventPayload is TouchPadTogglePayload TouchpadPayload)
            {
                ShowNotify(ButtonType.Trackpad, TouchpadPayload.IsTouchPadEnabled);
            }
        }

        // Runs on the WMI event thread; an event that arrives while the app is closing must not crash it.
        private void ShowNotify(ButtonType button, bool enabled)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    Invoke(formNotify.Notify, button, enabled);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        private void TaskbarNotifyIcon_Click(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                popup.TrayClicked();
        }

        private void SetRunOnStartup(bool runOnStartup)
        {
            var applied = false;
            try
            {
                if (!runOnStartup)
                {
                    StartupTask.Unregister();
                    applied = true;
                }
                else if (StartupTask.Register())
                {
                    applied = true;
                }
                else
                {
                    MessageBox.Show(
                        "Only the installed copy can start at logon. Run install.ps1, then tick 'Run on startup' " +
                        @"in the copy in C:\Program Files\OmenFanPlus.",
                        "OmenFanPlus", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Changing the startup task failed", ex);
                MessageBox.Show($"Could not change the startup task: {ex.Message}", "OmenFanPlus",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Don't leave the tick claiming something the task scheduler refused.
            if (!applied && Settings.Default.RunAtStartup != !runOnStartup)
            {
                Settings.Default.RunAtStartup = !runOnStartup;
                FanModeController.TrySave();
            }
        }
    }
}
