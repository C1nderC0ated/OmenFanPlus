using System.IO.Compression;
using System.Text.Json;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

/// <summary>Which .NET the app runs on, which one it offers, and what an app update needs before it is installed.</summary>
public class DotNetRuntimeTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // ---- the runtime these tests run on --------------------------------------------------------------

    /// <summary>
    /// The release workflow runs the tests once pinned to .NET 10 and once on the newest .NET, and says which it
    /// expects in OMENFANPLUS_EXPECT_RUNTIME_MAJOR, so a run that quietly used the other one fails here.
    /// </summary>
    [Fact]
    public void The_tests_run_on_the_dotnet_version_the_workflow_asked_for()
    {
        var expected = Environment.GetEnvironmentVariable("OMENFANPLUS_EXPECT_RUNTIME_MAJOR");
        if (string.IsNullOrEmpty(expected))
            return;
        Assert.Equal(int.Parse(expected), Environment.Version.Major);
    }

    [Fact]
    public void The_app_is_built_for_dotnet_10_and_tested_up_to_11()
    {
        Assert.Equal(10, DotNetRuntimes.BuiltForMajor);
        Assert.Equal(11, DotNetRuntimes.NewestTestedMajor);
        Assert.InRange(Environment.Version.Major, DotNetRuntimes.BuiltForMajor, DotNetRuntimes.NewestTestedMajor);
    }

    /// <summary>The app's own runtimeconfig.json, as built: it asks for .NET 10 and may start on anything newer.</summary>
    [Fact]
    public void The_built_app_starts_on_dotnet_10_or_any_newer_one()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "OmenFanPlus.runtimeconfig.json");
        Assert.True(File.Exists(path), $"{path} is missing: the app's runtimeconfig.json should be copied next to the tests");
        var requirement = DotNetRuntimes.ReadRequirement(File.ReadAllText(path));

        Assert.NotNull(requirement);
        Assert.Equal(new Version(10, 0, 0), requirement.MinimumDesktopRuntime);
        Assert.Equal("LatestMajor", requirement.RollForward);
        Assert.True(DotNetRuntimes.CanStart(requirement, new[] { new Version(10, 0, 15) }));   // only .NET 10
        Assert.True(DotNetRuntimes.CanStart(requirement, new[] { new Version(11, 0, 0) }));    // only .NET 11
        Assert.False(DotNetRuntimes.CanStart(requirement, new[] { new Version(9, 0, 11) }));   // only .NET 9
    }

    // ---- names --------------------------------------------------------------------------------------

    [Fact]
    public void Package_names_and_urls_follow_the_dotnet_version()
    {
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json", DotNetRuntimes.ReleasesUrl(11));
        Assert.Equal("dotnet-10.0-desktopruntime", DotNetRuntimes.ChocoPackage(10));
        Assert.Equal("dotnet-11.0-desktopruntime", DotNetRuntimes.ChocoPackage(11));
        Assert.Equal("Microsoft.DotNet.DesktopRuntime.11", DotNetRuntimes.WingetId(11));
        Assert.Contains("https://dotnet.microsoft.com/download/dotnet/11.0", DotNetRuntimes.DownloadHint(11));
    }

    // ---- what is installed ---------------------------------------------------------------------------

    [Fact]
    public void Runtime_folders_are_read_as_versions_and_previews_are_left_out()
    {
        var versions = DotNetRuntimes.ParseRuntimeFolders(new[] { "11.0.0", "10.0.15", "10.0.10", "12.0.0-preview.2.27101.5", "notes", null });
        Assert.Equal(new[] { new Version(10, 0, 10), new Version(10, 0, 15), new Version(11, 0, 0) }, versions);
    }

    [Fact]
    public void The_installed_runtimes_include_the_one_these_tests_run_on()
    {
        Assert.Contains(DotNetRuntimes.InstalledDesktopRuntimes(), v => v.Major == Environment.Version.Major);
    }

    [Fact]
    public void A_runtime_update_counts_as_installed_only_within_its_own_dotnet_version()
    {
        var installed = new[] { new Version(10, 0, 15), new Version(11, 0, 0) };
        Assert.True(UpdateRunner.IsRuntimeInstalled(installed, new Version(10, 0, 15)));
        Assert.True(UpdateRunner.IsRuntimeInstalled(installed, new Version(10, 0, 13)));
        Assert.True(UpdateRunner.IsRuntimeInstalled(installed, new Version(11, 0, 0)));
        // .NET 11 being there doesn't mean the .NET 10 update went in.
        Assert.False(UpdateRunner.IsRuntimeInstalled(installed, new Version(10, 0, 16)));
        Assert.False(UpdateRunner.IsRuntimeInstalled(installed, new Version(11, 0, 1)));
        Assert.False(UpdateRunner.IsRuntimeInstalled(installed, new Version(12, 0, 0)));
    }

    // ---- Microsoft's metadata ------------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "latest-runtime": "11.0.0", "support-phase": "active" }""", "11.0.0", "active", true)]
    [InlineData("""{ "latest-runtime": "8.0.22", "support-phase": "maintenance" }""", "8.0.22", "maintenance", true)]
    [InlineData("""{ "latest-runtime": "9.0.14", "support-phase": "eol" }""", "9.0.14", "eol", false)]
    [InlineData("""{ "latest-runtime": "12.0.0-preview.2.27101.5", "support-phase": "preview" }""", null, "preview", false)]
    [InlineData("""{ "latest-runtime": "12.0.0-rc.1.27460.3", "support-phase": "go-live" }""", null, "go-live", false)]
    [InlineData("""{ }""", null, null, false)]
    public void A_channel_is_offered_only_when_it_is_a_finished_supported_release(string json, string? latest, string? phase, bool offered)
    {
        var root = Root(json);
        Assert.Equal(latest == null ? null : Version.Parse(latest), DotNetRuntimes.LatestRuntime(root));
        Assert.Equal(phase, DotNetRuntimes.SupportPhase(root));
        Assert.Equal(offered, DotNetRuntimes.IsSupportedRelease(root) && DotNetRuntimes.LatestRuntime(root) != null);
    }

    [Fact]
    public void A_new_dotnet_version_is_offered_but_never_installed_unasked()
    {
        var update = AvailableUpdate.DotNetRuntime(new Version(11, 0, 0), new Uri("https://builds.dotnet.microsoft.com/x.exe"), new string('A', 128));
        Assert.True(update.AutoInstall);                                   // fixes for the running version
        Assert.False((update with { AutoInstall = false }).AutoInstall);   // the offer of a newer version
        Assert.True(AvailableUpdate.App(new Version(2, 3, 0), new Uri("https://github.com/x.zip"), new string('a', 64)).AutoInstall);
    }

    // ---- what an app package needs -------------------------------------------------------------------

    private const string ConfigNet10RollForward = """
        { "runtimeOptions": { "tfm": "net10.0", "rollForward": "LatestMajor",
            "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" }, { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } ] } }
        """;

    private const string ConfigNet11 = """
        { "runtimeOptions": { "tfm": "net11.0",
            "frameworks": [ { "name": "Microsoft.NETCore.App", "version": "11.0.0" }, { "name": "Microsoft.WindowsDesktop.App", "version": "11.0.0" } ] } }
        """;

    [Fact]
    public void A_runtimeconfig_gives_the_desktop_runtime_and_the_roll_forward_policy()
    {
        Assert.Equal(new RuntimeRequirement(new Version(10, 0, 0), "LatestMajor"), DotNetRuntimes.ReadRequirement(ConfigNet10RollForward));
        Assert.Equal(new RuntimeRequirement(new Version(11, 0, 0), "Minor"), DotNetRuntimes.ReadRequirement(ConfigNet11));
    }

    [Fact]
    public void Other_runtimeconfig_shapes_are_read_too()
    {
        // A single "framework", as console apps have it.
        Assert.Equal(new RuntimeRequirement(new Version(11, 0, 0), "Minor"),
            DotNetRuntimes.ReadRequirement("""{ "runtimeOptions": { "framework": { "name": "Microsoft.NETCore.App", "version": "11.0.0" } } }"""));
        // A policy on the framework reference wins over the app-wide one.
        Assert.Equal(new RuntimeRequirement(new Version(10, 0, 0), "Major"),
            DotNetRuntimes.ReadRequirement("""{ "runtimeOptions": { "rollForward": "Minor", "frameworks": [ { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0", "rollForward": "Major" } ] } }"""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ }")]
    [InlineData("""{ "runtimeOptions": { "frameworks": [ ] } }""")]
    [InlineData("""{ "runtimeOptions": { "frameworks": [ { "name": "Microsoft.WindowsDesktop.App", "version": "eleven" } ] } }""")]
    [InlineData("""{ "runtimeOptions": { "includedFrameworks": [ { "name": "Microsoft.WindowsDesktop.App", "version": "11.0.0" } ] } }""")]   // self-contained
    public void A_runtimeconfig_that_names_no_runtime_gives_nothing(string json)
    {
        Assert.Null(DotNetRuntimes.ReadRequirement(json));
    }

    [Fact]
    public void The_requirement_is_read_from_the_release_zip()
    {
        var folder = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var good = Path.Combine(folder, "good.zip");
            using (var archive = ZipFile.Open(good, ZipArchiveMode.Create))
            {
                SelfUpdateTests.WriteEntry(archive, "OmenFanPlus.exe", "exe");
                SelfUpdateTests.WriteEntry(archive, "OmenFanPlus.runtimeconfig.json", ConfigNet11);
            }
            var without = Path.Combine(folder, "without.zip");
            using (var archive = ZipFile.Open(without, ZipArchiveMode.Create))
                SelfUpdateTests.WriteEntry(archive, "OmenFanPlus.exe", "exe");
            var notZip = Path.Combine(folder, "not.zip");
            File.WriteAllText(notZip, "not a zip");

            Assert.Equal(new RuntimeRequirement(new Version(11, 0, 0), "Minor"), DotNetRuntimes.ReadRequirementFromZip(good));
            Assert.Null(DotNetRuntimes.ReadRequirementFromZip(without));
            Assert.Null(DotNetRuntimes.ReadRequirementFromZip(notZip));
            Assert.Null(DotNetRuntimes.ReadRequirementFromZip(Path.Combine(folder, "missing.zip")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    // needs, policy, installed, starts?
    [InlineData("10.0.0", "LatestMajor", "10.0.15", true)]
    [InlineData("10.0.0", "LatestMajor", "11.0.0", true)]
    [InlineData("10.0.0", "Major", "11.0.0", true)]
    [InlineData("10.0.0", "LatestMajor", "9.0.11", false)]      // older never does
    [InlineData("11.0.0", "Minor", "11.0.0", true)]
    [InlineData("11.0.0", "Minor", "11.0.3", true)]
    [InlineData("11.0.0", "Minor", "10.0.15", false)]           // the case a release built for .NET 11 hits on a .NET 10 PC
    [InlineData("11.0.0", "Minor", "12.0.0", false)]            // the default policy doesn't cross to .NET 12
    [InlineData("11.0.0", "LatestMinor", "11.1.0", true)]
    [InlineData("11.0.2", "Minor", "11.0.1", false)]
    [InlineData("11.0.0", "LatestPatch", "11.0.5", true)]
    [InlineData("11.0.0", "LatestPatch", "11.1.0", false)]
    [InlineData("11.0.0", "Disable", "11.0.0", true)]
    [InlineData("11.0.0", "Disable", "11.0.1", false)]
    [InlineData("11.0.0", "something-new", "11.0.1", true)]     // an unknown policy is treated like the default
    public void An_installed_runtime_lets_the_app_start_or_not(string needs, string policy, string installed, bool starts)
    {
        var requirement = new RuntimeRequirement(Version.Parse(needs), policy);
        Assert.Equal(starts, DotNetRuntimes.Satisfies(requirement, Version.Parse(installed)));
        Assert.Equal(starts, DotNetRuntimes.CanStart(requirement, new[] { new Version(8, 0, 0), Version.Parse(installed) }));
    }

    [Fact]
    public void No_runtime_at_all_never_lets_the_app_start()
    {
        Assert.False(DotNetRuntimes.CanStart(new RuntimeRequirement(new Version(10, 0, 0), "LatestMajor"), Array.Empty<Version>()));
    }

    // ---- an app update that needs a runtime this PC lacks --------------------------------------------

    [Fact]
    public void A_new_dotnet_for_an_app_update_is_asked_about_and_never_installed_by_the_automatic_check()
    {
        var builtFor11 = new RuntimeRequirement(new Version(11, 0, 0), "Minor");
        var only10 = new[] { new Version(10, 0, 15) };
        var both = new[] { new Version(10, 0, 15), new Version(11, 0, 0) };

        // From the dialog there is someone to ask; the automatic check has nobody, so it leaves the update alone.
        Assert.Equal(DependencyInstaller.RuntimeStep.Ask, DependencyInstaller.RuntimeStepFor(builtFor11, only10, canAsk: true));
        Assert.Equal(DependencyInstaller.RuntimeStep.Refuse, DependencyInstaller.RuntimeStepFor(builtFor11, only10, canAsk: false));
        // Where the runtime is there (or goes in with the same run), nothing is asked either way.
        Assert.Equal(DependencyInstaller.RuntimeStep.NotNeeded, DependencyInstaller.RuntimeStepFor(builtFor11, both, canAsk: false));
        Assert.Equal(DependencyInstaller.RuntimeStep.NotNeeded, DependencyInstaller.RuntimeStepFor(builtFor11, both, canAsk: true));
        // This release (built for 10, starts on anything newer) never needs one on a PC that runs it today.
        var thisRelease = new RuntimeRequirement(new Version(10, 0, 0), "LatestMajor");
        Assert.Equal(DependencyInstaller.RuntimeStep.NotNeeded, DependencyInstaller.RuntimeStepFor(thisRelease, only10, canAsk: false));
    }

    [Fact]
    public void The_dialog_names_what_each_question_installs()
    {
        var app = AvailableUpdate.App(new Version(2, 3, 1), new Uri("https://github.com/x.zip"), new string('a', 64));
        var patch = AvailableUpdate.DotNetRuntime(new Version(10, 0, 16), new Uri("https://builds.dotnet.microsoft.com/x.exe"), new string('A', 128));
        var offer = AvailableUpdate.DotNetRuntime(new Version(11, 0, 0), new Uri("https://builds.dotnet.microsoft.com/y.exe"), new string('B', 128)) with { AutoInstall = false };
        var all = new[] { app, patch, offer };

        // Fixes and the offer of a new .NET version are asked about separately.
        Assert.Equal("OmenFanPlus 2.3.1 and .NET Desktop Runtime 10.0.16", UpdateCheck.Names(all.Where(u => u.AutoInstall)));
        Assert.Equal(".NET Desktop Runtime 11.0.0", UpdateCheck.Names(all.Where(u => !u.AutoInstall)));
    }

    // ---- the run script ------------------------------------------------------------------------------

    [Fact]
    public void Installers_run_before_the_apps_own_files_are_replaced()
    {
        var app = new ScriptItem(UpdateComponent.App, new Version(3, 0, 0), @"C:\x\app.zip", "");
        var pawnIo = new ScriptItem(UpdateComponent.PawnIo, new Version(2, 3, 0), @"C:\x\PawnIO_setup.exe", "-install -silent");
        var runtime = new ScriptItem(UpdateComponent.DotNetRuntime, new Version(11, 0, 0), @"C:\x\runtime.exe", "/install /quiet /norestart");

        var ordered = DependencyInstaller.OrderForScript(new[] { app, pawnIo, runtime });

        Assert.Equal(new[] { pawnIo, runtime, app }, ordered);   // the others keep their order; the app goes last
    }

    [Theory]
    [InlineData("10.0.0", "LatestMajor", "$v -ge [version]'10.0.0'")]
    [InlineData("11.0.0", "Minor", "$v -ge [version]'11.0.0' -and $v.Major -eq 11")]
    [InlineData("11.0.0", "LatestPatch", "$v -ge [version]'11.0.0' -and $v.Major -eq 11 -and $v.Minor -eq 0")]
    [InlineData("11.0.0", "Disable", "$v -eq [version]'11.0.0'")]
    public void The_scripts_runtime_check_is_the_same_rule(string needs, string policy, string condition)
    {
        Assert.Equal(condition, DotNetRuntimes.PowerShellCondition(new RuntimeRequirement(Version.Parse(needs), policy), "$v"));
    }

    [Fact]
    public void The_script_checks_the_runtime_before_it_replaces_the_app()
    {
        var item = new ScriptItem(UpdateComponent.App, new Version(3, 0, 0), @"C:\x\app.zip", "", new RuntimeRequirement(new Version(11, 0, 0), "Minor"));
        var script = UpdateRunner.BuildScript(new[] { item }, @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App");

        Assert.Contains(@"Get-ChildItem -LiteralPath 'C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App' -Directory", script);
        Assert.Contains("[System.Version]::TryParse($d.Name, [ref]$v) -and ($v -ge [version]'11.0.0' -and $v.Major -eq 11)", script);
        var check = script.IndexOf("-not $runtimeOk", StringComparison.Ordinal);
        var mirror = script.IndexOf("& robocopy $new $installDir", StringComparison.Ordinal);
        Assert.True(check > 0 && check < mirror, "the runtime check must come before the files are replaced");
        Assert.Contains("'runtime-missing'", script);
        Assert.All(script, c => Assert.True(c < 128));
    }

    /// <summary>
    /// For real in Windows PowerShell 5.1: an app version that needs .NET 11 must not replace the installed files
    /// on a PC that only has .NET 10 (and a preview of 11), and must go in once .NET 11 is there.
    /// </summary>
    [Fact]
    public async Task An_app_that_needs_a_runtime_this_pc_lacks_is_not_installed()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return;

        var root = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "OmenFanPlus");
        var staging = Path.Combine(install, "updates", "update-test");
        var runtimes = Path.Combine(root, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(Path.Combine(runtimes, "10.0.15"));
        Directory.CreateDirectory(Path.Combine(runtimes, "11.0.0-rc.2.26501.1"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(install, "OmenFanPlus.exe"), "old exe");
            var zip = Path.Combine(staging, "OmenFanPlus-9.9.9-win-x64.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                SelfUpdateTests.WriteEntry(archive, "OmenFanPlus.exe", "new exe");
                SelfUpdateTests.WriteEntry(archive, "OmenFanPlus.dll", "new dll");
                SelfUpdateTests.WriteEntry(archive, "OmenFanPlus.runtimeconfig.json", ConfigNet11);
            }
            var requires = DotNetRuntimes.ReadRequirementFromZip(zip);
            Assert.NotNull(requires);
            var exe = Path.Combine(install, "OmenFanPlus.exe");

            var result = await SelfUpdateTests.RunScriptAsync(powershell, staging, exe, zip, requires: requires, desktopRuntimeFolder: runtimes);
            Assert.Equal(new[] { "App 9.9.9 runtime-missing" }, result);
            Assert.Equal("old exe", await File.ReadAllTextAsync(exe));
            Assert.False(File.Exists(Path.Combine(install, "OmenFanPlus.dll")));

            // The runtime installer ran earlier in the same script: now the folder is there.
            Directory.CreateDirectory(Path.Combine(runtimes, "11.0.0"));
            File.Delete(Path.Combine(staging, "result.txt"));
            result = await SelfUpdateTests.RunScriptAsync(powershell, staging, exe, zip, requires: requires, desktopRuntimeFolder: runtimes);
            var line = Assert.Single(result);
            Assert.True(int.Parse(line.Split(' ')[2]) < 8, line);
            Assert.Equal("new exe", await File.ReadAllTextAsync(exe));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
