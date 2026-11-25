# Exercise only the BAM executable selected by build configuration inside this repository.
[CmdletBinding()]
param([ValidateSet('Build', 'Publish')] [string] $Output = 'Publish')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$folder = if ($Output -eq 'Build') {
    'src/BAM.Desktop/bin/Release/net10.0-windows/win-x64'
} else {
    'artifacts/publish/win-x64'
}
$exe = (Resolve-Path -LiteralPath (Join-Path $root "$folder/BAM.exe")).Path
if (-not ('BamSmokeWindow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BamSmokeWindow {
    [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr hwnd);
}
'@
}
function Start-Bam {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.WorkingDirectory = Split-Path $exe
    $info.UseShellExecute = $false
    [Diagnostics.Process]::Start($info)
}
function Wait-For([scriptblock] $Condition, [string] $Failure) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($timer.Elapsed.TotalSeconds -gt 10) { throw $Failure }
        Start-Sleep -Milliseconds 50
    }
}
function Wait-Window([Diagnostics.Process] $Process) {
    Wait-For {
        $Process.Refresh()
        if ($Process.HasExited) { throw "BAM exited before its window was available (code $($Process.ExitCode))." }
        $Process.MainWindowHandle -ne [IntPtr]::Zero -and $Process.MainWindowTitle -eq 'BAM · Phase 1 验证'
    } 'Window did not appear.'
}
function Close-Bam([Diagnostics.Process] $Process) {
    if (-not $Process.CloseMainWindow() -or -not $Process.WaitForExit(5000) -or $Process.ExitCode -ne 0) {
        throw 'Normal close or exit code validation failed.'
    }
}

$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    $primary = Start-Bam
    $owned.Add($primary)
    Wait-Window $primary
    $handle = $primary.MainWindowHandle
    [void][BamSmokeWindow]::ShowWindowAsync($handle, 7)
    Wait-For { [BamSmokeWindow]::IsIconic($handle) } 'Window did not minimize.'
    $secondary = Start-Bam
    $owned.Add($secondary)
    if (-not $secondary.WaitForExit(5000) -or $secondary.ExitCode -ne 0) { throw 'Secondary did not signal and exit.' }
    Wait-For { -not [BamSmokeWindow]::IsIconic($handle) } 'Activation did not restore the primary.'
    $primary.Refresh()
    if ($primary.HasExited -or $primary.MainWindowHandle -ne $handle) { throw 'Primary window changed.' }
    Close-Bam $primary
    $restart = Start-Bam
    $owned.Add($restart)
    Wait-Window $restart
    Close-Bam $restart
    Write-Output "PASS $Output WPF startup, duplicate activation, primary close and relaunch."
} finally {
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                [void]$process.WaitForExit(3000)
            }
        } finally { $process.Dispose() }
    }
}
