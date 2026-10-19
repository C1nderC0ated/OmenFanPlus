using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OmenFanPlus.Properties;
using OmenFanPlus.Lib;

namespace OmenFanPlus.Services
{
    /// <summary>What a check found: the text for the dialog, and the updates the app can install itself.</summary>
    internal sealed record UpdateReport(string Text, IReadOnlyList<AvailableUpdate> Available);

    /// <summary>
    /// Checks whether the .NET runtime or PawnIO has a newer release (2 minutes after the app starts, then every
    /// 24 hours, unless turned off in the tray menu) and tells the user, with the Chocolatey or winget command
    /// and the download link. With "Install updates automatically" on, or from the dialog's Install button, the
    /// app fetches the installer itself: see <see cref="DependencyInstaller"/> for how it is checked and run.
    /// OmenFanPlus itself is checked against its GitHub releases the same way. The checks send only GETs to
    /// builds.dotnet.microsoft.com, api.github.com and (as a fallback) github.com with a "OmenFanPlus/version"
    /// User-Agent.
    /// </summary>
    internal sealed class UpdateCheck : IDisposable
    {
        private const string RuntimeReleasesUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json";
        private const string PawnIoLatestApiUrl = "https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest";
        // Redirects to .../releases/tag/<version>; used when the API's hourly limit is used up.
        private const string PawnIoLatestPageUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest";
        private const string PawnIoDownloadPattern = "https://github.com/namazso/PawnIO.Setup/releases/download/{0}/PawnIO_setup.exe";
        private const string PawnIoDownload = "PawnIO_setup.exe from https://pawnio.eu";
        private static readonly string AppLatestApiUrl = $"https://api.github.com/repos/{UpdateCatalog.AppRepository}/releases/latest";
        private static readonly string AppLatestPageUrl = $"https://github.com/{UpdateCatalog.AppRepository}/releases/latest";

        private static readonly HttpClient Http = CreateClient(followRedirects: true);
        private static readonly HttpClient HttpNoRedirect = CreateClient(followRedirects: false);
        private readonly System.Threading.Timer _timer;
        private readonly DependencyInstaller _installer = new();
        private readonly HashSet<string> _notified = new();
        private int _running;
        private int _installing;

        /// <summary>Raised (on a thread-pool thread) with a short message when something can be updated.</summary>
        public event EventHandler<string>? UpdateAvailable;

        /// <summary>Raised (on a thread-pool thread) while an install is prepared, and when it fails.</summary>
        public event EventHandler<string>? InstallProgress;

        public UpdateCheck()
        {
            _installer.Progress += (sender, message) => InstallProgress?.Invoke(this, message);
            _timer = new System.Threading.Timer(_ => RunScheduled(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24));
        }

        public bool IsInstalling => _installing == 1;

        private async void RunScheduled()
        {
            try
            {
                if (!Settings.Default.CheckForUpdates)
                    return;

                var install = Settings.Default.AutoInstallUpdates && DependencyInstaller.CanInstall;
                var report = await RunAsync(notify: !install);
                if (!install || report.Available.Count == 0)
                    return;

                // A version that was tried in the last 24 hours and is still not installed failed; the app restarts
                // after every attempt, so without this it would try again 2 minutes later, for ever.
                var now = DateTime.UtcNow;
                var due = report.Available.Where(u => UpdateRunner.ShouldAutoInstall(Settings.Default.LastInstallAttempt, u, now)).ToList();
                foreach (var skipped in report.Available.Except(due))
                    AppLog.Info($"Update: not retrying {skipped.DisplayName} {skipped.Version} automatically yet (tried in the last 24 h)");
                if (due.Count == 0)
                    return;

                var error = await InstallAsync(due);
                if (error != null)
                    InstallProgress?.Invoke(this, error + " Right-click the tray icon > Check for updates to try again.");
            }
            catch (Exception ex)
            {
                AppLog.Error("Update check failed", ex);
            }
        }

        /// <summary>Runs the checks and returns the report for the "Check for updates" dialog.</summary>
        public async Task<UpdateReport> RunAsync(bool notify)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return new UpdateReport("A check is already running.", Array.Empty<AvailableUpdate>());

