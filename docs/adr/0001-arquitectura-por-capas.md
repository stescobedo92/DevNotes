# ADR-0001: Arquitectura limpia por capas y estructura de la solución

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

DevNotes es una aplicación de escritorio local-first que debe poder probarse sin interfaz, sin
disco y sin SQLite reales, y que crecerá por fases (metadatos, enlaces, Git, exportación).

## Decisión

Cuatro proyectos con dependencias en una sola dirección, más un proyecto de tests por capa:

```
Domain  ←  Application  ←  Infrastructure
                 ↑               ↑
                 └──── Desktop ──┘
```

- **Domain**: value objects (`NotePath`, `NoteId`, `Tag`, `Slug`…) sin dependencias externas. Las
  invariantes de seguridad viven aquí: un `NotePath` no se puede construir con `..`, rutas
  absolutas ni nombres reservados, así que el path traversal queda descartado por diseño.
- **Application**: casos de uso y puertos (`INoteFileStore`, `INoteIndex`, `IVaultWatcher`,
  `ISettingsStore`). Decide *qué* se indexa, cuándo hay conflicto y cómo se edita el frontmatter.
- **Infrastructure**: adaptadores (sistema de archivos, SQLite/FTS5, `FileSystemWatcher`, JSON de
  ajustes, log a fichero).
- **Desktop**: Avalonia (vistas, view models, servicios de UI) y raíz de composición (`AppHost`).

Plataforma: .NET 10 (LTS vigente), C# con `Nullable` y `TreatWarningsAsErrors`, analizadores
`latest-recommended` con `EnforceCodeStyleInBuild`, versiones de paquetes centralizadas en
`Directory.Packages.props` y solución en formato `.slnx`.

## Consecuencias

- La lógica de indexación y de conflictos se prueba con dobles en memoria; los adaptadores se
  prueban contra SQLite y el sistema de archivos reales.
- Dentro de `DevNotes.Desktop` el identificador `Application` resuelve al espacio de nombres
  `DevNotes.Application`, por lo que la clase de Avalonia se referencia como
  `Avalonia.Application`.
- El repositorio Git es propio de la carpeta `DevNotes` (independiente de la carpeta padre).
