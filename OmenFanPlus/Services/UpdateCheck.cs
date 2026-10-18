using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OmenFanPlus.Properties;
using OmenHubLight.Lib;

namespace OmenFanPlus.Services
{
    /// <summary>
    /// Checks whether the .NET runtime or PawnIO has a newer release and tells the user how to update
    /// (2 minutes after the app starts, then every 24 hours, unless turned off in the tray menu).
    /// Nothing is downloaded or installed automatically: the runtime installer removes the running version, and
    /// PawnIO is a kernel driver, so both are left to the user (with the Chocolatey or winget command, or a
    /// download link). The only requests are GETs to builds.dotnet.microsoft.com, api.github.com and (as a
    /// fallback) github.com with a "OmenFanPlus/version" User-Agent. The app itself has no release feed; it is
    /// updated by re-running install.ps1.
    /// </summary>
    internal sealed class UpdateCheck : IDisposable
    {
        private const string RuntimeReleasesUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
        private const string PawnIoLatestApiUrl = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
        // Redirects to .../releases/tag/<version>; used when the API's hourly limit is used up.
        private const string PawnIoLatestPageUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest";

        private static readonly HttpClient Http = CreateClient(followRedirects: true);
        private static readonly HttpClient HttpNoRedirect = CreateClient(followRedirects: false);
        private readonly System.Threading.Timer _timer;
        private readonly HashSet<string> _notified = new();
        private int _running;

        /// <summary>Raised (on a thread-pool thread) with a short message when something can be updated.</summary>
        public event EventHandler<string>? UpdateAvailable;

        public UpdateCheck()
        {
            _timer = new System.Threading.Timer(_ => RunScheduled(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));
        }

