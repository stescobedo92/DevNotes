# Build and packaging

This directory is reserved for platform-specific packaging scripts and installers (`.exe`, `.deb`,
and `.dmg`). Packaging and release publishing are planned for a later development phase; there are
no installer scripts here yet.

The current [CI workflow](../.github/workflows/ci.yml) restores dependencies, verifies formatting,
builds with analyzers enabled, and runs tests on Windows, Linux, and macOS. Test and coverage reports
are uploaded as workflow artifacts. These artifacts are test evidence, not desktop installers.

For local restore, build, test, benchmark, and run commands, see the
[main README](../README.md#build-test-and-ci).
