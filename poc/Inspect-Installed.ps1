# PowerShell 7 on Windows. Read-only, sanitized output.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('BamObservation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class BamObservation {
    [DllImport("kernel32", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);
    public static string ImagePath(int pid) {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try {
            int size = 32768;
            var name = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
        } finally { CloseHandle(handle); }
    }
    delegate bool Callback(IntPtr window, IntPtr data);
    [DllImport("user32")] static extern bool EnumWindows(Callback callback, IntPtr data);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int size);
    public static string[] Windows(int target) {
        var items = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr unused) {
            int pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var title = new StringBuilder(512);
                items.Add("visible; titleLength=" + GetWindowText(h, title, title.Capacity) + "; hung=" + IsHungAppWindow(h));
            }
            return true;
        }, IntPtr.Zero);
        return items.ToArray();
    }
}
'@
}
$shell = New-Object -ComObject WScript.Shell
$folders = @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('CommonDesktopDirectory'),
    (Join-Path $env:USERPROFILE 'Desktop')) + @(Get-ChildItem Env:OneDrive* | ForEach-Object { Join-Path $_.Value 'Desktop' })
foreach ($folder in ($folders | Sort-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    foreach ($link in (Get-ChildItem -LiteralPath $folder -Filter '*.lnk')) {
        $shortcut = $shell.CreateShortcut($link.FullName)
        $target = $shortcut.TargetPath
        if (-not $target -or ($link.BaseName + ' ' + $target) -notmatch '(?i)maaend|\bMAA\b|bbchannel') { continue }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        $root = Split-Path -LiteralPath $target
        $workers = @($target)
        if ([IO.Path]::GetFileName($target) -eq '启动.cmd' -and $link.BaseName -match 'BBchannel') {
            $workers = @('dist\BBchannel64\BBchannel.exe','dist\BBchannel\BBchannel.exe',
                'dist\BBchannel64\BBchannel_debug.exe','dist\BBchannel\BBchannel_debug.exe') | ForEach-Object { Join-Path $root $_ }
        }
        if ($shortcut.IconLocation -match '^(.*\.ico),0$') {
            $icon = [Drawing.Icon]::new($Matches[1])
        } else { $icon = [Drawing.Icon]::ExtractAssociatedIcon($target) }
        try { Write-Output ("{0} icon={1}x{2}" -f $link.BaseName,$icon.Width,$icon.Height) } finally { if ($icon) { $icon.Dispose() } }
        $names = @($workers | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) } | Sort-Object -Unique)
        foreach ($process in (Get-Process -Name $names -ErrorAction SilentlyContinue)) {
            try {
                $path = [BamObservation]::ImagePath($process.Id)
                if (-not $path -or $workers -notcontains $path) { Write-Output "$($link.BaseName) candidate identity unavailable or mismatched"; continue }
                $created = $process.StartTime
                $before = $process.TotalProcessorTime
                $timer = [Diagnostics.Stopwatch]::StartNew()
                Start-Sleep -Milliseconds 1000
                $process.Refresh()
                if ($process.HasExited -or $process.StartTime -ne $created) { continue }
                $delta = $process.TotalProcessorTime - $before
                $ms = $timer.Elapsed.TotalMilliseconds
                $cpu = 100 * $delta.TotalMilliseconds / ($ms * [Environment]::ProcessorCount)
                [pscustomobject]@{ Script=$link.BaseName; Identity='exact path + PID + creation'; LogicalCPUs=[Environment]::ProcessorCount;
                    CpuPercent=[math]::Round($cpu,3); SampleMs=[math]::Round($ms); WorkingSetMiB=[math]::Round($process.WorkingSet64/1MB,1);
                    PrivateMiB=[math]::Round($process.PrivateMemorySize64/1MB,1); UptimeSeconds=[int]((Get-Date)-$created).TotalSeconds;
                    VisibleWindows=@([BamObservation]::Windows($process.Id)) } | ConvertTo-Json -Compress
            } catch { Write-Output "$($link.BaseName) sample unavailable ($($_.Exception.GetType().Name))" }
            finally { $process.Dispose() }
        }
    }
}
