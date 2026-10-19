using Hp.Ohl.WmiService.Models;
using OmenFanPlus.Properties;
using OmenFanPlus.Services;
using System.Diagnostics;
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

        internal BackgroundForm(FanModeController fanModes)
        {
            _fanModes = fanModes;
            InitializeComponent();
            popup = new FormPopup(fanModes);
            Visible = false;
            AddMenuItems();
            DarkMenuRenderer.Apply(contextMenuStrip1);

            _updates.UpdateAvailable += (sender, message) => Balloon("Update available", message, ToolTipIcon.Info);
            _updates.InstallProgress += (sender, message) => Balloon("OmenFanPlus update", message, ToolTipIcon.Info);
            FormClosed += (sender, e) => _updates.Dispose();

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

        private void UpdateTrayText()
        {
            var text = string.IsNullOrEmpty(_fanModes.Status)
                ? "OmenFanPlus"
                : $"OmenFanPlus - {_fanModes.Status}";
            // NotifyIcon.Text is limited to 127 characters.
            TaskbarNotifyIcon.Text = text.Length > 127 ? text[..127] : text;
        }

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
                    if (Settings.Default.UseOmenAlternateKey)
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
