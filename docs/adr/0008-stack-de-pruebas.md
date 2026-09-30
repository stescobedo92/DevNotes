# ADR-0008: Stack de pruebas: xUnit v3, FluentAssertions 7.x, NSubstitute y Avalonia.Headless

- **Estado:** Aceptada
- **Fecha:** 2026-09-30

## Contexto

La especificación pide xUnit, FluentAssertions, NSubstitute y Avalonia.Headless. Dos de esas
piezas tenían restricciones de versión y de licencia en el momento de elegirlas.

## Decisión

- **xUnit v3 3.2.x.** `Avalonia.Headless.XUnit` 12.x está compilado contra `xunit.v3` 3.2.x; la
  línea 4.x cambia la plataforma de ejecución subyacente, así que la versión de xUnit se fija y
  solo se sube junto con Avalonia.Headless.
- **FluentAssertions 7.2.x.** Es la última línea con licencia Apache-2.0; desde la 8.0 requiere
  licencia comercial para uso no personal. Se fija la 7.x en `Directory.Packages.props`. Si algún
  día hiciera falta una función posterior, la alternativa es AwesomeAssertions (fork Apache-2.0
  con la misma API).
- **NSubstitute** para dobles de puertos sencillos; para los puertos con estado (almacén de
  archivos, índice) se usan dobles en memoria escritos a mano, más fieles que un mock.
- **Avalonia.Headless** para los flujos críticos de interfaz (buscar, crear, editar, guardar)
  manejados solo con teclado simulado, sobre la composición real: archivos en disco temporal e
  índice SQLite.
- **`FakeTimeProvider`** (`Microsoft.Extensions.TimeProvider.Testing`) para debounce, autoguardado
  e indicadores con retardo: los tests no esperan tiempo real.
- Tests de integración contra SQLite real (en memoria y en disco) y contra el sistema de archivos.
- Cobertura con `coverlet.collector`; objetivo mínimo del 80 % en Domain y Application.

## Consecuencias

- Las versiones de xUnit y FluentAssertions no se actualizan de forma independiente (hay un
  comentario junto a cada una en `Directory.Packages.props`).
- Los tests de interfaz comparten un único hilo de UI headless: deben esperar a que termine el
  trabajo asíncrono que inician antes de acabar (ver `DesktopHarness.SettleAsync`).