        private async void RunScheduled()
        {
            try
            {
                if (Settings.Default.CheckForUpdates)
                    await RunAsync(notify: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("Update check failed", ex);
            }
        }

        /// <summary>Runs the checks and returns a report for the "Check for updates" dialog.</summary>
        public async Task<string> RunAsync(bool notify)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return "A check is already running.";

            try
            {
                var report = new StringBuilder();
                report.AppendLine($"OmenFanPlus {Application.ProductVersion}");
                report.AppendLine("  Update it by pulling the latest code and running install.ps1 again.");
                report.AppendLine();

                await CheckRuntimeAsync(report, notify);
                report.AppendLine();
                await CheckPawnIoAsync(report, notify);

                return report.ToString();
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private async Task CheckRuntimeAsync(StringBuilder report, bool notify)
        {
            var current = Environment.Version;
            report.AppendLine($".NET runtime {current}");
            try
            {
                using var json = JsonDocument.Parse(await Http.GetStringAsync(RuntimeReleasesUrl));
                var root = json.RootElement;
                if (!Version.TryParse(root.GetProperty("latest-runtime").GetString(), out var latest))
                {
                    report.AppendLine("  Couldn't read the latest version.");
                    return;
                }

                if (latest > current)
                {
                    var security = NewerReleaseHasSecurityFixes(root, current);
                    report.AppendLine($"  {latest} is available{(security ? " and includes security fixes" : "")}.");
                    report.AppendLine("  Quit OmenFanPlus first (the installer removes the version it runs on), then:");
                    AppendInstallOptions(report, "dotnet-10.0-desktopruntime", "Microsoft.DotNet.DesktopRuntime.10",
                        "the Windows x64 \"Desktop Runtime\" from https://dotnet.microsoft.com/download/dotnet/10.0");
                    report.AppendLine("  and start OmenFanPlus again.");
                    Notify(notify, $"runtime {latest}",
                        $".NET runtime {latest} is available{(security ? " with security fixes" : "")}. " +
                        "Right-click the tray icon > Check for updates for how to install it.");
                }
                else
                {
                    report.AppendLine("  Up to date.");
                }
                AppLog.Info($"Update check: .NET runtime {current}, latest {latest}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  Couldn't check: {ex.Message}");
                AppLog.Warn($"Update check for the .NET runtime failed: {ex.Message}");
            }
        }

        // The latest release alone doesn't say whether a skipped one in between was a security release.
        private static bool NewerReleaseHasSecurityFixes(JsonElement root, Version current)
        {
            if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var release in releases.EnumerateArray())
            {
                var versionText = release.TryGetProperty("runtime", out var runtime) &&
                                  runtime.TryGetProperty("version", out var runtimeVersion)
                    ? runtimeVersion.GetString()
                    : release.TryGetProperty("release-version", out var releaseVersion) ? releaseVersion.GetString() : null;

                // Preview/RC versions ("10.0.0-rc.2...") don't parse and are skipped.
                if (Version.TryParse(versionText, out var version) && version > current &&
                    release.TryGetProperty("security", out var security) && security.ValueKind == JsonValueKind.True)
                    return true;
            }

            return false;
        }

        private async Task CheckPawnIoAsync(StringBuilder report, bool notify)
        {
            var installed = ReadInstalledPawnIoVersion();
            report.AppendLine(installed == null ? "PawnIO: not installed (optional)" : $"PawnIO {installed}");
            if (installed == null)
            {
                report.AppendLine("  It lets the app read the CPU's own temperature sensor instead of the ACPI zone.");
                report.AppendLine("  To install it:");
                AppendInstallOptions(report, "pawnio", "namazso.PawnIO", PawnIoDownload);
                report.AppendLine("  then restart OmenFanPlus.");
                return;
            }

            if (!TemperatureService.PawnIoInstalled)
                report.AppendLine("  Restart OmenFanPlus to start using it.");

            try
            {
                var latest = await GetLatestPawnIoVersionAsync();
                if (latest == null)
                {
                    report.AppendLine("  Couldn't read the latest version.");
                    return;
                }

                if (latest > installed)
                {
                    // The app keeps the PawnIO device open, and the driver can't be replaced while it is in use.
                    report.AppendLine($"  {latest} is available. Quit OmenFanPlus (and other programs that use PawnIO, such as");
                    report.AppendLine("  FanControl or LibreHardwareMonitor), then:");
                    AppendInstallOptions(report, "pawnio", "namazso.PawnIO", PawnIoDownload);
                    report.AppendLine("  and start OmenFanPlus again. Restart Windows if the installer asks for it.");
                    Notify(notify, $"pawnio {latest}",
                        $"PawnIO {latest} is available. Right-click the tray icon > Check for updates for how to install it.");
                }
                else
                {
                    report.AppendLine("  Up to date.");
                }
                AppLog.Info($"Update check: PawnIO {installed}, latest {latest}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  Couldn't check: {ex.Message}");
                AppLog.Warn($"Update check for PawnIO failed: {ex.Message}");
            }
        }

        private static async Task<Version?> GetLatestPawnIoVersionAsync()
        {
            string? tag;
            using (var response = await Http.GetAsync(PawnIoLatestApiUrl))
            {
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    // Unauthenticated API calls are limited to 60 an hour per IP address; the release page isn't.
                    using var page = await HttpNoRedirect.GetAsync(PawnIoLatestPageUrl);
                    tag = page.Headers.Location?.OriginalString.TrimEnd('/').Split('/').LastOrDefault();
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    tag = json.RootElement.GetProperty("tag_name").GetString();
                }
            }

            return Version.TryParse(tag?.TrimStart('v', 'V'), out var version) ? version : null;
        }

        private const string PawnIoDownload = "PawnIO_setup.exe from https://pawnio.eu";

        /// <summary>
        /// The command for the package manager this PC has (Chocolatey, else winget), plus the download link,
        /// which always works.
        /// </summary>
        private static void AppendInstallOptions(StringBuilder report, string chocoPackage, string wingetId, string download)
        {
            if (HasChocolatey())
                report.AppendLine($"    choco upgrade {chocoPackage} -y");
            else if (HasWinget())
                report.AppendLine($"    winget install --id {wingetId} -e");
            report.AppendLine($"    {(HasChocolatey() || HasWinget() ? "or download" : "download")} {download}");
        }

        private static bool HasChocolatey()
        {
            var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
            return File.Exists(Path.Combine(root, "bin", "choco.exe"));
        }

        private static bool HasWinget() => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"));

        // Read fresh each time: LibreHardwareMonitor only checks once, when the app starts.
        private static Version? ReadInstalledPawnIoVersion()
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

        private void Notify(bool notify, string key, string message)
        {
            // One balloon per new version per run of the app.
            if (notify && _notified.Add(key))
                UpdateAvailable?.Invoke(this, message);
        }

        private static HttpClient CreateClient(bool followRedirects)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = followRedirects,
                AutomaticDecompression = DecompressionMethods.All,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OmenFanPlus", Application.ProductVersion));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        public void Dispose() => _timer.Dispose();
    }
}
