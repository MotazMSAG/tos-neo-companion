<#
.SYNOPSIS
    Builds the TOS companion, runs every offline test and (by default) publishes dist\ (SPEC 10.6).

.DESCRIPTION
    1. Sets the portable SDK environment (..\.dotnet next to the repo folder when present, else the installed SDK; no telemetry, no multilevel lookup).
    2. dotnet build Companion.sln -c <Configuration>
    3. dotnet test Companion.sln -c <Configuration> --no-build (all tests are offline: no game, no window, no elevation)
    4. dotnet publish src\Companion.App and src\Companion.Cli (win-x64, self-contained) into dist\   (skip: -NoPublish)
    5. Checks dist\data, dist\templates and dist\features\* are present.
    6. Prints the build hash (same algorithm as the host's trusted-build list) and a summary.

    This script NEVER starts Companion.App and NEVER elevates. -Install only copies dist\ to
    %ProgramFiles%\TOS-companion when the current PowerShell is ALREADY elevated (run it from an elevated prompt
    yourself); otherwise it prints what to do and copies nothing.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -NoPublish
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$NoPublish,
    [switch]$NoTest,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sdkRoot = Join-Path (Split-Path -Parent $root) '.dotnet'
$dotnet = Join-Path $sdkRoot 'dotnet.exe'
$solution = Join-Path $root 'Companion.sln'
$dist = Join-Path $root 'dist'

if (Test-Path $dotnet) {
    # 1. Portable SDK environment (SPEC 2).
    $env:DOTNET_ROOT = $sdkRoot
} else {
    # No portable SDK next to the repo: use the .NET SDK installed on this PC (global.json asks for 10.0.x).
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw "No .NET 10 SDK found: install it from https://dotnet.microsoft.com/download/dotnet/10.0" }
    $dotnet = $cmd.Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$summary = New-Object System.Collections.Generic.List[string]
$failed = $false
$watch = [System.Diagnostics.Stopwatch]::StartNew()

function Invoke-Dotnet {
    param([string[]]$Arguments)
    Write-Host ("> dotnet " + ($Arguments -join ' ')) -ForegroundColor Cyan
    $lines = New-Object System.Collections.Generic.List[string]
    & $dotnet @Arguments 2>&1 | ForEach-Object {
        $line = "$_"
        $lines.Add($line)
        Write-Host $line
    }
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Lines = $lines }
}

function Get-BuildHash {
    param([string]$InstallDir)
    $full = (Resolve-Path $InstallDir).Path.TrimEnd('\')
    $dataRoots = @('data', 'templates', 'features') | ForEach-Object { (Join-Path $full $_) + '\' }
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($pattern in @('*.dll', '*.exe')) {
        Get-ChildItem -Path $full -Filter $pattern -Recurse -File | ForEach-Object {
            $p = $_.FullName
            $inData = $false
            foreach ($d in $dataRoots) { if ($p.StartsWith($d, [StringComparison]::OrdinalIgnoreCase)) { $inData = $true } }
            if (-not $inData) { $files.Add($p) }
        }
    }
    foreach ($d in @('data', 'templates', 'features')) {
        $dir = Join-Path $full $d
        if (Test-Path $dir) { Get-ChildItem -Path $dir -Recurse -File | ForEach-Object { $files.Add($_.FullName) } }
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $rel = $f.Substring($full.Length + 1).ToUpperInvariant()
        $stream = [System.IO.File]::OpenRead($f)
        try { $h = ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose() }
        $lines.Add($rel + "`t" + $h + "`n")
    }
    $lines.Sort([System.StringComparer]::Ordinal)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join ''))
    $hash = ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    $sha.Dispose()
    return [pscustomobject]@{ Hash = $hash; Files = $files.Count }
}

# 2. Build.
$build = Invoke-Dotnet @('build', $solution, '-c', $Configuration, '-nologo')
if ($build.ExitCode -ne 0) {
    $summary.Add("BUILD   FAILED (exit $($build.ExitCode))")
    $failed = $true
} else {
    $warn = @($build.Lines | Where-Object { $_ -match '^\s*(\d+) Warning\(s\)' } | Select-Object -Last 1)
    $summary.Add("BUILD   ok ($Configuration) " + ($(if ($warn.Count -gt 0) { $warn[0].Trim() } else { '' })))
}

