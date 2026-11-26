[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src/BAM.Desktop/BAM.Desktop.csproj'
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+-alpha\.\d+$') { throw 'This packaging entry point expects an Alpha version.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -cne "v$version") { throw 'Tag and package version differ.' }

$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Select a new packaging output directory.' }
$build = Join-Path $output 'build'
$package = Join-Path $output 'BAM'
dotnet restore $project --locked-mode -p:SelfContained=true -p:UseArtifactsOutput=true -p:ArtifactsPath=$build
dotnet publish $project -c Release --self-contained true --no-restore --artifacts-path $build -o $package

$runtime = (Get-Content -LiteralPath (Join-Path $package 'BAM.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
$frameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App')
if ($runtime.framework -or $runtime.frameworks -or $runtime.includedFrameworks.Count -ne $frameworks.Count -or
    @(Compare-Object $frameworks @($runtime.includedFrameworks.name)).Count -ne 0) { throw 'Self-contained runtime verification failed.' }
foreach ($required in 'BAM.exe', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'Microsoft.AspNetCore.Server.Kestrel.Core.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) { throw "Missing $required" }
}
$disallowed = Get-ChildItem -LiteralPath $package -File -Recurse -Force | Where-Object {
    $_.Name -in 'settings.json', 'installations.json', 'UIsettings.json', 'scripts_settings.json', 'web.config' -or
    $_.Extension -in '.pdb', '.log', '.lnk', '.pfx', '.key', '.pem', '.trx'
}
if ($disallowed) { throw 'The package contains unexpected private or build files.' }

$packages = $env:NUGET_PACKAGES
if (-not $packages) { $packages = ((dotnet nuget locals global-packages --list) -split ':', 2)[1].Trim() }
$notices = New-Item -Path (Join-Path $package 'THIRD-PARTY') -ItemType Directory
Copy-Item -LiteralPath (Join-Path $root 'docs/releases/THIRD-PARTY-MIT.txt') -Destination $notices.FullName
Copy-Item -LiteralPath (Join-Path $root "docs/releases/v$version.md") -Destination (Join-Path $package '使用说明.md')
foreach ($framework in $runtime.includedFrameworks) {
    $path = Join-Path $packages ("{0}.runtime.win-x64/{1}" -f $framework.name.ToLowerInvariant(), $framework.version)
    $target = New-Item -Path (Join-Path $notices.FullName $framework.name) -ItemType Directory
    $files = @(Get-ChildItem -LiteralPath $path -File | Where-Object Name -Match '^(LICENSE|THIRD[-_]PARTY[-_]NOTICES)(\.|$)')
    if (-not ($files | Where-Object Name -Match '^LICENSE')) { throw "Missing license for $($framework.name)" }
    $files | Copy-Item -Destination $target.FullName
}
$lock = Get-Content -LiteralPath (Join-Path $root 'src/BAM.Desktop/packages.lock.json') -Raw | ConvertFrom-Json
$dependencies = $lock.dependencies.'net10.0-windows7.0'
foreach ($name in 'WPF-UI', 'WPF-UI.Abstractions', 'Microsoft.Extensions.DependencyInjection', 'Microsoft.Extensions.DependencyInjection.Abstractions') {
    $path = Join-Path $packages ("{0}/{1}" -f $name.ToLowerInvariant(), $dependencies.$name.resolved)
    $target = New-Item -Path (Join-Path $notices.FullName $name) -ItemType Directory
    $files = @(Get-ChildItem -LiteralPath $path -File | Where-Object Name -Match '^(LICENSE|THIRD[-_]PARTY[-_]NOTICES)(\.|$)')
    if ($files.Count -eq 0) { throw "Missing notices for $name" }
    $files | Copy-Item -Destination $target.FullName
}

$zipName = "BAM-$version-win-x64.zip"
$zip = Join-Path $output $zipName
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $zipName" -Encoding ascii
Write-Output "$zipName $hash"
