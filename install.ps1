<#
.SYNOPSIS
    Builds OmenFanPlus and installs it into Program Files, replacing any running copy.

.DESCRIPTION
    Run from an elevated PowerShell in the repository folder:

        powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1

    Steps:
      1. dotnet publish into artifacts\publish (emptied first)
      2. ask running copies to quit (old OmenHubLighterPlus builds are stopped)
      3. remove the WinRing0 driver service that OmenHubLighterPlus builds before 2.0 left behind
      4. copy the build into the install folder (default: C:\Program Files\OmenFanPlus)
      5. point the "Run on startup" task at the installed copy, replacing the pre-rename task
      6. start the installed copy

    Settings are carried over on the first start of the installed copy (Services\SettingsMigration.cs),
    including those of OmenHubLighterPlus. Run it again after pulling changes; a second run only replaces files.
#>
#Requires -RunAsAdministrator
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'OmenFanPlus'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$warnings = 0

# From 32-bit PowerShell, $env:ProgramFiles is "Program Files (x86)", which the x64 app doesn't treat as installed.
if (-not [Environment]::Is64BitProcess) { throw "Run install.ps1 from 64-bit PowerShell." }
$appName = 'OmenFanPlus'
$legacyName = 'OmenHubLighterPlus'
$exeName = "$appName.exe"
$taskName = 'OmenFanPlusStartup'
$legacyTaskName = 'OmenHubLighterPlusStartup'
$driverName = 'WinRing0_1_2_0'
$project = Join-Path $PSScriptRoot "$appName\$appName.csproj"
$staging = Join-Path $PSScriptRoot 'artifacts\publish'

# robocopy /MIR deletes everything else in the target folder, so only accept a folder that is clearly ours:
# named after the app, and either new, empty, or already holding the app.
# A relative path is taken from the PowerShell location (GetFullPath alone would use the process directory).
$InstallDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir)
$InstallDir = [IO.Path]::GetFullPath($InstallDir).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ((Split-Path -Leaf $InstallDir) -ne $appName) {
    throw "InstallDir must be a folder named '$appName' (got '$InstallDir')."
}
if ((Test-Path -LiteralPath $InstallDir) -and
    @(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0 -and
    -not (Test-Path -LiteralPath (Join-Path $InstallDir $exeName))) {
    throw "'$InstallDir' already contains other files and no $exeName; refusing to mirror into it."
}
$exe = Join-Path $InstallDir $exeName

$inProgramFiles = $InstallDir.StartsWith($env:ProgramFiles + '\', [StringComparison]::OrdinalIgnoreCase)
if (-not $inProgramFiles) {
    # The app starts elevated at logon, so only a copy where only administrators can change it gets the task.
    Write-Warning "$InstallDir is outside Program Files, so no startup task will be created for it."
    $warnings++
}

$sdkHint = "The .NET 10 SDK is needed to build. Install it with one of:`n" +
    "    choco upgrade dotnet-10.0-sdk -y`n" +
    "    winget install --id Microsoft.DotNet.SDK.10 -e`n" +
    "    or download 'SDK 10.0.x, Windows x64' from https://dotnet.microsoft.com/download/dotnet/10.0"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw $sdkHint }
$sdks = @(& dotnet --list-sdks)
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) { throw $sdkHint }

Write-Host "[1/6] Building (about a minute the first time, a few seconds after that)..."
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
& dotnet publish $project -c Release -r win-x64 --self-contained false -o $staging -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
$builtExe = Join-Path $staging $exeName
if (-not (Test-Path -LiteralPath $builtExe)) { throw "The build did not produce $exeName." }

Write-Host "[2/6] Closing running copies of the app..."
# "--quit" asks a running OmenFanPlus to exit the normal way, so it hands the fans back to the BIOS first.
$quitAsked = $false
if (Get-Process -Name $appName -ErrorAction SilentlyContinue) {
    Write-Host "      asking the running OmenFanPlus to quit"
    Start-Process -FilePath $builtExe -ArgumentList '--quit' -Wait
    $quitAsked = $true
}
$running = @(Get-Process -Name $appName -ErrorAction SilentlyContinue)
foreach ($p in $running) { [void]$p.WaitForExit(15000) }
# Copies that are still running, and old OmenHubLighterPlus builds (they have only a hidden window and no
# --quit), are stopped by process ID.
$leftover = @(Get-Process -Name $appName, $legacyName -ErrorAction SilentlyContinue | Where-Object { -not $_.HasExited })
foreach ($p in $leftover) {
    $path = $p.Path
    if (-not $path) { $path = '(path unknown)' }
    Write-Host "      stopping PID $($p.Id): $path"
    # It may have finished exiting on its own in the meantime.
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
foreach ($p in $leftover) { [void]$p.WaitForExit(10000) }
if ($quitAsked -and $leftover.Count -eq 0) {
    Write-Host "      it closed"
} elseif (-not $quitAsked -and $leftover.Count -eq 0) {
    Write-Host "      none running"
}

Write-Host "[3/6] Checking for the old WinRing0 driver..."
# OmenHubLighterPlus builds before 2.0 loaded the vulnerable WinRing0 driver from their own folder, as
# OpenHardwareMonitorLib.sys next to OmenHubLighterPlus.exe, and removed it only on Quit from the tray.
# Other programs built on Open Hardware Monitor use the same service name, so only remove ours.
$driver = Get-CimInstance -ClassName Win32_SystemDriver -Filter "Name='$driverName'" -ErrorAction SilentlyContinue
$sysFile = $null
if ($driver) {
    $sysFile = $driver.PathName
    if ($sysFile.StartsWith('\??\')) { $sysFile = $sysFile.Substring(4) }
}
if ($driver -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $sysFile) "$legacyName.exe"))) {
    Write-Host "      removing driver service $driverName ($sysFile)"
    & sc.exe stop $driverName | Out-Null
    & sc.exe delete $driverName | Out-Null

    if (Get-CimInstance -ClassName Win32_SystemDriver -Filter "Name='$driverName'" -ErrorAction SilentlyContinue) {
        Write-Warning "$driverName is still registered; Windows removes it at the next restart."
        $warnings++
    }
    if (Test-Path -LiteralPath $sysFile) {
        try {
            Remove-Item -LiteralPath $sysFile -Force
            Write-Host "      deleted $sysFile"
        } catch {
            Write-Warning "Could not delete $sysFile ($($_.Exception.Message)); delete it after a restart."
            $warnings++
        }
    }
} elseif ($driver) {
    Write-Host "      $driverName belongs to another program ($sysFile); leaving it alone"
} else {
    Write-Host "      not installed"
}

