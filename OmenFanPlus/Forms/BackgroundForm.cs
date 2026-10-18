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

            _updates.UpdateAvailable += (sender, message) =>
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(() => TaskbarNotifyIcon.ShowBalloonTip(10000, "Update available", message, ToolTipIcon.Info));
            };
            FormClosed += (sender, e) => _updates.Dispose();

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
                var hotkey = new FormHotkey();
                hotkey.Show();
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
                    var report = await _updates.RunAsync(notify: false);
                    MessageBox.Show(report, "OmenFanPlus updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

            var openLog = new ToolStripMenuItem("Open log folder");
            openLog.Click += (sender, e) =>
            {
                Directory.CreateDirectory(AppLog.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Folder}\"") { UseShellExecute = true });
            };

            var quitIndex = contextMenuStrip1.Items.IndexOf(TaskbarQuit);
            contextMenuStrip1.Items.Insert(quitIndex, openLog);
            contextMenuStrip1.Items.Insert(quitIndex, autoCheck);
            contextMenuStrip1.Items.Insert(quitIndex, checkUpdates);
        }

        private void UpdateTrayText()
        {
            var text = string.IsNullOrEmpty(_fanModes.Status)
                ? "OmenFanPlus"
                : $"OmenFanPlus - {_fanModes.Status}";
            // NotifyIcon.Text is limited to 127 characters.
            TaskbarNotifyIcon.Text = text.Length > 127 ? text[..127] : text;
        }

        public async void WmiEventWatcher_HpBiosEventArrived(object sender, HpBiosEventArgs e)
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
                        catch (Exception Ex)
                        {
                            MessageBox.Show(Ex.Message);
                        }
                    }
                    else
                    {
                        Invoke(popup.OmenKeyPressed);
                    }

                }
                catch
                {
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
            try
            {
                if (e.Button == MouseButtons.Left)
                {
                    Invoke(popup.OmenKeyPressed);
                }
            }
            catch
            {
            }
        }

        private static void SetRunOnStartup(bool runOnStartup)
        {
            try
            {
                if (!runOnStartup)
                    StartupTask.Unregister();
                else if (!StartupTask.Register())
                    MessageBox.Show(
                        "Only the installed copy can start at logon. Run install.ps1, then tick 'Run on startup' " +
                        @"in the copy in C:\Program Files\OmenFanPlus.",
                        "OmenFanPlus", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error("Changing the startup task failed", ex);
                MessageBox.Show($"Could not change the startup task: {ex.Message}", "OmenFanPlus",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
