[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.9.0-beta.1',

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$NumericVersion = '0.9.0.0',

    [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'publish-release.ps1') `
    -Version $Version `
    -NumericVersion $NumericVersion `
    -SkipNativeBuild:$SkipNativeBuild

if ($LASTEXITCODE -ne 0) {
    throw 'Release packaging failed.'
}
