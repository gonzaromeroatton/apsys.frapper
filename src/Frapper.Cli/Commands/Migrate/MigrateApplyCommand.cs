using Frapper.Cli.Configuration;
using Frapper.SqlServer.MigrationExecution;  // ✅ AGREGAR
using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace Frapper.Cli.Commands.Migrate;

/// <summary>
/// Define el comando CLI para aplicar migraciones SQL pendientes.
/// </summary>
internal static class MigrateApplyCommand
{
    internal static readonly Option<string?> ConnectionOption =
        new("--connection")
        {
            Description = "Connection string, nombre lógico o clave de configuración. " +
                         "Si se omite, se lee de frapper.config.json (campo 'connection')."
        };

    internal static readonly Option<string?> DirectoryOption =
        new("--dir")
        {
            Description = "Directorio que contiene los archivos .sql de migración. " +
                         "Si se omite, se usa el valor de frapper.config.json."
        };

    internal static readonly Option<int?> TimeoutOption =
        new("--timeout")
        {
            Description = "Timeout en segundos para cada migración (default: 300)."
        };

    internal static readonly Option<bool> DryRunOption =
        new("--dry-run")
        {
            Description = "Simula la aplicación sin ejecutar migraciones reales."
        };

    internal static readonly Option<bool> VerboseOption =
        new("--verbose", "-v")
        {
            Description = "Muestra información detallada de la ejecución."
        };

    /// <summary>
    /// Construye el comando 'migrate apply' para aplicar migraciones pendientes.
    /// </summary>
    /// <param name="configuration">Configuración de Frapper</param>
    /// <returns>
    /// Command que retorna:
    /// - 0: Éxito (todas las migraciones aplicadas correctamente)
    /// - 1: Error general (excepción durante ejecución)
    /// - 2: Error de validación (parámetros inválidos)
    /// - 130: Cancelado por usuario (SIGINT)
    /// </returns>
    public static Command Build(FrapperConfiguration configuration)
    {
        var command = new Command(
            name: "apply",
            description: "Aplica las migraciones SQL pendientes en orden secuencial sobre la base de datos.");

        command.Add(ConnectionOption);
        command.Add(DirectoryOption);
        command.Add(TimeoutOption);
        command.Add(DryRunOption);
        command.Add(VerboseOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            // Configurar logging
            var verbose = parseResult.GetValue(VerboseOption);
            var minLogLevel = verbose ? LogLevel.Debug : LogLevel.Information;

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(minLogLevel);
            });

            var commandLogger = loggerFactory.CreateLogger("MigrateApplyCommand");
            var migrationLogger = loggerFactory.CreateLogger<SqlServerMigrationRunner>();

            commandLogger.LogInformation("Iniciando comando 'migrate apply'");
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Cargar configuración del proyecto
                FrapperProjectFile projectFile;
                try
                {
                    projectFile = await new FrapperProjectFileLoader().LoadOrThrowAsync();
                }
                catch (FileNotFoundException)
                {
                    await Console.Error.WriteLineAsync(
                        "Error: No se encontró frapper.config.json. Ejecute 'frapper init' primero.")
                        .ConfigureAwait(false);
                    return 2;
                }
                catch (JsonException ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Error: frapper.config.json tiene formato inválido: {ex.Message}")
                        .ConfigureAwait(false);
                    return 2;
                }

                // 2. Resolver parámetros
                var rawConnection = parseResult.GetValue(ConnectionOption) ?? projectFile.Connection;
                var directory = parseResult.GetValue(DirectoryOption) ?? projectFile.MigrationsPath;
                var timeout = parseResult.GetValue(TimeoutOption) ?? 300;
                var dryRun = parseResult.GetValue(DryRunOption);

                // 3. Validar connection string
                if (string.IsNullOrWhiteSpace(rawConnection))
                {
                    await Console.Error.WriteLineAsync(
                        "Error: Connection string no especificado. Use --connection o configure frapper.config.json")
                        .ConfigureAwait(false);
                    return 2;
                }

                // 4. Validar y normalizar directorio
                if (string.IsNullOrWhiteSpace(directory))
                {
                    await Console.Error.WriteLineAsync(
                        "Error: Directorio de migraciones no especificado. Use --dir o configure frapper.config.json")
                        .ConfigureAwait(false);
                    return 2;
                }

                try
                {
                    directory = ValidateAndNormalizePath(directory);
                }
                catch (SecurityException ex)
                {
                    await Console.Error.WriteLineAsync(
                        "Error: Ruta de directorio inválida.")  // ✅ Genérico
                        .ConfigureAwait(false);
                    commandLogger.LogError(ex, "Path validation failed for directory: {Directory}", directory);  // ✅ Log detallado
                    return 2;
                }

