#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# SemVer 2.0.0, with a required Git tag prefix. Build metadata is deliberately
# excluded from release names; the commit is recorded by Git and the release.
$pattern = '\Av(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z'
$match = [regex]::Match($Tag, $pattern, [System.Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromSeconds(1))
if (-not $match.Success) {
    throw 'Use a release tag such as v0.1.0 or v0.2.0-rc.1, without leading zeroes or build metadata.'
}

$prerelease = $match.Groups['prerelease'].Value
foreach ($identifier in $prerelease.Split('.')) {
    if ($identifier -cmatch '\A0[0-9]+\z') {
        throw 'Numeric prerelease identifiers must not contain leading zeroes.'
    }
}

foreach ($component in @('major', 'minor', 'patch')) {
    $number = [System.Numerics.BigInteger]::Parse($match.Groups[$component].Value, [System.Globalization.CultureInfo]::InvariantCulture)
    if ($number -gt 65534) {
        throw 'Release components must not exceed 65534 because they are also used in .NET assembly versions.'
    }
}

[pscustomobject]@{
    Tag = $Tag
    Version = $Tag.Substring(1)
    CoreVersion = '{0}.{1}.{2}' -f $match.Groups['major'].Value, $match.Groups['minor'].Value, $match.Groups['patch'].Value
    IsPrerelease = $prerelease.Length -gt 0
}
