using System.Security;
using System.Text;
using System.Text.Json;
using Frapper.Core;
using Microsoft.Extensions.Logging;

namespace Frapper.Cli.Configuration;

/// <summary>
/// Carga y valida el archivo de configuración del proyecto Frapper.
/// </summary>
internal sealed class FrapperProjectFileLoader
{
    /// <summary>
    /// Nombre de archivo por defecto para la configuración.
    /// </summary>
    public const string DefaultFileName = FrapperConstants.ConfigFileName;

    private readonly ILogger<FrapperProjectFileLoader>? _logger;

    /// <summary>
    /// Opciones de serialización JSON reutilizables.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 64  // ✅ Prevenir stack overflow con JSON deeply nested
    };

    /// <summary>
    /// Inicializa una nueva instancia de <see cref="FrapperProjectFileLoader"/>.
    /// </summary>
    /// <param name="logger">Logger opcional para diagnósticos.</param>
    public FrapperProjectFileLoader(ILogger<FrapperProjectFileLoader>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Carga el archivo de configuración o lanza excepción si falla.
    /// </summary>
    /// <param name="path">Path opcional del archivo. Si es null, usa <see cref="DefaultFileName"/>.</param>
    /// <returns>Configuración del proyecto validada.</returns>
    /// <exception cref="FileNotFoundException">Si el archivo no existe.</exception>
    /// <exception cref="InvalidOperationException">Si el archivo es inválido o corrupto.</exception>
    /// <exception cref="System.Security.SecurityException">Si hay path traversal.</exception>
    public async Task<FrapperProjectFile> LoadOrThrowAsync(string? path = null)
    {
        _logger?.LogDebug("Iniciando carga de archivo de configuración");

        // 1. Resolver y validar path
        string filePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            filePath = DefaultFileName;
        }
        else
        {
            filePath = ValidateAndNormalizePath(path);
        }

        // 2. Obtener FileInfo (combina existencia + tamaño)
        var fileInfo = new FileInfo(filePath);

        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException(
                $"No se encontró el archivo de configuración: '{fileInfo.Name}'.");
        }

        // 3. Validar tamaño
        const long maxConfigFileSize = FrapperConstants.Limits.MaxConfigFileSize;
        if (fileInfo.Length > maxConfigFileSize)
        {
            throw new InvalidOperationException(
                $"El archivo de configuración es demasiado grande ({fileInfo.Length / 1024}KB). " +
                $"Máximo permitido: {maxConfigFileSize / 1024}KB");
        }

        _logger?.LogDebug("Leyendo archivo de configuración: {FileName} ({Size} bytes)", 
            fileInfo.Name, 
            fileInfo.Length);

        try
        {
            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);

            FrapperProjectFile? config;

            try
            {
                config = JsonSerializer.Deserialize<FrapperProjectFile>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"El archivo '{Path.GetFileName(filePath)}' contiene JSON inválido: {ex.Message}",
                    ex);
            }

            if (config is null)
            {
                throw new InvalidOperationException(
                    "No fue posible deserializar el archivo de configuración.");
            }

            // Validar contenido
            ValidateConfigContent(config);

            // Calcular ANTES del logging
            int migrationCount = 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(config.MigrationsPath) && 
                    Directory.Exists(config.MigrationsPath))
                {
                    migrationCount = Directory.EnumerateFiles(config.MigrationsPath, "*.sql")
                        .Take(1001)  // Limitar para evitar enumerar millones
                        .Count();
                }
            }
            catch (Exception ex)
            {
                // Log del error pero no fallar
                _logger?.LogWarning(ex, "No se pudo contar archivos de migración en {Path}", config.MigrationsPath);
            }

            // LUEGO hacer logging
            _logger?.LogDebug(  // ✅ Cambiar a Debug
                "Archivo de configuración cargado: Provider={Provider}",
                config.Provider);

            return config;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"No tiene permisos para leer el archivo de configuración.",
                ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Error al leer el archivo de configuración: {ex.Message}",
                ex);
        }
    }

    private static void ValidateConfigContent(FrapperProjectFile config)
    {
        var errors = new List<string>();

        // Validar Provider
        if (string.IsNullOrWhiteSpace(config.Provider))
            errors.Add("El campo 'provider' es requerido.");
        else if (!string.Equals(config.Provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
            errors.Add($"Provider '{config.Provider}' no soportado. Solo se soporta 'SqlServer'.");

        // Validar Connection
        if (string.IsNullOrWhiteSpace(config.Connection))
            errors.Add("El campo 'connection' es requerido.");
        else if (config.Connection.Length > 4096)  // Connection strings raramente exceden esto
            errors.Add($"Connection string demasiado largo ({config.Connection.Length} caracteres).");

        // Validar MigrationsPath
        if (string.IsNullOrWhiteSpace(config.MigrationsPath))
            errors.Add("El campo 'migrationsPath' es requerido.");
        else
        {
            try
            {
                // Validar que sea un path válido (no validamos que exista, solo sintaxis)
                var _ = Path.GetFullPath(config.MigrationsPath);
            }
            catch
            {
                errors.Add($"El campo 'migrationsPath' contiene un path inválido: '{config.MigrationsPath}'.");
            }
        }

        // Validar BaseSnapshot
        if (string.IsNullOrWhiteSpace(config.BaseSnapshot))
            errors.Add("El campo 'baseSnapshot' es requerido.");
        else if (!config.BaseSnapshot.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            errors.Add($"El campo 'baseSnapshot' debe ser un archivo .json: '{config.BaseSnapshot}'.");

        // Validar Snapshot
        if (string.IsNullOrWhiteSpace(config.Snapshot))
            errors.Add("El campo 'snapshot' es requerido.");
        else if (!config.Snapshot.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            errors.Add($"El campo 'snapshot' debe ser un archivo .json: '{config.Snapshot}'.");

        if (errors.Any())
        {
            throw new InvalidOperationException(
                "El archivo de configuración es inválido:\n" +
                string.Join("\n", errors.Select(e => $"  - {e}")));
        }
    }

    private static string ValidateAndNormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("El path no puede ser nulo o vacío.", nameof(path));

        try
        {
            // 1. Normalizar path
            var normalizedPath = Path.GetFullPath(path.Trim());

            // 2. Obtener directorio del proyecto
            var projectDir = Directory.GetCurrentDirectory();
            var normalizedProjectDir = Path.GetFullPath(projectDir);

            // 3. Asegurar trailing separator
            if (!normalizedProjectDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                normalizedProjectDir += Path.DirectorySeparatorChar;

            // 4. VALIDAR que el path está dentro del directorio del proyecto
            if (!normalizedPath.StartsWith(normalizedProjectDir, StringComparison.Ordinal))
            {
                throw new System.Security.SecurityException(
                    "Path traversal detected. Configuration file must be within project directory.");
            }

            // 5. Validar que no sea un path absoluto fuera del proyecto
            if (Path.IsPathRooted(path) && !normalizedPath.StartsWith(normalizedProjectDir, StringComparison.Ordinal))
            {
                throw new System.Security.SecurityException(
                    "Absolute paths outside project directory are not allowed.");
            }

            // 6. Validar extensión .json
            var extension = Path.GetExtension(normalizedPath);
            if (!string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Configuration file must be a .json file. Received: {extension}",
                    nameof(path));
            }

            // 7. Validar caracteres peligrosos en Windows
            if (OperatingSystem.IsWindows())
            {
                var invalidChars = new[] { '<', '>', ':', '"', '|', '?', '*' };
                var fileName = Path.GetFileName(normalizedPath);
                
                if (fileName.Any(c => invalidChars.Contains(c)))
                    throw new ArgumentException("Path contains invalid characters.", nameof(path));

                // Validar device names
                var deviceNames = new[] { "CON", "PRN", "AUX", "NUL", 
                                          "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                                          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };
                
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName).ToUpperInvariant().TrimEnd('.', ' ');
                if (deviceNames.Contains(fileNameWithoutExt))
                    throw new ArgumentException($"Path uses reserved device name: {fileName}", nameof(path));
            }

            // 8. Validar symlinks en Unix
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                if (File.Exists(normalizedPath) || Directory.Exists(normalizedPath))
                {
                    var realPath = new FileInfo(normalizedPath).FullName;
                    if (!realPath.StartsWith(normalizedProjectDir, StringComparison.Ordinal))
                    {
                        throw new System.Security.SecurityException(
                            "Symlink detected pointing outside project directory.");
                    }
                }
            }

            return normalizedPath;
        }
        catch (System.Security.SecurityException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new System.Security.SecurityException("Invalid configuration file path.", ex);
        }
    }
}