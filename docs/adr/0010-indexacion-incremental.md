# ADR-0010: Indexación incremental en segundo plano

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

El índice debe reflejar lo que hay en disco sin bloquear la interfaz, tolerar ediciones externas
y ráfagas de eventos (un `git checkout` toca cientos de archivos) y no reindexar lo que no cambió.

## Decisión

```
Carpeta de .md → FileSystemWatcher → debounce → cola de trabajos (un solo consumidor)
              → indexador incremental → SQLite/FTS5 → eventos → interfaz
```

- **Una sesión por bóveda abierta** (`VaultSession`) es dueña del almacén de archivos, el índice,
  el watcher y un único trabajador en segundo plano que consume una cola (`Channel`). Los trabajos
  no se solapan, así que el índice solo tiene un escritor.
- **Detección de cambios en dos niveles:** primero tamaño + fecha de modificación (barato, sin
  leer el archivo); si difieren, se lee y se compara el SHA-256 del contenido. Un archivo solo
  "tocado" actualiza su marca y no se vuelve a indexar.
- **Exploración inicial** al abrir la bóveda: lectura y análisis en paralelo (grado configurable)
  y escritura por lotes en una transacción por lote. Un lote se limita por número de notas y por
  bytes (32 MB), para que una bóveda con cientos de archivos grandes no se cargue de golpe.
- **Eventos del watcher agrupados** con un debounce (300 ms por defecto). Los lotes son
  *basados en estado*: un evento solo dice "mira esta ruta"; si la nota se indexa o se elimina
  lo decide lo que hay en disco en ese momento. Un evento de borrado atrasado no puede expulsar
  una nota que se restauró o reescribió entretanto.
- **Un archivo ilegible no oculta el resto del lote.** Lo habitual es que el programa que está
  guardando la nota aún la tenga abierta: ese archivo se vuelve a mirar tras el siguiente periodo
  de calma (tres intentos como máximo) y los demás cambios del lote se indexan y se notifican.
- **Desbordamiento del watcher** o demasiados cambios acumulados → exploración incremental
  completa en lugar de perder cambios. Tras un error que no sea un desbordamiento el watcher se
  reinicia (el del sistema se detiene), para no quedarse sin avisos durante el resto de la sesión.
  Si el watcher no está disponible la app sigue funcionando y los cambios externos se recogen en
  la siguiente exploración.
- **Las operaciones de la propia app** (crear, guardar, renombrar, mover, eliminar) actualizan el
  índice directamente, sin esperar al watcher.
- Las carpetas ocultas, los temporales y los archivos de más de 16 MB no se tratan como notas.

## Consecuencias

- La interfaz se entera por eventos (`NotesChanged`, `IndexStatusChanged`) que pueden llegar
  desde cualquier hilo: los view models los reenvían al hilo de UI. Un suscriptor que falla se
  registra en el log y no interrumpe ni al trabajador ni a los demás suscriptores.
- `WhenIdleAsync` permite esperar a que todo lo notificado esté indexado (tests y cierre).
- Los parámetros (debounce, tamaño de lote, paralelismo) se ajustan en `appsettings.json`.
