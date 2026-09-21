[CmdletBinding()]
param(
    [switch]$SkipDependencyInstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot = Join-Path $repositoryRoot 'native\StarSimCore.Native'
$toolsRoot = Join-Path $repositoryRoot '.tools'
$vcpkgRoot = Join-Path $toolsRoot 'vcpkg'
$vcpkgCommit = '319504a5326aa870edde46438c5455fa76305a56'

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

    throw 'CMake was not found. Install the Visual Studio CMake tools for Windows component.'
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw '.NET SDK was not found.'
}

$dotnetVersionText = (& $dotnet.Source --version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $dotnetVersionText) {
    throw '.NET SDK discovery failed. Finish the Visual Studio installation or install the .NET 10 SDK.'
}
$dotnetVersion = [Version]($dotnetVersionText -replace '-.*$', '')
if ($dotnetVersion.Major -lt 10) {
    throw ".NET SDK 10 or newer is required; found $dotnetVersionText."
}

$git = Get-Command git.exe -ErrorAction SilentlyContinue
if ($null -eq $git) {
    throw 'Git was not found.'
}

$cmake = Resolve-CMake
$cmakeVersion = (& $cmake --version | Select-Object -First 1).Trim()

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer discovery tool (vswhere) was not found.'
}

$visualStudioRoot = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $visualStudioRoot) {
    throw 'Visual Studio with the MSVC x64/x86 tools was not detected.'
}

$compiler = Get-ChildItem -Path (Join-Path $visualStudioRoot 'VC\Tools\MSVC\*\bin\Hostx64\x64\cl.exe') `
    -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $compiler) {
    throw 'The MSVC x64 compiler was not found in the Visual Studio installation.'
}

$windowsSdkHeader = Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Include\*\um\Windows.h') `
    -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1
if ($null -eq $windowsSdkHeader) {
    throw 'A Windows 10/11 SDK was not detected.'
}

Write-Host ".NET SDK: $dotnetVersionText"
Write-Host "CMake: $cmakeVersion"
Write-Host "Git: $((& $git.Source --version).Trim())"
Write-Host "MSVC: $($compiler.FullName)"
Write-Host "Windows SDK: $($windowsSdkHeader.Directory.Parent.Name)"

New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $vcpkgRoot '.git'))) {
    New-Item -ItemType Directory -Force -Path $vcpkgRoot | Out-Null
    & $git.Source -C $vcpkgRoot init
    & $git.Source -C $vcpkgRoot remote add origin https://github.com/microsoft/vcpkg.git
}

& $git.Source -C $vcpkgRoot cat-file -e "$vcpkgCommit^{commit}" 2>$null
$commitExists = $LASTEXITCODE -eq 0
if (-not $commitExists) {
    & $git.Source -C $vcpkgRoot fetch --depth 1 origin $vcpkgCommit
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to fetch the pinned vcpkg commit.'
    }
}

& $git.Source -C $vcpkgRoot checkout --detach $vcpkgCommit
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to check out the pinned vcpkg commit.'
}

$vcpkgExecutable = Join-Path $vcpkgRoot 'vcpkg.exe'
if (-not (Test-Path -LiteralPath $vcpkgExecutable)) {
    & (Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat') -disableMetrics
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to bootstrap vcpkg.'
    }
}

if (-not $SkipDependencyInstall) {
    & $vcpkgExecutable install "--x-manifest-root=$nativeRoot" --triplet x64-windows --disable-metrics
    if ($LASTEXITCODE -ne 0) {
        throw 'vcpkg dependency restore failed.'
    }
}

& $dotnet.Source restore (Join-Path $repositoryRoot 'StarSimCore.sln')
if ($LASTEXITCODE -ne 0) {
    throw '.NET dependency restore failed.'
}

Write-Host 'Bootstrap completed successfully.'
