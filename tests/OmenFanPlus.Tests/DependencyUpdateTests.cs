using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

public class DependencyUpdateTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    // ---- what the publishers' metadata says -----------------------------------------------------------------

    // builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json, reduced to the parts that are read.
    private const string Releases = """
        {
          "latest-runtime": "10.0.13",
          "releases": [
            {
              "release-version": "10.0.13", "security": true,
              "runtime": { "version": "10.0.13" },
              "windowsdesktop": {
                "version": "10.0.13",
                "files": [
                  { "name": "windowsdesktop-runtime-win-x86.exe", "rid": "win-x86", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x86.exe", "hash": "1111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111111" },
                  { "name": "windowsdesktop-runtime-win-x64.zip", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.zip", "hash": "22222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222222" },
                  { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe", "hash": "ADAF5E06B66B1458ADAF5E06B66B1458ADAF5E06B66B1458ADAF5E06B66B1458ADAF5E06B66B1458ADAF5E06B66B1458ADAF5E06B66B1458ADAF5E06B66B1458" }
                ]
              }
            },
            {
              "release-version": "10.0.12", "security": false,
              "runtime": { "version": "10.0.12" },
              "windowsdesktop": { "version": "10.0.12", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe", "hash": "33333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333333" } ] }
            }
          ]
        }
        """;

    [Fact]
    public void The_x64_exe_installer_of_the_wanted_version_is_picked_with_its_hash()
    {
        var found = UpdateCheck.FindRuntimeInstaller(Root(Releases), new Version(10, 0, 13));
        Assert.NotNull(found);
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe", found.Value.Url.ToString());
        Assert.StartsWith("ADAF5E06B66B1458", found.Value.Sha512);
        Assert.Equal(128, found.Value.Sha512.Length);
    }

    [Fact]
    public void An_older_release_is_not_picked_for_a_newer_version()
    {
        var found = UpdateCheck.FindRuntimeInstaller(Root(Releases), new Version(10, 0, 12));
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe", found!.Value.Url.ToString());
        Assert.Null(UpdateCheck.FindRuntimeInstaller(Root(Releases), new Version(10, 0, 14)));
    }

    [Fact]
    public void A_runtime_entry_without_a_proper_hash_is_not_installable()
    {
        var json = """
            { "releases": [ { "windowsdesktop": { "version": "10.0.13", "files": [
                { "name": "windowsdesktop-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/x.exe", "hash": "" } ] } } ] }
            """;
        Assert.Null(UpdateCheck.FindRuntimeInstaller(Root(json), new Version(10, 0, 13)));
    }

    // api.github.com/repos/namazso/PawnIO.Setup/releases/latest, reduced.
    private const string PawnIoRelease = """
        {
          "tag_name": "2.2.0",
          "assets": [
            { "name": "PawnIO_setup.exe", "size": 3410960, "digest": "sha256:1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032",
              "browser_download_url": "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe" }
          ]
        }
        """;

    [Fact]
    public void The_PawnIO_installer_asset_is_found_with_its_sha256()
    {
        var found = UpdateCheck.FindPawnIoAsset(Root(PawnIoRelease));
        Assert.NotNull(found);
        Assert.Equal("https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", found.Value.Url.ToString());
        Assert.Equal("1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032", found.Value.Sha256);
    }

    [Fact]
    public void A_release_without_a_digest_still_gives_the_url_but_no_hash()
    {
        var json = """{ "assets": [ { "name": "PawnIO_setup.exe", "browser_download_url": "https://github.com/namazso/PawnIO.Setup/releases/download/2.3.0/PawnIO_setup.exe" } ] }""";
        var found = UpdateCheck.FindPawnIoAsset(Root(json));
        Assert.NotNull(found);
        Assert.Null(found.Value.Sha256);
    }

    [Fact]
    public void A_release_without_the_installer_asset_gives_nothing()
    {
        Assert.Null(UpdateCheck.FindPawnIoAsset(Root("""{ "assets": [ { "name": "Source.zip", "browser_download_url": "https://github.com/x.zip" } ] }""")));
        Assert.Null(UpdateCheck.FindPawnIoAsset(Root("""{ "tag_name": "2.2.0" }""")));
    }

    // ---- the checks on a downloaded file ------------------------------------------------------------------

    [Theory]
    [InlineData("sha256:1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032", "SHA256", "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032")]
    [InlineData("SHA-256:1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032", "SHA256", "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032")]
    public void GitHub_digests_are_parsed(string digest, string algorithm, string hex)
    {
        var parsed = InstallerVerifier.ParseDigest(digest);
        Assert.NotNull(parsed);
        Assert.Equal(algorithm, parsed.Value.Algorithm);
        Assert.Equal(hex, parsed.Value.Hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:abc")]                                                                      // wrong length
    [InlineData("md5:1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032")]           // unsupported
    [InlineData("sha256:1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba30zz")]        // not hex
    public void Unusable_digests_are_rejected(string? digest)
    {
        Assert.Null(InstallerVerifier.ParseDigest(digest));
    }

    [Fact]
    public void Hash_comparison_ignores_case_and_rejects_a_missing_expectation()
    {
        Assert.True(InstallerVerifier.HashMatches("abcd", "ABCD"));
        Assert.True(InstallerVerifier.HashMatches(" abcd ", "abcd"));
        Assert.False(InstallerVerifier.HashMatches("abce", "abcd"));
        Assert.False(InstallerVerifier.HashMatches(null, "abcd"));
        Assert.False(InstallerVerifier.HashMatches("", "abcd"));
    }

    [Fact]
    public async Task The_hash_of_a_file_is_computed_in_upper_case_hex()
    {
        // SHA-256("abc") is a well-known test vector.
        using var stream = new MemoryStream("abc"u8.ToArray());
        var hash = await InstallerVerifier.ComputeHashAsync(stream, "SHA256", CancellationToken.None);
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", hash);
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU", "namazso")]
    [InlineData("CN=Somebody, C=US", null)]
    public void The_signer_organization_comes_from_the_O_attribute_only(string subject, string? organization)
    {
        Assert.Equal(organization, InstallerVerifier.OrganizationOf(new X500DistinguishedName(subject)));
    }

    [Fact]
    public void An_unsigned_file_fails_the_signature_check()
    {
        // The test assembly carries no Authenticode signature.
        var path = typeof(DependencyUpdateTests).Assembly.Location;
        Assert.Null(InstallerVerifier.ReadSignerCertificate(path));
        var check = InstallerVerifier.VerifySignature(path, "Microsoft Corporation");
        Assert.False(check.Valid);
        Assert.Contains("0x800B0100", check.Detail); // TRUST_E_NOSIGNATURE
    }

    [Fact]
    public void A_file_that_is_not_a_program_fails_the_signature_check()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not a program");
            Assert.False(InstallerVerifier.VerifySignature(path, "Microsoft Corporation").Valid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Microsofts_own_dotnet_host_passes_the_signature_check_for_Microsoft()
    {
        // Needs a machine with the shared runtime installed in the usual place (as the one running these tests is).
        var dotnet = Path.Combine(UpdateRunner.DotnetRoot(), "dotnet.exe");
        if (!File.Exists(dotnet))
            return;

        var check = InstallerVerifier.VerifySignature(dotnet, "Microsoft Corporation");
        Assert.True(check.Valid, check.Detail);
        Assert.False(InstallerVerifier.VerifySignature(dotnet, "namazso").Valid);
    }

    [Fact]
    public void The_installed_PawnIO_tools_pass_the_signature_check_for_namazso()
    {
        // Only meaningful where PawnIO is installed (as on the machine these were written on).
        var util = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOUtil.exe");
        if (!File.Exists(util))
            return;

        var check = InstallerVerifier.VerifySignature(util, "namazso");
        Assert.True(check.Valid, check.Detail);
        Assert.False(InstallerVerifier.VerifySignature(util, "Microsoft Corporation").Valid);
    }

    [Fact]
    public async Task A_file_that_fails_verification_is_deleted()
    {
        var path = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllTextAsync(path, "not a program");
        var problem = await DependencyInstaller.VerifyAsync(path, "00", "SHA256", "Microsoft Corporation", CancellationToken.None);
        Assert.NotNull(problem);
        Assert.Contains("hash", problem);
        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// The whole download-and-verify path against the real PawnIO release (3.4 MB from GitHub). Set
    /// OMENFANPLUS_INTEGRATION=1 to run it; it is skipped otherwise so the normal test run stays offline.
    /// Nothing is installed: the file is deleted at the end.
    /// </summary>
    [Fact]
    public async Task Integration_the_current_PawnIO_installer_downloads_and_verifies()
    {
        if (Environment.GetEnvironmentVariable("OMENFANPLUS_INTEGRATION") != "1")
            return;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OmenFanPlus-tests/1.0");
        using var json = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest"));
        var version = Version.Parse(json.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v'));
        var asset = UpdateCheck.FindPawnIoAsset(json.RootElement);
        Assert.NotNull(asset);
        Assert.NotNull(asset.Value.Sha256); // GitHub's API publishes the digest for release assets

        var folder = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var installer = new DependencyInstaller();
            var messages = new List<string>();
            installer.Progress += (sender, message) => messages.Add(message);

            var path = await installer.DownloadVerifiedAsync(
                AvailableUpdate.PawnIo(version, asset.Value.Url, asset.Value.Sha256), folder, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.Equal("PawnIO_setup.exe", Path.GetFileName(path));
            Assert.Contains(messages, m => m.StartsWith("Downloading PawnIO"));
            Assert.Contains(messages, m => m.StartsWith("Checking PawnIO"));

            // The same checks again, and a wrong expectation is caught (which deletes the file).
            Assert.Null(await DependencyInstaller.VerifyAsync(path, asset.Value.Sha256, "SHA256", "namazso", CancellationToken.None));
            Assert.NotNull(await DependencyInstaller.VerifyAsync(path, asset.Value.Sha256, "SHA256", "Microsoft Corporation", CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- where installers may come from ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe", true)]
    [InlineData("https://download.visualstudio.microsoft.com/download/pr/abc/windowsdesktop-runtime-10.0.13-win-x64.exe", true)]
    [InlineData("https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", true)]
    [InlineData("https://GitHub.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", true)]
    [InlineData("https://github.com/someone-else/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", false)]  // another repository
    [InlineData("https://github.com/namazso/OtherRepo/releases/download/1.0/x.exe", false)]
    [InlineData("http://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe", false)]      // not https
    [InlineData("https://github.com.evil.example/PawnIO_setup.exe", false)]
    [InlineData("https://example.com/windowsdesktop-runtime-10.0.13-win-x64.exe", false)]
    public void Only_https_downloads_from_the_publishers_sites_may_start(string url, bool allowed)
    {
        Assert.Equal(allowed, DependencyInstaller.IsAllowedSource(new Uri(url)));
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/abc?x=y", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/abc", true)]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/x.exe", true)]
    [InlineData("https://evil.example/PawnIO_setup.exe", false)]
    [InlineData("http://objects.githubusercontent.com/abc", false)]
    public void Only_the_publishers_hosts_may_serve_the_file_after_redirects(string url, bool allowed)
    {
        Assert.Equal(allowed, DependencyInstaller.IsAllowedFinalHost(new Uri(url)));
    }

    [Fact]
    public void The_installer_keeps_the_publishers_file_name_when_it_is_a_plain_exe_name()
    {
        var runtime = AvailableUpdate.DotNetRuntime(new Version(10, 0, 13),
            new Uri("https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.13/windowsdesktop-runtime-10.0.13-win-x64.exe"), new string('A', 128));
        Assert.Equal("windowsdesktop-runtime-10.0.13-win-x64.exe", runtime.FileName);

        var odd = AvailableUpdate.PawnIo(new Version(2, 3, 0), new Uri("https://github.com/namazso/PawnIO.Setup/releases/download/2.3.0/"), new string('A', 64));
        Assert.Equal("PawnIo-2.3.0.exe", odd.FileName);
    }

    [Fact]
    public void The_catalog_fixes_signer_and_switches_per_component()
    {
        var runtime = UpdateCatalog.Describe(UpdateComponent.DotNetRuntime);
        Assert.Equal("Microsoft Corporation", runtime.SignerOrganization);
        Assert.Equal("/install /quiet /norestart", runtime.InstallerArguments);
        Assert.Equal("SHA512", runtime.HashAlgorithm);
        var pawnIo = UpdateCatalog.Describe(UpdateComponent.PawnIo);
        Assert.Equal("namazso", pawnIo.SignerOrganization);
        Assert.Equal("-install -silent", pawnIo.InstallerArguments);
        Assert.Equal("SHA256", pawnIo.HashAlgorithm);
    }

    [Fact]
    public void The_staging_folder_lets_only_administrators_and_SYSTEM_in()
    {
        // Creating under Program Files needs administrator rights; the test is a no-op without them.
        if (!DependencyInstaller.IsElevated())
            return;

        // A root of its own, so the test leaves nothing under Program Files\OmenFanPlus (install.ps1 refuses a
        // non-empty folder there).
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        var folder = DependencyInstaller.CreateStagingFolder(root);
        try
        {
            Assert.StartsWith(root, folder);
            Assert.StartsWith(UpdateRunner.FolderPrefix, Path.GetFileName(folder));

            var security = new DirectoryInfo(folder).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected, "the folder must not inherit the parent's rules");
            var rules = security.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Select(r => r.IdentityReference.Value)
                .OrderBy(s => s)
                .ToArray();
            Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544" }, rules); // SYSTEM, BUILTIN\Administrators
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- the run script ----------------------------------------------------------------------------------

    private static readonly ScriptItem[] TwoItems =
    {
        new(UpdateComponent.PawnIo, new Version(2, 3, 0), @"C:\Program Files\OmenFanPlus\updates\update-x\PawnIO_setup.exe", "-install -silent"),
        new(UpdateComponent.DotNetRuntime, new Version(10, 0, 14), @"C:\Program Files\OmenFanPlus\updates\update-x\windowsdesktop-runtime-10.0.14-win-x64.exe", "/install /quiet /norestart"),
    };

    [Fact]
    public void The_run_script_is_ascii_with_crlf_line_endings_and_takes_the_app_details_as_parameters()
    {
        var script = UpdateRunner.BuildScript(TwoItems);

        Assert.DoesNotContain("\n", script.Replace("\r\n", ""));                         // CRLF only
        Assert.All(script, c => Assert.True(c < 128, $"non-ASCII character {(int)c}"));
        Assert.Contains("[string] $Exe,", script);
        Assert.Contains("[int] $AppPid,", script);
        Assert.Contains("[string] $MutexName,", script);
        Assert.Contains("[string] $QuitEvent", script);
        Assert.Contains("[System.Threading.EventWaitHandle]::OpenExisting($QuitEvent).Set()", script);
        Assert.Contains("System.Threading.Mutex -ArgumentList $false, $MutexName", script);
        Assert.Contains("-FilePath 'C:\\Program Files\\OmenFanPlus\\updates\\update-x\\PawnIO_setup.exe' -ArgumentList '-install -silent'", script);
        Assert.Contains("-ArgumentList '/install /quiet /norestart'", script);
        Assert.Contains("\"PawnIo 2.3.0 $code\"", script);
        Assert.Contains("\"DotNetRuntime 10.0.14 $code\"", script);
        Assert.Contains("app-still-running", script);
        Assert.Contains("$lock.ReleaseMutex()", script);
        Assert.Contains("Start-Process -FilePath $Exe", script);
        Assert.DoesNotContain("OmenFanPlus.exe", script); // the app's path is never written into the script
    }

    [Fact]
    public void Quote_characters_in_a_path_are_doubled_for_powershell()
    {
        var script = UpdateRunner.BuildScript(
            new[] { new ScriptItem(UpdateComponent.PawnIo, new Version(1, 0), "C:\\Users\\O'Brien\u2019s\\x.exe", "-install -silent") });
        Assert.Contains("-FilePath 'C:\\Users\\O''Brien''s\\x.exe'", script);
    }

    /// <summary>
    /// Runs the generated script for real in Windows PowerShell 5.1: cmd.exe stands in for the installers (it exits
    /// with the given code), one installer file is missing, the app path doesn't exist (so nothing is restarted),
    /// the PID is of a process that has already exited, and the mutex and event names are private to the test (the
    /// real app is running on the machine these were written on). Checks that PowerShell parses the script and
    /// that result.txt holds one line per installer.
    /// </summary>
    [Fact]
    public async Task The_run_script_runs_in_windows_powershell_and_records_each_installers_exit_code()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(powershell) || !File.Exists(cmd))
            return;

        using var gone = Process.Start(new ProcessStartInfo(cmd, "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        await gone.WaitForExitAsync();

        var folder = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var items = new[]
            {
                new ScriptItem(UpdateComponent.PawnIo, new Version(9, 9, 9), cmd, "/c exit 3010"),
                new ScriptItem(UpdateComponent.DotNetRuntime, new Version(9, 9, 9), cmd, "/c exit 0"),
                new ScriptItem(UpdateComponent.DotNetRuntime, new Version(8, 8, 8), Path.Combine(folder, "missing-installer.exe"), "/quiet"),
            };
            var scriptPath = Path.Combine(folder, "run-update.ps1");
            await File.WriteAllTextAsync(scriptPath, UpdateRunner.BuildScript(items), new UTF8Encoding(true));

            // PowerShell's own parser first: the only proof the script is valid.
            var parse = await RunAsync(powershell, $"-NoProfile -NonInteractive -Command \"$errors = $null; " +
                $"[System.Management.Automation.Language.Parser]::ParseFile('{scriptPath}', [ref]$null, [ref]$errors) | Out-Null; " +
                "$errors.Count\"");
            Assert.Equal("0", parse.Output.Trim());

            var name = "OmenFanPlus-test-" + Guid.NewGuid().ToString("N");
            var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                            $"-Exe \"{Path.Combine(folder, "no-such-app.exe")}\" -AppPid {gone.Id} -MutexName Local\\{name} -QuitEvent Local\\{name}.Quit";
            var run = await RunAsync(powershell, arguments);
            Assert.Equal(0, run.ExitCode);

            var result = (await File.ReadAllLinesAsync(Path.Combine(folder, "result.txt"))).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            Assert.Equal(4, result.Length);
            Assert.Equal("PawnIo 9.9.9 3010", result[0]);
            Assert.Equal("DotNetRuntime 9.9.9 0", result[1]);
            Assert.StartsWith("# ", result[2]);                   // why the third installer couldn't be started
            Assert.Equal("DotNetRuntime 8.8.8 not-started", result[3]);

            // With the app's mutex held (as a running copy holds it), the script installs nothing.
            File.Delete(Path.Combine(folder, "result.txt"));
            using (new Mutex(true, "Local\\" + name))
            {
                var blocked = await RunAsync(powershell, arguments);
                Assert.Equal(0, blocked.ExitCode);
            }
            Assert.Equal(new[] { "app-still-running" },
                (await File.ReadAllLinesAsync(Path.Combine(folder, "result.txt"))).Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output + error);
    }

    // ---- reading the outcome -----------------------------------------------------------------------------

    [Theory]
    [InlineData(0, InstallOutcome.Installed)]
    [InlineData(3010, InstallOutcome.InstalledRebootNeeded)]
    [InlineData(1641, InstallOutcome.InstalledRebootNeeded)]
    [InlineData(1638, InstallOutcome.AlreadyInstalled)]
    [InlineData(1, InstallOutcome.Failed)]
    [InlineData(1602, InstallOutcome.Failed)]
    [InlineData(-1, InstallOutcome.Failed)]
    public void Installer_exit_codes_are_read_the_Windows_Installer_way(int exitCode, InstallOutcome outcome)
    {
        Assert.Equal(outcome, UpdateRunner.InterpretExitCode(exitCode));
    }

    [Theory]
    [InlineData("PawnIo 2.3.0 0", true, "PawnIO 2.3.0 installed.")]
    [InlineData("PawnIo 2.3.0 0", false, "PawnIO 2.3.0: the installer finished, but that version isn't on this PC. Try again from Check for updates.")]
    [InlineData("DotNetRuntime 10.0.14 3010", true, ".NET Desktop Runtime 10.0.14 installed; restart Windows to finish.")]
    [InlineData("DotNetRuntime 10.0.14 1638", false, ".NET Desktop Runtime 10.0.14 was already installed.")]
    [InlineData("PawnIo 2.3.0 1", false, "PawnIO 2.3.0 failed to install (installer exit code 1); close other programs that use PawnIO (FanControl, LibreHardwareMonitor) and try again.")]
    [InlineData("DotNetRuntime 10.0.14 1602", false, ".NET Desktop Runtime 10.0.14 failed to install (installer exit code 1602).")]
    [InlineData("PawnIo 2.3.0 timeout", false, "PawnIO 2.3.0: the installer didn't finish within 15 minutes and was stopped.")]
    [InlineData("PawnIo 2.3.0 not-started", false, "PawnIO 2.3.0: the installer couldn't be started (see the line before this one).")]
    [InlineData("# This program is blocked by group policy.", false, "The update script reported: This program is blocked by group policy.")]
    [InlineData("app-still-running", false, "OmenFanPlus didn't close for the update (or was started again too soon), so nothing was installed.")]
    [InlineData("garbage line", false, "The update script left an unreadable result: garbage line")]
    public void Result_lines_become_messages_for_the_user(string line, bool installedNow, string expected)
    {
        Assert.Equal(expected, UpdateRunner.DescribeResultLine(line, (c, v) => installedNow));
    }

    [Fact]
    public void Blank_result_lines_are_ignored()
    {
        Assert.Null(UpdateRunner.DescribeResultLine("   ", (c, v) => true));
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.13\", @"C:\Program Files\dotnet")]
    [InlineData(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.13", @"C:\Program Files\dotnet")]
    [InlineData(@"D:\tools\dotnet\shared\Microsoft.NETCore.App\10.0.0\", @"D:\tools\dotnet")]
    public void The_dotnet_root_is_three_levels_above_the_runtime_folder(string runtimeDirectory, string root)
    {
        Assert.Equal(root, UpdateRunner.DotnetRootFrom(runtimeDirectory));
    }

    [Fact]
    public void The_installed_runtime_check_sees_the_runtime_these_tests_run_on()
    {
        Assert.True(UpdateRunner.IsInstalled(UpdateComponent.DotNetRuntime, Environment.Version));
        Assert.False(UpdateRunner.IsInstalled(UpdateComponent.DotNetRuntime, new Version(99, 0, 0)));
    }

    // ---- not retrying a failed automatic install every few minutes --------------------------------------

    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AvailableUpdate PawnIo230 = AvailableUpdate.PawnIo(new Version(2, 3, 0),
        new Uri("https://github.com/namazso/PawnIO.Setup/releases/download/2.3.0/PawnIO_setup.exe"), new string('A', 64));
    private static readonly AvailableUpdate Runtime1014 = AvailableUpdate.DotNetRuntime(new Version(10, 0, 14),
        new Uri("https://builds.dotnet.microsoft.com/x.exe"), new string('B', 128));

    [Fact]
    public void A_version_tried_in_the_last_24_hours_is_not_installed_automatically_again()
    {
        var record = UpdateRunner.RecordAttempt(null, new[] { PawnIo230 }, Now);
        Assert.Equal("PawnIo 2.3.0 2026-10-19T12:00:00Z", record);

        Assert.False(UpdateRunner.ShouldAutoInstall(record, PawnIo230, Now.AddMinutes(2)));
        Assert.False(UpdateRunner.ShouldAutoInstall(record, PawnIo230, Now.AddHours(23)));
        Assert.True(UpdateRunner.ShouldAutoInstall(record, PawnIo230, Now.AddHours(24)));
    }

    [Fact]
    public void A_newer_version_or_another_component_is_not_held_back()
    {
        var record = UpdateRunner.RecordAttempt(null, new[] { PawnIo230 }, Now);
        var pawnIo240 = PawnIo230 with { Version = new Version(2, 4, 0) };
        Assert.True(UpdateRunner.ShouldAutoInstall(record, pawnIo240, Now.AddMinutes(2)));
        Assert.True(UpdateRunner.ShouldAutoInstall(record, Runtime1014, Now.AddMinutes(2)));
    }

    [Fact]
    public void The_record_keeps_one_entry_per_component_and_survives_junk()
    {
        var record = UpdateRunner.RecordAttempt("PawnIo 2.2.0 2026-10-01T00:00:00Z;nonsense;DotNetRuntime 10.0.13 2026-10-01T00:00:00Z",
            new[] { PawnIo230 }, Now);
        Assert.Equal("PawnIo 2.3.0 2026-10-19T12:00:00Z;DotNetRuntime 10.0.13 2026-10-01T00:00:00Z", record);
        Assert.True(UpdateRunner.ShouldAutoInstall(null, PawnIo230, Now));
        Assert.True(UpdateRunner.ShouldAutoInstall("", PawnIo230, Now));
        Assert.True(UpdateRunner.ShouldAutoInstall("garbage", PawnIo230, Now));
    }
}
