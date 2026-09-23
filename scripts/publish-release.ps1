[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$NumericVersion = '1.0.0.0',

    [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repositoryRoot 'src\StarSimCore.App\StarSimCore.App.csproj'
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish\win-x64\StarSimCore'
$packageRoot = Join-Path $repositoryRoot 'artifacts\package'
$archivePath = Join-Path $packageRoot "StarSimCore-$Version-win-x64-portable.zip"
$nativeStageRoot = Join-Path $repositoryRoot 'artifacts\native\win-x64\Release'
$nativeBuildRoot = Join-Path $repositoryRoot 'native\StarSimCore.Native\out\build\windows-x64-release'
$releaseNotesSource = Join-Path $repositoryRoot "releases\$Version.md"

function Assert-ArtifactPath {
    param([Parameter(Mandatory)][string]$Path)

    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository artifacts directory: $resolvedPath"
    }
}

Assert-ArtifactPath -Path $publishRoot
Assert-ArtifactPath -Path $archivePath

if (-not $SkipNativeBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw 'Release build failed.'
    }
}

if (-not (Test-Path -LiteralPath $nativeStageRoot)) {
    throw "Native Release files are missing at $nativeStageRoot. Run without -SkipNativeBuild."
}
if (-not (Test-Path -LiteralPath $releaseNotesSource)) {
    throw "Release notes are missing: $releaseNotesSource"
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

$publishArguments = @(
    'publish',
    $appProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $publishRoot,
    "-p:Version=$Version",
    "-p:AssemblyVersion=$NumericVersion",
    "-p:FileVersion=$NumericVersion",
    "-p:InformationalVersion=$Version",
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw 'The self-contained win-x64 publish failed.'
}

Get-ChildItem -LiteralPath $nativeStageRoot -Filter '*.dll' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $publishRoot -Force
}

$pluginDestination = Join-Path $publishRoot 'plugins'
New-Item -ItemType Directory -Force -Path $pluginDestination | Out-Null
$samplePlugin = Get-ChildItem -LiteralPath $nativeBuildRoot -Filter 'starsim_diagnostic_plugin.dll' -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\debug\\' } |
    Select-Object -First 1
if ($null -eq $samplePlugin) {
    throw "The demonstration plugin was not found below $nativeBuildRoot."
}
Copy-Item -LiteralPath $samplePlugin.FullName -Destination $pluginDestination -Force

$sdkDestination = Join-Path $publishRoot 'sdk'
$sdkIncludeDestination = Join-Path $sdkDestination 'include'
New-Item -ItemType Directory -Force -Path $sdkIncludeDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'plugins\sdk\CMakeLists.txt') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'plugins\sdk\README.md') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\PLUGIN_SDK.md') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\StarSimCore.SamplePlugin\sample_plugin.cpp') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\StarSimCore.Native\include\starsim_core_plugin.h') -Destination $sdkIncludeDestination -Force

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'RELEASE_README.md') -Destination (Join-Path $publishRoot 'README.md') -Force
Copy-Item -LiteralPath $releaseNotesSource -Destination (Join-Path $publishRoot 'RELEASE_NOTES.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Destination $publishRoot -Force
$legalFiles = @('LICENSE', 'NOTICE', 'SECURITY.md', 'THIRD_PARTY_NOTICES.md')
foreach ($legalFile in $legalFiles) {
    $source = Join-Path $repositoryRoot $legalFile
    Copy-Item -LiteralPath $source -Destination $publishRoot -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'NOTICE') -Destination $sdkDestination -Force

$documentationDestination = Join-Path $publishRoot 'docs'
New-Item -ItemType Directory -Force -Path $documentationDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\MODULE_GUIDE_RO.md') -Destination $documentationDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\MODULE_GUIDE_EN.md') -Destination $documentationDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\PROCESSING_ENGINE_MATH.md') -Destination $documentationDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\PRESET_ENGINE_V1.md') -Destination $documentationDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\BATCH_PROCESSING.md') -Destination $documentationDestination -Force

$licensesDestination = Join-Path $publishRoot 'licenses'
New-Item -ItemType Directory -Force -Path $licensesDestination | Out-Null

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
if (Test-Path -LiteralPath $dotnetLicense) {
    Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $licensesDestination 'dotnet-LICENSE.txt') -Force
}
if (Test-Path -LiteralPath $dotnetNotices) {
    Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $licensesDestination 'dotnet-ThirdPartyNotices.txt') -Force
}

$vcpkgShareRoot = Join-Path $repositoryRoot 'native\StarSimCore.Native\vcpkg_installed\x64-windows\share'
$nativeLicensePorts = @('libpng', 'tiff', 'zlib', 'libjpeg-turbo', 'liblzma', 'libspng')
foreach ($port in $nativeLicensePorts) {
    $copyrightFile = Join-Path $vcpkgShareRoot "$port\copyright"
    if (Test-Path -LiteralPath $copyrightFile) {
        Copy-Item -LiteralPath $copyrightFile -Destination (Join-Path $licensesDestination "$port-COPYRIGHT.txt") -Force
    }
}

$nugetLicensePackages = @('communitytoolkit.mvvm', 'skiasharp', 'harfbuzzsharp')
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
foreach ($packageName in $nugetLicensePackages) {
    $nugetPackageRoot = Join-Path $nugetRoot $packageName
    if (-not (Test-Path -LiteralPath $nugetPackageRoot)) {
        continue
    }

    $packageVersionRoot = Get-ChildItem -LiteralPath $nugetPackageRoot -Directory |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $packageVersionRoot) {
        continue
    }

    Get-ChildItem -LiteralPath $packageVersionRoot.FullName -File |
        Where-Object { $_.Name -match '^(LICENSE|License|NOTICE|ThirdPartyNotices)' } |
        ForEach-Object {
            $destinationName = "$packageName-$($_.Name)"
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licensesDestination $destinationName) -Force
        }
}

$requiredPaths = @(
    'StarSimCore.App.exe',
    'StarSimCore.Native.dll',
    'libpng16.dll',
    'tiff.dll',
    'z.dll',
    'README.md',
    'RELEASE_NOTES.md',
    'CHANGELOG.md',
    'LICENSE',
    'NOTICE',
    'SECURITY.md',
    'THIRD_PARTY_NOTICES.md',
    'plugins\starsim_diagnostic_plugin.dll',
    'presets\builtin',
    'docs\MODULE_GUIDE_RO.md',
    'docs\MODULE_GUIDE_EN.md',
    'docs\PROCESSING_ENGINE_MATH.md',
    'docs\PRESET_ENGINE_V1.md',
    'docs\BATCH_PROCESSING.md',
    'sdk\PLUGIN_SDK.md',
    'sdk\include\starsim_core_plugin.h'
)
foreach ($requiredPath in $requiredPaths) {
    $absolutePath = Join-Path $publishRoot $requiredPath
    if (-not (Test-Path -LiteralPath $absolutePath)) {
        throw "Release staging is incomplete. Missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host ''
Write-Host 'Self-contained release prepared successfully.' -ForegroundColor Green
Write-Host "Application folder: $publishRoot"
Write-Host "Portable ZIP:       $archivePath"
