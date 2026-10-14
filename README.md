# DevNotes

DevNotes is a local-first desktop notebook for developers on Windows, Linux, and macOS.
Your notes remain ordinary Markdown files; a rebuildable SQLite FTS5 index makes technical
notes, code snippets, design decisions, and runbooks searchable.

> **Status:** under development, through Phase 2 (metadata and quick capture). Run from source
> using the instructions below. Installers and release packaging are planned for a later phase.
> See the [architecture decision records](docs/adr/README.md) for design decisions and limitations.

## Features

- **Markdown vaults:** choose a folder on first launch and register additional vaults as needed.
  Create, rename, move, and delete notes, with an internal trash for recovery.
- **Editing and preview:** an AvaloniaEdit/TextMate editor with editor-only, split, and preview-only
  modes, syntax-highlighted code blocks, and copy buttons.
- **Safer saves:** autosave, atomic file writes, external-change conflict detection, and preservation
  of supported file encodings. UTF-8 and BOM-marked UTF-16 are supported; legacy bytes use a
  Windows-1252 fallback. Adding characters that the legacy encoding cannot represent converts
  that note to UTF-8.
- **Search and organization:** full-text search, prefix and substring matching, highlighted snippets,
  sorting by date/title/relevance, and combinable project, tag, type, and date filters.
- **Templates:** note, resolved bug, design decision, runbook, learning, and snippet templates.
  Customize them in the app and version `.devnotes/templates/` alongside your notes.
- **Quick capture:** a floating window with a configurable global shortcut and retained drafts.
- **Project context:** map projects to repository folders in Settings. DevNotes can detect the
  active project at launch, prefill it in new notes, and offer it as a filter. The status bar also
  shows the vault's Git branch when available.
- **Keyboard access and appearance:** command palette, light/dark/system themes, adjustable editor
  font size, and English and Spanish interfaces.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
  [`global.json`](global.json) requests `10.0.100`, permits newer stable `10.0` feature bands,
  and excludes prerelease SDKs. Run `dotnet --version` from the repository to check selection.
