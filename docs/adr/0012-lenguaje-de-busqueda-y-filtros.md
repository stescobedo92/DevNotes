# ADR-0012: Lenguaje de búsqueda, filtros combinables y facetas

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La Fase 2 pide un panel de proyectos y etiquetas con conteos, filtros combinables y una sintaxis
de búsqueda avanzada (`project:`, `tag:`, `type:`, `commit:`, `ticket:`, `created:>fecha`,
`"frase exacta"`, `-excluir`) sobre el índice de la Fase 1 (ADR-0003), sin perder la latencia
por debajo de 50 ms con miles de notas.

## Decisión

### Sintaxis

`SearchQueryParser` convierte el texto en términos libres y un `NoteFilter`. Cualquier cosa que
no sea un filtro reconocido es texto normal: una consulta nunca falla al analizarse.

- Palabras sueltas: coincidencia por palabra (unicode61) y, en su defecto, por subcadena
  (trigramas); la última palabra que se está tecleando coincide por prefijo.
- `"frase exacta"`: una frase FTS5. Una comilla sin cerrar es una frase en curso y también
  coincide por prefijo.
- `-palabra` y `-"frase"`: exclusión por palabra (índice unicode61). La negación se aplica solo a
  palabras y frases; `-project:x` se ignora en vez de adivinar su sentido.
- `#etiqueta` es un atajo de `tag:etiqueta`.
- Filtros `clave:valor`, con alias en español (`proyecto:`, `etiqueta:`, `tipo:`, `creada:`,
  `actualizada:`) y valores entre comillas cuando llevan espacios. `type:` acepta solo los tipos
  conocidos: un tipo desconocido no puede coincidir con ninguna nota.
- Fechas: `2026-09-01`, `2026-09` (el mes), `2026` (el año), comparaciones `>`, `>=`, `<`, `<=`
  y rangos `a..b`. Una fecha mal formada se ignora (probablemente se está tecleando) en lugar de
  vaciar la lista.

### Combinación

Los filtros del texto y la selección de la barra lateral se combinan con AND. Dentro de un campo:

- **Proyectos, tipos, commits y tickets** son alternativas (OR). Dos fuentes con valores
  distintos del mismo campo se intersecan; si no queda ninguno, el resultado se sabe vacío sin
  consultar el índice (`NoteFilter.IsUnsatisfiable`).
- **Etiquetas** se acumulan: la nota debe tenerlas todas.
- **Fechas** estrechan el rango.

Proyectos, commits y tickets se comparan sin distinguir mayúsculas ni acentos (`TextKey`, la
misma clave que ordena los títulos). Los commits coinciden por prefijo en ambos sentidos: un
hash corto encuentra el largo y viceversa.

### Ejecución en SQLite

Los filtros se resuelven como un **conjunto de rowids** obtenido de los índices pequeños
(`project_key`, `type`, `created`, `updated`, la tabla `tags` y la de `links`), y el recorrido de
texto completo solo comprueba pertenencia a ese conjunto (`+rowid IN (…)`). Hacer un `JOIN` con
las filas anchas de `notes` dentro del bucle FTS duplicaba el tiempo de la búsqueda (medido:
41 ms frente a 20 ms) porque cada coincidencia leía su fila. La exclusión es un `NOT IN` sobre el
índice de palabras.

Las facetas (proyectos, etiquetas y tipos con conteos) son tres `GROUP BY` servidos por índices
cubrientes; los conteos son globales de la bóveda, no de la selección actual. El esquema pasa a
la versión 3 (columna `project_key`, índices de fechas y de proyecto cubriente); el índice se
reconstruye en el siguiente arranque.

## Medidas (5.000 notas, i9-12900K, BenchmarkDotNet en modo corto)

| Consulta | Media |
| --- | ---: |
| Lista con filtro de proyecto (625 notas), sin texto | 7,1 ms |
| Dos letras tecleadas + proyecto + etiqueta | 9,3 ms |
| Palabra común por relevancia + tipo + exclusión | 21,3 ms |
| Notas creadas en un mes, por título | 1,7 ms |
| Facetas de la barra lateral | 1,1 ms |

## Consecuencias

- La apertura rápida (`Ctrl+K`) entiende la misma sintaxis pero ignora la selección de la barra
  lateral: sirve para saltar a cualquier nota.
- Los conteos de las facetas no cambian al seleccionar filtros; una faceta facetada se puede
  añadir sobre el mismo conjunto de rowids si hiciera falta.
- Fechas relativas (`hoy`, `semana`) y filtros negados quedan para más adelante.
