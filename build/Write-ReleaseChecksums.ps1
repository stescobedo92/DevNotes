#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $Directory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Tag "v$Version"
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$expected = @(
    "DevNotes-$($release.Tag)-linux-x64.tar.gz"
    "DevNotes-$($release.Tag)-osx-arm64.tar.gz"
    "DevNotes-$($release.Tag)-osx-x64.tar.gz"
    "DevNotes-$($release.Tag)-win-x64.zip"
)
$files = @(Get-ChildItem -LiteralPath $directoryPath -Force)
if ($files.Count -ne $expected.Count) {
    throw 'The release must contain exactly one package for each of the four supported runtimes.'
}

$lines = foreach ($name in $expected) {
    $path = Join-Path $directoryPath $name
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or $file.LinkType -or $file.Length -eq 0) {
        throw "Expected a nonempty regular release archive: $name"
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[System.IO.File]::WriteAllText((Join-Path $directoryPath 'SHA256SUMS'), ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host 'Created SHA256SUMS for all four release packages.'