# 3. Tests (offline).
if (-not $failed -and -not $NoTest) {
    $test = Invoke-Dotnet @('test', $solution, '-c', $Configuration, '--no-build', '-nologo', '--filter', 'TestCategory!=probe&TestCategory!=tune')
    $totalPassed = 0; $totalFailed = 0; $totalSkipped = 0
    foreach ($line in $test.Lines) {
        if ($line -match '(Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+).*-\s+(\S+\.dll)') {
            $totalFailed += [int]$Matches[2]; $totalPassed += [int]$Matches[3]; $totalSkipped += [int]$Matches[4]
            $summary.Add(("TEST    {0,-34} passed {1,4}  failed {2,3}  skipped {3,3}" -f $Matches[6], $Matches[3], $Matches[2], $Matches[4]))
        }
    }
    $summary.Add(("TESTS   total passed {0}, failed {1}, skipped {2} (exit {3})" -f $totalPassed, $totalFailed, $totalSkipped, $test.ExitCode))
    if ($test.ExitCode -ne 0 -or $totalFailed -gt 0) { $failed = $true }
}

# 4-6. Publish, check, hash.
if (-not $failed -and -not $NoPublish) {
    if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
    foreach ($proj in @('src\Companion.App\Companion.App.csproj', 'src\Companion.Cli\Companion.Cli.csproj')) {
        $pub = Invoke-Dotnet @('publish', (Join-Path $root $proj), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true', '-o', $dist, '-nologo')
        if ($pub.ExitCode -ne 0) {
            $summary.Add("PUBLISH FAILED $proj (exit $($pub.ExitCode))")
            $failed = $true
        }
    }

    if (-not $failed) {
        $missing = @()
        foreach ($required in @('Companion.App.exe', 'Companion.Cli.exe', 'data\ui-map.json', 'data\procedures.json', 'data\readers.json', 'templates\templates.json')) {
            if (-not (Test-Path (Join-Path $dist $required))) { $missing += $required }
        }
        foreach ($feature in @('inventory', 'recipes', 'crafting', 'survey', 'homestead', 'teahouse')) {
            if (-not (Test-Path (Join-Path $dist "features\$feature\procedures.json"))) { $missing += "features\$feature" }
        }
        if (Test-Path (Join-Path $dist 'templates\staging')) { $missing += 'templates\staging must not ship' }
        if ($missing.Count -gt 0) {
            $summary.Add("DIST    MISSING: " + ($missing -join ', '))
            $failed = $true
        } else {
            $h = Get-BuildHash -InstallDir $dist
            $summary.Add("DIST    ok: $dist")
            $summary.Add(("HASH    build {0} ({1} files) - compare with 'Trust this build...' in the tray" -f $h.Hash.Substring(0, 16), $h.Files))
        }
    }
}

# Optional install (never self-elevates).
if ($Install -and -not $failed) {
    $principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $summary.Add("INSTALL skipped: run this script from an elevated PowerShell yourself to copy dist\ to Program Files.")
    } elseif (-not (Test-Path $dist)) {
        $summary.Add("INSTALL skipped: no dist\ (do not use -NoPublish with -Install).")
    } else {
        $target = Join-Path $env:ProgramFiles 'TOS-companion'
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item -Path (Join-Path $dist '*') -Destination $target -Recurse -Force
        $summary.Add("INSTALL copied dist\ to $target (start it from there; trust the build in the tray).")
    }
}

$watch.Stop()
Write-Host ''
Write-Host '==================== SUMMARY ====================' -ForegroundColor Yellow
foreach ($s in $summary) { Write-Host $s }
Write-Host ("TIME    {0:n1} s" -f $watch.Elapsed.TotalSeconds)
if ($failed) {
    Write-Host 'RESULT  FAILED' -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT  OK' -ForegroundColor Green
exit 0
