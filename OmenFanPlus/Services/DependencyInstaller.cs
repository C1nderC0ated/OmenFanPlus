using System.Net;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OmenFanPlus.Services
{
    /// <summary>A problem the user can act on; the message is shown as is.</summary>
    internal sealed class UpdateException : Exception
    {
        public UpdateException(string message) : base(message) { }
    }

    /// <summary>
    /// Fetches installers for the app's dependencies and hands them to <see cref="UpdateRunner"/>. Every file is
    /// downloaded only from the publisher's own site, checked against the hash the publisher lists and against its
    /// Authenticode signature, and kept in an administrators-only folder; this process never runs it.
    /// </summary>
    internal sealed class DependencyInstaller
    {
        /// <summary>Where a download may start: Microsoft's CDNs, and the PawnIO author's GitHub releases.</summary>
        public static readonly string[] AllowedHosts =
            { "builds.dotnet.microsoft.com", "download.visualstudio.microsoft.com", "github.com" };

        /// <summary>On github.com only these repositories' release files: PawnIO's, and OmenFanPlus's own.</summary>
        public static readonly string[] GitHubPathPrefixes =
            { "/namazso/PawnIO.Setup/releases/download/", $"/{UpdateCatalog.AppRepository}/releases/download/" };

        /// <summary>Where a download may end after redirects (GitHub serves release files from its CDN).</summary>
        public static readonly string[] AllowedFinalHosts =
            { "builds.dotnet.microsoft.com", "download.visualstudio.microsoft.com", "github.com",
              "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

        private const long MaxDownloadBytes = 400L * 1024 * 1024;
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);
        private static readonly HttpClient Http = CreateClient();

        /// <summary>Progress lines for a balloon or dialog, raised on the calling thread.</summary>
        public event EventHandler<string>? Progress;

        public static bool IsAllowedSource(Uri url)
        {
            if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps ||
                !AllowedHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase))
                return false;
            return !string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                   GitHubPathPrefixes.Any(prefix => url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal));
        }

        public static bool IsAllowedFinalHost(Uri url) =>
            url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
            AllowedFinalHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);

        public static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Installing is for the copy in Program Files with administrator rights only: the run script starts that
        /// exe again with those rights, so it must be a file nobody without them can replace (the same rule the
        /// startup task follows).
        /// </summary>
        public static bool CanInstall => IsElevated() && StartupTask.IsInstalledCopy(Application.ExecutablePath);

        public const string CannotInstallHint =
            "Installing from here needs the copy in Program Files running with administrator rights (run install.ps1). " +
            "Install by hand with the commands in the update dialog.";

        /// <summary>
        /// install.ps1's rule for a folder it may mirror into, and so the rule for a self-update, which mirrors the
        /// same way: the exe is OmenFanPlus.exe in a folder named OmenFanPlus. Anything else (a zip unpacked into a
        /// shared tools folder, say) would lose every other file in that folder.
        /// </summary>
        public static bool IsOwnFolder(string exePath) =>
            string.Equals(Path.GetFileName(exePath), "OmenFanPlus.exe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(exePath) ?? ""), "OmenFanPlus", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether this copy may replace its own files: <see cref="CanInstall"/> and in its own folder.</summary>
        public static bool CanSelfUpdate => CanInstall && IsOwnFolder(Application.ExecutablePath);

        public const string CannotSelfUpdateHint =
            "This copy doesn't run as OmenFanPlus.exe in a folder named OmenFanPlus, so it won't replace its files itself.";

        /// <summary>
        /// Downloads and verifies every update into a fresh administrators-only folder, then starts the run
        /// script, which asks this app to quit. Throws <see cref="UpdateException"/> with a message for the user
        /// when something is off; nothing has been installed in that case.
        /// </summary>
        public async Task StageAndInstallAsync(IReadOnlyList<AvailableUpdate> updates, CancellationToken ct)
        {
            if (updates.Count == 0)
                throw new UpdateException("Nothing to install.");
            if (!CanInstall)
                throw new UpdateException(CannotInstallHint);
            if (updates.Any(u => u.Component == UpdateComponent.App) && !IsOwnFolder(Application.ExecutablePath))
                throw new UpdateException(CannotSelfUpdateHint);

            var folder = CreateStagingFolder(UpdateRunner.StagingRoot);
            try
            {
                var items = new List<ScriptItem>();
                foreach (var update in updates)
                {
                    var path = await DownloadVerifiedAsync(update, folder, ct);
                    items.Add(new ScriptItem(update.Component, update.Version, path, update.Info.InstallerArguments));
                }

                UpdateRunner.Start(folder, items);
                Progress?.Invoke(this, "Verified. OmenFanPlus closes now, installs the update and starts again.");
            }
            catch
            {
                try { Directory.Delete(folder, recursive: true); } catch { }
                UpdateRunner.RemoveEmptyStagingRoot();
                throw;
            }
        }

        /// <summary>
        /// A new folder under <paramref name="root"/> (itself under Program Files) that only SYSTEM and
        /// administrators can touch, whatever the parent folders say: the installers and the script wait in it
        /// between the check here and the run.
        /// </summary>
        internal static string CreateStagingFolder(string root)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            }

            Directory.CreateDirectory(root);
            var folder = Path.Combine(root, UpdateRunner.FolderPrefix + Guid.NewGuid().ToString("N"));
            new DirectoryInfo(folder).Create(security);
            return folder;
        }

        internal async Task<string> DownloadVerifiedAsync(AvailableUpdate update, string folder, CancellationToken ct)
        {
            if (!IsAllowedSource(update.DownloadUrl))
                throw new UpdateException($"{update.DisplayName}: {update.DownloadUrl.Host} isn't the publisher's download site; not downloading.");
            if (string.IsNullOrWhiteSpace(update.ExpectedHash))
                throw new UpdateException($"{update.DisplayName}: the publisher's hash for the installer isn't known; not downloading.");

            var path = Path.Combine(folder, update.FileName);
            Progress?.Invoke(this, $"Downloading {update.DisplayName} {update.Version}...");
            AppLog.Info($"Update: downloading {update.DownloadUrl}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(DownloadTimeout);
            try
            {
                using var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                var finalUrl = response.RequestMessage?.RequestUri ?? update.DownloadUrl;
                if (!IsAllowedFinalHost(finalUrl))
                    throw new UpdateException($"{update.DisplayName}: the download was redirected to {finalUrl.Host}, which isn't the publisher's; not downloading.");
                if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                    throw new UpdateException($"{update.DisplayName}: the download is unexpectedly large ({response.Content.Headers.ContentLength / (1024 * 1024)} MB); not downloading.");

                // FileShare.None: nothing else can touch the file while it is written.
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token))
                {
                    var buffer = new byte[1 << 16];
                    long total = 0;
                    int read;
                    while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        total += read;
                        if (total > MaxDownloadBytes)
                            throw new UpdateException($"{update.DisplayName}: the download exceeded {MaxDownloadBytes / (1024 * 1024)} MB; stopped.");
                        await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new UpdateException($"{update.DisplayName}: the download didn't finish within {DownloadTimeout.TotalMinutes} minutes.");
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException($"{update.DisplayName}: the download failed ({ex.Message}).");
            }

            Progress?.Invoke(this, $"Checking {update.DisplayName} {update.Version}...");
            var problem = await VerifyAsync(path, update.ExpectedHash, update.HashAlgorithm, update.SignerOrganization, ct);
            if (problem != null)
                throw new UpdateException($"{update.DisplayName}: {problem} The file was deleted and nothing was installed.");

            AppLog.Info($"Update: {update.FileName} verified ({update.HashAlgorithm} matches" +
                        (update.SignerOrganization == null ? ")" : $", signed by {update.SignerOrganization})"));
            return path;
        }

        /// <summary>
        /// Null when the file's hash matches and, where a signer is expected, it carries a valid signature from
        /// that organization; otherwise what is wrong. The file is deleted when it fails.
        /// </summary>
        public static async Task<string?> VerifyAsync(string path, string expectedHash, string hashAlgorithm,
            string? signerOrganization, CancellationToken ct)
        {
            string? problem = null;
            try
            {
                string actual;
                await using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    actual = await InstallerVerifier.ComputeHashAsync(stream, hashAlgorithm, ct);
                if (!InstallerVerifier.HashMatches(expectedHash, actual))
                    problem = $"its {hashAlgorithm} hash doesn't match the publisher's ({actual[..16]}... vs {expectedHash.Trim()[..Math.Min(16, expectedHash.Trim().Length)]}...).";

                if (problem == null && signerOrganization != null)
                {
                    var signature = InstallerVerifier.VerifySignature(path, signerOrganization);
                    if (!signature.Valid)
                        problem = $"its signature failed the check: {signature.Detail}.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                problem = $"it couldn't be checked ({ex.Message}).";
            }

            if (problem != null)
            {
                AppLog.Warn($"Update: {Path.GetFileName(path)} rejected: {problem}");
                try { File.Delete(path); } catch { }
            }
            return problem;
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,           // GitHub serves release files from its CDN; the final host is checked
                AutomaticDecompression = DecompressionMethods.None,
                MaxAutomaticRedirections = 5,
            })
            {
                Timeout = Timeout.InfiniteTimeSpan, // per-download token instead
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OmenFanPlus", Application.ProductVersion));
            return client;
        }
    }
}
