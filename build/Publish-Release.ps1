#Requires -Version 7.0
<#
.SYNOPSIS
Publishes a self-contained DevNotes desktop archive on its target operating system.
.EXAMPLE
./build/Publish-Release.ps1 -Version 0.1.0 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime,

    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/release'),

    # Opt in only on a disposable macOS desktop runner; starts the extracted application.
    [switch] $SmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeCommand {
    param([string] $Command, [string[]] $Arguments)

    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "'$Command' exited with code $LASTEXITCODE."
    }
}

function Invoke-MacOSSmokeTest {
    param([string] $ExecutablePath, [string] $DataDirectory)

    New-Item -ItemType Directory -Path $DataDirectory | Out-Null
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $DataDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Environment['DEVNOTES_DATA_DIR'] = $DataDirectory
    $startInfo.Environment['DEVNOTES_PROJECT_DIR'] = $DataDirectory
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) {
            throw 'The packaged application could not be started.'
        }
        # Drain both pipes concurrently so a verbose startup cannot block the child.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($process.WaitForExit(5000)) {
            $diagnostics = @($stdout.GetAwaiter().GetResult(), $stderr.GetAwaiter().GetResult())
            $logDirectory = Join-Path $DataDirectory 'logs'
            if (Test-Path -LiteralPath $logDirectory) {
                foreach ($logFile in (Get-ChildItem -LiteralPath $logDirectory -Filter '*.log' -File)) {
                    $diagnostics += Get-Content -LiteralPath $logFile.FullName -Tail 100
                }
            }
            throw "Packaged application exited during startup (code $($process.ExitCode)).`n$($diagnostics -join "`n")"
        }
        Write-Host 'Extracted macOS application stayed running for the five-second startup check.'
    }
    finally {
        # Only stop the exact child created here; never find processes by executable name.
        if ($started -and -not $process.HasExited) {
            $process.Kill()
            if (-not $process.WaitForExit(5000)) {
                throw 'Could not stop the packaged application after its startup check.'
            }
        }
        $process.Dispose()
    }
}

$release = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Tag "v$Version"
$platform = $Runtime.Split('-')[0]
if ($SmokeTest -and $platform -ne 'osx') {
    throw 'The optional startup smoke test is only supported for macOS packages.'
}
if (($platform -eq 'win' -and -not $IsWindows) -or
    ($platform -eq 'linux' -and -not $IsLinux) -or
    ($platform -eq 'osx' -and -not $IsMacOS)) {
    throw "Package $Runtime on its target operating system to preserve native executable permissions and signatures."
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($OutputDirectory))
$packageName = "DevNotes-$($release.Tag)-$Runtime"
$extension = if ($platform -eq 'win') { '.zip' } else { '.tar.gz' }
$archivePath = Join-Path $outputRoot "$packageName$extension"
if (Test-Path -LiteralPath $archivePath) {
    throw "Archive already exists: $archivePath. Use a different output directory to rebuild without overwriting a release."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$stagingPath = Join-Path $outputRoot ".package-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingPath | Out-Null

try {
    $packagePath = Join-Path $stagingPath $packageName
    $publishPath = $packagePath
    if ($platform -eq 'osx') {
        $appBundle = Join-Path $packagePath 'DevNotes.app'
        $contentsPath = Join-Path $appBundle 'Contents'
        $publishPath = Join-Path $contentsPath 'MacOS'
        New-Item -ItemType Directory -Path (Join-Path $contentsPath 'Resources') -Force | Out-Null
    }

    $publishArguments = @(
        'publish', (Join-Path $repositoryRoot 'src/DevNotes.Desktop/DevNotes.Desktop.csproj'),
        '--configuration', 'Release', '--runtime', $Runtime, '--self-contained', 'true',
        '--output', $publishPath,
        '-p:UseAppHost=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$($release.Version)",
        "-p:AssemblyVersion=$($release.CoreVersion).0",
        "-p:FileVersion=$($release.CoreVersion).0",
        "-p:InformationalVersion=$($release.Tag)",
        '-p:IncludeSourceRevisionInInformationalVersion=false'
    )
    Invoke-NativeCommand -Command 'dotnet' -Arguments $publishArguments

    $appHostName = if ($platform -eq 'win') { 'DevNotes.exe' } else { 'DevNotes' }
    $requiredFiles = @(
        $appHostName, 'DevNotes.dll', 'DevNotes.deps.json', 'DevNotes.runtimeconfig.json',
        'appsettings.json', 'System.Private.CoreLib.dll', 'TextMateSharp.dll',
        'TextMateSharp.Grammars.dll', 'Onigwrap.dll', 'SharpHook.dll'
    )
    $requiredFiles += switch ($platform) {
        'win' {
            'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'libSkiaSharp.dll',
            'libHarfBuzzSharp.dll', 'e_sqlite3.dll', 'libonigwrap.dll', 'uiohook.dll',
            'av_libglesv2.dll'
        }
        'linux' {
            'libcoreclr.so', 'libhostfxr.so', 'libhostpolicy.so', 'libSkiaSharp.so',
            'libHarfBuzzSharp.so', 'libe_sqlite3.so', 'libonigwrap.so', 'libuiohook.so',
            'libuiohook-x11.so', 'libuiohook-xrecord.so', 'libuiohook-wayland.so'
        }
        'osx' {
            'libcoreclr.dylib', 'libhostfxr.dylib', 'libhostpolicy.dylib', 'libSkiaSharp.dylib',
            'libHarfBuzzSharp.dylib', 'libe_sqlite3.dylib', 'libonigwrap.dylib',
            'libuiohook.dylib', 'libAvaloniaNative.dylib'
        }
    }
    foreach ($relativePath in $requiredFiles) {
        $filePath = Join-Path $publishPath $relativePath
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or (Get-Item -LiteralPath $filePath).Length -eq 0) {
            throw "Required runtime asset missing or empty: $relativePath"
        }
    }

    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishPath 'DevNotes.runtimeconfig.json') -Raw |
        ConvertFrom-Json -AsHashtable
    $runtimeOptions = $runtimeConfig['runtimeOptions']
    if (-not $runtimeOptions.ContainsKey('includedFrameworks') -or
        $runtimeOptions['includedFrameworks'].name -notcontains 'Microsoft.NETCore.App') {
        throw 'The published application must include its own .NET runtime.'
    }
    $dependencies = Get-Content -LiteralPath (Join-Path $publishPath 'DevNotes.deps.json') -Raw |
        ConvertFrom-Json -AsHashtable
    if (-not $dependencies['runtimeTarget']['name'].EndsWith("/$Runtime", [StringComparison]::Ordinal)) {
        throw "The published application does not target $Runtime."
    }
    $assemblyInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishPath 'DevNotes.dll'))
    if ($assemblyInfo.ProductVersion -cne $release.Tag) {
        throw "The embedded product version '$($assemblyInfo.ProductVersion)' does not match '$($release.Tag)'."
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $packagePath
    foreach ($licenseName in @('LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
        $licensePath = Join-Path $repositoryRoot $licenseName
        if (Test-Path -LiteralPath $licensePath -PathType Leaf) {
            Copy-Item -LiteralPath $licensePath -Destination $packagePath
        }
    }

    if ($platform -ne 'win') {
        Invoke-NativeCommand -Command 'chmod' -Arguments @('755', (Join-Path $publishPath $appHostName))
    }
    if ($platform -eq 'osx') {
        $plistPath = Join-Path $contentsPath 'Info.plist'
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>io.github.stescobedo92.devnotes</string>
    <key>CFBundleName</key><string>DevNotes</string>
    <key>CFBundleDisplayName</key><string>DevNotes</string>
    <key>CFBundleExecutable</key><string>DevNotes</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>$($release.CoreVersion)</string>
    <key>CFBundleShortVersionString</key><string>$($release.CoreVersion)</string>
    <key>LSMinimumSystemVersion</key><string>15.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@ | Set-Content -LiteralPath $plistPath -Encoding utf8NoBOM
        Invoke-NativeCommand -Command '/usr/bin/plutil' -Arguments @('-lint', $plistPath)

        # Apple requires non-Mach-O content outside code directories. Relative links preserve
        # .NET's publish layout without signing managed assemblies through fragile xattrs.
        # https://developer.apple.com/library/archive/technotes/tn2206/_index.html
        # https://docs.avaloniaui.net/docs/deployment/macos#sandbox-and-bundle
        $frameworksPath = Join-Path $contentsPath 'Frameworks'
        New-Item -ItemType Directory -Path $frameworksPath | Out-Null
        $nativeCodePaths = [System.Collections.Generic.List[string]]::new()
        $relativeLinks = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($item in (Get-ChildItem -LiteralPath $publishPath -Force)) {
            $isMachO = $false
            if (-not $item.PSIsContainer) {
                $fileType = & /usr/bin/file -b $item.FullName
                if ($LASTEXITCODE -ne 0) {
                    throw "Could not inspect published file: $($item.FullName)"
                }
                $isMachO = $fileType -match 'Mach-O'
            }

            if ($isMachO -and $item.Extension -ne '.dylib') {
                # Signing the main executable here would sign its enclosing bundle too early.
                if ($item.Name -ne $appHostName) {
                    $nativeCodePaths.Add($item.FullName)
                    # Runtime helpers must be discoverable beside both the managed entry point
                    # and the physical CoreCLR library (which resides in Frameworks).
                    foreach ($aliasFolder in @('Resources', 'Frameworks')) {
                        $aliasPath = "$aliasFolder/$($item.Name)"
                        $aliasTarget = "../MacOS/$($item.Name)"
                        New-Item -ItemType SymbolicLink -Path (Join-Path $contentsPath $aliasPath) -Target $aliasTarget | Out-Null
                        $relativeLinks.Add($aliasPath, $aliasTarget)
                    }
                }
                continue
            }

            $destinationFolder = if ($isMachO) { 'Frameworks' } else { 'Resources' }
            $destinationPath = Join-Path (Join-Path $contentsPath $destinationFolder) $item.Name
            Move-Item -LiteralPath $item.FullName -Destination $destinationPath
            $aliasTarget = "../$destinationFolder/$($item.Name)"
            New-Item -ItemType SymbolicLink -Path $item.FullName -Target $aliasTarget | Out-Null
            $relativeLinks.Add("MacOS/$($item.Name)", $aliasTarget)
            if ($isMachO) {
                $nativeCodePaths.Add($destinationPath)
                # .NET resolves the entry assembly's real path, making Resources its app root.
                # Native probing from that directory still needs the complete runtime payload.
                $aliasPath = "Resources/$($item.Name)"
                New-Item -ItemType SymbolicLink -Path (Join-Path $contentsPath $aliasPath) -Target $aliasTarget | Out-Null
                $relativeLinks.Add($aliasPath, $aliasTarget)
            }
        }
        # Hostpolicy resolves CoreCLR's physical directory before locating its core assembly.
        $coreLibAlias = 'Frameworks/System.Private.CoreLib.dll'
        $coreLibTarget = '../Resources/System.Private.CoreLib.dll'
        New-Item -ItemType SymbolicLink -Path (Join-Path $contentsPath $coreLibAlias) -Target $coreLibTarget | Out-Null
        $relativeLinks.Add($coreLibAlias, $coreLibTarget)

        # Ad-hoc signatures provide local code integrity (required on Apple Silicon).
        # They are not Developer ID signatures and do not provide notarization or Gatekeeper trust.
        foreach ($nativeCodePath in $nativeCodePaths) {
            Invoke-NativeCommand -Command '/usr/bin/codesign' -Arguments @(
                '--force', '--sign', '-', '--timestamp=none', $nativeCodePath
            )
        }
        # Sign the bundle (including its main executable) last; --deep is only for verification.
        Invoke-NativeCommand -Command '/usr/bin/codesign' -Arguments @(
            '--force', '--sign', '-', '--timestamp=none',
            '--entitlements', (Join-Path $PSScriptRoot 'macos-entitlements.plist'), $appBundle
        )
        Invoke-NativeCommand -Command '/usr/bin/codesign' -Arguments @('--verify', '--strict', '--deep', $appBundle)
    }

    $stagedArchive = Join-Path $stagingPath "$packageName$extension"
    if ($platform -eq 'win') {
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $packagePath, $stagedArchive, [System.IO.Compression.CompressionLevel]::Optimal, $true
        )
    }
    else {
        # Native tar retains executable permissions and the macOS bundle hierarchy.
        Invoke-NativeCommand -Command 'tar' -Arguments @('-czf', $stagedArchive, '-C', $stagingPath, $packageName)
    }

    if ($platform -eq 'osx') {
        # Verify the actual distributable after extraction, not only the source bundle.
        # This catches broken relative links, lost executable permissions, and signature damage.
        $verificationPath = Join-Path $stagingPath 'verification'
        New-Item -ItemType Directory -Path $verificationPath | Out-Null
        Invoke-NativeCommand -Command 'tar' -Arguments @('-xzf', $stagedArchive, '-C', $verificationPath)
        $extractedBundle = Join-Path (Join-Path $verificationPath $packageName) 'DevNotes.app'
        $extractedContents = Join-Path $extractedBundle 'Contents'
        $extractedMacOS = Join-Path $extractedContents 'MacOS'
        foreach ($relativePath in $requiredFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $extractedMacOS $relativePath) -PathType Leaf)) {
                throw "The extracted bundle cannot resolve required asset: $relativePath"
            }
        }
        foreach ($relativeLink in $relativeLinks.Keys) {
            $linkPath = Join-Path $extractedContents $relativeLink
            $link = Get-Item -LiteralPath $linkPath
            if ($link.LinkType -ne 'SymbolicLink' -or $link.Target -cne $relativeLinks[$relativeLink] -or
                -not (Test-Path -LiteralPath $linkPath)) {
                throw "The extracted bundle lost a relative symbolic link: $relativeLink"
            }
        }
        Invoke-NativeCommand -Command '/bin/test' -Arguments @('-x', (Join-Path $extractedMacOS $appHostName))
        Invoke-NativeCommand -Command '/usr/bin/codesign' -Arguments @('--verify', '--strict', '--deep', $extractedBundle)
        if ($SmokeTest) {
            Invoke-MacOSSmokeTest -ExecutablePath (Join-Path $extractedMacOS $appHostName) -DataDirectory (Join-Path $stagingPath 'smoke-data')
        }
    }

    # Move only a complete archive; never replace an already published filename.
    [System.IO.File]::Move($stagedArchive, $archivePath, $false)
    Write-Host "Created $archivePath"
    Write-Output $archivePath
}
finally {
    # Only the unique directory created above is eligible for recursive cleanup.
    $cleanupPath = [System.IO.Path]::GetFullPath($stagingPath)
    if ((Split-Path -Parent $cleanupPath) -ne $outputRoot -or
        (Split-Path -Leaf $cleanupPath) -notmatch '^\.package-[a-f0-9]{32}$') {
        throw "Refusing to clean unexpected staging path: $cleanupPath"
    }
    Remove-Item -LiteralPath $cleanupPath -Recurse -Force
}
