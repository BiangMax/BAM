$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$root = Join-Path (Split-Path $PSScriptRoot) ('.local\fixture-' + [guid]::NewGuid().ToString('N'))
$other = Join-Path $root 'other'
[void][IO.Directory]::CreateDirectory($other)
$exe = Join-Path $root 'BamPhase0Fixture.exe'
& $compiler /nologo /target:exe /reference:System.Windows.Forms.dll "/out:$exe" (Join-Path $PSScriptRoot 'ProcessFixture.cs')
if ($LASTEXITCODE) { throw 'Fixture compilation failed' }
$copy = Join-Path $other 'BamPhase0Fixture.exe'
Copy-Item -LiteralPath $exe -Destination $copy
& $exe $copy
if ($LASTEXITCODE) { throw 'Fixture assertions failed' }
$info = [Diagnostics.ProcessStartInfo]::new($exe, 'window-test')
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$windowTest = [Diagnostics.Process]::Start($info)
try {
    $output = $windowTest.StandardOutput.ReadToEndAsync()
    if (-not $windowTest.WaitForExit(5000)) {
        $windowTest.Kill()
        $windowTest.WaitForExit()
        Write-Output $output.GetAwaiter().GetResult()
        throw 'Private desktop fixture exceeded the exit timeout'
    }
    Write-Output $output.GetAwaiter().GetResult()
    if ($windowTest.ExitCode) { throw 'Private desktop fixture assertions failed' }
} finally { $windowTest.Dispose() }