                // 5 y 6. Validar directorio y archivos
                try
                {
                    if (!Directory.EnumerateFiles(directory, "*.sql").Take(1).Any())
                    {
                        Console.WriteLine($"No se encontraron archivos .sql en el directorio de migraciones.");
                        return 0;
                    }
                }
                catch (DirectoryNotFoundException)
                {
                    await Console.Error.WriteLineAsync(
                        "Error: El directorio de migraciones no existe.")
                        .ConfigureAwait(false);
                    return 2;
                }
                catch (UnauthorizedAccessException)
                {
                    await Console.Error.WriteLineAsync(
                        "Error: No tiene permisos para leer el directorio de migraciones.")
                        .ConfigureAwait(false);
                    return 2;
                }

                // 6.5. Validar límite de archivos
                const int maxMigrationFiles = 1000;
                var migrationCount = Directory.EnumerateFiles(directory, "*.sql").Take(maxMigrationFiles + 1).Count();

                if (migrationCount > maxMigrationFiles)  
                {
                    await Console.Error.WriteLineAsync(
                        $"Error: Demasiados archivos de migración ({migrationCount}). Máximo permitido: {maxMigrationFiles}")
                        .ConfigureAwait(false);
                    return 2;
                }

                if (migrationCount == 0)
                {
                    Console.WriteLine("No se encontraron archivos .sql en el directorio de migraciones.");
                    return 0;
                }

                commandLogger.LogInformation("Encontrados {MigrationCount} archivos de migración", migrationCount);

                // 7. Ejecutar handler
                var handler = new MigrateApplyHandler(
                    configuration,
                    new SqlServerMigrationRunner(migrationLogger));

                try
                {
                    var result = await handler.RunAsync(rawConnection, directory, cancellationToken)
                        .ConfigureAwait(false);

                    stopwatch.Stop();

                    // Validar que el código de retorno es válido
                    if (result is not (0 or 1 or 2 or 130))
                    {
                        commandLogger.LogWarning(
                            "Handler retornó código de salida inválido: {ExitCode}. Normalizando a 1.",
                            result);
                        result = 1; // Normalizar a error general
                    }

                    commandLogger.LogInformation(
                        "Comando 'migrate apply' completado en {ElapsedMs}ms con código {ExitCode}",
                        stopwatch.ElapsedMilliseconds,
                        result);

                    return result;
                }
                catch (OperationCanceledException)
                {
                    stopwatch.Stop();
                    commandLogger.LogWarning(
                        "Comando 'migrate apply' cancelado después de {ElapsedMs}ms",
                        stopwatch.ElapsedMilliseconds);
                    await Console.Error.WriteLineAsync("Operación cancelada.").ConfigureAwait(false);
                    return 130; // Exit code for SIGINT
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    commandLogger.LogError(
                        ex,
                        "Comando 'migrate apply' falló después de {ElapsedMs}ms",
                        stopwatch.ElapsedMilliseconds);
                    await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
                    return 1;
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                commandLogger.LogWarning(
                    "Comando 'migrate apply' cancelado después de {ElapsedMs}ms",
                    stopwatch.ElapsedMilliseconds);
                await Console.Error.WriteLineAsync("Operación cancelada.").ConfigureAwait(false);
                return 130; // Exit code for SIGINT
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                commandLogger.LogError(
                    ex,
                    "Comando 'migrate apply' falló después de {ElapsedMs}ms",
                    stopwatch.ElapsedMilliseconds);
                await Console.Error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Valida y normaliza un path para prevenir path traversal attacks.
    /// </summary>
    /// <param name="path">Path a validar</param>
    /// <returns>Path normalizado y validado</returns>
    /// <exception cref="SecurityException">Si se detecta path traversal</exception>
    private static string ValidateAndNormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));

        // Validar longitud (Windows tiene límite de 260 por defecto, 32767 con long paths)
        const int maxPathLength = 260;
        if (path.Length > maxPathLength)
        {
            throw new ArgumentException(
                $"Path too long ({path.Length} characters). Maximum: {maxPathLength}",
                nameof(path));
        }

        try
        {
            // 1. Normalizar path completo
            var fullPath = Path.GetFullPath(path);

            // 2. Obtener directorio raíz normalizado
            var currentDir = Directory.GetCurrentDirectory();
            var normalizedCurrentDir = Path.GetFullPath(currentDir);

            // 3. Asegurar trailing separator
            if (!normalizedCurrentDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                normalizedCurrentDir += Path.DirectorySeparatorChar;

            // 4. Validar que está dentro del directorio actual
            if (!fullPath.StartsWith(normalizedCurrentDir, StringComparison.Ordinal))
            {
                throw new SecurityException(
                    "Path traversal detected. Directory must be within project directory.");
            }

            // 5. Validar symlinks en Linux/macOS
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var pathToCheck = fullPath;
                
                // Si el path no existe, validar el directorio padre
                if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
                {
                    var parentDir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                    {
                        pathToCheck = parentDir;
                    }
                }
                
                if (Directory.Exists(pathToCheck) || File.Exists(pathToCheck))
                {
                    var realPath = new DirectoryInfo(pathToCheck).FullName;
                    if (!realPath.StartsWith(normalizedCurrentDir, StringComparison.Ordinal))
                    {
                        throw new SecurityException(
                            "Symlink detected pointing outside project directory.");
                    }
                }
            }

            return fullPath;
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SecurityException("Invalid path.", ex);
        }
    }
}