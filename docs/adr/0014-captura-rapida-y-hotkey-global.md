# ADR-0014: Captura rápida y atajo global multiplataforma

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La captura rápida debe abrirse con un atajo global (por defecto `Ctrl+Alt+N`, configurable)
desde cualquier aplicación, en una ventana pequeña siempre encima que guarda y se cierra con
`Ctrl+Enter`, y debe degradar con elegancia donde un atajo global no sea posible (Wayland). Un
atajo global toca la privacidad: la forma habitual de implementarlo es un gancho de teclado que
ve todas las pulsaciones del sistema.

## Decisión

### Registro del atajo, por plataforma

- **Windows:** `RegisterHotKey` contra la ventana principal, recibiendo `WM_HOTKEY` a través del
  `WndProc` de Avalonia (`Win32Properties.AddWndProcHookCallback`). No se instala ningún gancho de
  teclado: el sistema entrega un único mensaje cuando se pulsa la combinación y la app no ve
  ninguna otra tecla. Si otra aplicación ya tiene el atajo (`ERROR_HOTKEY_ALREADY_REGISTERED`) se
  informa en Ajustes.
- **macOS y Linux:** SharpHook 8.0 (libuiohook) con un gancho **solo de teclado**. El manejador
  compara cada pulsación con la combinación configurada y la descarta; no se almacena, registra
  ni reenvía nada. En macOS el gancho requiere el permiso de Accesibilidad: no se pide al
  arrancar, sino desde Ajustes con un botón explícito; hasta entonces el atajo se marca como no
  disponible. En Wayland el gancho necesita permisos sobre los dispositivos de entrada; si no los
  tiene, el fallo se reporta como "no disponible" con la explicación.
- **Degradación:** en todos los casos el mismo atajo funciona dentro de la app (`capture.quick`
  en la paleta y en las teclas de la ventana), y el estado real (activo, no disponible, fallido y
  por qué) se muestra en Ajustes. El atajo se puede desactivar.

Se evaluó implementar `RegisterEventHotKey` (Carbon) en macOS y `XGrabKey` en X11 para evitar el
gancho también allí; se pospone hasta poder probarlos en esas plataformas.

### La combinación

`HotkeyGesture` exige al menos `Ctrl`, `Alt` o `Win/Cmd` más una tecla (letra, cifra, F1–F24 o
unas pocas teclas con nombre): una letra sola o `Shift`+letra nunca se captura de todo el
sistema. Un valor inválido en `settings.json` vuelve al predeterminado sin fallar al arrancar.

### La ventana

`QuickCaptureWindow` es una ventana `Topmost`, sin entrada en la barra de tareas, con título,
texto y proyecto (prefijado con el proyecto activo, ADR-0015). `Ctrl+Enter` guarda con la
plantilla `note` de la bóveda; `Esc` o el botón de cerrar la ocultan **conservando el borrador**,
que solo se vacía cuando la nota está en disco. Sin título, la primera línea del texto pasa a ser
el título (y deja de repetirse en el cuerpo); sin nada que guardar, o sin bóveda abierta, se
explica y no se pierde el texto.

## Consecuencias

- Windows no depende de SharpHook en tiempo de ejecución, aunque el paquete se distribuye en
  todas las plataformas.
- El comportamiento en macOS y Linux está implementado según la documentación de SharpHook 8 y
  compilado en CI, pero **no se ha probado en esas plataformas** en esta fase.
- Un solo atajo global; combinaciones adicionales (por ejemplo, "mostrar DevNotes") quedan fuera.
