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

        /// <summary>What to do about the .NET runtime before an app update goes in.</summary>
        internal enum RuntimeStep
        {
            /// <summary>A runtime that can start the new version is installed (or is being installed in this run).</summary>
            NotNeeded,
            /// <summary>It needs a runtime this PC lacks, and someone can be asked whether to install it.</summary>
            Ask,
            /// <summary>It needs a runtime this PC lacks, and nobody is there to ask: the update is left for the dialog.</summary>
            Refuse,
        }

        internal static RuntimeStep RuntimeStepFor(RuntimeRequirement requires, IEnumerable<Version> runtimes, bool canAsk) =>
            DotNetRuntimes.CanStart(requires, runtimes) ? RuntimeStep.NotNeeded
            : canAsk ? RuntimeStep.Ask
            : RuntimeStep.Refuse;

        /// <summary>
        /// Downloads and verifies every update into a fresh administrators-only folder, then starts the run
        /// script, which asks this app to quit. Throws <see cref="UpdateException"/> with a message for the user
        /// when something is off; nothing has been installed in that case.
        /// </summary>
        /// <param name="confirmNewRuntime">
        /// Asked when an app update needs a .NET runtime this PC lacks: (the app update, the runtime) and whether to
        /// install the runtime too. Null when nobody can be asked (the automatic check): such an update is refused.
        /// </param>
        public async Task StageAndInstallAsync(IReadOnlyList<AvailableUpdate> updates,
            Func<AvailableUpdate, AvailableUpdate, bool>? confirmNewRuntime, CancellationToken ct)
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
                // What the new app version can count on: the runtimes on this PC, plus any being installed in this run.
                var runtimes = DotNetRuntimes.InstalledDesktopRuntimes()
                    .Concat(updates.Where(u => u.Component == UpdateComponent.DotNetRuntime).Select(u => u.Version))
                    .ToList();

                foreach (var update in updates)
                {
                    var path = await DownloadVerifiedAsync(update, folder, ct);
                    RuntimeRequirement? requires = null;
                    if (update.Component == UpdateComponent.App)
                    {
                        requires = DotNetRuntimes.ReadRequirementFromZip(path) ?? throw new UpdateException(
                            $"{update.DisplayName} {update.Version}: the package doesn't say which .NET it needs, so it wasn't installed.");
                        var major = requires.MinimumDesktopRuntime.Major;
                        var step = RuntimeStepFor(requires, runtimes, canAsk: confirmNewRuntime != null);
                        if (step == RuntimeStep.Refuse)
                            throw new UpdateException(
                                $"{update.DisplayName} {update.Version} is built for .NET {major}, which isn't on this PC, and a new " +
                                ".NET version is only installed when you ask.");
                        if (step == RuntimeStep.Ask)
                        {
                            // A version built for a newer .NET: that runtime goes in first, in the same run, if the
                            // user agrees.
                            var runtime = await FindRuntimeForAsync(update, requires, ct);
                            if (!confirmNewRuntime!(update, runtime))
                                throw new UpdateException(
                                    $"{update.DisplayName} {update.Version} needs .NET {major}, so it wasn't installed. Nothing was changed.");
                            var runtimePath = await DownloadVerifiedAsync(runtime, folder, ct);
                            items.Add(new ScriptItem(runtime.Component, runtime.Version, runtimePath, runtime.Info.InstallerArguments));
                            runtimes.Add(runtime.Version);
                        }
                    }
                    items.Add(new ScriptItem(update.Component, update.Version, path, update.Info.InstallerArguments, requires));
                }

                UpdateRunner.Start(folder, OrderForScript(items));
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
        /// Finds the newest Desktop Runtime installer of a .NET version at Microsoft. Set by the update check, which
        /// owns the release metadata; an app update that needs a runtime this PC lacks is refused without it.
        /// </summary>
        public Func<int, CancellationToken, Task<AvailableUpdate?>>? ResolveRuntime { get; set; }

        private async Task<AvailableUpdate> FindRuntimeForAsync(AvailableUpdate app, RuntimeRequirement requires, CancellationToken ct)
        {
            var major = requires.MinimumDesktopRuntime.Major;
            Progress?.Invoke(this, $"{app.DisplayName} {app.Version} needs .NET {major}; fetching that first...");
            AvailableUpdate? runtime = null;
            try
            {
                if (ResolveRuntime != null)
                    runtime = await ResolveRuntime(major, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
            {
                AppLog.Warn($"Update: looking up .NET {major} failed: {ex.Message}");
            }

            if (runtime == null || !DotNetRuntimes.Satisfies(requires, runtime.Version))
                throw new UpdateException(
                    $"{app.DisplayName} {app.Version} needs the .NET {major} Desktop Runtime, which isn't installed and couldn't be " +
                    $"found at Microsoft. Nothing was installed; install .NET {major} by hand, then try again.");
            AppLog.Info($"Update: {app.DisplayName} {app.Version} needs .NET {requires.MinimumDesktopRuntime} ({requires.RollForward}); adding .NET Desktop Runtime {runtime.Version}");
            return runtime;
        }

        /// <summary>
        /// Installers before the app's own files: a new app version is only put in place once the runtime it needs
        /// has been installed (the script checks that again before it replaces anything).
        /// </summary>
        internal static IReadOnlyList<ScriptItem> OrderForScript(IEnumerable<ScriptItem> items) =>
            items.OrderBy(i => i.Component == UpdateComponent.App ? 1 : 0).ToList();

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
