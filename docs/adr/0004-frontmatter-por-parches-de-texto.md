# ADR-0004: Frontmatter: lectura con YamlDotNet, escritura con parches de texto

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La app debe completar metadatos (`id`, `created`, `updated`, `title`) en el frontmatter YAML de
notas que el usuario también edita a mano. Deserializar y volver a serializar el YAML reordenaría
claves, eliminaría comentarios y cambiaría el estilo de comillas: un diff ruidoso en Git y, en el
peor caso, pérdida de contenido. Además, las notas pueden venir de repositorios clonados, así que
el YAML es entrada no confiable.

## Decisión

- **Lectura** con YamlDotNet (modelo de representación), tolerante: claves desconocidas se
  conservan, tipos inesperados se ignoran y un YAML inválido no impide abrir ni indexar la nota.
- **Lectura acotada.** El cargador de YamlDotNet construye el árbol de forma recursiva y no tiene
  límites propios: una nota con miles de secuencias anidadas (`- - - - …`, dos caracteres por
  nivel) desborda la pila, lo que no se puede capturar y tumbaría la app en cada arranque. El
  analizador se envuelve en un `IParser` que rechaza más de 32 niveles de anidamiento y más de 16
  alias (una cadena de alias usada como clave se resuelve en tiempo exponencial). El bloque
  tampoco puede superar 64 KB.
- **Escritura** mediante parches de texto línea a línea (`FrontmatterEditor`): solo se tocan las
  líneas de las claves que cambian; el resto del archivo, incluidos comentarios, orden, saltos de
  línea (LF/CRLF) y cuerpo, queda byte a byte como estaba.
- **Nunca se reescribe un bloque que no se entiende.** Se considera inutilizable, y por tanto no
  se sella ni se le antepone otro bloque, cuando:
  - no es YAML válido o no es un mapa de claves;
  - supera los límites anteriores;
  - está **sin cerrar**: la nota empieza por `---` seguido de una línea con forma `clave:` y no hay
    cierre. Es lo que ocurre cuando el autoguardado salta mientras el usuario aún teclea el
    frontmatter a mano; anteponer un bloque propio degradaría el suyo a texto para siempre. Una
    nota que empieza por una regla horizontal (`---` sin claves detrás) sí se trata como nota sin
    frontmatter.

  En esos casos el editor muestra un aviso con el motivo.
- Cada parche se verifica volviendo a analizar el resultado: si el frontmatter deja de ser válido
  o el cuerpo cambia, el parche se descarta y se guarda el texto del usuario sin tocar.
- Al guardar se sella `id` (si falta) y `updated` (fecha del día). `created` se escribe una sola
  vez, cuando la nota se crea desde la app.

## Consecuencias

- Los diffs de Git solo muestran lo que realmente cambió.
- El editor de frontmatter es código propio con su batería de tests (comentarios, CRLF, claves
  duplicadas, listas en bloque y en línea).
- Un frontmatter con más de 16 alias o 32 niveles se trata como inválido aunque sea YAML legal;
  en notas reales no ocurre.
- Markdig se usa para el cuerpo (títulos, esquema, vista previa); nunca para reescribir la nota.
