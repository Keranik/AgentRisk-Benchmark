# Builds the Agent Risk table app into client/build/windows/AgentRisk.exe.
#
# A raw Godot export is not enough for this project: it exits 0 even
# when the C# game assembly never made it into the export (a missing
# or non-export solution degrades it to warnings). This script checks
# the things that must be true, runs the export, and then verifies
# the product, failing with a nonzero exit and a plain reason
# otherwise. The run discipline: find the editor by candidates, never let a
# missing tool surface as a raw shell error, check template files
# rather than folders, grep run output for script errors (an exit 0
# can still be a broken run), and boot the exported executable at
# the end.
#
# The product is three things that ship together: AgentRisk.exe,
# AgentRisk.pck, and the data_AgentRiskClient_windows_x86_64 folder
# beside them (the game assembly and its .NET runtime).
#
# Requirements (the standard, non-mono packages cannot export a C#
# project):
#   - The Godot 4.7.2 MONO editor (Godot_v4.7.2-stable_mono_win64).
#   - The MONO export templates (Godot_v4.7.2-stable_mono_export_
#     templates.tpz), installed under
#     %APPDATA%\Godot\export_templates\4.7.2.stable.mono\
#   - .NET SDK 8 on PATH.
#
# Usage:  .\build-windows.ps1 [-GodotPath C:\path\to\godot.exe]
# The editor is found by -GodotPath, then $env:GODOT, then PATH.

param([string]$GodotPath = "")

$ErrorActionPreference = "Stop"
$clientDir = $PSScriptRoot
$exportDir = Join-Path $clientDir "build\windows"
$exePath = Join-Path $exportDir "AgentRisk.exe"
$pckPath = Join-Path $exportDir "AgentRisk.pck"
# A C# export publishes the game assembly and its .NET runtime into
# this folder beside the exe, not into the .pck (the preset leaves
# dotnet/embed_build_outputs off), and the exe loads them from here.
$dataDir = Join-Path $exportDir "data_AgentRiskClient_windows_x86_64"
$gameDll = Join-Path $dataDir "AgentRiskClient.dll"

function Fail([string]$reason) {
    Write-Host "BUILD FAILED: $reason"
    exit 1
}

# Start-Process joins -ArgumentList with spaces and does no quoting
# of its own.
function Quote-Arg([string]$arg) {
    if ($arg -match '[\s"]') { return '"' + ($arg -replace '"', '\"') + '"' }
    return $arg
}

# Lines that mean a broken run even under exit 0: script load
# errors, a .NET runtime or game assembly that did not load, a C#
# class the engine could not find, and exceptions out of game code.
$errorPattern = "SCRIPT ERROR|Parse Error|Failed to load script|\.NET: .*(fail|not found|missing|could not)|Cannot instantiate C# script|Unhandled exception|ERROR: System\.\S*Exception"

# Runs a program with output captured to temp files; returns the exit
# code, the file paths, any script-error lines, and whether it had to
# be killed for outliving $timeoutSeconds (0: no limit). It waits on
# the process itself, not with Start-Process -Wait, which also waits
# for every descendant: the export's dotnet publish leaves MSBuild
# nodes and the compiler server running for minutes after it is done.
$logDir = Join-Path $env:TEMP ("agentrisk_build_" + [guid]::NewGuid().ToString("n"))
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
function Invoke-Logged([string]$exe, [string]$name, [string[]]$arguments, [int]$timeoutSeconds = 0) {
    $out = Join-Path $logDir "$name.out"
    $err = Join-Path $logDir "$name.err"
    $p = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -ArgumentList ($arguments | ForEach-Object { Quote-Arg $_ }) `
        -RedirectStandardOutput $out -RedirectStandardError $err
    # Without -Wait, ExitCode stays readable only if the handle was
    # taken while the process was alive.
    $null = $p.Handle
    $timedOut = $false
    if ($timeoutSeconds -gt 0 -and -not $p.WaitForExit($timeoutSeconds * 1000)) {
        $timedOut = $true
        try { $p.Kill() } catch { }
    }
    $p.WaitForExit()
    $scriptErrors = Select-String -Path $out, $err -Pattern $errorPattern -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Code = $p.ExitCode; Out = $out; Err = $err; ScriptErrors = $scriptErrors; TimedOut = $timedOut }
}

# --- Find the editor -------------------------------------------------
# Resolve to a file that exists before invoking anything, so a
# missing godot is this script's failure line, not a shell throw.
$script:godotExe = $null
$candidates = @($GodotPath, $env:GODOT)
foreach ($c in $candidates) {
    if (-not [string]::IsNullOrEmpty($c) -and (Test-Path -LiteralPath $c -PathType Leaf)) {
        $script:godotExe = (Resolve-Path -LiteralPath $c).Path
        break
    }
}
if (-not $script:godotExe) {
    $onPath = Get-Command "Godot_v4.7.2-stable_mono_win64_console.exe", "Godot_v4.7.2-stable_mono_win64.exe", "godot", "godot4" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $script:godotExe = $onPath.Source }
}
if (-not $script:godotExe) {
    Fail "Godot was not found. Pass -GodotPath with the mono editor's path, or set `$env:GODOT."
}

