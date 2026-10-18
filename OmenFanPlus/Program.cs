using System.Configuration;
using Hp.Ohl.WmiService;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenFanPlus.Lib;
using OmenFanPlus.Forms;
using OmenFanPlus.Properties;
using OmenFanPlus.Services;

namespace OmenFanPlus
{
    internal static class Program
    {
        private const string InstanceName = @"Local\OmenFanPlus";
        private const string QuitEventName = @"Local\OmenFanPlus.Quit";
        private static FanModeController? _fanModes;

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        /// <param name="args">"--quit" asks a running copy to exit cleanly (used by install.ps1).</param>
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Any(a => string.Equals(a, "--quit", StringComparison.OrdinalIgnoreCase)))
                return AskRunningCopyToQuit() ? 0 : 1;

            // Two copies would fight over the fan mode.
            using var singleInstance = new Mutex(true, InstanceName, out var isFirstInstance);
            if (!isFirstInstance)
                return 0;
            using var quitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            AppLog.Start();
            Application.ThreadException += (sender, e) => AppLog.Error("Unhandled UI exception", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
                AppLog.Error("Unhandled exception", e.ExceptionObject as Exception);

            RecoverFromCorruptSettings();
            try
            {
                if (Settings.Default.FirstStart == true)
                {
                    if (!SettingsMigration.TryImportFromOtherCopy())
                    {
                        Settings.Default.PerformanceMode = PerformanceMode.Default;
                        Settings.Default.AlternateKey = Keys.None;
                        Settings.Default.ExecuteArguments = "";
                        Settings.Default.ExecutePath = "";
                    }
                    Settings.Default.FirstStart = false;
                    FanModeController.TrySave();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("The settings can't be read; the app can't start", ex);
                MessageBox.Show($"OmenFanPlus can't read its settings: {ex.Message}", "OmenFanPlus",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            try
            {
                TemperatureService.Open();
            }
            catch (Exception ex)
            {
                // The ACPI thermal zone still works without LibreHardwareMonitor.
                AppLog.Error("Opening the CPU/GPU sensors failed", ex);
            }
            var temperatures = TemperatureService.Read();
            AppLog.Info($"PawnIO installed: {TemperatureService.PawnIoInstalled} ({TemperatureService.PawnIoVersion}); " +
                        $"CPU {temperatures.Cpu?.ToString("0") ?? "n/a"} C from {temperatures.CpuSource}, " +
                        $"GPU {temperatures.Gpu?.ToString("0") ?? "n/a"} C");

            _fanModes = new FanModeController();
            try
            {
                _fanModes.Initialize();
            }
            catch (Exception ex)
            {
                // The tray icon must come up even if the first probe blew up; the probe retries on its own.
                AppLog.Error("The first BIOS probe failed", ex);
            }

            // Also removes the task of the pre-rename version (OmenHubLighterPlusStartup).
            StartupTask.EnsureUpToDate(Settings.Default.RunAtStartup);

            BackgroundForm bgForm = new BackgroundForm(_fanModes);
            WmiEventWatcher.HpBiosEventArrived += bgForm.WmiEventWatcher_HpBiosEventArrived;
            // Without it the OMEN key doesn't work; it keeps retrying in the background if WMI isn't ready yet.
            WmiEventWatcher.StartWithRetry();

            // "OmenFanPlus.exe --quit" signals this event; exit the same way as Quit from the tray menu.
            _ = bgForm.Handle;
            var quitWait = ThreadPool.RegisterWaitForSingleObject(quitRequested, (state, timedOut) =>
            {
                try
                {
                    if (bgForm.IsHandleCreated && !bgForm.IsDisposed)
                        bgForm.BeginInvoke(Quit);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    // The app is already closing.
                }
            }, null, Timeout.Infinite, executeOnlyOnce: true);

            // Stop everything that calls into the form before it is disposed.
            bgForm.FormClosed += (sender, e) =>
            {
                quitWait.Unregister(null);
                WmiEventWatcher.StopHpBiosEventWatcher();
            };

            Application.Run(bgForm);

            // Reached after Quit and --quit.
            WmiEventWatcher.StopHpBiosEventWatcher();
            _fanModes.Shutdown();
            _fanModes.Dispose();
            TemperatureService.Close();
            AppLog.Info("Exiting");
            return 0;
        }

        public static void Quit()
        {
            Application.Exit();
        }

        /// <summary>
        /// A user.config left empty or truncated (power cut during a save) makes every later settings read throw
        /// for the life of the process. Checking it this way first doesn't poison the settings system, so the
        /// bad file can be moved aside and the app continues with defaults (and the import from an older copy).
        /// </summary>
        private static void RecoverFromCorruptSettings()
        {
            try
            {
                ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal);
            }
            catch (ConfigurationErrorsException ex)
            {
                var file = ex.Filename ?? (ex.InnerException as ConfigurationErrorsException)?.Filename;
                AppLog.Error($"The settings file is unreadable ({file ?? "unknown file"})", ex);
                // Only ever touch a user.config; the shipped OmenFanPlus.dll.config next to the exe stays.
                if (file != null && file.EndsWith("user.config", StringComparison.OrdinalIgnoreCase) && File.Exists(file))
                {
                    try
                    {
                        File.Move(file, file + ".corrupt", overwrite: true);
                        AppLog.Warn($"Moved it to {file}.corrupt and continuing with default settings");
                    }
                    catch (Exception moveEx)
                    {
                        AppLog.Error("Couldn't move the unreadable settings file aside", moveEx);
                    }
                }
            }
        }

        private static bool AskRunningCopyToQuit()
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quitRequested))
                return false;

            using (quitRequested)
                quitRequested.Set();
            return true;
        }
    }
}
