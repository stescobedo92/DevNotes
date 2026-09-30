# ADR-0005: Identificadores ULID generados por la app

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

Cada nota necesita un identificador estable que sobreviva a renombrados y movimientos (los
enlaces entre notas de la Fase 3 dependerán de él) y que se pueda escribir en el frontmatter.

## Decisión

- `id` es un ULID (26 caracteres Crockford base32): ordenable por fecha de creación, sin
  caracteres ambiguos y seguro para URL y nombres de archivo.
- Generador propio en el dominio (`Ulid`, ~100 líneas) en lugar de un paquete NuGet: 48 bits de
  marca de tiempo + 80 bits aleatorios de `RandomNumberGenerator`, monotónico dentro del mismo
  milisegundo. El dominio se mantiene sin dependencias externas.
- La app genera el `id` si falta y lo escribe la primera vez que guarda la nota. Hasta entonces
  (por ejemplo, notas importadas que nunca se han editado) la nota se indexa con un identificador
  determinista derivado de su ruta (`path-<hash>`), sin modificar el archivo.
- Un `id` escrito por otra herramienta se acepta si es un token seguro (letras, dígitos, `.`, `_`,
  `-`); no se exige que sea ULID.
- **Un `id` que la app no puede usar no se sustituye.** Si el frontmatter tiene una clave `id`
  con un valor que no es un token seguro (`id: "ADR 0001"`, `id: docs/intro`, una lista…), ese
  valor pertenece al usuario o a otra herramienta: se deja como está y la nota se identifica por
  su ruta.
- **Una nota conserva su identidad.** Si el texto que se guarda ha perdido el `id` (un deshacer
  que retrocede más allá del momento en que se selló, una línea borrada por error), se vuelve a
  escribir el que sigue en disco en lugar de generar uno nuevo.
- "Guardar una copia" asigna siempre un `id` nuevo para que dos archivos no compartan identidad.

## Consecuencias

- El identificador no depende de la ruta: renombrar o mover no rompe referencias.
- Si dos archivos declaran el mismo `id` (por ejemplo tras copiar un archivo fuera de la app), el
  primero conserva el identificador y el segundo se indexa con uno derivado de su ruta; el caso
  se registra en el log y está cubierto por tests. Para separarlos hay que cambiar el `id` de uno
  de ellos a mano: borrar la línea no basta, porque la app restaura el que había.