# --- Preconditions ----------------------------------------------------
# The exporter looks for the solution beside the project and builds
# it as ExportRelease; without that configuration the export ships
# no game assembly and still exits 0.
$slnPath = Join-Path $clientDir "AgentRiskClient.sln"
if (-not (Test-Path $slnPath)) {
    Fail "client\AgentRiskClient.sln is missing. It is part of the repo; restore it before exporting."
}
if (-not (Select-String -Path $slnPath -Pattern "ExportRelease" -Quiet)) {
    Fail "AgentRiskClient.sln has no ExportRelease configuration. It must be the Godot-shaped solution from the repo, not a plain dotnet one."
}

$versionRun = Invoke-Logged $script:godotExe "version" @("--version")
$godotVersion = "$(Get-Content $versionRun.Out -Raw)".Trim()
if ($versionRun.Code -ne 0 -or -not $godotVersion.StartsWith("4.7.2")) {
    Fail "$($script:godotExe) reports version '$godotVersion'; this project needs Godot 4.7.2."
}
if ($godotVersion -notmatch "mono") {
    Fail "$($script:godotExe) is '$godotVersion', not the mono editor. A C# project needs Godot_v4.7.2-stable_mono_win64."
}
Write-Host "Godot $godotVersion"
Write-Host "   $($script:godotExe)"

# Check the template FILES, not just their folder: the standard set
# also creates a folder, and only the mono set has GodotSharp in its
# windows_release_x86_64.exe.
$templatesDir = Join-Path $env:APPDATA "Godot\export_templates\4.7.2.stable.mono"
$templateExe = Join-Path $templatesDir "windows_release_x86_64.exe"
if (-not (Test-Path $templateExe)) {
    Fail "Mono export template not found at $templateExe. Install Godot_v4.7.2-stable_mono_export_templates.tpz (Editor > Manage Export Templates)."
}
$templateVersion = Join-Path $templatesDir "version.txt"
if (Test-Path $templateVersion) {
    $tv = "$(Get-Content $templateVersion -Raw)".Trim()
    if ($tv -ne "4.7.2.stable.mono") {
        Fail "$templatesDir holds templates for '$tv', not 4.7.2.stable.mono."
    }
}

# A headless export does not create the preset's folder.
New-Item -ItemType Directory -Force -Path $exportDir | Out-Null

# Clear the last export's output: the checks below must see what THIS
# export wrote, and an earlier run's exe, pack or game assembly would
# otherwise pass them after an export that wrote nothing.
$consolePath = Join-Path $exportDir "AgentRisk.console.exe"
foreach ($stale in @($exePath, $pckPath, $consolePath, $dataDir)) {
    if (Test-Path -LiteralPath $stale) {
        try { Remove-Item -LiteralPath $stale -Recurse -Force }
        catch { Fail "Could not clear the previous export's $stale (is AgentRisk.exe still running?)." }
    }
}

# --- Export -------------------------------------------------------------
$export = Invoke-Logged $script:godotExe "export" @("--headless", "--path", $clientDir, "--export-release", "Windows Desktop", $exePath)
if ($export.Code -ne 0 -or $export.ScriptErrors) {
    $export.ScriptErrors | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
    Fail "Godot export failed (exit $($export.Code)); logs: $($export.Out), $($export.Err)"
}

if (-not (Test-Path $exePath)) { Fail "The export wrote no AgentRisk.exe." }
if (-not (Test-Path $pckPath)) { Fail "The export wrote no AgentRisk.pck." }

# The assembly check: the game assembly must be in the data folder
# beside the exe. An export that missed the C# build still exits 0;
# this does not.
if (-not (Test-Path $gameDll)) {
    Fail "The export wrote no $gameDll. The C# build was skipped during export; check the solution and the mono templates."
}

# The product check: boot the
# exported exe itself, which loads the game assembly and the .NET
# runtime from the data folder, so a missing or broken one shows up
# here as a load error. (Booting the .pck with the editor would not
# test them: an editor loads a project's assembly only from its own
# .godot\mono build output, and runs on silently without one.)
$bootTimeout = 120
$boot = Invoke-Logged $exePath "boot" @("--headless", "--quit-after", "180") $bootTimeout
if ($boot.TimedOut) {
    Fail "The exported AgentRisk.exe was still running $bootTimeout seconds into a headless boot and was killed; logs: $($boot.Out), $($boot.Err)"
}
if ($boot.Code -ne 0 -or $boot.ScriptErrors) {
    $boot.ScriptErrors | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
    Fail "The exported AgentRisk.exe did not boot clean (exit $($boot.Code)); logs: $($boot.Out), $($boot.Err)"
}

Remove-Item -Recurse -Force $logDir -ErrorAction SilentlyContinue
$size = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "Built $exePath ($size MB); the game assembly is in place and the exe boots clean."
Write-Host "Ship AgentRisk.exe, AgentRisk.pck and data_AgentRiskClient_windows_x86_64\ together; the exe does not run without the other two."
exit 0