- A Windows, Linux, or macOS desktop session. On Linux, Avalonia requires native desktop libraries;
  Debian/Ubuntu package names include `libx11-6`, `libice6`, `libsm6`, and `libfontconfig1`.
  See [Avalonia's platform requirements](https://docs.avaloniaui.net/docs/supported-platforms).
- SQLite with FTS5 is bundled through `Microsoft.Data.Sqlite` and
  `SQLitePCLRaw.bundle_e_sqlite3`; a separate SQLite installation is unnecessary.

## Quick start

From a local checkout:

```sh
dotnet restore DevNotes.slnx
dotnet build DevNotes.slnx -c Release --no-restore
dotnet run --project src/DevNotes.Desktop -c Release --no-build
```

1. Choose a folder for your first vault. Use a new folder or one containing existing `.md` files.
2. Create a note with `Ctrl+N` (`Cmd+N` on macOS), select a template, and start writing.
3. Use `Ctrl+K` to search or `Ctrl+Shift+P` to open the command palette.
4. Open Settings with `Ctrl+,` to choose appearance, language, repository mappings, or the global
   capture shortcut. Restart the app after changing the interface language.

To launch with an explicit repository context, replace the example path with your repository folder:

```sh
dotnet run --project src/DevNotes.Desktop -c Release --no-build -- --project-dir "/path/to/repository"
```

The launch directory is resolved in this order: `--project-dir`, `DEVNOTES_PROJECT_DIR`, then the
working directory. Detection first uses repository mappings in Settings, then matches the repository
folder name to an existing project in the vault. It does not create a project automatically.

## Search and metadata

Existing Markdown files work without frontmatter. Optional YAML fields include `title`, `project`,
`tags`, `type`, `created`, `updated`, and `links` (with `commits`, `tickets`, and `notes` lists).
DevNotes generates IDs when needed and preserves unknown frontmatter keys. Supported note types
are `note`, `bug`, `adr`, `runbook`, `learning`, and `snippet`.

Use these expressions in the search box and combine them with sidebar filters:

| Query | Finds |
| --- | --- |
| `"connection timeout"` | A phrase in the indexed text |
| `retry -legacy` | Notes matching `retry`, excluding `legacy` |
| `#database` or `tag:database` | Notes tagged `database` |
| `project:DevNotes type:bug` | Bug notes in the DevNotes project |
| `project:"Payment Service" tag:sql,performance` | Notes in Payment Service with both tags |
| `commit:a3f9c21` | Notes linked to a matching commit hash or its abbreviated form |
| `ticket:MS-482` | Notes linked to the specified ticket |
| `created:2026-09` | Notes created during September 2026 |
| `updated:>=2026-09-01` | Notes updated on or after the given date |
| `created:2026-01-01..2026-09-30` | Notes created within an inclusive date range |

Dates accept a day (`YYYY-MM-DD`), month (`YYYY-MM`), or year (`YYYY`), with comparisons or
`a..b` ranges. Spanish aliases such as `proyecto:`, `etiqueta:`, `tipo:`, and `creada:` also work.
Different filter fields combine with AND; repeated projects or types provide alternatives, while
every requested tag must be present. Sidebar counts describe the entire vault.

Word matches rank ahead of substring matches. Substring matching requires at least three characters,
which is useful for code identifiers. The note list shows up to 500 search results or 2,000 entries
without a text query and indicates when the result is truncated.

## Keyboard shortcuts

For main-window shortcuts, use `Cmd` instead of `Ctrl` and `Option` instead of `Alt` on macOS.

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` | New note |
| `Ctrl+S` | Save |
| `Ctrl+K` / `Ctrl+P` | Search / switch note |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+B` / `Ctrl+Shift+I` | Toggle sidebar / details panel |
| `Ctrl+Shift+F` | Focus the note-list search box |
| `Ctrl+E` | Cycle editor, split, and preview modes |
| `F2` | Rename note |
| `Ctrl` + `+` / `-` / `0` | Increase / decrease / reset editor font size |
| `Ctrl+Alt+N` | Open quick capture inside the app |
| `Ctrl+,` | Settings |

The **global** capture shortcut defaults to `Ctrl+Alt+N` on Windows and Linux and
`Control+Option+N` on macOS. It is separate from the main-window shortcut and can be changed or
disabled in Settings. DevNotes must be running for it to work. In the capture window, `Ctrl+Enter`
or `Cmd+Enter` saves and closes; `Esc` hides the window and retains the draft.

On macOS, global capture needs Accessibility permission, requested from Settings. On Linux, it
depends on the session and input-device permissions; Wayland may only allow the in-app shortcut.
Settings reports the global-shortcut status. Interactive global capture on macOS and Linux still
needs manual platform validation; passing CI does not verify operating-system permissions.

## Storage and backup

The vault is the source of truth. Application data is stored separately under the current user's
local application data directory, in a `DevNotes` folder.

| Location | Contents |
| --- | --- |
| Vault `*.md` files | Note text and metadata |
| Vault `.devnotes/templates/` | Customized templates |
| Vault `.devnotes/trash/` | Deleted notes and restore metadata |
| App data `settings.json` | Registered vaults, appearance, shortcuts, and repository mappings |
| App data `capture-draft.json` | The pending quick-capture draft, when present |
| App data `indexes/` | Rebuildable SQLite search indexes |
| App data `logs/` | Local diagnostic logs |

Back up your vault, including `.devnotes/` if you want templates and recoverable trash. Back up
`settings.json` and `capture-draft.json` separately to retain preferences and an unfinished capture.
The index can be rebuilt from Settings. Close DevNotes before copying its application-data files.

Vaults can be managed with Git or your own backup tools. DevNotes detects repository context but
does not commit, push, or synchronize notes. New vaults ignore trash in `.devnotes/.gitignore` while
allowing templates to be versioned. If an older vault has `*` in that file, replace that rule with
`trash/` and `*.tmp` to include templates in Git.

Set `DEVNOTES_DATA_DIR` to isolate app settings, drafts, indexes, and logs during development.
This changes application storage, not the location of your Markdown vault.

PowerShell:

```powershell
$env:DEVNOTES_DATA_DIR = Join-Path $env:TEMP 'devnotes-sandbox'
dotnet run --project src/DevNotes.Desktop -c Release --no-build
Remove-Item Env:DEVNOTES_DATA_DIR
```

Bash or Zsh:

```sh
DEVNOTES_DATA_DIR=/tmp/devnotes-sandbox dotnet run --project src/DevNotes.Desktop -c Release --no-build
```

## Build, test, and CI

Run these commands from the repository root:

```sh
dotnet restore DevNotes.slnx
dotnet format DevNotes.slnx --verify-no-changes --no-restore
dotnet build DevNotes.slnx -c Release --no-restore
dotnet test DevNotes.slnx -c Release --no-build

# Collect coverage and write reports beneath TestResults/.
dotnet test DevNotes.slnx -c Release --no-build --collect:"XPlat Code Coverage" --results-directory TestResults
```

The build enforces analyzers and code style, treating build warnings as errors except for NuGet
vulnerability advisories. Tests cover domain rules, application behavior, real SQLite/filesystem
integration, and Avalonia headless UI interactions.

The [CI workflow](.github/workflows/ci.yml) runs Release builds and tests on Windows, Ubuntu, and
macOS, checks formatting on Ubuntu, and uploads test results and coverage as per-platform artifacts.
It runs for pull requests, pushes to `main`, and manual **Run workflow** requests from GitHub Actions.
See [`build/`](build/README.md) for the current packaging scope.

## Benchmarks

```sh
# Run the full suite; this may take several minutes.
dotnet run -c Release --project benchmarks/DevNotes.Benchmarks -- --filter "*"

# Run a shorter search-only suite.
dotnet run -c Release --project benchmarks/DevNotes.Benchmarks -- --filter "*Search*" --job short
```

BenchmarkDotNet measures initial indexing and unchanged rescans for 1,000 and 5,000 synthetic notes,
plus the UI's search queries over 5,000 notes. Earlier measurements on an Intel Core i9-12900K with
Windows 11 recorded roughly 5-22 ms for search-as-you-type queries and 5.3 seconds for initial
indexing of 5,000 notes. These are reference measurements, not performance guarantees; rerun the
suite on your hardware. Details are recorded in
[ADR-0003](docs/adr/0003-sqlite-fts5-y-tokenizers.md) and
[ADR-0012](docs/adr/0012-lenguaje-de-busqueda-y-filtros.md).

## Repository structure

```text
src/
  DevNotes.Domain/          Value objects and invariants: safe paths, IDs, tags
  DevNotes.Application/     Use cases, ports, note parsing, indexing, and queries
  DevNotes.Infrastructure/  SQLite/FTS5, filesystem, watcher, settings, and logging
  DevNotes.Desktop/         Avalonia views, view models, styles, and UI services
tests/                     One test project per layer
benchmarks/                BenchmarkDotNet indexing and search workloads
docs/adr/                  Architecture decision records
build/                     Packaging scope; release scripts are not implemented yet
```

Dependency versions are centralized in [`Directory.Packages.props`](Directory.Packages.props);
shared build rules live in [`Directory.Build.props`](Directory.Build.props). Domain and application
logic are separated from storage and UI adapters. The desktop application composes those layers.

## Current limitations

- Run one instance at a time: multiple instances share settings, logs, and the global shortcut.
- Notes larger than 16 MiB, dot-prefixed files/folders, and symbolic links are not indexed as notes.
- The native Markdown preview does not render arbitrary HTML or download remote images.
- The index is rebuilt when corruption is detected while opening a vault. If corruption occurs
  during a session, reopen the vault before retrying searches.
- Installers, release publishing, and integrated Git synchronization are not implemented yet.

See the [ADR index](docs/adr/README.md) for further limitations and rationale. The index is in English;
the accepted historical ADR documents currently remain in Spanish.