$newVersion = (Get-Item -LiteralPath $builtExe).VersionInfo.ProductVersion
$oldVersion = 'nothing'
if (Test-Path -LiteralPath $exe) { $oldVersion = "version $((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion)" }
Write-Host "[4/6] Copying version $newVersion to $InstallDir (replacing $oldVersion)..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# /R:2 /W:2: the default is a million retries 30 s apart, which looks like a hang on one locked file.
$copyLog = & robocopy $staging $InstallDir /MIR /R:2 /W:2 /NP /NFL /NDL /NJH /NJS
$copyExit = $LASTEXITCODE
if ($copyExit -ge 8) {
    $copyLog | ForEach-Object { Write-Host "      $_" }
    throw "Copying the files failed (robocopy exit code $copyExit)."
}

Write-Host "[5/6] Updating the startup task..."
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$legacyTask = Get-ScheduledTask -TaskName $legacyTaskName -ErrorAction SilentlyContinue
if (($task -or $legacyTask) -and -not $inProgramFiles) {
    # Never point an elevated logon task at a folder outside Program Files; still retire the old one,
    # because it starts the pre-2.0 build that loads WinRing0.
    if ($legacyTask) {
        try {
            Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false
            Write-Host "      removed the old task $legacyTaskName"
        } catch {
            Write-Warning "Could not remove $legacyTaskName ($($_.Exception.Message)); delete it in Task Scheduler."
            $warnings++
        }
    }
    if ($task) {
        $keeps = ([string]@($task.Actions)[0].Execute).Trim('"')
        Write-Host "      $taskName was not changed: it still starts $keeps"
    } else {
        Write-Host "      not creating $taskName for a copy outside Program Files; autostart is off"
    }
} elseif ($task -or $legacyTask) {
    $oldExe = $null
    if ($legacyTask) { $oldExe = ([string]@($legacyTask.Actions)[0].Execute).Trim('"') }
    try {
        # The same task the app registers: this user, 10 s after logon, elevated, runs on battery,
        # normal priority (5), no time limit, one instance.
        $user = "$env:USERDOMAIN\$env:USERNAME"
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $trigger.Delay = 'PT10S'
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
            -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 5
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
        Register-ScheduledTask -TaskName $taskName -Description 'Start OmenFanPlus' -Force `
            -Action (New-ScheduledTaskAction -Execute $exe) -Trigger $trigger -Settings $settings `
            -Principal $principal | Out-Null
        Write-Host "      $taskName now starts $exe"

        if ($legacyTask) {
            Unregister-ScheduledTask -TaskName $legacyTaskName -Confirm:$false
            Write-Host "      removed the old task $legacyTaskName"
        }
    } catch {
        Write-Warning "Could not update the startup task ($($_.Exception.Message)); the app tries again when it starts."
        $warnings++
        # Don't leave the old build (which loads WinRing0) as the thing that starts at logon.
        if ($legacyTask) {
            try {
                Disable-ScheduledTask -TaskName $legacyTaskName | Out-Null
                Write-Host "      disabled the old task $legacyTaskName"
            } catch {
                Write-Warning "Could not disable $legacyTaskName either ($($_.Exception.Message)); delete it in Task Scheduler."
            }
        }
    }
    if ($oldExe -and (Test-Path -LiteralPath $oldExe)) {
        Write-Host "      note: the old build at $oldExe still exists. Don't start it: it loads WinRing0 again."
    }
} elseif ($inProgramFiles) {
    Write-Host "      no startup task yet (tick 'Run on startup' in the tray menu to create one)"
} else {
    Write-Host "      no startup task; autostart needs the Program Files install"
}

if ($NoStart) {
    Write-Host "[6/6] Not starting the app (-NoStart)."
} else {
    Write-Host "[6/6] Starting $exe"
    Start-Process -FilePath $exe -WorkingDirectory $InstallDir
}

$log = Join-Path $env:LOCALAPPDATA "$appName\$appName.log"
if ($warnings -gt 0) {
    Write-Host "[WARN] Installed, with $warnings warning(s) above. Log: $log" -ForegroundColor Yellow
} else {
    Write-Host "[OK] Installed to $InstallDir. Log: $log" -ForegroundColor Green
}
exit 0
