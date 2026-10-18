<#
.SYNOPSIS
    Checks install.ps1 without running it: it parses, and its install-folder guard refuses folders that
    robocopy /MIR must never mirror into.

.DESCRIPTION
    Only the guard block of install.ps1 is executed (the lines from the "robocopy /MIR deletes" comment up to
    "$exe = Join-Path"). The test refuses to run that block if it contains anything with a side effect.
    Prints one [PASS]/[FAIL] line per check and exits 1 on any failure.

        powershell -NoProfile -ExecutionPolicy Bypass -File tests\Test-InstallScript.ps1 [-ScriptPath <copy>]
#>
param(
    [string]$ScriptPath = (Join-Path $PSScriptRoot '..\install.ps1')
)

$ErrorActionPreference = 'Stop'
$failures = 0
$checks = 0

function Assert-Check([string]$Name, [bool]$Condition) {
    $script:checks++
    if ($Condition) { Write-Host "[PASS] $Name" }
    else { Write-Host "[FAIL] $Name"; $script:failures++ }
}

$ScriptPath = (Resolve-Path -LiteralPath $ScriptPath).Path
$lines = [IO.File]::ReadAllLines($ScriptPath)
$bytes = [IO.File]::ReadAllBytes($ScriptPath)

# Bytes: ASCII only (Windows PowerShell 5.1 reads a BOM-less UTF-8 file as ANSI), CRLF, no BOM.
$text = [Text.Encoding]::ASCII.GetString($bytes)
Assert-Check 'install.ps1 is pure ASCII' (@($bytes | Where-Object { $_ -gt 127 }).Count -eq 0)
Assert-Check 'install.ps1 has no BOM' (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB))
Assert-Check 'install.ps1 uses CRLF only' (($text -split "`r`n").Count -eq ($text -split "`n").Count)

$tokens = $null; $parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$tokens, [ref]$parseErrors)
Assert-Check 'install.ps1 parses without errors' ($parseErrors.Count -eq 0)

# Extract the guard block.
$start = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l -match '^# robocopy /MIR deletes' })
$end = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l -match '^\$exe = Join-Path' })
Assert-Check 'guard block found' ($start -ge 0 -and $end -gt $start)
if ($start -lt 0 -or $end -le $start) { exit 1 }

$guard = ($lines[$start..($end - 1)] -join "`r`n")
# Match against code only: the block's comment talks about robocopy.
$guardCode = (@($lines[$start..($end - 1)] | Where-Object { $_.Trim() -notmatch '^#' }) -join "`r`n")
Assert-Check 'guard block holds both refusals' (([regex]::Matches($guardCode, '\bthrow\b')).Count -eq 2)
$sideEffects = 'Remove-Item|robocopy|Stop-Process|sc\.exe|dotnet|Set-ScheduledTask|Register-ScheduledTask|Unregister-ScheduledTask|Start-Process|New-Item'
if ($guardCode -match $sideEffects) {
    Write-Host "[FAIL] guard block contains a command with side effects; not running it"
    exit 1
}

# The guard only protects anything if it runs first: no code line before its end may have a side effect
# or touch $InstallDir (apart from the param block's default).
$helpEnd = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l -match '^#>' })
$firstSideEffect = -1
for ($i = $helpEnd + 1; $i -lt $lines.Count; $i++) {
    $code = $lines[$i].Trim()
    if ($code -eq '' -or $code.StartsWith('#')) { continue }
    if ($code -match $sideEffects) { $firstSideEffect = $i; break }
}
Assert-Check 'no side effect runs before the guard' ($firstSideEffect -gt $end)
$paramEnd = [Array]::FindIndex($lines, [Predicate[string]]{ param($l) $l -match '^\)' })
$earlyUse = @($lines[($paramEnd + 1)..($start - 1)] | Where-Object { $_.Trim() -notmatch '^#' -and $_ -match '\$InstallDir' })
Assert-Check 'InstallDir is not used before the guard' ($earlyUse.Count -eq 0)

$runner = [scriptblock]::Create(
    "param(`$InstallDir)`r`n`$ErrorActionPreference = 'Stop'`r`n`$appName = 'OmenFanPlus'`r`n" +
    "`$exeName = 'OmenFanPlus.exe'`r`n" + $guard)

function Test-Accepted([string]$Dir) {
    try { & $runner $Dir; return $true } catch { return $false }
}

# Scratch folders for the cases that need files on disk.
$root = Join-Path ([IO.Path]::GetTempPath()) ("ohlp-guard-" + [Guid]::NewGuid().ToString('N'))
try {
    $fresh = Join-Path $root 'a\OmenFanPlus'
    $empty = Join-Path $root 'b\OmenFanPlus'
    $foreign = Join-Path $root 'c\OmenFanPlus'
    $installed = Join-Path $root 'd\OmenFanPlus'
    New-Item -ItemType Directory -Force -Path $empty, $foreign, $installed | Out-Null
    Set-Content -LiteralPath (Join-Path $foreign 'notes.txt') -Value 'someone else''s file'
    Set-Content -LiteralPath (Join-Path $installed 'OmenFanPlus.exe') -Value 'stub'

    Assert-Check 'refuses C:\Windows' (-not (Test-Accepted 'C:\Windows'))
    Assert-Check 'refuses a drive root' (-not (Test-Accepted 'C:\'))
    Assert-Check 'refuses a folder with another name' (-not (Test-Accepted (Join-Path $root 'a')))
    Assert-Check 'refuses an app-named folder holding other files' (-not (Test-Accepted $foreign))
    Assert-Check 'accepts a new folder' (Test-Accepted $fresh)
    Assert-Check 'accepts an empty folder' (Test-Accepted $empty)
    Assert-Check 'accepts a folder that already has the app' (Test-Accepted $installed)
    Assert-Check 'accepts the default path with a trailing backslash' (Test-Accepted ((Join-Path $env:ProgramFiles 'OmenFanPlus') + '\'))
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Write-Host ""
if ($failures -gt 0) { Write-Host "$failures of $checks checks failed"; exit 1 }
Write-Host "All $checks checks passed"
exit 0
