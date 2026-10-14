# Build and releases

The [release workflow](../.github/workflows/release.yml) builds portable, self-contained desktop
packages and publishes them to [GitHub Releases](https://github.com/stescobedo92/DevNotes/releases).
The .NET runtime is included. These archives do not install the application or register automatic
updates. For source builds, tests, and benchmarks, see the [main README](../README.md#build-test-and-ci).

## Version policy

DevNotes follows [Semantic Versioning 2.0.0](https://semver.org/). Git tags use `vMAJOR.MINOR.PATCH`;
the `v` is a tag prefix, not part of the application version. The initial release is `v0.1.0`.

| Change after 1.0.0 | Increment | Example |
| --- | --- | --- |
| Incompatible public contract change | Major; reset minor and patch | `1.4.2` to `2.0.0` |
| Compatible feature or deprecation | Minor; reset patch | `1.4.2` to `1.5.0` |
| Compatible bug fix | Patch | `1.4.2` to `1.4.3` |

The public contract includes documented note/frontmatter formats, command-line arguments,
environment variables, and settings behavior. During `0.x` development that contract remains
unstable; use minor releases for features or incompatible changes and patch releases for fixes.
Maintainers choose versions by compatibility impact; commit messages do not automatically bump them.

Prerelease tags such as `v0.2.0-rc.1` create GitHub prereleases. Core numbers and numeric prerelease
identifiers cannot have leading zeroes. Repository policy excludes SemVer's optional `+metadata`
suffix and caps each core component at `65534` for .NET assembly-version compatibility.

The tag is the release version authority. The workflow passes its validated version to .NET and
the package names; `Directory.Build.props` retains a development fallback. Published versions and
tags must never be changed or reused. Deliver corrections as a new version.

## Packages

| Runtime | Asset | Contents |
| --- | --- | --- |
| `win-x64` | `DevNotes-v<version>-win-x64.zip` | Windows x64 application and runtime |
| `linux-x64` | `DevNotes-v<version>-linux-x64.tar.gz` | Linux x64 application and runtime |
| `osx-x64` | `DevNotes-v<version>-osx-x64.tar.gz` | Intel macOS `DevNotes.app` bundle |
| `osx-arm64` | `DevNotes-v<version>-osx-arm64.tar.gz` | Apple silicon macOS `DevNotes.app` bundle |

Each archive contains a `DevNotes-v<version>-<runtime>` directory. Keep its files together. Linux
packages still require native desktop dependencies, including `libx11-6`, `libice6`, `libsm6`, and
`libfontconfig1` on Debian/Ubuntu. See [Avalonia's supported platforms](https://docs.avaloniaui.net/docs/supported-platforms)
and [.NET self-contained deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/).

The macOS packages target macOS 15 or newer, following the
[.NET 10 supported-OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md),
and include an ad-hoc code signature. They are not Developer ID signed or notarized. Windows
binaries are unsigned. These packages do not establish
publisher trust with the operating system, and security policy may prevent them from opening.
Signing certificates, notarization, `.msi`/`.deb`/`.dmg` installers, and automatic updates are future work.

## Build without publishing

Use the **Release** workflow's **Run workflow** action on a branch and enter a version without `v`,
such as `0.1.0`. The equivalent command with an authenticated GitHub CLI is:

```sh
gh workflow run release.yml --ref main -f version=0.1.0
```

A manual branch run validates the version, runs the same checks as a release, and uploads package artifacts.
It does not create a tag or a GitHub release. Download the archives from that workflow run to inspect
them before publishing. See [GitHub's manual workflow documentation](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
Running the workflow on an existing release tag instead follows the publication path and ignores
the version input.

To package locally, install the .NET SDK selected by `global.json` and PowerShell 7, then run from
the repository root on the target operating system:

```powershell
pwsh ./build/Publish-Release.ps1 -Version 0.1.0 -Runtime win-x64 -OutputDirectory artifacts/release
```

Choose a runtime from the table above. macOS bundling requires macOS and its `codesign` tool.
The script refuses to overwrite an existing archive. Local packaging does not replace the
workflow's formatting and test checks or publish a release.

## Publish a release

1. Merge and review all intended changes on `main`, choose the next unused version, and check the
   release notes for compatibility changes.
2. Update the local checkout and create an annotated tag on the commit to release:

   ```sh
   git switch main
   git pull --ff-only origin main
   git tag -a v0.1.0 -m "Release v0.1.0"
   git push origin v0.1.0
   ```

   Replace `v0.1.0` with the chosen version for subsequent releases.

3. Follow the **Release** run in GitHub Actions. The workflow requires the tagged commit to belong
   to `main`, validates the version, and runs the reusable CI workflow on Windows, Linux, and macOS.
4. It builds all four packages alongside the quality checks and creates `SHA256SUMS`. After all
   checks pass, it publishes the full asset set with automatically generated release notes. A failed
   check or package build prevents publication. Prereleases are marked separately from normal releases.

The workflow uses the repository's `GITHUB_TOKEN`; publishing needs `contents: write` only in the
release publication job. No personal access token or signing secret is required for the current
unsigned/ad-hoc-signed packages. GitHub's [release CLI documentation](https://cli.github.com/manual/gh_release_create)
explains generated release notes and draft/upload/publish handling.

If a run fails before creating a release, fix the cause and rerun failed jobs for the same tag only
when its source has not changed. A source fix needs a new commit and version. If an interrupted
publication leaves a draft release, inspect it before deliberate recovery; the workflow refuses
to overwrite an existing release, including drafts. Never replace assets on a published version.

## Verify a download

Download the archive and `SHA256SUMS` from the same release. Compare the archive's SHA-256 hash
with its entry in that file before extracting it.

PowerShell:

```powershell
Get-FileHash ./DevNotes-v0.1.0-win-x64.zip -Algorithm SHA256
Get-Content ./SHA256SUMS
```

Linux:

```sh
sha256sum DevNotes-v0.1.0-linux-x64.tar.gz
cat SHA256SUMS
```

macOS:

```sh
shasum -a 256 DevNotes-v0.1.0-osx-arm64.tar.gz
cat SHA256SUMS
```

Replace the version and runtime as appropriate. The hashes must match exactly, ignoring letter
case. Checksums detect corrupt or changed downloads; they are not a trusted publisher signature.
