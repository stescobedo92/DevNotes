# DevNotes

Notas técnicas para desarrolladores: aplicación de escritorio multiplataforma, local-first,
con archivos Markdown como fuente de verdad e índice SQLite FTS5 descartable para búsqueda
instantánea.

> Estado: en desarrollo (Fase 1, MVP). Ver [`docs/adr/`](docs/adr/README.md) para las decisiones
> de arquitectura.

## Qué hace hoy (Fase 1)

- Bóvedas: una carpeta de archivos `.md` elegida en el primer arranque; se pueden registrar varias.
- Lista de notas ordenable por fecha, título o relevancia.
- Editor Markdown (AvaloniaEdit + TextMate) con vista previa en vivo: editor, dividido o vista previa.
- Bloques de código con resaltado de sintaxis y botón de copiar.
- Crear, renombrar, mover y eliminar notas; eliminar envía a una papelera interna recuperable.
- Autoguardado con detección de conflictos: un cambio externo nunca se sobrescribe sin avisar.
- Las notas se guardan en la codificación en que estaban (UTF-8, UTF-16 o una página de códigos
  heredada): abrir y editar un archivo antiguo no altera los bytes que no se tocan.
- Búsqueda de texto completo (palabras, prefijos y subcadenas) con fragmentos resaltados.
- Uso completo por teclado, paleta de comandos, temas claro/oscuro/sistema, español e inglés.

| Atajo (`Cmd` en macOS) | Acción |
| --- | --- |
| `Ctrl+N` | Nueva nota |
| `Ctrl+S` | Guardar |
| `Ctrl+K` / `Ctrl+P` | Buscar / cambiar de nota |
| `Ctrl+Shift+P` | Paleta de comandos |
| `Ctrl+B` / `Ctrl+Shift+I` | Mostrar u ocultar la barra lateral / el panel de detalles |
| `Ctrl+Shift+F` | Ir al buscador de la lista de notas |
| `Ctrl+E` | Alternar editor, dividido y vista previa |
| `F2` | Renombrar |
| `Ctrl` `+` / `-` / `0` | Tamaño de letra |

## Requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download) (la versión exacta la fija `global.json`).
- Windows, Linux o macOS. No hace falta ninguna dependencia nativa adicional: SQLite con FTS5
  viene incluido en el paquete `SQLitePCLRaw.bundle_e_sqlite3`.

## Compilar, probar y ejecutar

```bash
# Restaurar y compilar (los analizadores y el estilo forman parte de la compilación)
dotnet restore DevNotes.slnx
dotnet build DevNotes.slnx -c Release --no-restore

# Tests (dominio, aplicación, infraestructura contra SQLite y disco reales, interfaz headless)
dotnet test DevNotes.slnx -c Release --no-build

# Tests con cobertura (genera coverage.cobertura.xml en TestResults/)
dotnet test DevNotes.slnx -c Release --no-build --collect:"XPlat Code Coverage" --results-directory TestResults

# Comprobar el formato, como hace el CI
dotnet format DevNotes.slnx --verify-no-changes --no-restore

# Ejecutar la aplicación
dotnet run --project src/DevNotes.Desktop -c Release
```

Los datos de la aplicación (ajustes, índices y logs) se guardan en la carpeta de datos locales
del usuario; la variable de entorno `DEVNOTES_DATA_DIR` permite usar otra carpeta, por ejemplo
para probar sin tocar la instalación habitual:

```bash
DEVNOTES_DATA_DIR=/tmp/devnotes-prueba dotnet run --project src/DevNotes.Desktop -c Release
```

## Benchmarks

```bash
# Todos (puede tardar varios minutos)
dotnet run -c Release --project benchmarks/DevNotes.Benchmarks -- --filter "*"

# Solo búsqueda, en modo rápido
dotnet run -c Release --project benchmarks/DevNotes.Benchmarks -- --filter "*Search*" --job short
```

Miden la indexación inicial y la reexploración de una bóveda sintética de 1.000 y 5.000 notas, y
las consultas tal y como las lanza la interfaz sobre 5.000 notas. Como referencia, en un
equipo de desarrollo (Intel Core i9-12900K, Windows 11) con 5.000 notas: búsqueda mientras se
escribe entre 5 y 22 ms, listado completo en 26 ms, indexación inicial en 5,3 s (en segundo plano)
y reexploración sin cambios en 31 ms. El detalle está en el
[ADR-0003](docs/adr/0003-sqlite-fts5-y-tokenizers.md).

## Estructura

```
src/
  DevNotes.Domain/          Value objects e invariantes (rutas seguras, ids, etiquetas)
  DevNotes.Application/     Casos de uso, puertos, parser de notas, indexador, consultas
  DevNotes.Infrastructure/  SQLite/FTS5, sistema de archivos, watcher, ajustes, log
  DevNotes.Desktop/         Avalonia: vistas, view models, estilos, servicios de UI
tests/                      Un proyecto de tests por capa
benchmarks/                 BenchmarkDotNet (indexación y búsqueda)
docs/adr/                   Decisiones de arquitectura
build/                      Scripts de empaquetado (fase posterior)
```
