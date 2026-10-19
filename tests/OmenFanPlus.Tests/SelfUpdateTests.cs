using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

/// <summary>OmenFanPlus updating itself from its GitHub releases.</summary>
public class SelfUpdateTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // api.github.com/repos/C1nderC0ated/OmenFanPlus/releases/latest, reduced, as the release workflow produces it.
    private static readonly string Release = $$"""
        {
          "tag_name": "v2.1.0",
          "assets": [
            { "name": "OmenFanPlus-2.1.0-win-x64.zip", "digest": "sha256:{{Sha}}",
              "browser_download_url": "https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/OmenFanPlus-2.1.0-win-x64.zip" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/SHA256SUMS.txt" }
          ]
        }
        """;

    [Fact]
    public void The_release_gives_the_zip_its_digest_and_the_sums_file()
    {
        var release = UpdateCheck.FindAppRelease(Root(Release));
        Assert.NotNull(release);
        Assert.Equal(new Version(2, 1, 0), release.Version);
        Assert.Equal("https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/OmenFanPlus-2.1.0-win-x64.zip", release.Zip.ToString());
        Assert.Equal(Sha, release.Sha256);
        Assert.Equal("https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/SHA256SUMS.txt", release.Sums!.ToString());
    }

    [Fact]
    public void A_release_without_the_zip_or_with_an_unreadable_tag_gives_nothing()
    {
        Assert.Null(UpdateCheck.FindAppRelease(Root("""{ "tag_name": "v2.1.0", "assets": [ { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/x" } ] }""")));
        Assert.Null(UpdateCheck.FindAppRelease(Root("""{ "tag_name": "nightly", "assets": [ { "name": "OmenFanPlus-2.1.0-win-x64.zip", "browser_download_url": "https://github.com/x.zip" } ] }""")));
        Assert.Null(UpdateCheck.FindAppRelease(Root("""{ "assets": [] }""")));
    }

    [Fact]
    public void A_release_without_digest_or_sums_still_parses_but_is_not_installable()
    {
        var release = UpdateCheck.FindAppRelease(Root("""{ "tag_name": "2.1.0", "assets": [ { "name": "OmenFanPlus-2.1.0-win-x64.zip", "browser_download_url": "https://github.com/C1nderC0ated/OmenFanPlus/releases/download/2.1.0/OmenFanPlus-2.1.0-win-x64.zip" } ] }"""));
        Assert.NotNull(release);
        Assert.Null(release.Sha256);
        Assert.Null(release.Sums);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  OmenFanPlus-2.1.0-win-x64.zip\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF *OmenFanPlus-2.1.0-win-x64.zip\r\n", "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("ffff  other.zip\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  omenfanplus-2.1.0-WIN-x64.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  OmenFanPlus-2.0.0-win-x64.zip", null)]   // another file
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef OmenFanPlus-2.1.0-win-x64.zip", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  OmenFanPlus-2.1.0-win-x64.zip", null)]   // not hex
    [InlineData("", null)]
    public void SHA256SUMS_lines_are_read_for_the_wanted_file(string text, string? hash)
    {
        Assert.Equal(hash, UpdateCheck.ParseSha256Sums(text, "OmenFanPlus-2.1.0-win-x64.zip"));
    }

    [Theory]
    [InlineData("https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/OmenFanPlus-2.1.0-win-x64.zip", true)]
    [InlineData("https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/SHA256SUMS.txt", true)]
    [InlineData("https://github.com/C1nderC0ated/OmenFanPlus/archive/refs/heads/main.zip", false)]       // not a release file
    [InlineData("https://github.com/someone/OmenFanPlus/releases/download/v9/OmenFanPlus-9-win-x64.zip", false)] // another owner
    public void Only_this_repositorys_release_files_may_be_downloaded(string url, bool allowed)
    {
        Assert.Equal(allowed, DependencyInstaller.IsAllowedSource(new Uri(url)));
    }

    [Theory]
    [InlineData(@"C:\Program Files\OmenFanPlus\OmenFanPlus.exe", true)]
    [InlineData(@"C:\Program Files\Foo\omenfanplus\OMENFANPLUS.EXE", true)]
    [InlineData(@"C:\Program Files\Utilities\OmenFanPlus.exe", false)]     // a shared folder: /MIR would empty it
    [InlineData(@"C:\Program Files\OmenFanPlus\Other.exe", false)]
    [InlineData(@"C:\Program Files\OmenFanPlus-old\OmenFanPlus.exe", false)]
    public void Only_OmenFanPlus_exe_in_a_folder_named_OmenFanPlus_may_replace_itself(string exe, bool own)
    {
        Assert.Equal(own, DependencyInstaller.IsOwnFolder(exe));
    }

    [Fact]
    public void The_app_update_has_no_signer_and_keeps_the_zip_name()
    {
        var update = AvailableUpdate.App(new Version(2, 1, 0),
            new Uri("https://github.com/C1nderC0ated/OmenFanPlus/releases/download/v2.1.0/OmenFanPlus-2.1.0-win-x64.zip"), Sha);
        Assert.Null(update.SignerOrganization);
        Assert.Equal("SHA256", update.HashAlgorithm);
        Assert.Equal("OmenFanPlus-2.1.0-win-x64.zip", update.FileName);
        Assert.Equal("OmenFanPlus", update.DisplayName);
    }

    [Fact]
    public async Task A_zip_is_verified_by_hash_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await File.WriteAllBytesAsync(path, "abc"u8.ToArray());
            var good = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"; // SHA-256("abc")
            Assert.Null(await DependencyInstaller.VerifyAsync(path, good, "SHA256", signerOrganization: null, CancellationToken.None));
            Assert.True(File.Exists(path));
            Assert.NotNull(await DependencyInstaller.VerifyAsync(path, "00", "SHA256", signerOrganization: null, CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The real release feed, end to end, the way the app reads it: the latest GitHub release must parse, its
    /// SHA256SUMS.txt must agree with GitHub's digest, and the zip (a few MB) must download and verify. Set
    /// OMENFANPLUS_INTEGRATION=1 to run it; skipped otherwise so the normal run stays offline. Nothing is installed.
    /// </summary>
    [Fact]
    public async Task Integration_the_latest_release_parses_and_its_zip_verifies()
    {
        if (Environment.GetEnvironmentVariable("OMENFANPLUS_INTEGRATION") != "1")
            return;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OmenFanPlus-tests/1.0");
        using var json = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{UpdateCatalog.AppRepository}/releases/latest"));
        var release = UpdateCheck.FindAppRelease(json.RootElement);
        Assert.NotNull(release);
        Assert.NotNull(release.Sha256);
        Assert.NotNull(release.Sums);
        Assert.True(DependencyInstaller.IsAllowedSource(release.Zip));
        Assert.True(DependencyInstaller.IsAllowedSource(release.Sums));

        var listed = UpdateCheck.ParseSha256Sums(await http.GetStringAsync(release.Sums), Path.GetFileName(release.Zip.LocalPath));
        Assert.Equal(release.Sha256, listed, ignoreCase: true);

        var folder = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = await new DependencyInstaller().DownloadVerifiedAsync(AvailableUpdate.App(release.Version, release.Zip, release.Sha256), folder, CancellationToken.None);
            using var archive = ZipFile.OpenRead(path);
            var names = archive.Entries.Select(e => e.FullName).ToArray();
            Assert.Contains("OmenFanPlus.exe", names);
            Assert.Contains("OmenFanPlus.dll", names);
            Assert.Contains("OmenFanPlus.runtimeconfig.json", names);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ---- how PowerShell is started ------------------------------------------------------------------------

    [Fact]
    public void PowerShell_is_started_by_full_path_with_only_the_system_module_folder()
    {
        var start = UpdateRunner.BuildStartInfo(@"C:\x", @"C:\x\run-update.ps1", @"C:\Program Files\OmenFanPlus\OmenFanPlus.exe", 42, @"Local\M", @"Local\Q");
        var system = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        Assert.Equal(Path.Combine(system, "powershell.exe"), start.FileName);
        Assert.Equal(Path.Combine(system, "Modules"), start.Environment["PSModulePath"]);
        Assert.False(start.Environment.ContainsKey("PSModuleAnalysisCachePath"));
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\x\run-update.ps1",
            "-Exe", @"C:\Program Files\OmenFanPlus\OmenFanPlus.exe", "-AppPid", "42", "-MutexName", @"Local\M", "-QuitEvent", @"Local\Q" }, start.ArgumentList);
    }

    [Fact]
    public void The_script_pins_the_module_path_before_its_first_cmdlet()
    {
        var script = UpdateRunner.BuildScript(new[] { AppItem(@"C:\x\OmenFanPlus-9.9.9-win-x64.zip") });
        var pin = script.IndexOf("$env:PSModulePath = [System.IO.Path]::Combine($PSHOME, 'Modules')", StringComparison.Ordinal);
        var firstCmdlet = script.IndexOf("Join-Path", StringComparison.Ordinal);
        Assert.True(pin > 0 && pin < firstCmdlet, "the module path must be pinned, with no cmdlet, before any cmdlet runs");
    }

    // ---- the script step ---------------------------------------------------------------------------------

    private static ScriptItem AppItem(string zip, string version = "9.9.9") =>
        new(UpdateComponent.App, Version.Parse(version), zip, "");

    [Fact]
    public void The_script_unpacks_backs_up_mirrors_and_spares_the_updates_folder()
    {
        var script = UpdateRunner.BuildScript(new[] { AppItem(@"C:\Program Files\OmenFanPlus\updates\update-x\OmenFanPlus-9.9.9-win-x64.zip") });
        Assert.Contains("Expand-Archive -LiteralPath 'C:\\Program Files\\OmenFanPlus\\updates\\update-x\\OmenFanPlus-9.9.9-win-x64.zip'", script);
        Assert.Contains("$installDir = Split-Path -Path $Exe -Parent", script);
        Assert.Contains("-ne 'OmenFanPlus.exe' -or (Split-Path -Path $installDir -Leaf) -ne 'OmenFanPlus'", script);
        Assert.Contains("robocopy $installDir $backup /MIR /XD $updatesDir", script);
        Assert.Contains("robocopy $new $installDir /MIR /XD $updatesDir", script);
        Assert.Contains("robocopy $backup $installDir /MIR /XD $updatesDir", script);
        Assert.Contains("/LOG+:$robocopyLog", script);
        Assert.Contains("'bad-zip'", script);
        Assert.Contains("'backup-failed'", script);
        Assert.Contains("'wrong-folder'", script);
        Assert.Contains("\"App 9.9.9 $code$restore\"", script);
        Assert.DoesNotContain("Start-Process -FilePath 'C:\\Program Files\\OmenFanPlus\\updates", script); // a zip is never run
        Assert.All(script, c => Assert.True(c < 128));
    }

    /// <summary>
    /// The self-update step for real in Windows PowerShell 5.1, against a throw-away "installed" folder (named
    /// OmenFanPlus, under a parent with a space): an old file must go, the zip's files must arrive, the updates
    /// folder (where the script itself lives) must stay, robocopy's log must exist and result.txt must hold the
    /// robocopy outcome. -NoRestart keeps the dummy exe from being started.
    /// </summary>
    [Fact]
    public async Task The_script_replaces_the_installed_files_from_the_zip()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return;

        var root = Path.Combine(Path.GetTempPath(), "OmenFanPlus test " + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "OmenFanPlus");
        var staging = Path.Combine(install, "updates", "update-test");
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(install, "old.dll"), "old");
            await File.WriteAllTextAsync(Path.Combine(install, "OmenFanPlus.exe"), "old exe");
            await File.WriteAllTextAsync(Path.Combine(install, "updates", "keep.txt"), "must survive /MIR");

            var zip = Path.Combine(staging, "OmenFanPlus-9.9.9-win-x64.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "OmenFanPlus.exe", "new exe");
                WriteEntry(archive, "OmenFanPlus.dll", "new dll");
                WriteEntry(archive, "OmenFanPlus.runtimeconfig.json", "{}");
                WriteEntry(archive, "new.dll", "new");
            }

            var result = await RunScriptAsync(powershell, staging, Path.Combine(install, "OmenFanPlus.exe"), zip);

            var line = Assert.Single(result);
            var parts = line.Split(' ');
            Assert.Equal(3, parts.Length);
            Assert.Equal("App", parts[0]);
            Assert.Equal("9.9.9", parts[1]);
            Assert.True(int.Parse(parts[2]) < 8, $"robocopy reported {parts[2]}");

            Assert.Equal("new exe", await File.ReadAllTextAsync(Path.Combine(install, "OmenFanPlus.exe")));
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(install, "new.dll")));
            Assert.False(File.Exists(Path.Combine(install, "old.dll")), "/MIR must remove files the new version doesn't have");
            Assert.Equal("must survive /MIR", await File.ReadAllTextAsync(Path.Combine(install, "updates", "keep.txt")));
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(staging, "backup", "old.dll")));
            var robocopyLog = await File.ReadAllTextAsync(Path.Combine(staging, "robocopy.log"));
            Assert.Contains("new.dll", robocopyLog);   // the kept log names the files it copied
            Assert.Contains("old.dll", robocopyLog);   // and the ones it removed
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_zip_without_the_whole_app_changes_nothing()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return;

        var root = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "OmenFanPlus");
        var staging = Path.Combine(install, "updates", "update-test");
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(install, "old.dll"), "old");
            var zip = Path.Combine(staging, "OmenFanPlus-9.9.9-win-x64.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "OmenFanPlus.exe", "exe but no dll or runtimeconfig");
                WriteEntry(archive, "readme.txt", "not an app");
            }

            var result = await RunScriptAsync(powershell, staging, Path.Combine(install, "OmenFanPlus.exe"), zip);

            Assert.Equal(new[] { "App 9.9.9 bad-zip" }, result);
            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(install, "old.dll")));
            Assert.False(File.Exists(Path.Combine(install, "readme.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_copy_outside_a_folder_named_OmenFanPlus_is_left_alone()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return;

        var root = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Utilities");            // a shared tools folder
        var staging = Path.Combine(root, "updates", "update-test");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(install, "othertool.exe"), "someone else's program");
            var zip = Path.Combine(staging, "OmenFanPlus-9.9.9-win-x64.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "OmenFanPlus.exe", "new exe");
                WriteEntry(archive, "OmenFanPlus.dll", "new dll");
                WriteEntry(archive, "OmenFanPlus.runtimeconfig.json", "{}");
            }

            var result = await RunScriptAsync(powershell, staging, Path.Combine(install, "OmenFanPlus.exe"), zip);

            Assert.Equal(new[] { "App 9.9.9 wrong-folder" }, result);
            Assert.Equal("someone else's program", await File.ReadAllTextAsync(Path.Combine(install, "othertool.exe")));
            Assert.False(File.Exists(Path.Combine(install, "OmenFanPlus.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A module planted first on PSModulePath that redefines Join-Path (as something in the user's Documents could)
    /// must not run: the script pins the module path before its first cmdlet.
    /// </summary>
    [Fact]
    public async Task A_planted_module_earlier_on_the_module_path_is_not_loaded()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            return;

        var root = Path.Combine(Path.GetTempPath(), "OmenFanPlus-test-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "OmenFanPlus");
        var staging = Path.Combine(install, "updates", "update-test");
        var modules = Path.Combine(root, "modules");
        var marker = Path.Combine(root, "hijacked.txt");
        Directory.CreateDirectory(staging);
        var planted = Path.Combine(modules, "Microsoft.PowerShell.Management", "9.9.9");
        Directory.CreateDirectory(planted);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(planted, "Microsoft.PowerShell.Management.psd1"),
                "@{ ModuleVersion = '9.9.9'; RootModule = 'Microsoft.PowerShell.Management.psm1'; FunctionsToExport = @('Join-Path') }");
            await File.WriteAllTextAsync(Path.Combine(planted, "Microsoft.PowerShell.Management.psm1"),
                "function Join-Path { param($Path, $ChildPath) [System.IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', 'hijacked'); [System.IO.Path]::Combine($Path, $ChildPath) }");

            var zip = Path.Combine(staging, "OmenFanPlus-9.9.9-win-x64.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "OmenFanPlus.exe", "new exe");
                WriteEntry(archive, "OmenFanPlus.dll", "new dll");
                WriteEntry(archive, "OmenFanPlus.runtimeconfig.json", "{}");
            }

            var hostilePath = modules + ";" + Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
            var result = await RunScriptAsync(powershell, staging, Path.Combine(install, "OmenFanPlus.exe"), zip, hostilePath);

            Assert.False(File.Exists(marker), "the planted Join-Path ran");
            var line = Assert.Single(result);
            Assert.True(int.Parse(line.Split(' ')[2]) < 8, line);
            Assert.Equal("new exe", await File.ReadAllTextAsync(Path.Combine(install, "OmenFanPlus.exe")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.ASCII.GetBytes(content));
    }

    private static async Task<string[]> RunScriptAsync(string powershell, string staging, string exe, string zip, string? modulePath = null)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var gone = Process.Start(new ProcessStartInfo(cmd, "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        await gone.WaitForExitAsync();

        var scriptPath = Path.Combine(staging, "run-update.ps1");
        await File.WriteAllTextAsync(scriptPath, UpdateRunner.BuildScript(new[] { AppItem(zip) }), new UTF8Encoding(true));

        var name = "OmenFanPlus-test-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(powershell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\" -Exe \"{exe}\" -AppPid {gone.Id} " +
            $"-MutexName Local\\{name} -QuitEvent Local\\{name}.Quit -NoRestart")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (modulePath != null)
            start.Environment["PSModulePath"] = modulePath;
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, output);

        return (await File.ReadAllLinesAsync(Path.Combine(staging, "result.txt"))).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    }

    // ---- the outcome -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("App 2.1.0 1", true, "OmenFanPlus 2.1.0 installed.")]                                            // robocopy 1 = files copied
    [InlineData("App 2.1.0 3", true, "OmenFanPlus 2.1.0 installed.")]
    [InlineData("App 2.1.0 8 1", false, "OmenFanPlus 2.1.0 couldn't be copied over the installed files (robocopy exit code 8); the previous version was put back.")]
    [InlineData("App 2.1.0 8 16", false, "OmenFanPlus 2.1.0 couldn't be copied over the installed files (robocopy exit code 8); putting the previous version back failed too (robocopy exit code 16); run install.ps1.")]
    [InlineData("App 2.1.0 bad-zip", false, "OmenFanPlus 2.1.0: the downloaded package is incomplete (no OmenFanPlus.exe, .dll or .runtimeconfig.json), so the installed files were left alone.")]
    [InlineData("App 2.1.0 backup-failed", false, "OmenFanPlus 2.1.0: the installed files couldn't be backed up first, so they were left alone.")]
    [InlineData("App 2.1.0 wrong-folder", false, "OmenFanPlus 2.1.0: this copy doesn't run as OmenFanPlus.exe in a folder named OmenFanPlus, so its files were left alone. Update it by hand.")]
    public void App_result_lines_read_robocopys_exit_codes(string line, bool installedNow, string expected)
    {
        Assert.Equal(expected, UpdateRunner.DescribeResultLine(line, (c, v) => installedNow));
    }

    [Fact]
    public void A_copied_but_not_running_version_is_reported_as_such()
    {
        var message = UpdateRunner.DescribeResultLine("App 2.1.0 1", (c, v) => false);
        Assert.StartsWith("OmenFanPlus 2.1.0: the files were copied, but the running copy is still", message);
    }

    [Fact]
    public void The_running_copy_counts_as_installed_for_its_own_version_only()
    {
        var running = Version.Parse(System.Windows.Forms.Application.ProductVersion);
        Assert.True(UpdateRunner.IsInstalled(UpdateComponent.App, running));
        Assert.False(UpdateRunner.IsInstalled(UpdateComponent.App, new Version(running.Major + 1, 0, 0)));
    }
}
