# Invoked by Test-Desktop.ps1 with an isolated profile and discovery disabled.
$fixture = Join-Path $profile 'BatchFixture.exe'
$compiler = Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll "/out:$fixture" (Join-Path $root 'poc/ProcessFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Batch fixture compilation failed.' }

$gates = @{}
try {
    $programs = foreach ($index in 1..4) {
        $dir = Join-Path $profile "group-$index"
        $workerDir = [IO.Directory]::CreateDirectory((Join-Path $dir 'dist/BBchannel64'))
        $worker = Join-Path $workerDir.FullName 'BBchannel.exe'
        Copy-Item -LiteralPath $fixture -Destination $worker
        $entry = Join-Path $dir '启动.cmd'
        $gateName = 'BamBatchSmoke' + [guid]::NewGuid().ToString('N')
        $gates[$index] = [Threading.EventWaitHandle]::new($false, 'ManualReset', $gateName)
        @('@echo off', "..\BatchFixture.exe wait-for-worker $gateName", 'if errorlevel 1 exit /b 1',
            'dist\BBchannel64\BBchannel.exe worker') | Set-Content -LiteralPath $entry -Encoding ascii
        $launcher = $fixture
        if ($index -le 2) {
            $launcher = Join-Path $dir 'OwnLauncher.exe'
            Copy-Item -LiteralPath $fixture -Destination $launcher
        }
        @{
            Id = [guid]::NewGuid().ToString()
            Name = "夹具 $index"
            Kind = 3
            InstallDirectory = $dir
            WorkerPaths = @($worker)
            Entry = @{ OriginalPath = $entry; TargetPath = $entry; WorkingDirectory = $dir; Arguments = '' }
            Emulator = @{ WaitSeconds = 0; Entry = @{
                OriginalPath = $launcher; TargetPath = $launcher; WorkingDirectory = $dir; Arguments = 'unexpected'
            } }
        }
    }
    @{ SchemaVersion = 1; Programs = $programs } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $profile 'installations.json') -Encoding utf8

    $primary = Start-Bam
    Wait-Window $primary
    $handle = $primary.MainWindowHandle
    $window = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $invoke = [Windows.Automation.InvokePattern]::Pattern
    function Get-Dialog([string] $title) {
        if ($primary.HasExited) { throw "BAM exited ($($primary.ExitCode)). $($primary.StandardError.ReadToEnd())" }
        $popup = [BamSmokeWindow]::FindDialog($handle, $primary.Id, $title)
        if ($popup -ne [IntPtr]::Zero) {
            return [Windows.Automation.AutomationElement]::FromHandle($popup)
        }
    }
    $batch = Wait-Element $window 'LaunchPaired' -ById
    Wait-For { $batch.Current.IsEnabled } 'Owned groups did not become eligible.'
    $batch.GetCurrentPattern($invoke).Invoke()
    Wait-For { Get-Dialog '批量启动' } 'Batch confirmation did not open.'
    $confirm = Get-Dialog '批量启动'
    $affirmative = $confirm.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '批量启动'),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty, $true)))
    $affirmative.GetCurrentPattern($invoke).Invoke()
    [void](Wait-Element $window '批量启动 · 已提交 2/4 组')
    [BamSmokeWindow]::Resize($handle, $primary.Id, 800, 540)
    $open = Wait-Element $window 'ShowBatchResult' -ById
    function Open-Report {
        Wait-For { $open.Current.IsEnabled -and -not $open.Current.IsOffscreen } 'Result command is unavailable.'
        if (-not $open.Current.HasKeyboardFocus) { $open.SetFocus() }
        $open.GetCurrentPattern($invoke).Invoke()
        Wait-For { Get-Dialog '上次批量启动结果' } 'Result dialog did not open.'
        Get-Dialog '上次批量启动结果'
    }
    function Close-Report($dialog) {
        [BamSmokeWindow]::PressKeyInOwnedWindow([IntPtr]$dialog.Current.NativeWindowHandle, $primary.Id, 0x1B)
        Wait-For { $open.Current.HasKeyboardFocus } 'Escape did not return focus to the result button.'
    }
    $report = Open-Report
    $close = Wait-Element $report 'CloseReport' -ById
    Wait-For { $close.Current.HasKeyboardFocus } 'The close button did not receive initial focus.'
    foreach ($index in 1..2) { [void](Wait-Element $report "夹具 $index · 已提交待确认") }
    foreach ($index in 3..4) { [void](Wait-Element $report "夹具 $index · 明确失败") }
    $conflict = '多组使用了相同的启动目标和参数，请为每组选择自己的模拟器入口。'
    [void](Wait-Element $report $conflict)
    [void]$gates[1].Set()
    [void](Wait-Element $report '夹具 1 · 已确认运行')
    [void](Wait-Element $report '夹具 2 · 已提交待确认')
    Close-Report $report

    [void]$gates[2].Set()
    $log = Join-Path $profile 'logs/bam.log'
    Wait-For { Select-String -LiteralPath $log -SimpleMatch $programs[1].Id |
        Where-Object { ($_.Line | ConvertFrom-Json).Message -eq '已确认软件运行' } } 'Second worker was not observed while the report was closed.'
    $report = Open-Report
    [BamSmokeWindow]::Resize([IntPtr]$report.Current.NativeWindowHandle, $primary.Id, 320, 260)
    foreach ($index in 1..2) { [void](Wait-Element $report "夹具 $index · 已确认运行") }
    $scroll = (Wait-Element $report '各组启动结果').GetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern)
    Wait-For { $scroll.Current.VerticallyScrollable } 'Narrow report could not scroll.'
    $scroll.SetScrollPercent(-1, 100)
    Wait-For { $scroll.Current.VerticalScrollPercent -gt 95 } 'Last result could not be reached.'
    $last = Wait-Element $report '夹具 4 · 明确失败'
    if ($last.Current.IsOffscreen) { throw 'Last result is clipped after scrolling.' }
    Close-Report $report
    Close-Bam $primary
    $fixtureImages = @($fixture) + $programs.WorkerPaths + $programs.Emulator.Entry.TargetPath
    foreach ($process in Get-Process -Name 'BBchannel', 'OwnLauncher', 'BatchFixture' -ErrorAction SilentlyContinue) {
        try {
            if ($process.Path -in $fixtureImages -and -not $process.WaitForExit(20000)) {
                throw "Owned batch fixture did not exit: $($process.Id)"
            }
        } finally { $process.Dispose() }
    }
    Write-Output 'PASS 2/4 submitted, two conflicts, live and closed-window confirmations, Escape and focus return, reopen and scroll at 320x260 DIP.'
} finally {
    foreach ($gate in $gates.Values) { $gate.Dispose() }
}
