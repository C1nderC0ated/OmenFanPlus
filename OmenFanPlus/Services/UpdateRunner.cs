using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace OmenFanPlus.Services
{
    public enum InstallOutcome
    {
        Installed,
        InstalledRebootNeeded,
        AlreadyInstalled,
        Failed,
    }

    /// <summary>
    /// One step for the run script: a verified installer and its switches, or (for <see cref="UpdateComponent.App"/>)
    /// the verified zip of OmenFanPlus itself.
    /// </summary>
    internal sealed record ScriptItem(UpdateComponent Component, Version Version, string File, string Arguments);

    /// <summary>
    /// Runs verified installers after the app has closed. The app can't run them itself: it holds the PawnIO
    /// device open and runs on the very .NET runtime an update replaces, and so would any second copy of this exe.
    /// So the app writes a small Windows PowerShell 5.1 script (which runs on the .NET Framework, not on .NET 10)
    /// next to the verified files, starts it and quits. The script signals the app's quit event, waits for the
    /// app, takes the app's single-instance mutex so no copy can start meanwhile, runs each installer silently,
    /// writes the exit codes to result.txt and starts the app again, which shows the outcome and deletes the
    /// folder. Everything sits in an administrators-only folder under Program Files, and the app path and process
    /// id travel on the command line, so nothing running without administrator rights can change what runs.
    /// </summary>
    internal static class UpdateRunner
    {
        public const string FolderPrefix = "update-";
        private const string ScriptName = "run-update.ps1";
        private const string ResultName = "result.txt";
        private const string AppStillRunning = "app-still-running";
        private const string NotStarted = "not-started";
        private const string BadZip = "bad-zip";
        private const string BackupFailed = "backup-failed";
        private const string WrongFolder = "wrong-folder";
        private const string RobocopyLogName = "robocopy.log";
        /// <summary>robocopy's exit codes below this mean the copy worked (bits 0-2 are counts of what changed).</summary>
        private const int RobocopyFirstFailureCode = 8;

        /// <summary>Where update folders live: inside Program Files, which only administrators can write to.</summary>
        public static string StagingRoot { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OmenFanPlus", "updates");

        /// <summary>Folders without a result older than this are leftovers (a script that never ran to its end).</summary>
        private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(2);

        /// <summary>Automatic installs of the same version aren't retried before this has passed.</summary>
        public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);

        // ---- the script ----------------------------------------------------------------------------------

        /// <summary>
        /// The script text (CRLF, ASCII: the only paths in it are the staging folder's, under Program Files).
        /// The app's path, process id and kernel-object names are parameters, passed on the command line.
        /// </summary>
        internal static string BuildScript(IReadOnlyList<ScriptItem> items)
        {
            if (items.Count == 0)
                throw new ArgumentException("No installers to run", nameof(items));

            var sb = new StringBuilder();
            void Line(string text) => sb.Append(text).Append("\r\n");

            Line("# Written by OmenFanPlus to install dependency updates once the app has closed.");
            Line("# The app deletes this folder when it starts again. Windows PowerShell 5.1.");
            Line("param(");
            Line("    [Parameter(Mandatory = $true)] [string] $Exe,");
            Line("    [Parameter(Mandatory = $true)] [int] $AppPid,");
            Line("    [Parameter(Mandatory = $true)] [string] $MutexName,");
            Line("    [Parameter(Mandatory = $true)] [string] $QuitEvent,");
            Line("    [switch] $NoRestart");
            Line(")");
            Line("$ErrorActionPreference = 'Continue'");
            Line("# Cmdlets are loaded from the first module folder on PSModulePath, which by default is under the user's");
            Line("# Documents, writable without administrator rights. Only the system folder is used (set before any cmdlet).");
            Line("$env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')");
            Line($"$result = Join-Path -Path $PSScriptRoot -ChildPath '{ResultName}'");
            Line("# Ask the app to quit the way its own --quit switch does, and wait for it.");
            Line("try { [System.Threading.EventWaitHandle]::OpenExisting($QuitEvent).Set() | Out-Null } catch { }");
            Line("$app = Get-Process -Id $AppPid -ErrorAction SilentlyContinue");
            Line("if ($null -ne $app) { $app.WaitForExit(120000) | Out-Null }");
            Line("# Holding the app's single-instance mutex keeps a copy started by hand from opening PawnIO meanwhile.");
            Line("$lock = New-Object -TypeName System.Threading.Mutex -ArgumentList $false, $MutexName");
            Line("try { $owned = $lock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $owned = $true }");
            Line("if (-not $owned) {");
            Line($"    Set-Content -Path $result -Value '{AppStillRunning}' -Encoding Ascii");
            Line("} else {");
            foreach (var item in items)
            {
                if (item.Component == UpdateComponent.App)
                    AppendSelfUpdate(Line, item);
                else
                    AppendInstaller(Line, item);
            }
            Line("    $lock.ReleaseMutex()");
            Line("}");
            Line("$lock.Dispose()");
            Line("if (-not $NoRestart -and (Test-Path -LiteralPath $Exe)) {");
            Line("    # Through the startup task where it points at this exe: the Task Scheduler starts the app the way logon");
            Line("    # does, with nothing inherited from this script (no environment, no package identity of a packaged shell).");
            Line($"    $task = Get-ScheduledTask -TaskName '{StartupTask.TaskName}' -ErrorAction SilentlyContinue");
            Line("    $viaTask = $false");
            Line("    if ($null -ne $task -and $task.State -ne 'Disabled' -and ([string]@($task.Actions)[0].Execute).Trim('\"') -ieq $Exe) {");
            Line("        try { Start-ScheduledTask -InputObject $task -ErrorAction Stop; $viaTask = $true } catch { }");
            Line("    }");
            Line("    if (-not $viaTask) { Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Path $Exe -Parent) }");
            Line("}");
            return sb.ToString();
        }

        private static void AppendInstaller(Action<string> line, ScriptItem item)
        {
            line("    $p = $null");
            line($"    $code = '{NotStarted}'");
            line($"    try {{ $p = Start-Process -FilePath '{Quote(item.File)}' -ArgumentList '{Quote(item.Arguments)}' -WorkingDirectory $PSScriptRoot -PassThru -ErrorAction Stop }}");
            line("    catch { Add-Content -Path $result -Value ('# ' + $_.Exception.Message) -Encoding Ascii }");
            line("    if ($null -ne $p) { if ($p.WaitForExit(900000)) { $null = $p.HasExited; $code = $p.ExitCode } else { try { $p.Kill() } catch { }; $code = 'timeout' } }");
            line($"    Add-Content -Path $result -Value \"{item.Component} {item.Version} $code\" -Encoding Ascii");
        }

        // OmenFanPlus itself: unpack the verified zip inside the update folder, keep a copy of the installed files,
        // mirror the new ones over them (the updates folder, which holds this script, stays), and put the copy back
        // if the mirror fails. /MIR deletes everything the zip doesn't hold, so only a folder named OmenFanPlus
        // holding OmenFanPlus.exe is ever mirrored (install.ps1's rule). robocopy exit codes below 8 mean success;
        // its log is kept for the app to copy into its own log.
        private static void AppendSelfUpdate(Action<string> line, ScriptItem item)
        {
            // No quiet switches: the console output is discarded anyway, and the log should name every file.
            const string robocopyOptions = "/MIR /XD $updatesDir /R:2 /W:2 /NP /LOG+:$robocopyLog";
            line($"    $code = '{BadZip}'");
            line("    $restore = ''");
            line("    $installDir = Split-Path -Path $Exe -Parent");
            line("    $updatesDir = Join-Path -Path $installDir -ChildPath 'updates'");
            line("    $new = Join-Path -Path $PSScriptRoot -ChildPath 'app'");
            line("    $backup = Join-Path -Path $PSScriptRoot -ChildPath 'backup'");
            line($"    $robocopyLog = Join-Path -Path $PSScriptRoot -ChildPath '{RobocopyLogName}'");
            line("    $expanded = $false");
            line($"    try {{ Expand-Archive -LiteralPath '{Quote(item.File)}' -DestinationPath $new -Force -ErrorAction Stop; $expanded = $true }}");
            line("    catch { Add-Content -Path $result -Value ('# ' + $_.Exception.Message) -Encoding Ascii }");
            line("    $complete = $expanded");
            line("    foreach ($must in @('OmenFanPlus.exe', 'OmenFanPlus.dll', 'OmenFanPlus.runtimeconfig.json')) {");
            line("        if (-not (Test-Path -LiteralPath (Join-Path -Path $new -ChildPath $must))) { $complete = $false }");
            line("    }");
            line("    if ((Split-Path -Path $Exe -Leaf) -ne 'OmenFanPlus.exe' -or (Split-Path -Path $installDir -Leaf) -ne 'OmenFanPlus') {");
            line($"        $code = '{WrongFolder}'");
            line("    } elseif ($complete) {");
            line($"        & robocopy $installDir $backup {robocopyOptions} | Out-Null");
            line($"        if ($LASTEXITCODE -ge {RobocopyFirstFailureCode}) {{ $code = '{BackupFailed}' }} else {{");
            line($"            & robocopy $new $installDir {robocopyOptions} | Out-Null");
            line("            $code = $LASTEXITCODE");
            line($"            if ($code -ge {RobocopyFirstFailureCode}) {{ & robocopy $backup $installDir {robocopyOptions} | Out-Null; $restore = \" $LASTEXITCODE\" }}");
            line("        }");
            line("    }");
            line($"    Add-Content -Path $result -Value \"{item.Component} {item.Version} $code$restore\" -Encoding Ascii");
        }

        // Single-quoted PowerShell strings end at ' and at the typographic single quotes too.
        private static string Quote(string text) =>
            text.Replace("'", "''").Replace("‘", "''").Replace("’", "''").Replace("‚", "''").Replace("‛", "''");

        /// <summary>Writes the script into the update folder and starts it, hidden, with this process's rights.</summary>
        public static void Start(string folder, IReadOnlyList<ScriptItem> items) =>
            Start(folder, items, Application.ExecutablePath, Environment.ProcessId, Program.InstanceName, Program.QuitEventName);

        internal static void Start(string folder, IReadOnlyList<ScriptItem> items, string appExe, int appProcessId,
            string mutexName, string quitEventName)
        {
            var scriptPath = Path.Combine(folder, ScriptName);
            // With the byte-order mark Windows PowerShell reads the file as UTF-8 (without it, as the ANSI code page).
            File.WriteAllText(scriptPath, BuildScript(items), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var start = BuildStartInfo(folder, scriptPath, appExe, appProcessId, mutexName, quitEventName);
            using var process = Process.Start(start) ?? throw new UpdateException("The update script didn't start.");
            AppLog.Info($"Update: run script started (pid {process.Id}) from {folder}");
        }

        /// <summary>How PowerShell is started: by full path, hidden, with only the system module folder on its path.</summary>
        internal static ProcessStartInfo BuildStartInfo(string folder, string scriptPath, string appExe, int appProcessId,
            string mutexName, string quitEventName)
        {
            var systemPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
            var start = new ProcessStartInfo(Path.Combine(systemPowerShell, "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = folder,
            };
            foreach (var arg in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                         "-Exe", appExe, "-AppPid", appProcessId.ToString(CultureInfo.InvariantCulture),
                         "-MutexName", mutexName, "-QuitEvent", quitEventName,
                     })
                start.ArgumentList.Add(arg);

            // Windows PowerShell puts the user's Documents\WindowsPowerShell\Modules first on PSModulePath unless the
            // variable is set, and loads any cmdlet from the first module of the right name it finds there. That
            // folder is writable without administrator rights; the script runs with them. (The script pins the path
            // again itself, in case it is ever started another way.)
            start.Environment["PSModulePath"] = Path.Combine(systemPowerShell, "Modules");
            start.Environment.Remove("PSModuleAnalysisCachePath");
            return start;
        }

        // ---- the outcome ---------------------------------------------------------------------------------

        /// <summary>
        /// Reads what the last run script did and removes its folder: a text for the user, or null when there was
        /// none. Folders without a result that are hours old are leftovers and are removed quietly.
        /// </summary>
        public static string? TakeResult()
        {
            if (!Directory.Exists(StagingRoot))
                return null;

            var messages = new List<string>();
            foreach (var folder in Directory.EnumerateDirectories(StagingRoot, FolderPrefix + "*"))
            {
                try
                {
                    var resultPath = Path.Combine(folder, ResultName);
                    if (File.Exists(resultPath))
                    {
                        foreach (var line in File.ReadAllLines(resultPath))
                        {
                            var message = DescribeResultLine(line, IsInstalled);
                            if (message != null)
                                messages.Add(message);
                        }
                        // robocopy's own account of a self-update, kept in the app log since the folder goes now.
                        var robocopyLog = Path.Combine(folder, RobocopyLogName);
                        if (File.Exists(robocopyLog))
                        {
                            var lines = File.ReadAllLines(robocopyLog).Where(l => l.Trim().Length > 0).ToArray();
                            AppLog.Info($"Update: robocopy log ({lines.Length} lines; last {Math.Min(lines.Length, 40)} follow)");
                            foreach (var l in lines.TakeLast(40))
                                AppLog.Info("  " + l.TrimEnd());
                        }
                        Directory.Delete(folder, recursive: true);
                    }
                    else if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > AbandonedAfter)
                    {
                        AppLog.Warn($"Update: removing the unfinished update folder {folder}");
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error($"Update: couldn't read or remove {folder}", ex);
                }
            }

            RemoveEmptyStagingRoot();
            return messages.Count == 0 ? null : string.Join(Environment.NewLine, messages);
        }

        /// <summary>The updates folder is only there while an update is pending.</summary>
        internal static void RemoveEmptyStagingRoot()
        {
            try
            {
                if (Directory.Exists(StagingRoot) && !Directory.EnumerateFileSystemEntries(StagingRoot).Any())
                    Directory.Delete(StagingRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// One line of result.txt: "app-still-running", "# message from the script", or
        /// "Component Version exitcode" (the exit code may be "timeout", "not-started", "bad-zip", "backup-failed" or
        /// "wrong-folder"; a failed self-update adds robocopy's restore code as a fourth word).
        /// <paramref name="isInstalled"/> tells whether the version is on the machine now, which is what counts when
        /// the installer said 0.
        /// </summary>
        internal static string? DescribeResultLine(string line, Func<UpdateComponent, Version, bool> isInstalled)
        {
            line = line.Trim();
            if (line.Length == 0)
                return null;
            if (line == AppStillRunning)
                return "OmenFanPlus didn't close for the update (or was started again too soon), so nothing was installed.";
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return $"The update script reported: {line[2..].Trim()}";

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (3 or 4) || !Enum.TryParse<UpdateComponent>(parts[0], out var component) ||
                !Version.TryParse(parts[1], out var version))
                return $"The update script left an unreadable result: {line}";

            var name = $"{UpdateCatalog.Describe(component).DisplayName} {version}";
            if (parts[2] == "timeout")
                return $"{name}: the installer didn't finish within 15 minutes and was stopped.";
            if (parts[2] == NotStarted)
                return $"{name}: the installer couldn't be started (see the line before this one).";
            if (parts[2] == BadZip)
                return $"{name}: the downloaded package is incomplete (no OmenFanPlus.exe, .dll or .runtimeconfig.json), so the installed files were left alone.";
            if (parts[2] == BackupFailed)
                return $"{name}: the installed files couldn't be backed up first, so they were left alone.";
            if (parts[2] == WrongFolder)
                return $"{name}: this copy doesn't run as OmenFanPlus.exe in a folder named OmenFanPlus, so its files were left alone. Update it by hand.";
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode))
                return $"The update script left an unreadable result: {line}";

            if (component == UpdateComponent.App)
            {
                if (exitCode >= RobocopyFirstFailureCode)
                {
                    var restored = parts.Length == 4 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var restoreCode)
                        ? restoreCode < RobocopyFirstFailureCode ? "the previous version was put back."
                            : $"putting the previous version back failed too (robocopy exit code {restoreCode}); run install.ps1."
                        : "the previous version was put back.";
                    return $"{name} couldn't be copied over the installed files (robocopy exit code {exitCode}); {restored}";
                }
                return isInstalled(component, version)
                    ? $"{name} installed."
                    : $"{name}: the files were copied, but the running copy is still {Application.ProductVersion}. Run install.ps1 again.";
            }

            return InterpretExitCode(exitCode) switch
            {
                InstallOutcome.Installed when isInstalled(component, version) => $"{name} installed.",
                InstallOutcome.Installed => $"{name}: the installer finished, but that version isn't on this PC. Try again from Check for updates.",
                InstallOutcome.InstalledRebootNeeded => $"{name} installed; restart Windows to finish.",
                InstallOutcome.AlreadyInstalled => $"{name} was already installed.",
                _ => $"{name} failed to install (installer exit code {exitCode})" +
                     (component == UpdateComponent.PawnIo
                         ? "; close other programs that use PawnIO (FanControl, LibreHardwareMonitor) and try again."
                         : "."),
            };
        }

        /// <summary>Installer exit codes: Windows Installer's 3010/1641 mean "done, reboot", 1638 "already there".</summary>
        internal static InstallOutcome InterpretExitCode(int exitCode) => exitCode switch
        {
            0 => InstallOutcome.Installed,
            3010 or 1641 => InstallOutcome.InstalledRebootNeeded,
            1638 => InstallOutcome.AlreadyInstalled,
            _ => InstallOutcome.Failed,
        };

        // ---- not retrying a failed automatic install every few minutes --------------------------------------

        /// <summary>
        /// Whether the daily check may install this update on its own: not when the same version was attempted
        /// less than <see cref="RetryAfter"/> ago (the record is "Component Version yyyy-MM-ddTHH:mm:ssZ;..." in
        /// the settings). Installs the user asks for by hand aren't held back.
        /// </summary>
        internal static bool ShouldAutoInstall(string? attemptRecord, AvailableUpdate update, DateTime nowUtc)
        {
            foreach (var (component, version, when) in ParseAttempts(attemptRecord))
            {
                if (component == update.Component && version == update.Version && nowUtc - when < RetryAfter)
                    return false;
            }
            return true;
        }

        /// <summary>The record with these updates noted as attempted now (one entry per component).</summary>
        internal static string RecordAttempt(string? attemptRecord, IEnumerable<AvailableUpdate> updates, DateTime nowUtc)
        {
            var entries = ParseAttempts(attemptRecord).ToDictionary(a => a.Component, a => (a.Version, a.When));
            foreach (var update in updates)
                entries[update.Component] = (update.Version, nowUtc);
            return string.Join(";", entries.Select(e =>
                $"{e.Key} {e.Value.Version} {e.Value.When.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}"));
        }

        private static IEnumerable<(UpdateComponent Component, Version Version, DateTime When)> ParseAttempts(string? record)
        {
            if (string.IsNullOrWhiteSpace(record))
                yield break;
            foreach (var entry in record.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && Enum.TryParse<UpdateComponent>(parts[0], out var component) &&
                    Version.TryParse(parts[1], out var version) &&
                    DateTime.TryParseExact(parts[2], "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                    yield return (component, version, when);
            }
        }

        // ---- what is installed ---------------------------------------------------------------------------

        /// <summary>Whether this version (or a newer one) of the component is on the machine now.</summary>
        internal static bool IsInstalled(UpdateComponent component, Version version)
        {
            switch (component)
            {
                case UpdateComponent.DotNetRuntime:
                    var desktop = Path.Combine(DotnetRoot(), "shared", "Microsoft.WindowsDesktop.App");
                    return Directory.Exists(desktop) && Directory.EnumerateDirectories(desktop)
                        .Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : null)
                        .Any(v => v != null && v >= version);
                case UpdateComponent.PawnIo:
                    var installed = ReadInstalledPawnIoVersion();
                    return installed != null && installed >= version;
                case UpdateComponent.App:
                    // Asked by the copy that started after the update, so its own version is the answer.
                    return Version.TryParse(Application.ProductVersion, out var running) && running >= version;
                default:
                    return false;
            }
        }

        /// <summary>The dotnet folder this process runs from (…\shared\Microsoft.NETCore.App\10.0.x\ is three levels down).</summary>
        internal static string DotnetRoot() => DotnetRootFrom(RuntimeEnvironment.GetRuntimeDirectory());

        internal static string DotnetRootFrom(string runtimeDirectory)
        {
            var dir = runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < 3; i++)
                dir = Path.GetDirectoryName(dir) ?? dir;
            return dir;
        }

        // Read fresh each time: LibreHardwareMonitor only checks once, when the app starts.
        internal static Version? ReadInstalledPawnIoVersion()
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) ? version : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