            try
            {
                var report = new StringBuilder();
                var available = new List<AvailableUpdate>();
                await CheckAppAsync(report, available, notify);
                report.AppendLine();
                await CheckRuntimeAsync(report, available, notify);
                report.AppendLine();
                await CheckPawnIoAsync(report, available, notify);

                return new UpdateReport(report.ToString(), available);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>
        /// Downloads and verifies the updates, then hands them to the run script, which closes the app, installs
        /// them and starts the app again. Returns null when that is under way, else a message saying what went
        /// wrong (nothing was installed then).
        /// </summary>
        public async Task<string?> InstallAsync(IReadOnlyList<AvailableUpdate> updates)
        {
            if (Interlocked.Exchange(ref _installing, 1) == 1)
                return "An installation is already running.";

            try
            {
                AppLog.Info($"Update: installing {string.Join(", ", updates.Select(u => $"{u.DisplayName} {u.Version}"))}");
                // Noted before anything is downloaded, so a failing download isn't repeated every 2 minutes either.
                Settings.Default.LastInstallAttempt = UpdateRunner.RecordAttempt(Settings.Default.LastInstallAttempt, updates, DateTime.UtcNow);
                FanModeController.TrySave();
                await _installer.StageAndInstallAsync(updates, CancellationToken.None);
                return null;
            }
            catch (UpdateException ex)
            {
                AppLog.Warn($"Update: {ex.Message}");
                return ex.Message;
            }
            catch (Exception ex)
            {
                AppLog.Error("Update: installing failed", ex);
                return $"The update couldn't be installed: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _installing, 0);
            }
        }

        private async Task CheckRuntimeAsync(StringBuilder report, List<AvailableUpdate> available, bool notify)
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
                    var installer = FindRuntimeInstaller(root, latest);
                    if (installer != null && DependencyInstaller.CanInstall)
                    {
                        available.Add(AvailableUpdate.DotNetRuntime(latest, installer.Value.Url, installer.Value.Sha512));
                        report.AppendLine("  OmenFanPlus can install it (it closes, installs and starts again). By hand:");
                    }
                    else
                    {
                        if (installer != null)
                            report.AppendLine($"  {DependencyInstaller.CannotInstallHint}");
                        report.AppendLine("  Quit OmenFanPlus first (the installer removes the version it runs on), then:");
                    }
                    AppendInstallOptions(report, "dotnet-10.0-desktopruntime", "Microsoft.DotNet.DesktopRuntime.10",
                        "the Windows x64 \"Desktop Runtime\" from https://dotnet.microsoft.com/download/dotnet/10.0");
                    Notify(notify, $"runtime {latest}",
                        $".NET runtime {latest} is available{(security ? " with security fixes" : "")}. " +
                        "Right-click the tray icon > Check for updates to install it.");
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
        internal static bool NewerReleaseHasSecurityFixes(JsonElement root, Version current)
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

        /// <summary>
        /// The Windows x64 Desktop Runtime installer of the given version from releases.json: its URL and the
        /// SHA-512 hash Microsoft lists next to it. Null when the metadata doesn't have it.
        /// </summary>
        internal static (Uri Url, string Sha512)? FindRuntimeInstaller(JsonElement root, Version version)
        {
            if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var release in releases.EnumerateArray())
            {
                if (!release.TryGetProperty("windowsdesktop", out var desktop) || desktop.ValueKind != JsonValueKind.Object ||
                    !desktop.TryGetProperty("version", out var versionElement) ||
                    !Version.TryParse(versionElement.GetString(), out var desktopVersion) || desktopVersion != version ||
                    !desktop.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var file in files.EnumerateArray())
                {
                    var rid = file.TryGetProperty("rid", out var ridElement) ? ridElement.GetString() : null;
                    var name = file.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    if (rid != "win-x64" || name == null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var url = file.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    var hash = file.TryGetProperty("hash", out var hashElement) ? hashElement.GetString()?.Trim() : null;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && hash is { Length: 128 } && hash.All(Uri.IsHexDigit))
                        return (uri, hash);
                }
            }

            return null;
        }

        private async Task CheckPawnIoAsync(StringBuilder report, List<AvailableUpdate> available, bool notify)
        {
            var installed = UpdateRunner.ReadInstalledPawnIoVersion();
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
                var latest = await GetLatestPawnIoReleaseAsync();
                if (latest == null)
                {
                    report.AppendLine("  Couldn't read the latest version.");
                    return;
                }

                if (latest.Value.Version > installed)
                {
                    report.AppendLine($"  {latest.Value.Version} is available.");
                    if (latest.Value.Sha256 == null)
                    {
                        report.AppendLine("  GitHub's API limit is reached right now, so the installer's hash can't be read and OmenFanPlus");
                        report.AppendLine("  won't install it until a later check. By hand, quit OmenFanPlus, then:");
                    }
                    else if (!DependencyInstaller.CanInstall)
                    {
                        report.AppendLine($"  {DependencyInstaller.CannotInstallHint}");
                        report.AppendLine("  Quit OmenFanPlus, then:");
                    }
                    else
                    {
                        available.Add(AvailableUpdate.PawnIo(latest.Value.Version, latest.Value.Url, latest.Value.Sha256));
                        // The app keeps the PawnIO device open, and the driver can't be replaced while it is in use.
                        report.AppendLine("  OmenFanPlus can install it (it closes, installs and starts again; close FanControl or");
                        report.AppendLine("  LibreHardwareMonitor first if they run). By hand, quit OmenFanPlus, then:");
                    }
                    AppendInstallOptions(report, "pawnio", "namazso.PawnIO", PawnIoDownload);
                    report.AppendLine("  and start OmenFanPlus again. Restart Windows if the installer asks for it.");
                    Notify(notify, $"pawnio {latest.Value.Version}",
                        $"PawnIO {latest.Value.Version} is available. Right-click the tray icon > Check for updates to install it.");
                }
                else
                {
                    report.AppendLine("  Up to date.");
                }
                AppLog.Info($"Update check: PawnIO {installed}, latest {latest.Value.Version}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  Couldn't check: {ex.Message}");
                AppLog.Warn($"Update check for PawnIO failed: {ex.Message}");
            }
        }

