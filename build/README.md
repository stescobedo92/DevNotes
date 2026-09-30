# build/

Scripts de empaquetado por plataforma (`.exe`, `.deb`, `.dmg`).

Todavía vacío a propósito: el empaquetado y la publicación de releases se implementan en una
fase posterior (ver la sección "Empaquetado y distribución" de la especificación). La Fase 0
solo incluye el CI mínimo (`.github/workflows/ci.yml`): restaurar, verificar formato, compilar
con analizadores y ejecutar los tests en Windows, Linux y macOS.
