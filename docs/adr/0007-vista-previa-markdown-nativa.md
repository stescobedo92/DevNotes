# ADR-0007: Vista previa Markdown con controles nativos (sin HTML ni WebView)

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La vista previa en vivo debe ser segura frente a notas no confiables (repositorios clonados),
ligera al arrancar y capaz de ofrecer bloques de código con resaltado y botón de copiar. Se
evaluaron tres caminos:

1. **Markdig → HTML → WebView.** `NativeWebView` de Avalonia pertenece a la oferta comercial (XPF)
   y no cubre Linux embebido; obliga a sanear HTML y añade un motor de navegador al arranque.
2. **Bibliotecas de terceros** (Markdown.Avalonia, LiveMarkdown.Avalonia). En el momento de la
   decisión la primera no tenía versión estable para Avalonia 12 y la segunda arrastra versiones
   de TextMateSharp distintas de las de AvaloniaEdit y carga imágenes remotas por defecto.
3. **Renderizador propio** sobre el árbol de sintaxis de Markdig.

## Decisión

Renderizador propio (`MarkdownView`): recorre el AST de Markdig y construye controles de Avalonia
(`SelectableTextBlock`, `Grid` para tablas, los símbolos ☑ y ☐ para las listas de tareas).

- El pipeline usa `DisableHtml()`: el HTML en bruto se muestra como texto. No existe HTML
  intermedio, por lo que **no hay superficie de XSS que sanear** y nada se ejecuta.
- **Sin red:** las imágenes se representan por su texto alternativo; no se descarga nada.
- **Enlaces:** solo se abren `http`, `https` y `mailto` (`LinkPolicy`). `file:`, rutas UNC y
  protocolos personalizados se rechazan, porque podrían lanzar programas; el usuario recibe un
  aviso cuando un enlace se rechaza.
- **Código:** resaltado con TextMateSharp (las mismas gramáticas y temas que el editor
  AvaloniaEdit) para C#, C++, SQL, PowerShell, JSON, YAML y Bash, entre otros, con botón de
  copiar por bloque. El resaltado está acotado por tamaño y por tiempo (por línea y por bloque);
  lo que no se tokeniza a tiempo se muestra como texto plano, sin perder contenido.
- **Entrada patológica.** Miles de niveles de anidamiento caben en unos pocos kilobytes:
  - Markdig rechaza con una excepción lo que supera su límite de profundidad (cientos de
    `> > > …`, una tabla gigantesca). La vista previa muestra entonces la nota como texto plano y
    el esquema queda vacío; la nota se abre igual.
  - Lo que Markdig sí acepta puede ser un árbol de miles de niveles (`[[[[…`). El renderizador
    limita la profundidad de bloques (24) y de elementos en línea (48): más allá, el resto se
    muestra como su texto. El texto plano se extrae con un recorrido iterativo, sin recursión.
- **Sin trabajo mientras está oculta:** en modo "solo editor" la vista previa no analiza ni
  resalta en cada pulsación; se pone al día al volver a mostrarse.
- El editor es AvaloniaEdit con TextMate; `TextMateSharp.Grammars` se fija a la versión contra la
  que se compiló `AvaloniaEdit.TextMate`.

## Consecuencias

- Seguridad por construcción y arranque sin motor web.
- El subconjunto de Markdown soportado es el que implementa el renderizador (CommonMark, tablas,
  listas de tareas, tachado, autolinks). Las imágenes locales quedan para una fase posterior.
- El renderizador es código propio que mantener, con tests de interfaz headless (incluidos los de
  entrada patológica).
