# ADR-0009: Interfaz: Avalonia 12, MVVM estricto, tokens de diseño e i18n con .resx

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La interfaz debe ser multiplataforma (Windows, Linux, macOS), usable al 100 % con teclado,
accesible, con tema claro/oscuro/sistema, escalable y preparada para español e inglés.

## Decisión

- **Avalonia 12.1** con tema Fluent y **bindings compilados** por defecto
  (`AvaloniaUseCompiledBindingsByDefault`): los errores de binding fallan en compilación.
- **MVVM estricto** con CommunityToolkit.Mvvm (generadores sobre propiedades `partial`). Los view
  models no referencian controles ni `Dispatcher`: el acceso a la plataforma pasa por interfaces
  (`IUiDispatcher`, `IFolderPicker`, `IClipboardService`, `ILinkOpener`, `IThemeService`,
  `IDialogService`), lo que permite probarlos sin interfaz.
- **Diálogos y overlays dentro de la ventana** (host de diálogos, apertura rápida, paleta de
  comandos) en lugar de ventanas modales del sistema: mismo comportamiento en las tres
  plataformas, foco controlado y testeable en modo headless.
- **Un único catálogo de comandos** (`AppCommand`) alimenta los atajos de teclado y la paleta de
  comandos. El modificador "primario" es Ctrl en Windows/Linux y Cmd en macOS (se resuelve con la
  configuración de la plataforma al abrir la ventana). Mientras hay un diálogo modal abierto los
  atajos de la ventana no actúan.
- **Tokens de diseño centralizados** (`Styles/Tokens.axaml`): colores por tema
  (`ThemeDictionaries`), espaciados en escala de 4 px, radios, tipografías y sombras. Las vistas
  solo usan `DynamicResource`; no hay valores literales en ellas. La paleta de Fluent se alinea
  con los mismos colores para que los controles estándar encajen.
- **Escala tipográfica** derivada de un tamaño base (14 px, `Ctrl +/-/0`) y **densidad**
  compacta/cómoda aplicadas en caliente por `ThemeService`.
- **Movimiento reducido:** las animaciones (≤150 ms) solo se activan con la clase `motion` de la
  ventana; se desactivan por ajuste explícito o, en Windows, siguiendo la preferencia del sistema
  (Avalonia no la expone, se consulta con `SystemParametersInfo`).
- **Accesibilidad:** `AutomationProperties.Name` en los controles interactivos, foco visible y
  regiones vivas para el estado del índice y las notificaciones.
- **Foco siempre en algún sitio:** al cerrar la apertura rápida o un diálogo el foco vuelve a
  donde estaba, y hay comandos con atajo para llevarlo al editor y al buscador de la lista
  (`Ctrl+Shift+F`); el editor consume `Tab`, así que sin ellos no se podría salir de él con el
  teclado.
- **Paneles visibles en cuanto hay bóvedas registradas,** aunque la bóveda activa no se pueda
  abrir (unidad desconectada): la barra lateral es donde se cambia de bóveda o se quita la
  averiada. La pantalla de bienvenida solo aparece cuando no hay ninguna bóveda.
- **Notificaciones:** los mensajes informativos desaparecen solos; un error permanece hasta que
  se descarta y un mensaje informativo posterior no lo tapa.
- **Internacionalización** con `.resx` (`Strings.resx` en inglés como neutro, `Strings.es.resx`);
  la clase fuertemente tipada se genera en compilación con MSBuild, sin depender del IDE. El
  idioma se aplica antes de crear la ventana.
- **Arranque:** host genérico vacío (`Host.CreateEmptyApplicationBuilder`) para DI, configuración
  y logging sin los proveedores por defecto que la app no necesita; la ventana se muestra antes
  de abrir la bóveda y la indexación continúa en segundo plano.

## Consecuencias

- Las listas usan virtualización y las consultas se versionan: un resultado obsoleto nunca pisa a
  uno más reciente, y el indicador de carga solo aparece si una consulta supera los 200 ms.
- En Linux y macOS la preferencia de movimiento reducido del sistema no se detecta todavía; el
  ajuste explícito sí funciona.
- Los comandos asíncronos ligados a atajos que solo persisten ajustes permiten ejecuciones
  concurrentes, para que una escritura lenta no se trague la siguiente pulsación.
- Las peticiones de abrir una nota pueden solaparse (mantener pulsada una flecha en la lista):
  solo la última carga su nota, termine en el orden que termine cada lectura.
