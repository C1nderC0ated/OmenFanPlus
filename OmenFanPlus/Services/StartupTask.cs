using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// The "Run on startup" scheduled task. It runs elevated because the HP BIOS WMI calls need admin rights,
    /// so it is only ever pointed at the installed copy under Program Files (a folder only admins can change).
    /// </summary>
    internal static class StartupTask
    {
        internal const string TaskName = "OmenFanPlusStartup";

        /// <summary>The task created by versions before the rename to OmenFanPlus.</summary>
        private const string LegacyTaskName = "OmenHubLighterPlusStartup";

        private static readonly TimeSpan LogonDelay = TimeSpan.FromSeconds(10);

        private static string ProgramFiles =>
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar;

        public static bool IsInstalledCopy(string exePath) =>
            exePath.StartsWith(ProgramFiles, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Registers the task for this copy. Returns false (and registers nothing) when this copy isn't the
        /// installed one, because an elevated logon task must not start a file that any program could replace.
        /// </summary>
        public static bool Register()
        {
            var exe = Application.ExecutablePath;
            if (!IsInstalledCopy(exe))
            {
                AppLog.Warn($"Not registering the startup task for {exe}: only the copy installed by install.ps1 can run at logon");
                return false;
            }

            Register(exe);
            return true;
        }

        private static void Register(string exePath)
        {
            using var ts = new TaskService();
            var user = WindowsIdentity.GetCurrent().Name;

            TaskDefinition td = ts.NewTask();
            td.RegistrationInfo.Description = "Start OmenFanPlus";
            td.Principal.UserId = user;
            td.Principal.LogonType = TaskLogonType.InteractiveToken;
            td.Principal.RunLevel = TaskRunLevel.Highest;

            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            // Task Scheduler defaults to "AC power only", which killed the app when the charger was unplugged.
            td.Settings.DisallowStartIfOnBatteries = false;
            td.Settings.StopIfGoingOnBatteries = false;
            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
            // The default task priority (7) is below normal, which delays mode changes under load.
            td.Settings.Priority = ProcessPriorityClass.Normal;

            // A short delay gives the HP WMI provider time to come up after logon.
            td.Triggers.Add(new LogonTrigger { UserId = user, Delay = LogonDelay });
            td.Actions.Add(new ExecAction(exePath));

            ts.RootFolder.RegisterTaskDefinition(TaskName, td);
            AppLog.Info($"Registered startup task for {exePath}");
        }

        /// <summary>
        /// Removes the pre-rename task, and this copy's task. A copy that isn't installed leaves the installed
        /// copy's task alone (returns false then), so testing a build can't switch off autostart.
        /// </summary>
        public static bool Unregister()
        {
            using var ts = new TaskService();
            if (ts.RootFolder.Tasks.Any(t => t.Name == LegacyTaskName))
            {
                ts.RootFolder.DeleteTask(LegacyTaskName);
                AppLog.Info($"Removed startup task {LegacyTaskName}");
            }

            var task = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == TaskName);
            if (task == null)
                return true;

            var exe = Application.ExecutablePath;
            var taskPath = (task.Definition.Actions.FirstOrDefault() as ExecAction)?.Path?.Trim('"');
            if (!IsInstalledCopy(exe) && !string.Equals(taskPath, exe, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info($"Left {TaskName} alone: it starts {taskPath}, not this copy ({exe})");
                return false;
            }

            ts.RootFolder.DeleteTask(TaskName);
            AppLog.Info($"Removed startup task {TaskName}");
            return true;
        }

        /// <summary>
        /// Called at every start. Removes the pre-rename task (it starts the old OmenHubLighterPlus build, which
        /// loads the WinRing0 driver) and, when "Run on startup" is on, makes sure the task starts the installed
        /// copy with the current conditions (runs on battery, normal priority, this user, 10 s delay). A copy run
        /// from a build folder never takes the task over.
        /// </summary>
        public static void EnsureUpToDate(bool runAtStartup)
        {
            try
            {
                using var ts = new TaskService();

                var legacy = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == LegacyTaskName);
                var task = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == TaskName);
                var taskPath = (task?.Definition.Actions.FirstOrDefault() as ExecAction)?.Path?.Trim('"');

                var exe = Application.ExecutablePath;
                string? target = IsInstalledCopy(exe) ? exe
                    : taskPath != null && IsInstalledCopy(taskPath) && File.Exists(taskPath) ? taskPath
                    : null;

                if (runAtStartup)
                {
                    if (target == null)
                    {
                        AppLog.Warn($"'Run on startup' is on, but this copy ({exe}) isn't installed; run install.ps1 to install it");
                    }
                    else if (task == null || !IsUpToDate(task, target))
                    {
                        AppLog.Info($"Startup task needs updating (exists={task != null}, path={taskPath ?? "none"})");
                        try
                        {
                            Register(target);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("Registering the startup task failed", ex);
                            // Don't leave the old build (which loads WinRing0) as the only thing starting at logon.
                            if (legacy is { Enabled: true })
                            {
                                legacy.Enabled = false;
                                AppLog.Warn($"Disabled the pre-rename startup task {LegacyTaskName}");
                            }
                        }
                        task = ts.RootFolder.Tasks.FirstOrDefault(t => t.Name == TaskName);
                    }
                }

                // Only drop the old task once the new one exists, or when startup is off anyway.
                if (legacy != null && (task != null || !runAtStartup))
                {
                    ts.RootFolder.DeleteTask(LegacyTaskName);
                    AppLog.Info($"Removed the pre-rename startup task {LegacyTaskName}");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not check the startup task", ex);
            }
        }

        private static bool IsUpToDate(Microsoft.Win32.TaskScheduler.Task task, string target)
        {
            var definition = task.Definition;
            var path = (definition.Actions.FirstOrDefault() as ExecAction)?.Path?.Trim('"');
            var trigger = definition.Triggers.Count == 1 ? definition.Triggers[0] as LogonTrigger : null;

            return string.Equals(path, target, StringComparison.OrdinalIgnoreCase)
                   && !definition.Settings.DisallowStartIfOnBatteries
                   && !definition.Settings.StopIfGoingOnBatteries
                   && definition.Settings.Priority == ProcessPriorityClass.Normal
                   && definition.Principal.RunLevel == TaskRunLevel.Highest
                   && trigger != null
                   && trigger.Delay >= LogonDelay
                   && IsCurrentUser(trigger.UserId);
        }

        // Task Scheduler may hand the user back in another form (e.g. "PC\name" vs "name"), so compare SIDs.
        private static bool IsCurrentUser(string? account)
        {
            if (string.IsNullOrEmpty(account))
                return false;
            try
            {
                var sid = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                    ? new SecurityIdentifier(account)
                    : (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
                return sid == WindowsIdentity.GetCurrent().User;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
