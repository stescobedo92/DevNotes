# ADR-0002: Markdown como fuente de verdad e índice descartable fuera de la bóveda

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

Las notas deben ser versionables con Git, editables con cualquier editor y libres de lock-in. La
búsqueda instantánea necesita un índice, pero ese índice no puede convertirse en una segunda
fuente de verdad ni ensuciar la carpeta del usuario.

## Decisión

- Los archivos `.md` de la bóveda son la única fuente de verdad. La app nunca guarda en otro sitio
  información que no pueda reconstruirse a partir de ellos.
- El índice SQLite es una caché: una base de datos por bóveda en
  `<datos de la app>/indexes/<id-de-bóveda>.db`, **fuera de la bóveda**, para que los archivos WAL
  no se sincronicen ni se confirmen junto con las notas.
- El esquema se versiona con `PRAGMA user_version`. Como el índice es descartable no hay
  migraciones incrementales: una base con otra versión (o corrupta) se elimina y se reconstruye.
- "Reindexar todo" borra el índice y lo vuelve a generar desde los archivos.
- Dentro de la bóveda la app solo posee la carpeta `.devnotes/` (papelera), que se autoexcluye de
  Git con su propio `.gitignore`.

## Consecuencias

- Borrar la carpeta de datos de la app nunca pierde notas; solo obliga a reindexar.
- Las carpetas ocultas (`.git`, `.obsidian`, `.devnotes`…) no se recorren ni se indexan.
- Cambiar el esquema es barato (subir `IndexSchema.Version`), a costa de una reindexación completa
  en el siguiente arranque.
