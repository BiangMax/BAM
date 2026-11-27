[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src/BAM.Desktop/BAM.Desktop.csproj'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-alpha\.\d+)?$') { throw 'Expected a stable or Alpha version.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -cne "v$version") { throw 'Tag and package version differ.' }
$stable = $version -notmatch '-'
$license = Join-Path $root 'LICENSE'
if ($stable -and -not (Test-Path -LiteralPath $license -PathType Leaf)) { throw 'A stable package requires the BAM license.' }

$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new packaging output directory.' }
$build = Join-Path $output 'build'
$package = Join-Path $output 'BAM'
$bundle = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
    '-p:EnableSingleFileAnalyzer=false') # Keep the existing locked dependency graph. Verify the bundled app with a desktop smoke test.
dotnet restore $project --locked-mode -p:SelfContained=true -p:UseArtifactsOutput=true -p:ArtifactsPath=$build @bundle
dotnet publish $project -c Release --self-contained true --no-restore --artifacts-path $build -o $package @bundle

$runtimeConfig = Join-Path $build 'bin/BAM.Desktop/release_win-x64/BAM.runtimeconfig.json'
$runtime = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions
$frameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')
if ($runtime.framework -or $runtime.frameworks -or $runtime.includedFrameworks.Count -ne $frameworks.Count -or
    @(Compare-Object $frameworks @($runtime.includedFrameworks.name)).Count -ne 0) { throw 'Self-contained runtime verification failed.' }
$payload = @(Get-ChildItem -LiteralPath $package -Force)
if ($payload.Count -ne 1 -or $payload[0].PSIsContainer -or $payload[0].Name -cne 'BAM.exe') {
    throw 'The publish directory must contain only the bundled BAM.exe.'
}

$packages = $env:NUGET_PACKAGES
if (-not $packages) { $packages = ((dotnet nuget locals global-packages --list) -split ':', 2)[1].Trim() }
$notices = New-Item -Path (Join-Path $package 'THIRD-PARTY') -ItemType Directory
$noticePattern = '^(LICENSE|THIRD[-_]?PARTY[-_]?NOTICES)(\.|$)'
if ($stable) { Copy-Item -LiteralPath $license -Destination (Join-Path $package 'LICENSE.txt') }
Copy-Item -LiteralPath (Join-Path $root 'docs/releases/THIRD-PARTY-MIT.txt') -Destination $notices.FullName
$readme = Get-Content -LiteralPath (Join-Path $root "docs/releases/v$version.md") -Raw
Set-Content -LiteralPath (Join-Path $package '使用说明.txt') -Value ($readme -replace '(?m)^#{1,6} ', '' -replace '\x60', '').TrimEnd() -Encoding utf8
foreach ($framework in $runtime.includedFrameworks) {
    $path = Join-Path $packages ("{0}.runtime.win-x64/{1}" -f $framework.name.ToLowerInvariant(), $framework.version)
    $target = New-Item -Path (Join-Path $notices.FullName $framework.name) -ItemType Directory
    $files = @(Get-ChildItem -LiteralPath $path -File | Where-Object Name -Match $noticePattern)
    if (-not ($files | Where-Object Name -Match '^LICENSE')) { throw "Missing license for $($framework.name)" }
    $files | Copy-Item -Destination $target.FullName
}
$lock = Get-Content -LiteralPath (Join-Path $root 'src/BAM.Desktop/packages.lock.json') -Raw | ConvertFrom-Json
$dependencies = $lock.dependencies.'net10.0-windows7.0'
foreach ($name in 'CommunityToolkit.Mvvm', 'WPF-UI', 'WPF-UI.Abstractions',
    'Microsoft.Extensions.DependencyInjection', 'Microsoft.Extensions.DependencyInjection.Abstractions') {
    $path = Join-Path $packages ("{0}/{1}" -f $name.ToLowerInvariant(), $dependencies.$name.resolved)
    $target = New-Item -Path (Join-Path $notices.FullName $name) -ItemType Directory
    $files = @(Get-ChildItem -LiteralPath $path -File | Where-Object Name -Match $noticePattern)
    if ($files.Count -eq 0) { throw "Missing notices for $name" }
    $files | Copy-Item -Destination $target.FullName
}

$zipName = "BAM-$version-win-x64.zip"
$zip = Join-Path $output $zipName
$rootEntries = @((Get-ChildItem -LiteralPath $package -Force).Name)
$expectedEntries = @('BAM.exe', '使用说明.txt', 'THIRD-PARTY')
if ($stable) { $expectedEntries += 'LICENSE.txt' }
if (@(Compare-Object $expectedEntries $rootEntries -CaseSensitive).Count -ne 0) {
    throw 'Unexpected top-level package layout.'
}
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $zipName" -Encoding ascii
Write-Output "$zipName $hash"
