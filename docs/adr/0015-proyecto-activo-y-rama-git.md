# ADR-0015: Detección del proyecto activo y rama Git sin LibGit2Sharp

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La Fase 2 pide detectar el proyecto activo "según el repositorio Git abierto o la carpeta
indicada" para prefiltrar las notas, y la barra de estado del diseño muestra la rama Git.
LibGit2Sharp está previsto para la Fase 4 (mensajes y diffs de commits); cargar una biblioteca
nativa en el arranque solo para leer una rama no está justificado.

## Decisión

- **Localizar el repositorio** leyendo el sistema de archivos: se sube desde la carpeta de
  partida hasta encontrar `.git` (carpeta) o el archivo `.git` con `gitdir:` de un worktree o
  submódulo, y se lee `HEAD` (`ref: refs/heads/<rama>` o el hash de un HEAD separado, mostrado
  corto). Nada de esto lanza excepciones: lo ilegible es "sin repositorio".
- **Carpeta de partida** (`LaunchContext`): `--project-dir <ruta>` en la línea de comandos, la
  variable `DEVNOTES_PROJECT_DIR` o, si no, el directorio de trabajo con el que se lanzó la app
  (salvo que sea su propia carpeta, lo que ocurre al abrirla desde un acceso directo). Así
  `devnotes` desde el terminal de un repositorio, o un acceso directo por proyecto con
  `--project-dir`, prefiltra sus notas.
- **Proyecto activo:** el proyecto cuya carpeta de repositorio configurada en Ajustes contiene
  la raíz encontrada; si no hay ninguno, el proyecto que se llame como la carpeta del repositorio
  (sin distinguir mayúsculas ni acentos), siempre que exista en la bóveda. Se muestra en la barra
  de estado, un clic lo activa o desactiva como filtro de proyecto, se prefija en la captura
  rápida y en las notas nuevas. Se recalcula al cambiar las notas o los ajustes.
- **Rama de la bóveda:** si la carpeta de la bóveda está dentro de un repositorio, la barra de
  estado muestra su rama. Se relee en cada cambio de notas; no hay un watcher de `.git`.

## Consecuencias

- Sin dependencia nativa nueva y sin coste de arranque medible (dos lecturas de archivos
  diminutos).
- El "repositorio abierto" es el de la carpeta con la que se lanzó la app, no el del IDE que esté
  en primer plano: detectar la ventana activa de otras aplicaciones no es portable ni respetuoso
  con la privacidad.
- Un cambio de rama con la app abierta se refleja tras el siguiente cambio de notas o al reabrir
  la bóveda.
