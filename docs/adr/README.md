# Architecture decision records (ADRs)

This directory records significant project decisions. Each ADR explains the context, decision,
and consequences. An accepted ADR is retained as a historical record: if a decision changes, add
a replacement ADR and link it from the earlier one. The index below is in English; the existing
ADR documents are written in Spanish.

| No. | Decision | Status |
| --- | --- | --- |
| [0001](0001-arquitectura-por-capas.md) | Layered clean architecture and solution structure | Accepted |
| [0002](0002-markdown-fuente-de-verdad.md) | Markdown as the source of truth with a disposable index outside the vault | Accepted |
| [0003](0003-sqlite-fts5-y-tokenizers.md) | SQLite FTS5: native bundle, external-content tables, and two tokenizers | Accepted |
| [0004](0004-frontmatter-por-parches-de-texto.md) | Frontmatter: read with YamlDotNet and write with text patches | Accepted |
| [0005](0005-identificadores-ulid.md) | Application-generated ULID identifiers | Accepted |
| [0006](0006-proteccion-contra-perdida-de-datos.md) | Data-loss protection: atomic writes, conflicts, encoding, and trash | Accepted |
| [0007](0007-vista-previa-markdown-nativa.md) | Markdown preview using native controls, without HTML or a WebView | Accepted |
| [0008](0008-stack-de-pruebas.md) | Test stack: xUnit v3, FluentAssertions 7.x, NSubstitute, and Avalonia.Headless | Accepted |
| [0009](0009-interfaz-avalonia-mvvm.md) | UI: Avalonia 12, strict MVVM, design tokens, and .resx localization | Accepted |
| [0010](0010-indexacion-incremental.md) | Incremental background indexing | Accepted |
| [0011](0011-datos-de-aplicacion-y-privacidad.md) | Application data, privacy, and logging | Accepted |
| [0012](0012-lenguaje-de-busqueda-y-filtros.md) | Search language, combinable filters, and facets | Accepted |
| [0013](0013-plantillas-en-la-boveda.md) | Note templates stored inside the vault | Accepted |
| [0014](0014-captura-rapida-y-hotkey-global.md) | Quick capture and cross-platform global shortcuts | Accepted |
| [0015](0015-proyecto-activo-y-rama-git.md) | Active-project and Git-branch detection without LibGit2Sharp | Accepted |

Future phases will document integrated Git operations (originally planned with LibGit2Sharp in
Phase 4), packaging, and distribution (`.exe`, `.deb`, `.dmg`) when implemented.

## Known limitations through Phase 2

- **Multiple instances:** there is no single-instance guard. Concurrent instances share
  `settings.json` (the last writer wins), the log file, and the global shortcut. On Windows, only
  the first instance can register the same shortcut.
- **Index corruption during a session:** corruption is detected and the index rebuilt when opening
  a vault. If it occurs while the vault is open, queries can fail until the vault is reopened.
- **External case-only renames:** on case-insensitive filesystems, a note may temporarily appear
  twice in the index until the next scan reconciles it.
- **Duplicate note IDs:** notes with the same `id` are not automatically assigned replacement IDs
  (ADR-0005).
- **Global shortcuts on macOS and Linux:** the SharpHook integration is built in CI but still needs
  manual validation on those platforms (ADR-0014). macOS requires Accessibility permission; Wayland
  may require input-device permissions. The in-app capture shortcut remains available when a global
  shortcut cannot be registered.
- **Facet counts:** sidebar counts apply to the whole vault rather than the current selection
  (ADR-0012).
- **Interface language:** changing the language in Settings takes effect after restarting the app.
- **Older vault ignore rules:** a Phase 1 vault may contain `*` in `.devnotes/.gitignore`. Replace
  that rule with `trash/` and `*.tmp` to allow templates to be versioned (ADR-0013).

For setup, usage, storage, and development commands, return to the [main README](../../README.md).
