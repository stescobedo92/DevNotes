# ADR-0013: Plantillas de notas guardadas en la bóveda

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La Fase 2 pide plantillas (bug resuelto, decisión de diseño, runbook, aprendizaje, snippet)
editables por el usuario. Había que decidir dónde viven, cómo se rellenan y qué pasa cuando una
plantilla está mal formada.

## Decisión

- **Integradas:** seis plantillas (`note`, `bug`, `adr`, `runbook`, `learning`, `snippet`) en el
  idioma de la interfaz (español o inglés). La plantilla `note` es la de una nota normal y la que
  usa la captura rápida.
- **Ubicación:** `.devnotes/templates/<clave>.md` dentro de la bóveda. Una copia con la clave de
  una integrada la sustituye; cualquier otro archivo es una plantilla propia. Así un equipo puede
  versionarlas con las notas: el `.gitignore` que la app escribe en `.devnotes` ignora solo
  `trash/` y los temporales. (Las bóvedas creadas por la Fase 1 tienen un `.gitignore` con `*`;
  quien quiera versionar sus plantillas lo cambia a `trash/`).
- **Marcadores:** `{{id}}`, `{{title}}`, `{{project}}`, `{{date}}`, `{{type}}` y `{{body}}`
  (mayúsculas y espacios internos indiferentes). Dentro del frontmatter los valores se escriben
  como escalares YAML seguros; en el cuerpo, como texto. Una línea del frontmatter cuyo marcador
  queda vacío (`project:` sin proyecto) se elimina en vez de dejar una clave vacía; el `{{body}}`
  vacío tampoco deja rastro, y un cuerpo sin marcador se añade al final. Los marcadores
  desconocidos se conservan tal cual.
- **Robustez:** la nota generada se sella como cualquier otra (ADR-0004): si la plantilla no
  rellenó el `id` o no tiene frontmatter, se genera. El tipo de una plantilla propia es el que
  declare su `type:` una vez rellenada. Las plantillas se leen con la misma decodificación
  estricta que las notas, se escriben de forma atómica, se limitan a 64 KB y nunca se escriben a
  través de un `.devnotes` enlazado (ADR-0006).
- **Edición:** un diálogo en la app lista integradas, personalizadas y propias, con editor de
  texto, guardar, restaurar la integrada (o eliminar la propia) y crear nuevas. Cambiar de
  plantilla con cambios sin guardar pide confirmación.

## Consecuencias

- Cada bóveda tiene sus plantillas; no hay plantillas globales de la app (se pueden copiar el
  archivo).
- Sin dependencias nuevas: la sustitución es un recorrido de líneas, sin motor de plantillas.
- Las plantillas que dependen de fechas relativas, listas de etiquetas o lógica condicional
  quedan fuera de alcance.
