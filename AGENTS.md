# Agent Instructions

Base oficial: develop

Frapper es una herramienta de línea de comandos en .NET para migraciones y comparación de esquemas de SQL Server.

## Testing

- **Excepción declarada (2026-11-18, aprobada por el mantenedor):** el trabajo en curso recuperado de respaldo (snapshot, migraciones y lector de esquema) entra a `develop` con cobertura parcial del diff. La deuda está enumerada en el issue de pruebas de este repo (TK-FRP). Es una excepción única: desde el siguiente cambio rige la regla normal.
- **Regla:** el 100% de las líneas de lógica que cambia un PR debe estar cubierto; la cobertura total no baja.
- **Ejecutor:** `dotnet test frapper.sln` (xUnit).
- **Limitación conocida:** `diff-cover` no mide bien los proyectos .NET en este entorno; la cobertura del diff se calcula con Cobertura XML (`--collect:"XPlat Code Coverage"`).
