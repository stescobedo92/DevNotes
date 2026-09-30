# ADR-0011: Datos de la aplicación, privacidad y logging

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

Principios del producto: local-first, privacidad total (sin telemetría ni red salvo integraciones
opcionales explícitas), no almacenar secretos y logging estructurado sin contenido de las notas.

## Decisión

- **Ubicación de los datos de la app:** carpeta de datos locales del usuario
  (`%LOCALAPPDATA%\DevNotes`, `~/.local/share/DevNotes`, `~/Library/Application Support/DevNotes`
  según la plataforma), reubicable con la variable `DEVNOTES_DATA_DIR` (instalaciones portables y
  tests). Contiene `settings.json`, `indexes/` y `logs/`.
- **Ajustes** en JSON legible, escritos de forma atómica y serializados con generadores de código
  de `System.Text.Json`. Los valores se normalizan al cargar (el archivo puede editarse a mano).
  Un archivo ilegible se aparta como copia (`settings.unreadable-<fecha>.json`) y se arranca con
  los valores por defecto: un ajuste roto nunca impide abrir la app ni se borra nada del usuario.
  Primero se persiste y después se actualiza la memoria, para no afirmar un estado que no llegó
  al disco.
- **Sin red y sin telemetría:** ningún componente abre conexiones. La vista previa no descarga
  imágenes y los enlaces se delegan en el navegador del sistema solo para `http(s)` y `mailto`.
- **Sin secretos:** los ajustes no contienen credenciales; las integraciones futuras con tickets
  solo abrirán URL.
- **Logging:** proveedor propio mínimo a fichero (uno por día, siete días de retención, cola
  acotada escrita por una tarea en segundo plano). Los mensajes se definen con `LoggerMessage` y
  llevan rutas, contadores y excepciones, **nunca el contenido de las notas** ni el texto de las
  búsquedas. Un error de E/S pierde ese lote de líneas, no el log: el siguiente lote lo vuelve a
  intentar.
- **Errores:** los fallos esperados (E/S, permisos, entrada inválida, índice) se muestran como
  mensajes accionables y localizados; cualquier otra excepción se considera un defecto, se
  registra con detalle y no se oculta.

## Consecuencias

- Desinstalar o borrar la carpeta de datos no afecta a las notas.
- El log puede contener rutas de archivos del usuario; no sale del equipo.
- No se añade una dependencia de logging de terceros para una necesidad tan acotada.
