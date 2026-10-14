#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1'

$valid = @(
    @{ Tag = 'v0.1.0'; Core = '0.1.0'; Prerelease = $false }
    @{ Tag = 'v1.2.3'; Core = '1.2.3'; Prerelease = $false }
    @{ Tag = 'v65534.65534.65534'; Core = '65534.65534.65534'; Prerelease = $false }
    @{ Tag = 'v1.2.3-rc.1'; Core = '1.2.3'; Prerelease = $true }
    @{ Tag = 'v1.2.3-0'; Core = '1.2.3'; Prerelease = $true }
    @{ Tag = 'v1.2.3-alpha.01a'; Core = '1.2.3'; Prerelease = $true }
)
foreach ($case in $valid) {
    $actual = & $validator -Tag $case.Tag
    if ($actual.Tag -cne $case.Tag -or $actual.Version -cne $case.Tag.Substring(1) -or
        $actual.CoreVersion -cne $case.Core -or $actual.IsPrerelease -ne $case.Prerelease) {
        throw "Incorrect release metadata for $($case.Tag)."
    }
}

$invalid = @(
    '1.2.3', 'V1.2.3', 'v1.2', 'v1.2.3.4', 'v01.2.3', 'v1.02.3', 'v1.2.03',
    'v-1.2.3', 'v1.2.3-', 'v1.2.3-rc..1', 'v1.2.3-rc.01', 'v1.2.3-00',
    'v1.2.3-rc_1', 'v1.2.3+build.1', 'v1.2.3-rc.1+build.1',
    'v65535.0.0', 'v1.65535.0', 'v1.0.65535', 'v99999999999999999999999999.0.0',
    ' v1.2.3', 'v1.2.3 ', "v1.2.3`n", "v1.2.3`r`n", 'v1.2.3;echo unsafe',
    'v1.2.3-$(echo unsafe)', 'v1.2.3-../../main', 'v1.2.3-é', 'v١.2.3'
)
foreach ($tag in $invalid) {
    $rejected = $false
    try {
        $null = & $validator -Tag $tag
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Invalid release tag was accepted: $tag"
    }
}

Write-Host "Release version checks passed: $($valid.Count) valid and $($invalid.Count) invalid cases."
