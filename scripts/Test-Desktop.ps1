# Exercise only the BAM executable selected by build configuration inside this repository.
[CmdletBinding()]
param(
    [ValidateSet('Build', 'Publish', 'Demo', 'DemoEmpty', 'Verification', 'VerificationEmpty', 'OwnLoad')] [string] $Output = 'Publish',
    [string] $ExecutablePath,
    [switch] $Capture,
    [string] $CaptureDirectory = 'artifacts/screenshots',
    [ValidateSet('Both', 'Tray')] [string] $OwnLoadPhase = 'Both',
    [ValidateRange(10, 600)] [int] $OwnLoadSeconds = 600,
    [switch] $CheckMonitorDpi,
    [switch] $RequireForegroundChecks,
    [switch] $CheckChartAccessibility,
    [switch] $CheckLogStress,
    [switch] $CheckLogReplay,
    [switch] $CheckBatchResult
)

$ErrorActionPreference = 'Stop'
if (($CheckMonitorDpi -or $RequireForegroundChecks -or $CheckLogStress -or $CheckLogReplay) -and $Output -ne 'VerificationEmpty') {
    throw 'Monitor, foreground and log checks require an isolated VerificationEmpty build.'
}
if ($CheckLogReplay -and $CheckLogStress) { throw 'Run log replay and stress separately.' }
if ($CheckBatchResult -and $Output -ne 'VerificationEmpty') { throw 'Batch feedback requires an isolated VerificationEmpty build.' }
if ($CheckChartAccessibility -and $Output -ne 'OwnLoad') { throw 'The chart accessibility check requires the isolated OwnLoad fixture.' }
$root = Split-Path $PSScriptRoot
$folder = switch ($Output) {
    Build { 'src/BAM.Desktop/bin/Release/net10.0-windows/win-x64' }
    Publish { 'artifacts/publish/win-x64' }
    { $_ -in 'Verification', 'OwnLoad' } { 'artifacts/publish/verification-win-x64' }
    VerificationEmpty { 'artifacts/publish/verification-empty-win-x64' }
    { $_ -in 'Demo', 'DemoEmpty' } { 'artifacts/publish/demo-win-x64' }
}
if ($ExecutablePath) {
    if ($Output -ne 'Publish') { throw 'ExecutablePath is only used for production publish smoke tests.' }
    $exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
    if ((Split-Path $exe -Leaf) -cne 'BAM.exe' -or
        -not $exe.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Select a BAM.exe built inside this repository.'
    }
} else {
    $exe = (Resolve-Path -LiteralPath (Join-Path $root "$folder/BAM.exe")).Path
}
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
if ($CheckChartAccessibility -and -not ('BamSmokeNotifications' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'DesktopSmoke.Notifications.cs') -ReferencedAssemblies @(
        (Join-Path $runtimeFolder 'UIAutomationTypes.dll'), (Join-Path $runtimeFolder 'UIAutomationClient.dll'),
        (Join-Path $PSHOME 'ref/System.Runtime.dll'), (Join-Path $PSHOME 'ref/System.Collections.Concurrent.dll'))
}
if ($Output -in 'Verification', 'VerificationEmpty', 'OwnLoad') {
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
    if ($Output -in 'Verification', 'VerificationEmpty', 'OwnLoad') {
        $info.ArgumentList.Add('--verification-profile')
        $info.ArgumentList.Add($profile)
    }
    if ($Output -in 'VerificationEmpty', 'OwnLoad') { $info.ArgumentList.Add('--verification-skip-discovery') }
    if ($CheckLogStress) { $info.ArgumentList.Add('--verification-log-stress') }
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
function Test-ChartAccessibility($Window, [IntPtr] $Handle, [int] $ProcessId) {
    $plot = Wait-Element $Window '最近 120 秒资源曲线'
    if ($plot.Current.HelpText -notlike '使用左右方向键查看采样。*') { throw 'The plot did not expose its keyboard instructions.' }
    $notifications = [BamSmokeNotifications]::new($plot)
    try {
        $inspect = {
            param([int] $Key, [string] $UnitPattern)
            [BamSmokeWindow]::PressKeyInOwnedWindow($Handle, $ProcessId, $Key)
            Wait-For { $notifications.Count -gt 0 } 'Keyboard inspection did not raise a UI Automation notification.'
            $event = $notifications.Take()
            if ($event.NotificationKind -ne 'ActionCompleted' -or $event.DisplayString -notmatch $UnitPattern -or
                $event.DisplayString -ne $plot.Current.HelpText -or
                $event.NotificationProcessing -ne 'MostRecent' -or $event.ActivityId -ne 'ResourceChart') {
                throw ('Unexpected chart notification: {0}, help text: {1}' -f $event.DisplayString, $plot.Current.HelpText)
            }
            $event.DisplayString
        }

        $plot.SetFocus()
        Wait-For { $plot.Current.HasKeyboardFocus } 'The chart did not accept keyboard focus.'
        Start-Sleep -Milliseconds 1500
        if ($notifications.Count -ne 0) { throw 'Background chart updates announced without keyboard input.' }
        $latest = & $inspect 0x25 '\d{2}:\d{2}:\d{2} · CPU [\d.,]+%$'
        $earlier = & $inspect 0x25 '\d{2}:\d{2}:\d{2} · CPU [\d.,]+%$'
        if ($earlier -eq $latest -or (& $inspect 0x27 '\d{2}:\d{2}:\d{2} · CPU [\d.,]+%$') -ne $latest) {
            throw 'The arrow keys did not inspect distinct CPU samples in both directions.'
        }

        $memory = Wait-Element $Window 'ChartMemory' -ById
        $memory.SetFocus()
        $memory.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        [void](Wait-Element $Window '工作集 · MiB')
        $plot.SetFocus()
        Wait-For { $plot.Current.HasKeyboardFocus } 'The memory chart did not accept keyboard focus.'
        [void](& $inspect 0x27 '\d{2}:\d{2}:\d{2} · 工作集 [\d.,]+ MiB$')
        Start-Sleep -Milliseconds 1500
        if ($notifications.Count -ne 0) { throw 'Chart refresh repeated a previous announcement.' }
    } finally { $notifications.Dispose() }
    Write-Output 'PASS Keyboard chart inspection announces CPU and working-set samples through UI Automation. Passive refresh stays quiet.'
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
function Measure-OwnLoad($Process, [string] $Scenario, [scriptblock] $Verify) {
    $elapsed = 0
    Write-Output "START $OwnLoadSeconds-second $Scenario observation."
    while ($elapsed -lt $OwnLoadSeconds) {
        $seconds = [Math]::Min(120, $OwnLoadSeconds - $elapsed)
        $elapsed += $seconds
        Measure-OwnedBam $Process "$Scenario $elapsed/$OwnLoadSeconds" $seconds
        & $Verify
    }
}

$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    if ($CheckBatchResult) {
        & (Join-Path $PSScriptRoot 'DesktopSmoke.BatchResult.ps1')
        return
    }
    if ($CheckLogReplay) {
        $logs = Join-Path $profile 'logs'
        [void][IO.Directory]::CreateDirectory($logs)
        $stamp = [DateTimeOffset]::UtcNow.ToString('O')
        $rows = for ($index = 1; $index -le 2100; $index++) {
            @{ Time = $stamp; Source = 'BAM'; Severity = 0; Message = "回放夹具 $index"; Detail = '' } | ConvertTo-Json -Compress
        }
        Set-Content -LiteralPath (Join-Path $logs 'bam.log') -Value $rows -Encoding utf8
    }
    if ($Output -eq 'OwnLoad') {
        $fixturePath = Join-Path $profile 'BamResourceFixture.exe'
        $compiler = Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        & $compiler /nologo /target:exe /platform:x64 /reference:System.Windows.Forms.dll "/out:$fixturePath" (Join-Path $root 'poc/ProcessFixture.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Owned resource fixture compilation failed.' }
        @{
            SchemaVersion = 1
            Programs = @(@{
                Id = [guid]::NewGuid().ToString()
                Name = '资源采样夹具'
                Kind = 0
                Entry = @{ OriginalPath = $fixturePath; TargetPath = $fixturePath; WorkingDirectory = $profile; Arguments = 'resource-load' }
                InstallDirectory = $profile
                WorkerPaths = @($fixturePath)
            })
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $profile 'installations.json') -Encoding utf8
        $fixture = Start-Process -FilePath $fixturePath -ArgumentList 'resource-load' -WorkingDirectory $profile -WindowStyle Hidden -PassThru
        $owned.Add($fixture)
    }
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
        $first = Wait-Element $window 'MaaEnd'
        $second = Wait-Element $window 'MAA'
        Wait-For {
            $left = $first.Current.BoundingRectangle
            $right = $second.Current.BoundingRectangle
            $left.Width -gt 0 -and $right.Width -gt 0 -and $right.Left -gt $left.Left -and
                [Math]::Abs($right.Top - $left.Top) -lt 2
        } 'Script cards did not reflow to two columns.'
        if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }

        [BamSmokeWindow]::Resize($handle, $primary.Id, 800, 540)
        $narrowScript = Wait-Element $window 'MaaEnd'
        $narrowChart = Wait-Element $window '资源监控'
        $narrowEvents = Wait-Element $window '最近运行事件'
        $manageScripts = Wait-Element $window '管理脚本'
        Save-Window $primary $handle '03-dashboard-narrow'
        Wait-For {
            $scroll.Current.VerticallyScrollable -and -not $narrowChart.Current.IsOffscreen -and
                -not $narrowEvents.Current.IsOffscreen -and
                $narrowChart.Current.BoundingRectangle.Top -ge $overview.Current.BoundingRectangle.Bottom
        } 'The narrow overview did not keep the chart and recent events below the scrollable area.'
        $chartTop = $narrowChart.Current.BoundingRectangle.Top
        $eventsTop = $narrowEvents.Current.BoundingRectangle.Top
        Wait-For {
            $viewport = $overview.Current.BoundingRectangle
            $script = $narrowScript.Current.BoundingRectangle
            $manage = $manageScripts.Current.BoundingRectangle
            if ($script.Height -gt 0 -and $script.Top -ge $viewport.Top -and $script.Bottom -le $viewport.Bottom -and
                $manage.Height -gt 0 -and $manage.Top -ge $viewport.Top -and $manage.Bottom -le $viewport.Bottom) { return $true }
            $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount, [Windows.Automation.ScrollAmount]::SmallIncrement)
            $false
        } 'The narrow overview did not scroll to the first script and its management entry.'
        if ($narrowChart.Current.BoundingRectangle.Top -ne $chartTop -or $narrowEvents.Current.BoundingRectangle.Top -ne $eventsTop) {
            throw 'Scrolling moved the chart or recent events.'
        }
        Save-Window $primary $handle '09-narrow-cards'
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
    } elseif ($Output -eq 'VerificationEmpty') {
        [void](Wait-Element $window '尚未添加脚本')
        [void](Wait-Element $window '自动检测')
        [void](Wait-Element $window '手动添加')
        [void](Wait-Element $window '暂无资源采样')
        if ($CheckLogReplay) {
            Select-Item $nav '运行日志'
            [void](Wait-Element $window '2000 条记录')
            $retention = Wait-Element $window 'LogRetention' -ById
            if ($retention.Current.Name -notmatch '已略过 101 条') { throw 'Unexpected count after replay and the BAM startup event.' }
            Write-Output 'PASS On-disk replay retained 2,000 and reported 101 older records omitted after the startup event.'
            Select-Item $nav '总览'
        }
        if ($CheckLogStress) {
            Select-Item $nav '运行日志'
            $search = (Wait-Element $window 'LogSearch' -ById).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
            $retention = Wait-Element $window 'LogRetention' -ById
            Wait-For { $retention.Current.Name -match '已略过' } 'The log limit count did not update under load.'
            $probes = [Diagnostics.Stopwatch]::StartNew()
            $slowest = 0
            for ($probe = 0; $probe -lt 6; $probe++) {
                $response = [Diagnostics.Stopwatch]::StartNew()
                $search.SetValue('合成日志偶数')
                [void](Wait-Element $window '1000 条记录')
                $slowest = [Math]::Max($slowest, $response.ElapsedMilliseconds)
                $search.SetValue('没有匹配的合成编号')
                [void](Wait-Element $window '没有符合当前筛选条件的记录')
                $remaining = 10000 * ($probe + 1) - $probes.ElapsedMilliseconds
                if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
            }
            if ($slowest -gt 2500) { throw "Filtering response exceeded 2.5 seconds under load ($slowest ms)." }
            $search.SetValue('')
            [void](Wait-Element $window '2000 条记录')
            Wait-For { $retention.Current.Name -match '已略过 58[^\d]?000 条' } 'Expected 58,000 excluded events after the fixture completed.'
            Write-Output "PASS Live log snapshots at 1,000 records per second for 60 seconds. Filter response max $slowest ms, retained 2,000, excluded 58,000."
            Select-Item $nav '总览'
        }
        $batch = Wait-Element $window 'LaunchPaired' -ById
        if ($batch.Current.IsEnabled) { throw 'Batch launch was enabled with no configured pairs.' }
        if (Find-Element $window 'ShowBatchResult' -ById) { throw 'A batch result was shown before any batch ran.' }
        $chartOption = Wait-Element $window 'ChartMemory' -ById
        if ($chartOption.Current.Name -ne '内存') { throw 'The chart selector did not expose its accessible name.' }
        $selectionPattern = [Windows.Automation.SelectionItemPattern]::Pattern
        $chartSelection = $chartOption.GetCurrentPattern($selectionPattern)
        [void](Wait-Element $window 'CPU · %')
        $chartSelection.Select()
        Wait-For { $chartSelection.Current.IsSelected } 'The memory metric did not become selected.'
        [void](Wait-Element $window '工作集 · MiB')
        Write-Output 'PASS Chart selection and units through UI Automation.'
        if ($CheckMonitorDpi) {
            $monitorCount = [BamSmokeWindow]::MonitorCount()
            $visited = for ($monitor = 0; $monitor -lt $monitorCount; $monitor++) {
                [BamSmokeWindow]::MoveToMonitor($handle, $primary.Id, $monitor)
                [void](Wait-Element $window '尚未添加脚本')
                $chartOption = Wait-Element $window 'ChartMemory' -ById
                $chartSelection = $chartOption.GetCurrentPattern($selectionPattern)
                $unit = Wait-Element $window '工作集 · MiB'
                Wait-For { $chartSelection.Current.IsSelected -and -not $chartOption.Current.IsOffscreen -and
                    $chartOption.Current.BoundingRectangle.Width -gt 0 -and -not $unit.Current.IsOffscreen } 'The selected memory metric or its unit was not visible on the monitor.'
                $windowDpi = [BamSmokeWindow]::GetDpiForWindow($handle)
                if ($windowDpi -eq 0) { throw 'Could not read the owned window DPI.' }
                $windowDpi
            }
            Write-Output "PASS Owned overview and selected metric on $monitorCount monitor(s): $($visited -join ', ') DPI."
            if ($monitorCount -lt 2) { Write-Output 'SKIP Cross-monitor movement: only one monitor is available.' }
            elseif (@($visited | Sort-Object -Unique).Count -lt 2) { Write-Output 'SKIP Mixed-DPI movement: the visited monitors use the same scale.' }
        }
        $keyboardChecked = $false
        if ([BamSmokeWindow]::IsForeground($handle)) {
            $cpuOption = Wait-Element $window 'ChartCpu' -ById
            $cpuSelection = $cpuOption.GetCurrentPattern($selectionPattern)
            $cpuOption.SetFocus()
            Wait-For { $cpuOption.Current.HasKeyboardFocus -or -not [BamSmokeWindow]::IsForeground($handle) } 'The CPU selector did not receive keyboard focus.'
            if ([BamSmokeWindow]::TryPressForegroundKey($handle, $primary.Id, 0x20)) {
                Wait-For { $cpuSelection.Current.IsSelected -or -not [BamSmokeWindow]::IsForeground($handle) } 'Space did not select the CPU metric in the foreground.'
                $keyboardChecked = $cpuSelection.Current.IsSelected
            }
        }
        if ($keyboardChecked) {
            [void](Wait-Element $window 'CPU · %')
            Write-Output 'PASS Chart selection using Space in the foreground.'
        }
        else { Write-Output 'SKIP Space check without foreground keyboard focus.' }
        $menuChecked = $false
        if ([BamSmokeWindow]::IsForeground($handle)) {
            [BamSmokeWindow]::OpenSystemMenu($handle, $primary.Id)
            try {
                Wait-For { [BamSmokeWindow]::IsSystemMenuOpen($handle, $primary.Id) -or
                    -not [BamSmokeWindow]::IsForeground($handle) } 'The owned window System menu did not open.'
                $menuChecked = [BamSmokeWindow]::IsSystemMenuOpen($handle, $primary.Id)
            } finally { [BamSmokeWindow]::DismissSystemMenu($handle, $primary.Id) }
            Wait-For { -not [BamSmokeWindow]::IsSystemMenuOpen($handle, $primary.Id) } 'The owned window System menu did not close.'
        }
        if ($menuChecked) { Write-Output 'PASS Native System menu in the foreground.' }
        else { Write-Output 'SKIP Native System menu check without the owned window in the foreground.' }
        Select-Item $nav '设置'
        $valuePattern = [Windows.Automation.ValuePattern]::Pattern
        $poll = (Wait-Element $window 'MaaGetTaskAddress' -ById).GetCurrentPattern($valuePattern)
        $report = (Wait-Element $window 'MaaReportStatusAddress' -ById).GetCurrentPattern($valuePattern)
        if (-not $poll.Current.IsReadOnly -or -not $report.Current.IsReadOnly -or
            $poll.Current.Value -ne 'http://127.0.0.1:18373/getTask' -or $report.Current.Value -ne 'http://127.0.0.1:18373/reportStatus') {
            throw 'Loopback addresses were not available before starting the service.'
        }
        (Wait-Element $window 'MaaPort' -ById).GetCurrentPattern($valuePattern).SetValue('19373')
        Wait-For { $poll.Current.Value -eq 'http://127.0.0.1:19373/getTask' -and
            $report.Current.Value -eq 'http://127.0.0.1:19373/reportStatus' } 'Loopback addresses did not follow the selected port.'
        [void](Wait-Element $window '已停止。')
        Write-Output 'PASS Confirmed empty overview, add actions, and loopback addresses with listening off.'
        if ($CheckMonitorDpi) {
            if ((Toggle-Setting $window 'CloseToTray') -ne 'On') { throw 'Close-to-tray did not enable in the isolated profile.' }
            if (-not $primary.CloseMainWindow()) { throw 'Closing to tray failed.' }
            Wait-For { -not [BamSmokeWindow]::IsWindowVisible($handle) } 'BAM did not hide to tray.'
            Test-Duplicate $primary $handle
            Select-Item $nav '设置'
            if ((Toggle-Setting $window 'CloseToTray') -ne 'Off') { throw 'Close-to-tray did not disable.' }
            Write-Output 'PASS Own tray hide and activation on the visited monitor.'
        }
        if ($RequireForegroundChecks -and (-not $keyboardChecked -or -not $menuChecked)) {
            throw 'Required foreground checks were skipped because the owned BAM window did not retain the foreground.'
        }
    } elseif ($Output -eq 'OwnLoad') {
        [void](Wait-Element $window '资源采样夹具')
        $checkSamples = {
            Wait-For {
                # Navigation can rebuild the page and its automation peers.
                $plot = Find-Element $window '最近 120 秒资源曲线'
                $plot -and $plot.Current.HelpText -like '*CPU 1/1 · 工作集 1/1' -and
                    -not (Find-Element $window '暂无资源采样')
            } 'The CPU and memory samples did not become visible.'
        }
        & $checkSamples
        $memory = Wait-Element $window 'ChartMemory' -ById
        $memory.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        [void](Wait-Element $window '工作集 · MiB')
        & $checkSamples
        $cpu = Wait-Element $window 'ChartCpu' -ById
        $cpu.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if ($CheckChartAccessibility) {
            Test-ChartAccessibility $window $handle $primary.Id
            $cpu.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        if ($OwnLoadPhase -eq 'Both') {
            $scheduler = [Diagnostics.Stopwatch]::StartNew()
            $before = $fixture.TotalProcessorTime
            Measure-OwnLoad $primary 'foreground' $checkSamples
            $fixture.Refresh()
            $loadCpu = 100 * ($fixture.TotalProcessorTime - $before).TotalSeconds / $scheduler.Elapsed.TotalSeconds / [Environment]::ProcessorCount
            $cpuSample = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^脚本 CPU，(\d+(?:[.,]\d+)?%)$' } |
                ForEach-Object { $_.Split('，')[1] } | Select-Object -First 1
            if ($loadCpu -le 0 -or -not $cpuSample -or [math]::Abs([double]::Parse($cpuSample.TrimEnd('%')) - $loadCpu) -gt (30 / [Environment]::ProcessorCount)) {
                throw "CPU measurement outside fixture tolerance. BAM=$cpuSample reference=$loadCpu"
            }
            Write-Output ('PASS Owned fixture CPU {0}; measured reference {1:N2}%.' -f $cpuSample, $loadCpu)
        }

        Select-Item $nav '设置'
        if ((Toggle-Setting $window 'CloseToTray') -ne 'On') { throw 'Close-to-tray did not toggle.' }
        Select-Item $nav '总览'
        if (-not $primary.CloseMainWindow()) { throw 'Closing to tray failed.' }
        Wait-For { -not [BamSmokeWindow]::IsWindowVisible($handle) } 'BAM did not hide to tray.'
        Measure-OwnLoad $primary 'tray' {
            if ($fixture.HasExited -or [BamSmokeWindow]::IsWindowVisible($handle)) { throw 'Fixture or tray state changed.' }
        }
        Test-Duplicate $primary $handle
        & $checkSamples
        $latestSample = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ -match '^最近有效采样 \d{2}:\d{2}:\d{2}$' } | Select-Object -First 1
        if (-not $latestSample) { throw 'No valid sample time after restoring the overview.' }
        $sampleTime = [DateTime]::ParseExact($latestSample.Substring(7), 'HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
        $now = [DateTime]::Now
        if ($sampleTime -gt $now) { $sampleTime = $sampleTime.AddDays(-1) }
        if (($now - $sampleTime).TotalSeconds -gt 15) { throw 'The last valid sample after restoring the overview is stale.' }
        Select-Item $nav '设置'
        if ((Toggle-Setting $window 'CloseToTray') -ne 'Off') { throw 'Close-to-tray did not disable.' }
        Write-Output 'PASS Owned tray sampling and fresh CPU and memory after restore.'
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
