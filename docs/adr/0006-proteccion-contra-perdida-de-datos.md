# ADR-0006: Protección contra pérdida de datos: escritura atómica, conflictos, codificación y papelera

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

"Sin pérdida de datos jamás" es un principio del producto. Las notas pueden cambiar fuera de la
app (VS Code, `git pull`, sincronización en la nube) mientras están abiertas, el proceso puede
morir en mitad de un guardado, el usuario puede borrar por error y los archivos pueden venir de
herramientas antiguas o de repositorios de terceros.

## Decisión

### Escritura

- **Escritura atómica:** todo archivo se escribe en un temporal oculto de la misma carpeta, se
  vuelca al dispositivo (`Flush(flushToDisk: true)`) y se renombra sobre el destino. Un lector, o
  un corte de corriente, ve el archivo antiguo o el nuevo, nunca uno a medias. El nombre del
  temporal es corto e independiente del de la nota (una nota con un nombre cercano al límite del
  sistema de archivos se puede seguir guardando). En Unix el temporal nace con permisos `0600` y
  recibe después los del archivo que reemplaza. El renombrado se reintenta durante unas milésimas
  cuando otro proceso tiene el archivo abierto (habitual en Windows con antivirus, indexadores y
  clientes de sincronización).
- **Límite de tamaño simétrico:** una nota de más de 16 MB no se lee ni se escribe. Si el límite
  solo se aplicara al leer, una nota guardada por encima de él no se podría volver a abrir.

### Conflictos

- **Por hash de contenido:** al abrir una nota se guarda el SHA-256 de sus bytes. Al guardar, si
  el archivo en disco ya no tiene ese hash (o ha desaparecido), **no se escribe** y el usuario
  decide: conservar su versión, cargar la del disco o guardar la suya como copia (con un `id`
  nuevo). Un cambio externo sobre una nota sin modificaciones locales se recarga sin preguntar.
- **Segunda comprobación justo antes de reemplazar.** Entre comparar el hash y renombrar el
  temporal hay una escritura con `fsync` (decenas de milisegundos en un disco lento), tiempo
  suficiente para que otro programa guarde la nota. Con el contenido nuevo ya en disco, y justo
  antes del renombrado, se vuelve a comparar tamaño y fecha de modificación con la versión que se
  leyó; si no coinciden, el guardado se abandona y se presenta como conflicto. La ventana pasa de
  decenas de milisegundos a microsegundos (no puede cerrarse del todo sin bloqueos que otros
  editores no respetarían).
- Tras recargar una nota desde disco, el editor descarta su historial de deshacer: deshacer
  devolvería un texto basado en una versión que ya no existe y el siguiente guardado pisaría el
  cambio externo sin conflicto.

### Codificación

- **Decodificación estricta.** Los bytes que no son Unicode válido nunca se sustituyen por
  U+FFFD: ese carácter se escribiría en el siguiente guardado y destruiría el original.
- Se detectan UTF-8 (con o sin BOM) y UTF-16 (con BOM). Un archivo que no es Unicode válido se
  lee como Windows-1252, donde cada byte es un carácter y viceversa: los bytes que no se editan
  sobreviven exactamente, sea cual sea la página de códigos real del archivo.
- **La nota se escribe en la codificación en que se leyó.** Solo si el texto ya no cabe en la
  página de códigos heredada (el usuario escribe un carácter que no existe en ella) la nota pasa
  a UTF-8, porque lo tecleado nunca se descarta. Las notas nuevas son UTF-8 sin BOM.

### Flujos de la interfaz

- **Autoguardado con debounce** y guardado forzado al cambiar de nota, de bóveda o al cerrar la
  ventana. Ningún flujo descarta un texto que no se pudo guardar sin que el usuario lo decida:
  cambiar o añadir una bóveda, abrir otra nota y eliminar se detienen con un aviso; cerrar la
  ventana pregunta antes de descartar.
- Mientras una nota se está cambiando, renombrando, moviendo o eliminando, o la app se está
  cerrando, el editor es de **solo lectura**: lo tecleado en ese instante se escribiría en el
  sitio equivocado o no lo guardaría nadie.
- **Renombrar y mover nunca sobrescriben** un archivo existente.

### Papelera

- **Papelera interna recuperable:** eliminar mueve la nota a `.devnotes/trash/<id>/` junto con un
  `entry.json` (ruta original y fecha). Restaurar la devuelve a su sitio, o a un nombre libre si
  el original se reutilizó. Solo "eliminar definitivamente" y "vaciar papelera" borran, y piden
  confirmación; en los diálogos destructivos el foco inicial está en "Cancelar".
- Una nota con cambios que no se pueden guardar **no se elimina**: la papelera recibiría la
  versión del disco y el texto del editor no podría restaurarse nunca.
- **La papelera debe ser una carpeta real de la bóveda.** Un repositorio clonado podría traer
  `.devnotes` o `.devnotes/trash` como enlace a cualquier carpeta del equipo, y vaciar la papelera
  borraría su contenido. Si cualquiera de las dos es un enlace, la papelera no se usa (ni para
  leer, ni para mover, ni para borrar); una entrada que sea un enlace se desenlaza, no se sigue.

### Contención

- Toda ruta se resuelve contra la raíz y se comprueba que queda dentro (también cuando la bóveda
  es la raíz de una unidad). Los enlaces simbólicos y las junctions no se siguen ni se tratan
  como notas, porque su destino puede estar en cualquier parte. Los reparse points que no son
  enlaces (por ejemplo los marcadores de OneDrive) sí se leen, para no ocultar bóvedas
  sincronizadas.
- Eliminar una bóveda de la lista **nunca** borra sus archivos.

## Consecuencias

- Guardar cuesta un `fsync`, un renombrado y una consulta de metadatos por archivo: despreciable
  para notas de texto.
- Un archivo en una codificación heredada distinta de Windows-1252 (Shift-JIS, por ejemplo) se
  ve con caracteres incorrectos en el editor, pero sus bytes no se alteran mientras no se editen.
- En sistemas de archivos con fechas de baja resolución (FAT: 2 s), la segunda comprobación no
  distingue un cambio externo del mismo tamaño hecho en el mismo intervalo; el hash lo detecta en
  el guardado siguiente.
- Un temporal huérfano tras un fallo empieza por punto, así que ni se indexa ni se muestra.
