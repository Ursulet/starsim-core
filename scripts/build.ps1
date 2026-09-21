[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repositoryRoot 'native\StarSimCore.Native'
$solutionPath = Join-Path $repositoryRoot 'StarSimCore.sln'
$vcpkgToolchain = Join-Path $repositoryRoot '.tools\vcpkg\scripts\buildsystems\vcpkg.cmake'

function Resolve-CMake {
    $command = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installationPath = & $vswhere -latest -products * -property installationPath
        if ($installationPath) {
            $candidate = Join-Path $installationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    throw 'CMake was not found. Run scripts/bootstrap.ps1 after installing the required Visual Studio components.'
}

if (-not (Test-Path -LiteralPath $vcpkgToolchain)) {
    throw 'The pinned local vcpkg toolchain is missing. Run scripts/bootstrap.ps1 first.'
}

$cmake = Resolve-CMake
$presetSuffix = $Configuration.ToLowerInvariant()
$configurePreset = "windows-x64-$presetSuffix"
$buildPreset = "$configurePreset-build"

Push-Location $nativeRoot
try {
    & $cmake --preset $configurePreset
    if ($LASTEXITCODE -ne 0) {
        throw "Native configure failed for $Configuration."
    }

    & $cmake --build --preset $buildPreset
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed for $Configuration."
    }
}
finally {
    Pop-Location
}

& dotnet build $solutionPath --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Managed build failed for $Configuration."
}

$nativeBuildRoot = Join-Path $nativeRoot "out\build\$configurePreset"
$nativeDll = Get-ChildItem -LiteralPath $nativeBuildRoot -Filter 'StarSimCore.Native.dll' -Recurse |
    Select-Object -First 1
if ($null -eq $nativeDll) {
    throw "StarSimCore.Native.dll was not found below $nativeBuildRoot."
}

$stageRoot = Join-Path $repositoryRoot "artifacts\native\win-x64\$Configuration"
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
Copy-Item -LiteralPath $nativeDll.FullName -Destination $stageRoot -Force

$vcpkgBin = if ($Configuration -eq 'Debug') {
    Join-Path $nativeRoot 'vcpkg_installed\x64-windows\debug\bin'
}
else {
    Join-Path $nativeRoot 'vcpkg_installed\x64-windows\bin'
}
$runtimeDlls = @($nativeDll)
if (Test-Path -LiteralPath $vcpkgBin) {
    $runtimeDlls += Get-ChildItem -LiteralPath $vcpkgBin -Filter '*.dll'
}
$runtimeDlls | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $stageRoot -Force
}

$runtimeDestinations = @(
    (Join-Path $repositoryRoot "src\StarSimCore.App\bin\$Configuration\net10.0")
)

foreach ($destination in $runtimeDestinations) {
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $runtimeDlls | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
}

$pluginDlls = Get-ChildItem -LiteralPath $nativeBuildRoot -Filter '*plugin*.dll' -Recurse
if ($pluginDlls) {
    foreach ($destination in $runtimeDestinations) {
        $pluginDest = Join-Path $destination 'plugins'
        New-Item -ItemType Directory -Force -Path $pluginDest | Out-Null
        $pluginDlls | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $pluginDest -Force
        }
    }
}

$appOutput = Join-Path $repositoryRoot "src\StarSimCore.App\bin\$Configuration\net10.0"
$sdkDestination = Join-Path $appOutput 'sdk'
$sdkIncludeDestination = Join-Path $sdkDestination 'include'
New-Item -ItemType Directory -Force -Path $sdkIncludeDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'plugins\sdk\CMakeLists.txt') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'plugins\sdk\README.md') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\PLUGIN_SDK.md') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\StarSimCore.SamplePlugin\sample_plugin.cpp') -Destination $sdkDestination -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'native\StarSimCore.Native\include\starsim_core_plugin.h') -Destination $sdkIncludeDestination -Force

Write-Host "Build completed: $Configuration x64."
