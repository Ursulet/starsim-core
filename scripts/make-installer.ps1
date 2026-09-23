[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '1.0.0',

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$NumericVersion = '1.0.0.0',

    [switch]$SkipNativeBuild,
    [switch]$PrepareOnly,
    [switch]$RefreshVCRedist
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $repositoryRoot 'installer\StarSimCore.iss'
$publishRoot = Join-Path $repositoryRoot 'artifacts\publish\win-x64\StarSimCore'
$installerOutput = Join-Path $repositoryRoot 'artifacts\installer'
$prerequisiteRoot = Join-Path $installerOutput 'prerequisites'
$vcRedistPath = Join-Path $prerequisiteRoot 'vc_redist.x64.exe'
$portableArchive = Join-Path $repositoryRoot "artifacts\package\StarSimCore-$Version-win-x64-portable.zip"
$setupPath = Join-Path $installerOutput "StarSimCore-Setup-$Version-win-x64.exe"
$releaseNotesSource = Join-Path $repositoryRoot "releases\$Version.md"
$releaseReadmePath = Join-Path $installerOutput "StarSimCore-Setup-$Version-win-x64-README.md"

& (Join-Path $PSScriptRoot 'publish-release.ps1') `
    -Version $Version `
    -NumericVersion $NumericVersion `
    -SkipNativeBuild:$SkipNativeBuild
if ($LASTEXITCODE -ne 0) {
    throw 'Release preparation failed.'
}

New-Item -ItemType Directory -Force -Path $prerequisiteRoot | Out-Null
if ($RefreshVCRedist -or -not (Test-Path -LiteralPath $vcRedistPath)) {
    Write-Host 'Downloading the official Microsoft Visual C++ x64 runtime...'
    Invoke-WebRequest -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $vcRedistPath
}

if ((Get-Item -LiteralPath $vcRedistPath).Length -lt 1MB) {
    throw "The downloaded Visual C++ runtime is unexpectedly small: $vcRedistPath"
}

$vcSignature = Get-AuthenticodeSignature -LiteralPath $vcRedistPath
if ($vcSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $null -eq $vcSignature.SignerCertificate -or
    $vcSignature.SignerCertificate.Subject -notmatch 'Microsoft') {
    throw 'The Visual C++ runtime does not have a valid Microsoft Authenticode signature. It will not be packaged.'
}

if (-not (Test-Path -LiteralPath $releaseNotesSource)) {
    throw "Release notes are missing: $releaseNotesSource"
}
Copy-Item -LiteralPath $releaseNotesSource -Destination $releaseReadmePath -Force

if ($PrepareOnly) {
    Write-Host ''
    Write-Host 'Files are ready for Inno Setup.' -ForegroundColor Green
    Write-Host "Open: $installerScript"
    Write-Host 'Then choose Build -> Compile (or press Ctrl+F9).'
    exit 0
}

$innoCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)
$innoCompiler = $innoCandidates |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $innoCompiler) {
    throw 'Inno Setup 6 was not found. Open installer\StarSimCore.iss manually or install Inno Setup 6.'
}

New-Item -ItemType Directory -Force -Path $installerOutput | Out-Null

$innoArguments = @(
    "/DMyAppVersion=$Version",
    "/DMyAppNumericVersion=$NumericVersion",
    "/DMyPayloadDir=$publishRoot",
    "/DMyInstallerOutputDir=$installerOutput",
    "/DMyVCRedistFile=$vcRedistPath",
    $installerScript
)

& $innoCompiler @innoArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Inno Setup compilation failed.'
}
if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Inno Setup completed but the expected installer was not found: $setupPath"
}

$releaseFiles = @($setupPath, $portableArchive)
$checksumLines = foreach ($releaseFile in $releaseFiles) {
    if (-not (Test-Path -LiteralPath $releaseFile)) {
        throw "Release output is missing: $releaseFile"
    }
    $hash = Get-FileHash -LiteralPath $releaseFile -Algorithm SHA256
    '{0}  {1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $releaseFile)
}
$checksumPath = Join-Path $installerOutput 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines($checksumPath, $checksumLines, [System.Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host 'StarSim Core installer created successfully.' -ForegroundColor Green
Write-Host "Installer: $setupPath"
Write-Host "Portable:  $portableArchive"
Write-Host "Readme:    $releaseReadmePath"
Write-Host "Checksums: $checksumPath"
