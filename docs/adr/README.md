# Decisiones de arquitectura (ADR)

Registro breve de las decisiones relevantes del proyecto. Cada ADR explica el contexto, la
decisión y sus consecuencias. Un ADR aceptado no se reescribe: si una decisión cambia, se añade
uno nuevo que lo reemplaza y se enlaza desde el anterior.

| N.º | Decisión | Estado |
| --- | --- | --- |
| [0001](0001-arquitectura-por-capas.md) | Arquitectura limpia por capas y estructura de la solución | Aceptada |
| [0002](0002-markdown-fuente-de-verdad.md) | Markdown como fuente de verdad e índice descartable fuera de la bóveda | Aceptada |
| [0003](0003-sqlite-fts5-y-tokenizers.md) | SQLite FTS5: bundle nativo, tablas de contenido externo y dos tokenizers | Aceptada |
| [0004](0004-frontmatter-por-parches-de-texto.md) | Frontmatter: lectura con YamlDotNet, escritura con parches de texto | Aceptada |
| [0005](0005-identificadores-ulid.md) | Identificadores ULID generados por la app | Aceptada |
| [0006](0006-proteccion-contra-perdida-de-datos.md) | Protección contra pérdida de datos: escritura atómica, conflictos, codificación y papelera | Aceptada |
| [0007](0007-vista-previa-markdown-nativa.md) | Vista previa Markdown con controles nativos (sin HTML ni WebView) | Aceptada |
| [0008](0008-stack-de-pruebas.md) | Stack de pruebas: xUnit v3, FluentAssertions 7.x, NSubstitute y Avalonia.Headless | Aceptada |
| [0009](0009-interfaz-avalonia-mvvm.md) | Interfaz: Avalonia 12, MVVM estricto, tokens de diseño e i18n con .resx | Aceptada |
| [0010](0010-indexacion-incremental.md) | Indexación incremental en segundo plano | Aceptada |
| [0011](0011-datos-de-aplicacion-y-privacidad.md) | Datos de la aplicación, privacidad y logging | Aceptada |
| [0012](0012-lenguaje-de-busqueda-y-filtros.md) | Lenguaje de búsqueda, filtros combinables y facetas | Aceptada |
| [0013](0013-plantillas-en-la-boveda.md) | Plantillas de notas guardadas en la bóveda | Aceptada |
| [0014](0014-captura-rapida-y-hotkey-global.md) | Captura rápida y atajo global multiplataforma | Aceptada |
| [0015](0015-proyecto-activo-y-rama-git.md) | Detección del proyecto activo y rama Git sin LibGit2Sharp | Aceptada |

Pendientes de fases posteriores (se documentarán cuando se implementen): integración con Git
mediante LibGit2Sharp (Fase 4), empaquetado y distribución (`.exe`, `.deb`, `.dmg`).

## Limitaciones conocidas (Fases 1 y 2)

Decisiones conscientes de no resolver todavía; ninguna compromete los datos de las notas.

- **Una sola instancia.** No hay guardia de instancia única. Dos instancias abiertas a la vez
  comparten `settings.json` (gana la última que escribe), el fichero de log y el atajo global
  (solo la primera lo consigue en Windows).
- **Índice dañado con la app abierta.** Un índice corrupto se detecta y se reconstruye al abrir la
  bóveda; si se dañara durante la sesión, las consultas fallarían con un mensaje hasta reabrirla.
- **Renombrado externo que solo cambia mayúsculas** en sistemas de archivos que no las
  distinguen: el índice puede mostrar la nota duplicada hasta la siguiente exploración, que lo
  corrige.
- **Notas con el mismo `id`** (ADR-0005): no se reasigna automáticamente.
- **Atajo global en macOS y Linux** (ADR-0014): implementado con SharpHook según su
  documentación y compilado en CI, pero sin pruebas manuales en esas plataformas en esta fase. En
  Wayland sin permisos sobre los dispositivos de entrada solo funciona el atajo dentro de la app.
- **Conteos de las facetas** (ADR-0012): son globales de la bóveda, no de la selección actual.
- **Idioma:** cambiarlo en Ajustes se aplica al reiniciar la app (los textos son recursos
  estáticos).
- **`.gitignore` de bóvedas de la Fase 1** (ADR-0013): contiene `*`; para versionar las
  plantillas hay que cambiarlo a `trash/`.
