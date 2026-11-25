# Exercise only the BAM executable selected by build configuration inside this repository.
[CmdletBinding()]
param(
    [ValidateSet('Build', 'Publish', 'Demo', 'DemoEmpty', 'Verification')] [string] $Output = 'Publish',
    [switch] $Capture,
    [string] $CaptureDirectory = 'artifacts/screenshots'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$folder = switch ($Output) {
    Build { 'src/BAM.Desktop/bin/Release/net10.0-windows/win-x64' }
    Publish { 'artifacts/publish/win-x64' }
    Verification { 'artifacts/publish/verification-win-x64' }
    { $_ -in 'Demo', 'DemoEmpty' } { 'artifacts/publish/demo-win-x64' }
}
$exe = (Resolve-Path -LiteralPath (Join-Path $root "$folder/BAM.exe")).Path
$desktopRuntime = dotnet --list-runtimes |
    Where-Object { $_ -match "^Microsoft.WindowsDesktop.App $([Environment]::Version.Major)\." } |
    Select-Object -Last 1
if ($desktopRuntime -notmatch '^Microsoft.WindowsDesktop.App (\S+) \[(.+)\]') {
    throw 'The Windows Desktop runtime matching PowerShell 7 is needed for UI Automation.'
}
$runtimeFolder = Join-Path $Matches[2] $Matches[1]
foreach ($assembly in 'UIAutomationTypes', 'UIAutomationClient') {
    Add-Type -Path (Join-Path $runtimeFolder "$assembly.dll")
}
if (-not ('BamSmokeWindow' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'DesktopSmoke.Native.cs')
}
if ($Output -eq 'Verification') {
    $profile = Join-Path $root ('.local/phase3-desktop-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($profile)
}
if ($Capture) {
    if ($Output -notin 'Demo', 'DemoEmpty', 'Verification') { throw 'Capture requires a separate demo or verification build.' }
    $screenshots = [IO.Path]::GetFullPath($CaptureDirectory, $root)
    [void][IO.Directory]::CreateDirectory($screenshots)
    $captures = [Collections.Generic.List[string]]::new()
}

function Start-Bam {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.WorkingDirectory = Split-Path $exe
    $info.UseShellExecute = $false
    $info.RedirectStandardError = $true
    # Passing the same argument to production verifies that the provider is excluded.
    if ($Output -ne 'DemoEmpty') { $info.ArgumentList.Add('--demo') }
    if ($Output -eq 'Verification') {
        $info.ArgumentList.Add('--verification-profile')
        $info.ArgumentList.Add($profile)
    }
    if ($Capture) {
        $info.ArgumentList.Add('--capture-dir')
        $info.ArgumentList.Add($screenshots)
    }
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    $process
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
        if ($Process.HasExited) {
            throw "BAM exited before its window was available (code $($Process.ExitCode)). $($Process.StandardError.ReadToEnd())"
        }
        [BamSmokeWindow]::IsOwned($Process.MainWindowHandle, $Process.Id) -and $Process.MainWindowTitle -eq 'BAM'
    } 'Window did not appear.'
}
function Close-Bam([Diagnostics.Process] $Process) {
    if (-not $Process.CloseMainWindow() -or -not $Process.WaitForExit(5000) -or $Process.ExitCode -ne 0) {
        throw 'Normal close or exit code validation failed.'
    }
}
function Find-Element($Root, [string] $Name, [switch] $ById, [switch] $Selectable) {
    $property = if ($ById) { [Windows.Automation.AutomationElement]::AutomationIdProperty }
        else { [Windows.Automation.AutomationElement]::NameProperty }
    $condition = [Windows.Automation.PropertyCondition]::new($property, $Name)
    if ($Selectable) {
        $available = [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)
        $condition = [Windows.Automation.AndCondition]::new($condition, $available)
    }
    $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Element($Root, [string] $Name, [switch] $ById, [switch] $Selectable) {
    Wait-For { $null -ne (Find-Element $Root $Name -ById:$ById -Selectable:$Selectable) } "UI element not found: $Name"
    Find-Element $Root $Name -ById:$ById -Selectable:$Selectable
}
function Select-Item($Root, [string] $Name) {
    $item = Wait-Element $Root $Name -Selectable
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Select-Option($Root, [string] $Control, [string] $Name) {
    $combo = Wait-Element $Root $Control -ById
    $expand = $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try { Select-Item $combo $Name }
    finally {
        if ($expand.Current.ExpandCollapseState -eq 'Expanded') { $expand.Collapse() }
    }
}
function Toggle-Setting($Root, [string] $Id) {
    $element = Wait-Element $Root $Id -ById
    $toggle = $element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()
    $toggle.Current.ToggleState
}
function Save-Window($Process, [IntPtr] $Handle, [string] $Name) {
    if (-not $Capture) { return }
    Start-Sleep -Milliseconds 250
    $path = Join-Path $screenshots "$Name.png"
    $index = [int]$Name.Substring(0, 2)
    $previous = if ([IO.File]::Exists($path)) { [IO.File]::GetLastWriteTimeUtc($path) } else { [DateTime]::MinValue }
    if (-not [BamSmokeWindow]::IsOwned($Handle, $Process.Id) -or
        -not [BamSmokeWindow]::PostMessage($Handle, 0x8042, [IntPtr]$index, [IntPtr]::Zero)) {
        throw 'Failed to request capture from the owned window.'
    }
    Wait-For { [IO.File]::Exists($path) -and [IO.File]::GetLastWriteTimeUtc($path) -gt $previous } "BAM did not save $Name."
    $captures.Add($path)
}
function Test-Duplicate($Primary, [IntPtr] $Handle) {
    $secondary = Start-Bam
    if (-not $secondary.WaitForExit(5000) -or $secondary.ExitCode -ne 0) { throw 'Secondary did not signal and exit.' }
    Wait-For {
        [BamSmokeWindow]::IsWindowVisible($Handle) -and -not [BamSmokeWindow]::IsIconic($Handle)
    } 'Activation did not restore the primary.'
    $Primary.Refresh()
    if ($Primary.HasExited -or $Primary.MainWindowHandle -ne $Handle) { throw 'Primary window changed.' }
}
function Measure-OwnedBam([Diagnostics.Process] $Process, [string] $Scenario, [int] $Seconds) {
    $Process.Refresh()
    $before = $Process.TotalProcessorTime
    $handles = $Process.HandleCount
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $Seconds
    $Process.Refresh()
    $cpu = 100 * ($Process.TotalProcessorTime - $before).TotalSeconds / $elapsed.Elapsed.TotalSeconds / [Environment]::ProcessorCount
    Write-Output ('METRIC {0} {1:N1}s CPU {2:N3}% WS {3:N1} MiB Private {4:N1} MiB Handles {5}->{6}' -f
        $Scenario, $elapsed.Elapsed.TotalSeconds, $cpu, ($Process.WorkingSet64 / 1MB),
        ($Process.PrivateMemorySize64 / 1MB), $handles, $Process.HandleCount)
}

$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    $startup = [Diagnostics.Stopwatch]::StartNew()
    $primary = Start-Bam
    Wait-Window $primary
    $startup.Stop()
    $handle = $primary.MainWindowHandle
    $window = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $nav = Wait-Element $window '主导航'
    $dpi = [BamSmokeWindow]::GetDpiForWindow($handle)
    if ($Output -eq 'Demo') {
        Save-Window $primary $handle '01-dashboard-dark'
        [void](Wait-Element $window '演示模式')
        [void](Wait-Element $window 'MaaEnd')

        $memory = Wait-Element $window 'ChartMemory' -ById
        $memory.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        [void](Wait-Element $window '工作集 · MiB')
        Save-Window $primary $handle '07-dashboard-memory'
        $cpu = Wait-Element $window 'ChartCpu' -ById
        $cpu.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        [void](Wait-Element $window 'CPU · %')
        $errors = Wait-Element $window 'OpenErrors' -ById
        $errors.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        [void](Wait-Element $window '1 条记录')
        Select-Option $window 'LogLevel' '全部级别'

        Select-Item $nav '脚本管理'
        $list = Wait-Element $window 'ManagedScripts' -ById
        [void](Wait-Element $window '<示例目录>\MaaEnd\MaaEnd.exe')
        Save-Window $primary $handle '04-scripts'
        Select-Item $list 'BBchannel，Fate/Grand Order'
        [void](Wait-Element $window '<示例目录>\BBchannel\BBchannel.exe')

        Select-Item $nav '运行日志'
        [void](Wait-Element $window '8 条记录')
        Save-Window $primary $handle '05-logs'
        Select-Option $window 'LogLevel' '错误'
        [void](Wait-Element $window '1 条记录')
        Select-Option $window 'LogSource' 'BBchannel'
        [void](Wait-Element $window '演示故障 · 日志来源暂不可用，仍可查看历史样本。')
        $search = Wait-Element $window 'LogSearch' -ById
        $search.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('no-result-phrase')
        [void](Wait-Element $window '没有符合当前筛选条件的记录')
        [void](Toggle-Setting $window 'LogFollow')

        Select-Item $nav '设置'
        [void](Wait-Element $window 'ThemeChoice' -ById)
        Save-Window $primary $handle '06-settings'
        if ((Toggle-Setting $window 'ReduceMotion') -ne 'On') { throw 'Reduce-motion toggle did not change.' }
        Select-Option $window 'ThemeChoice' '跟随系统'
        Select-Option $window 'ThemeChoice' '浅色'
        Select-Item $nav '总览'
        [void](Wait-Element $window '资源监控')
        Save-Window $primary $handle '02-dashboard-light'

        Select-Item $nav '设置'
        Select-Option $window 'ThemeChoice' '深色'
        Select-Item $nav '总览'
        [BamSmokeWindow]::Resize($handle, $primary.Id, 920, 730)
        $overview = Wait-Element $window '总览内容'
        $scroll = $overview.GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
        $scroll.SetScrollPercent(-1, 22)
        $first = Wait-Element $window 'MaaEnd'
        $second = Wait-Element $window 'MAA'
        Wait-For {
            $left = $first.Current.BoundingRectangle
            $right = $second.Current.BoundingRectangle
            $left.Width -gt 0 -and $right.Width -gt 0 -and $right.Left -gt $left.Left -and
                [Math]::Abs($right.Top - $left.Top) -lt 2
        } 'Script cards did not reflow to two columns.'
        $scroll.SetScrollPercent(-1, 0)

        [BamSmokeWindow]::Resize($handle, $primary.Id, 800, 540)
        $narrowScript = Wait-Element $window 'MaaEnd'
        $narrowChart = Wait-Element $window '资源监控'
        $manageScripts = Wait-Element $window '管理脚本'
        Save-Window $primary $handle '03-dashboard-narrow'
        Wait-For {
            $viewport = $overview.Current.BoundingRectangle
            $script = $narrowScript.Current.BoundingRectangle
            $manage = $manageScripts.Current.BoundingRectangle
            $chart = $narrowChart.Current
            $script.Height -gt 0 -and $script.Top -ge $viewport.Top -and $script.Bottom -le $viewport.Bottom -and
                $manage.Height -gt 0 -and $manage.Top -ge $viewport.Top -and $manage.Bottom -le $viewport.Bottom -and
                ($chart.IsOffscreen -or $chart.BoundingRectangle.Top -ge $viewport.Bottom)
        } 'The narrow overview did not prioritize script status over the chart.'
        if ($Capture) {
            $scroll.SetScrollPercent(-1, 24)
            Save-Window $primary $handle '09-narrow-cards'
        }
        [void][BamSmokeWindow]::ShowWindowAsync($handle, 3)
        Wait-For { [BamSmokeWindow]::IsZoomed($handle) } 'Window did not maximize.'
        [void][BamSmokeWindow]::ShowWindowAsync($handle, 9)
        Wait-For { -not [BamSmokeWindow]::IsZoomed($handle) } 'Window did not restore from maximize.'

        Select-Item $nav '设置'
        if ((Toggle-Setting $window 'CloseToTray') -ne 'On') { throw 'Close-to-tray toggle did not change.' }
        if (-not $primary.CloseMainWindow()) { throw 'Closing to tray failed.' }
        Wait-For { -not [BamSmokeWindow]::IsWindowVisible($handle) } 'BAM did not hide to tray.'
        Test-Duplicate $primary $handle
        Select-Item $nav '设置'
        if ((Toggle-Setting $window 'CloseToTray') -ne 'Off') { throw 'Close-to-tray did not disable.' }
        Write-Output "PASS Demo navigation, chart units, script selection, log filters, theme selection, reduced motion, resize, maximize/restore and tray reactivation ($dpi DPI)."
    } elseif ($Output -eq 'Verification') {
        [void](Wait-Element $window '本地进程监控')
        if (Find-Element $window '演示模式') { throw 'Verification build enabled the demo provider.' }
        foreach ($name in 'MaaEnd', 'MAA', 'BBchannel') { [void](Wait-Element $window $name) }
        Write-Output "METRIC Warm/unspecified-cache startup $($startup.ElapsedMilliseconds) ms, DPI $dpi."
        Start-Sleep -Seconds 5
        Measure-OwnedBam $primary 'foreground' 12
        Save-Window $primary $handle '01-dashboard-dark'
        Select-Item $nav '脚本管理'
        [void](Wait-Element $window '已管理软件')
        Select-Item (Wait-Element $window 'ManagedScripts' -ById) 'BBchannel，Fate/Grand Order'
        Save-Window $primary $handle '04-scripts'
        Select-Item $nav '运行日志'
        [void](Wait-Element $window 'BAM 已启动')
        Save-Window $primary $handle '05-logs'
        Select-Item $nav '设置'
        [void](Wait-Element $window 'CollectResources' -ById)
        Save-Window $primary $handle '06-settings'
        if ((Toggle-Setting $window 'CollectResources') -ne 'Off') { throw 'Pause monitoring did not toggle.' }
        [void](Wait-Element $window '资源采样已暂停')
        if ((Toggle-Setting $window 'CollectResources') -ne 'On') { throw 'Resume monitoring did not toggle.' }
        Select-Option $window 'ThemeChoice' '浅色'
        Select-Item $nav '总览'
        Save-Window $primary $handle '02-dashboard-light'
        [BamSmokeWindow]::Resize($handle, $primary.Id, 800, 600)
        Save-Window $primary $handle '03-dashboard-narrow'
        Select-Item $nav '设置'
        Select-Option $window 'ThemeChoice' '深色'
        if ((Toggle-Setting $window 'CloseToTray') -ne 'On') { throw 'Close-to-tray did not toggle.' }
        if (-not $primary.CloseMainWindow()) { throw 'Closing to tray failed.' }
        Wait-For { -not [BamSmokeWindow]::IsWindowVisible($handle) } 'BAM did not hide to tray.'
        Measure-OwnedBam $primary 'tray' 11
        Test-Duplicate $primary $handle
        Select-Item $nav '设置'
        if ((Toggle-Setting $window 'CloseToTray') -ne 'Off') { throw 'Close-to-tray did not disable.' }
        Write-Output 'PASS Live provider, three real cards, log navigation, themes, sampling preference and tray only.'
    } elseif ($Output -eq 'DemoEmpty') {
        [void](Wait-Element $window '本地进程监控')
        if (Find-Element $window '演示模式') { throw 'Demo-enabled build entered demo without its token.' }
    } else {
        [void](Wait-Element $window '本地进程监控')
        if (Find-Element $window '演示模式') { throw 'Production enabled the demo provider.' }
    }

    [void][BamSmokeWindow]::ShowWindowAsync($handle, 7)
    Wait-For { [BamSmokeWindow]::IsIconic($handle) } 'Window did not minimize.'
    Test-Duplicate $primary $handle
    Close-Bam $primary
    if ($Output -eq 'Verification' -and $Capture) {
        $config = Join-Path $profile 'installations.json'
        $data = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json -AsHashtable
        $missing = Join-Path $profile 'missing-fixture.exe'
        $data['Programs'] += @{
            Id = [guid]::NewGuid().ToString()
            Name = '缺失路径夹具'
            Kind = 0
            Entry = @{ OriginalPath = $missing; TargetPath = $missing; WorkingDirectory = $profile; Arguments = '' }
            InstallDirectory = $profile
            WorkerPaths = @($missing)
        }
        $data | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $config -Encoding UTF8
    }
    $restart = Start-Bam
    Wait-Window $restart
    if ($Output -eq 'Verification' -and $Capture) {
        $restartWindow = [Windows.Automation.AutomationElement]::FromHandle($restart.MainWindowHandle)
        Select-Item (Wait-Element $restartWindow '主导航') '脚本管理'
        Select-Item (Wait-Element $restartWindow 'ManagedScripts' -ById) '缺失路径夹具，Windows 程序'
        [void](Wait-Element $restartWindow '启动入口已失效，请重新定位。')
        Save-Window $restart $restart.MainWindowHandle '10-unavailable'
    }
    Close-Bam $restart
    if ($Capture) { $captures | Write-Output }
    Write-Output "PASS $Output WPF startup, duplicate activation, primary close and relaunch."
} catch {
    Write-Output $_.ScriptStackTrace
    throw
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
