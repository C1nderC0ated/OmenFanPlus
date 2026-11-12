using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OmenFanPlus.Services
{
    /// <summary>What an app package needs to start: read from its runtimeconfig.json.</summary>
    /// <param name="MinimumDesktopRuntime">The Desktop Runtime version the package asks for, e.g. 10.0.0.</param>
    /// <param name="RollForward">The host's roll-forward policy, e.g. "Minor" (the default) or "LatestMajor".</param>
    internal sealed record RuntimeRequirement(Version MinimumDesktopRuntime, string RollForward);

    /// <summary>
    /// Which .NET the app runs on. It is built for .NET <see cref="BuiltForMajor"/> and set to roll forward to the
    /// newest major installed, so it uses .NET 11 as soon as that is there and keeps starting on .NET 10 where it
    /// isn't. The update check follows the version it actually runs on, and offers the newest one it is tested on.
    /// </summary>
    internal static class DotNetRuntimes
    {
        /// <summary>The .NET version the app is built for; it starts on this or any newer one.</summary>
        public const int BuiltForMajor = 10;

        /// <summary>The newest .NET the app is tested on: the one it offers to install. Nothing newer is offered.</summary>
        public const int NewestTestedMajor = 11;

        public static string ReleasesUrl(int major) =>
            $"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{major}.0/releases.json";

        public static string ChocoPackage(int major) => $"dotnet-{major}.0-desktopruntime";

        public static string WingetId(int major) => $"Microsoft.DotNet.DesktopRuntime.{major}";

        public static string DownloadHint(int major) =>
            $"the Windows x64 \"Desktop Runtime\" from https://dotnet.microsoft.com/download/dotnet/{major}.0";

        /// <summary>The dotnet folder this process runs from (…\shared\Microsoft.NETCore.App\10.0.x\ is three levels down).</summary>
        public static string DotnetRoot() => DotnetRootFrom(RuntimeEnvironment.GetRuntimeDirectory());

        internal static string DotnetRootFrom(string runtimeDirectory)
        {
            var dir = runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < 3; i++)
                dir = Path.GetDirectoryName(dir) ?? dir;
            return dir;
        }

        /// <summary>Where the Desktop Runtimes are installed, one folder per version.</summary>
        public static string DesktopRuntimeFolder() => Path.Combine(DotnetRoot(), "shared", "Microsoft.WindowsDesktop.App");

        /// <summary>The Desktop Runtime versions on this PC (released ones; previews don't count).</summary>
        public static IReadOnlyList<Version> InstalledDesktopRuntimes()
        {
            try
            {
                var folder = DesktopRuntimeFolder();
                return Directory.Exists(folder)
                    ? ParseRuntimeFolders(Directory.EnumerateDirectories(folder).Select(Path.GetFileName))
                    : Array.Empty<Version>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<Version>();
            }
        }

        /// <summary>Folder names like "10.0.15" as versions; preview names ("11.0.0-rc.2.26501.1") are left out.</summary>
        internal static IReadOnlyList<Version> ParseRuntimeFolders(IEnumerable<string?> names) =>
            names.Select(n => Version.TryParse(n, out var v) ? v : null).Where(v => v != null).Select(v => v!).OrderBy(v => v).ToList();

        /// <summary>"latest-runtime" of a channel's releases.json; null while the channel only has previews.</summary>
        internal static Version? LatestRuntime(JsonElement root) =>
            root.TryGetProperty("latest-runtime", out var latest) && latest.ValueKind == JsonValueKind.String &&
            Version.TryParse(latest.GetString(), out var version) ? version : null;

        /// <summary>The channel's "support-phase" in releases.json: preview, go-live, active, maintenance or eol.</summary>
        internal static string? SupportPhase(JsonElement root) =>
            root.TryGetProperty("support-phase", out var phase) && phase.ValueKind == JsonValueKind.String ? phase.GetString() : null;

        /// <summary>A finished release that still gets fixes: the only kind that is offered as a new .NET version.</summary>
        internal static bool IsSupportedRelease(JsonElement root) => SupportPhase(root) is "active" or "maintenance";

        /// <summary>
        /// Reads a runtimeconfig.json: the Microsoft.WindowsDesktop.App version it asks for (or, failing that, the
        /// Microsoft.NETCore.App one) and its roll-forward policy. Null when it names neither.
        /// </summary>
        internal static RuntimeRequirement? ReadRequirement(string runtimeConfigJson)
        {
            try
            {
                using var json = JsonDocument.Parse(runtimeConfigJson);
                if (!json.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object)
                    return null;

                var frameworks = new List<JsonElement>();
                if (options.TryGetProperty("frameworks", out var list) && list.ValueKind == JsonValueKind.Array)
                    frameworks.AddRange(list.EnumerateArray());
                if (options.TryGetProperty("framework", out var single) && single.ValueKind == JsonValueKind.Object)
                    frameworks.Add(single);

                Version? desktop = null, core = null;
                string? frameworkPolicy = null;
                foreach (var framework in frameworks)
                {
                    var name = framework.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!framework.TryGetProperty("version", out var v) || !Version.TryParse(v.GetString(), out var version))
                        continue;
                    if (name == "Microsoft.WindowsDesktop.App")
                    {
                        desktop = version;
                        // A policy on the framework reference itself overrides the app-wide one.
                        if (framework.TryGetProperty("rollForward", out var own) && own.ValueKind == JsonValueKind.String)
                            frameworkPolicy = own.GetString();
                    }
                    else if (name == "Microsoft.NETCore.App")
                    {
                        core = version;
                    }
                }

                var minimum = desktop ?? core;
                if (minimum == null)
                    return null;

                var policy = frameworkPolicy ??
                             (options.TryGetProperty("rollForward", out var rf) && rf.ValueKind == JsonValueKind.String ? rf.GetString() : null) ??
                             "Minor";
                return new RuntimeRequirement(minimum, policy);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The requirement of the app inside a release zip (its OmenFanPlus.runtimeconfig.json), or null.</summary>
        internal static RuntimeRequirement? ReadRequirementFromZip(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry("OmenFanPlus.runtimeconfig.json");
                if (entry == null || entry.Length > 64 * 1024)
                    return null;
                using var reader = new StreamReader(entry.Open());
                return ReadRequirement(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Whether one installed Desktop Runtime version lets an app with this requirement start.</summary>
        internal static bool Satisfies(RuntimeRequirement requirement, Version installed)
        {
            var min = requirement.MinimumDesktopRuntime;
            if (installed < min)
                return false;

            return requirement.RollForward.ToLowerInvariant() switch
            {
                // Any newer version will do, across major versions.
                "major" or "latestmajor" => true,
                "latestpatch" => installed.Major == min.Major && installed.Minor == min.Minor,
                "disable" => installed == min,
                // "Minor" (the default) and "LatestMinor": the same major version.
                _ => installed.Major == min.Major,
            };
        }

        /// <summary>Whether an app with this requirement can start with these Desktop Runtimes installed.</summary>
        internal static bool CanStart(RuntimeRequirement requirement, IEnumerable<Version> installed) =>
            installed.Any(v => Satisfies(requirement, v));

        /// <summary>
        /// The same rule as <see cref="Satisfies"/> as a PowerShell condition on a [version] variable, for the run
        /// script's last check before it replaces the app's files.
        /// </summary>
        internal static string PowerShellCondition(RuntimeRequirement requirement, string variable)
        {
            var min = requirement.MinimumDesktopRuntime;
            var atLeast = $"{variable} -ge [version]'{min}'";
            return requirement.RollForward.ToLowerInvariant() switch
            {
                "major" or "latestmajor" => atLeast,
                "latestpatch" => $"{atLeast} -and {variable}.Major -eq {min.Major} -and {variable}.Minor -eq {min.Minor}",
                "disable" => $"{variable} -eq [version]'{min}'",
                _ => $"{atLeast} -and {variable}.Major -eq {min.Major}",
            };
        }
    }
}