        private static async Task<(Version Version, Uri Url, string? Sha256)?> GetLatestPawnIoReleaseAsync()
        {
            using var response = await Http.GetAsync(PawnIoLatestApiUrl);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                // Unauthenticated API calls are limited to 60 an hour per IP address; the release page isn't. It
                // only gives the tag, so the installer's hash stays unknown and the release is reported, not installed.
                using var page = await HttpNoRedirect.GetAsync(PawnIoLatestPageUrl);
                var tag = page.Headers.Location?.OriginalString.TrimEnd('/').Split('/').LastOrDefault();
                if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var tagVersion))
                    return null;
                return (tagVersion, new Uri(string.Format(PawnIoDownloadPattern, Uri.EscapeDataString(tag!))), null);
            }

            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var release = json.RootElement;
            if (!Version.TryParse(release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V'), out var version))
                return null;

            var asset = FindPawnIoAsset(release);
            if (asset == null)
                return null;
            return (version, asset.Value.Url, asset.Value.Sha256);
        }

        /// <summary>PawnIO_setup.exe in a GitHub release: its URL and SHA-256 (null when the API gives no digest).</summary>
        internal static (Uri Url, string? Sha256)? FindPawnIoAsset(JsonElement release)
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (!string.Equals(name, "PawnIO_setup.exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return null;

                var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                var parsed = InstallerVerifier.ParseDigest(digest);
                return (uri, parsed is { Algorithm: "SHA256" } p ? p.Hex : null);
            }

            return null;
        }

        /// <summary>A release of the app on GitHub: the win-x64 zip, GitHub's digest of it, and the SHA256SUMS.txt beside it.</summary>
        internal sealed record AppRelease(Version Version, Uri Zip, string? Sha256, Uri? Sums);

        private async Task CheckAppAsync(StringBuilder report, List<AvailableUpdate> available, bool notify)
        {
            var current = Version.TryParse(Application.ProductVersion, out var running) ? running : new Version(0, 0);
            report.AppendLine($"OmenFanPlus {Application.ProductVersion}");
            try
            {
                using var response = await Http.GetAsync(AppLatestApiUrl);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    report.AppendLine("  No release has been published yet. To update, pull the latest code and run install.ps1 again.");
                    AppLog.Info($"Update check: OmenFanPlus {current}, no release published yet");
                    return;
                }

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    // Rate-limited: the release page's redirect gives the tag, but no hash, so only report.
                    using var page = await HttpNoRedirect.GetAsync(AppLatestPageUrl);
                    var tag = page.Headers.Location?.OriginalString.TrimEnd('/').Split('/').LastOrDefault();
                    if (!Version.TryParse(tag?.TrimStart('v', 'V'), out var tagVersion))
                        report.AppendLine("  Couldn't read the latest release.");
                    else if (tagVersion <= current)
                        report.AppendLine("  Up to date.");
                    else
                    {
                        report.AppendLine($"  {tagVersion} is available. GitHub's API limit is reached right now, so the package's hash can't be");
                        report.AppendLine("  read and OmenFanPlus won't install it until a later check. By hand:");
                        AppendAppManualOptions(report);
                        Notify(notify, $"app {tagVersion}", $"OmenFanPlus {tagVersion} is available. Right-click the tray icon > Check for updates for how to install it.");
                    }
                    return;
                }

                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var release = FindAppRelease(json.RootElement);
                if (release == null)
                {
                    report.AppendLine("  Couldn't read the latest release.");
                    return;
                }
                if (release.Version <= current)
                {
                    report.AppendLine("  Up to date.");
                    AppLog.Info($"Update check: OmenFanPlus {current}, latest {release.Version}");
                    return;
                }

                report.AppendLine($"  {release.Version} is available.");
                var hash = await ConfirmedAppHashAsync(release, report);
                if (hash != null && DependencyInstaller.CanSelfUpdate)
                {
                    available.Add(AvailableUpdate.App(release.Version, release.Zip, hash));
                    report.AppendLine("  OmenFanPlus can install it (it closes, replaces its files and starts again; settings stay). By hand:");
                }
                else if (hash != null)
                {
                    report.AppendLine($"  {(DependencyInstaller.CanInstall ? DependencyInstaller.CannotSelfUpdateHint : DependencyInstaller.CannotInstallHint)}");
                }
                AppendAppManualOptions(report);
                Notify(notify, $"app {release.Version}",
                    $"OmenFanPlus {release.Version} is available. Right-click the tray icon > Check for updates to install it.");
                AppLog.Info($"Update check: OmenFanPlus {current}, latest {release.Version}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  Couldn't check: {ex.Message}");
                AppLog.Warn($"Update check for OmenFanPlus failed: {ex.Message}");
            }
        }

        // GitHub's digest for the zip must agree with the SHA256SUMS.txt the release workflow wrote. Both come from
        // the same release, so this catches a broken upload rather than an attacker: the app has no code-signing
        // certificate, so its identity rests on TLS to GitHub plus these hashes.
        private static async Task<string?> ConfirmedAppHashAsync(AppRelease release, StringBuilder report)
        {
            if (release.Sha256 == null)
            {
                report.AppendLine("  GitHub lists no digest for the package, so OmenFanPlus won't install it.");
                return null;
            }
            if (release.Sums == null || !DependencyInstaller.IsAllowedSource(release.Sums))
            {
                report.AppendLine("  The release has no SHA256SUMS.txt, so OmenFanPlus won't install it.");
                return null;
            }

            var listed = ParseSha256Sums(await Http.GetStringAsync(release.Sums), Path.GetFileName(release.Zip.LocalPath));
            if (!string.Equals(listed, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                report.AppendLine("  The release's SHA256SUMS.txt doesn't match GitHub's digest for the package, so OmenFanPlus won't install it.");
                AppLog.Warn($"Update check: SHA256SUMS.txt ({listed ?? "no entry"}) and GitHub's digest ({release.Sha256}) disagree for {release.Zip}");
                return null;
            }
            return release.Sha256;
        }

        private static void AppendAppManualOptions(StringBuilder report)
        {
            report.AppendLine("    git pull in the source folder, then install.ps1;");
            report.AppendLine($"    or the zip from https://github.com/{UpdateCatalog.AppRepository}/releases/latest, unpacked over");
            report.AppendLine(@"    C:\Program Files\OmenFanPlus after quitting the app");
        }

        /// <summary>
        /// The win-x64 zip of a GitHub release of the app (name OmenFanPlus-&lt;version&gt;-win-x64.zip), GitHub's
        /// digest for it and the SHA256SUMS.txt asset. Null when the release has no such zip.
        /// </summary>
        internal static AppRelease? FindAppRelease(JsonElement release)
        {
            if (!release.TryGetProperty("tag_name", out var tagElement) ||
                !Version.TryParse(tagElement.GetString()?.TrimStart('v', 'V'), out var version) ||
                !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            Uri? zip = null;
            string? sha256 = null;
            Uri? sums = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var urlElement) &&
                          Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var parsed) ? parsed : null;
                if (name == null || url == null)
                    continue;

                if (name.StartsWith("OmenFanPlus-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zip = url;
                    var digest = InstallerVerifier.ParseDigest(asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null);
                    sha256 = digest is { Algorithm: "SHA256" } d ? d.Hex : null;
                }
                else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = url;
                }
            }

            return zip == null ? null : new AppRelease(version, zip, sha256, sums);
        }

        /// <summary>The hash listed for a file in a SHA256SUMS.txt ("hex  name" or "hex *name" per line), or null.</summary>
        internal static string? ParseSha256Sums(string text, string fileName)
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length <= 65 || line[64] != ' ')
                    continue;
                var hex = line[..64];
                var name = line[65..].TrimStart(' ', '*').Trim();
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) && hex.All(Uri.IsHexDigit))
                    return hex;
            }
            return null;
        }

        /// <summary>
        /// The command for the package manager this PC has (Chocolatey, else winget), plus the download link,
        /// which always works.
        /// </summary>
        private static void AppendInstallOptions(StringBuilder report, string chocoPackage, string wingetId, string download)
        {
            var choco = HasChocolatey();
            if (choco)
                report.AppendLine($"    choco upgrade {chocoPackage} -y");
            else if (HasWinget())
                report.AppendLine($"    winget install --id {wingetId} -e");
            report.AppendLine($"    {(choco || HasWinget() ? "or download" : "download")} {download}");
        }

        // Only decides which command to print; nothing is ever run from here.
        private static bool HasChocolatey()
        {
            var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
            return File.Exists(Path.Combine(root, "bin", "choco.exe"));
        }

        private static bool HasWinget() => File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"));

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
