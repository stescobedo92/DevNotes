# ADR-0003: SQLite FTS5: bundle nativo, tablas de contenido externo y dos tokenizers

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La búsqueda debe responder en milisegundos con miles de notas, resaltar fragmentos y encontrar
tanto palabras del lenguaje natural como trozos de código (`GetUserById`, `WHERE_SELECT`), donde la
búsqueda por palabras completas no basta.

## Decisión

- **Proveedor:** `Microsoft.Data.Sqlite`, que arrastra `SQLitePCLRaw.bundle_e_sqlite3`. Ese bundle
  trae su propia compilación nativa de SQLite con FTS5 para Windows, Linux y macOS, de modo que no
  se depende del SQLite del sistema. Los tests de integración crean las tablas FTS5 reales y se
  ejecutan en los tres sistemas en CI: si una plataforma no tuviera FTS5 o el tokenizer `trigram`,
  el CI fallaría.
- **API síncrona en el thread pool:** SQLite no tiene E/S asíncrona (los métodos `*Async` de
  Microsoft.Data.Sqlite se ejecutan de forma síncrona), así que cada operación usa la API síncrona
  dentro de `Task.Run`. `journal_mode=WAL` permite leer mientras se escribe; las escrituras se
  serializan en proceso. `foreign_keys=ON` en todas las conexiones.
- **Tablas FTS5 de contenido externo** (`content='notes'`): el texto vive una sola vez en `notes`
  y tres triggers mantienen sincronizados los índices.
- **Dos índices de texto completo** sobre `title`, `body` y `tags`:
  - `notes_fts` con `unicode61 remove_diacritics 2` y `prefix='2 3'`: palabras, sin distinguir
    acentos ni mayúsculas, con búsqueda por prefijo mientras se escribe.
  - `notes_trigram` con `trigram`: subcadenas de 3 o más caracteres, para identificadores y código.

  La consulta usa primero el índice de palabras; si no llena el límite de resultados y hay
  términos de 3 o más caracteres, las coincidencias por subcadena completan la lista (sin
  duplicados y siempre después de las coincidencias por palabra).
- **Ranking:** `bm25` con pesos título 10, cuerpo 1, etiquetas 5; resaltado con `snippet()` y
  `highlight()`. Los marcadores de inicio y fin son caracteres del área de uso privado de Unicode
  (U+E000/U+E001), que se eliminan del texto al indexarlo: el contenido de una nota no puede
  falsificar un resaltado y no hay HTML ni escapes de por medio.
- **Entrada del usuario:** `FtsQueryBuilder` convierte cada término en una cadena FTS5 entre
  comillas (los operadores `AND`, `NEAR`, `*`, `:`… se tratan como texto) y la expresión `MATCH`
  se pasa siempre como parámetro. Todas las consultas SQL son parametrizadas. Solo el error de
  sintaxis de FTS5 se traduce a "sin resultados"; cualquier otro error de SQLite se propaga.

### Forma de la consulta (medida con `benchmarks/`)

Las primeras letras que se teclean (`d`, `de`…) coinciden con casi todas las notas. Una consulta
plana `SELECT … snippet() … JOIN notes … ORDER BY … LIMIT` tardaba 40–155 ms con 5.000 notas
porque SQLite evalúa las columnas del resultado *antes* de ordenar y limitar: leía la fila
completa de `notes` y construía el fragmento de cada coincidencia para quedarse con treinta. La
consulta se resuelve ahora en dos pasos dentro de una sola sentencia:

1. Una subconsulta decide **qué** notas se devuelven sin tocar las filas anchas:
   - por relevancia, ordenando solo con `bm25` (que sale del índice FTS);
   - por fecha o por título, recorriendo el índice de ordenación (`ix_notes_recent`,
     `ix_notes_title`, que incluyen la ruta para cubrir el orden completo) y comprobando cada
     entrada contra el conjunto de coincidencias, de modo que el recorrido se detiene al alcanzar
     el límite.
2. La consulta externa construye los fragmentos resaltados solo para esas notas.

El `+` unario de `+x.rowid IN (…)` es deliberado: impide que el planificador use la lista como
acceso por `rowid` (lo que relanzaría la consulta FTS una vez por fila; medido: 1,3 s) y la deja
como filtro de pertenencia.

Además, la columna `body` es la última de `notes`: SQLite guarda las columnas en orden de
declaración y un texto largo se desborda a páginas adicionales que habría que recorrer para
llegar a cualquier columna declarada después.

El coste restante lo domina `snippet()`, unos 40 µs por fila devuelta; por eso la lista limita
una **búsqueda** a 500 resultados (el listado sin búsqueda muestra hasta 2.000) y avisa cuando
recorta.

Medidas con BenchmarkDotNet sobre 5.000 notas en un Intel Core i9-12900K con Windows 11
(objetivo: latencia percibida inferior a 50 ms):

| Consulta | Media |
| --- | ---: |
| Apertura rápida, 2 letras tecleadas (coinciden todas las notas), 30 filas | 5,0 ms |
| Apertura rápida, dos términos | 6,8 ms |
| Apertura rápida, subcadena de un identificador (trigramas) | 6,9 ms |
| Lista, 2 letras tecleadas, más recientes primero, 500 filas | 18,8 ms |
| Lista, palabra común, por título | 18,8 ms |
| Lista, palabra común, por relevancia | 22,3 ms |
| Listado sin búsqueda, 2.000 filas | 26,4 ms |
| Búsqueda sin resultados | 0,4 ms |

Indexación inicial de 5.000 notas: 5,3 s en segundo plano; reexploración sin cambios: 31 ms.

## Consecuencias

- El índice de trigramas aumenta el tamaño de la base (aceptable: es una caché local).
- Las consultas de menos de 3 caracteres solo usan el índice de palabras (por prefijo).
- Entre notas con exactamente la misma relevancia, el corte del límite es arbitrario (por
  `rowid`); dentro del resultado se ordenan de la más reciente a la más antigua.
- Cambiar el esquema (versión 2) reconstruye el índice en el siguiente arranque (ADR-0002).
- La sintaxis avanzada (`project:`, `tag:`, `"frase exacta"`, `-excluir`) pertenece a la Fase 2 y
  se construirá sobre este mismo generador.
