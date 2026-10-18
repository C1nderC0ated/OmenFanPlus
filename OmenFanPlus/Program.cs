using Hp.Ohl.WmiService;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenHubLight.Lib;
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
                Settings.Default.Save();
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
            _fanModes.Initialize();

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
