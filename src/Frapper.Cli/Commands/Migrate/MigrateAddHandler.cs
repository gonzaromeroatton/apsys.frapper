using Frapper.Core.Domain.Diff;
using Frapper.Core.Snapshot;
using Frapper.EFMigrationEmitter;
using System.Security;
using System.Globalization;

namespace Frapper.Cli.Commands.Migrate;

internal sealed class MigrateAddHandler
{
    private readonly ISchemaSnapshotSerializer _serializer;

    public MigrateAddHandler(ISchemaSnapshotSerializer serializer)
    {
        _serializer = serializer;
    }

    public async Task<int> RunAsync(
        string migrationName,
        string snapshotPath,
        string baseSnapshotPath,
        string outDir,
        bool allowDestructiveChanges,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(migrationName))
        {
            Console.Error.WriteLine("Error: migrationName es requerido.");
            return 2;
        }

        // Validar longitud
        if (migrationName.Length > 200)
        {
            Console.Error.WriteLine("Error: migrationName es demasiado largo (máximo 200 caracteres).");
            return 2;
        }

        // Validar caracteres permitidos (solo alfanuméricos, guiones, guiones bajos)
        if (!System.Text.RegularExpressions.Regex.IsMatch(migrationName, @"^[a-zA-Z0-9_\-\s]+$"))
        {
            Console.Error.WriteLine("Error: migrationName solo puede contener letras, números, espacios, guiones y guiones bajos.");
            return 2;
        }

        // Validar que no sea palabra reservada de Windows
        var reservedNames = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", 
                                     "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", 
                                     "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        if (reservedNames.Contains(migrationName.ToUpperInvariant()))
        {
            Console.Error.WriteLine($"Error: '{migrationName}' es un nombre reservado del sistema.");
            return 2;
        }

        // VALIDAR PATHS
        snapshotPath = ValidateAndNormalizePath(snapshotPath);
        baseSnapshotPath = ValidateAndNormalizePath(baseSnapshotPath);
        outDir = ValidateAndNormalizePath(outDir);

        const long MaxSnapshotFileSize = 50 * 1024 * 1024; // 50MB (de FrapperConstants)

        var snapshotFileInfo = new FileInfo(snapshotPath);
        if (snapshotFileInfo.Length > MaxSnapshotFileSize)
        {
            Console.Error.WriteLine(
                $"Error: El snapshot '{Path.GetFileName(snapshotPath)}' es demasiado grande " +
                $"({snapshotFileInfo.Length / (1024 * 1024)}MB). Máximo permitido: {MaxSnapshotFileSize / (1024 * 1024)}MB");
            return 2;
        }

        var baseSnapshotFileInfo = new FileInfo(baseSnapshotPath);
        if (baseSnapshotFileInfo.Length > MaxSnapshotFileSize)
        {
            Console.Error.WriteLine(
                $"Error: El snapshot base '{Path.GetFileName(baseSnapshotPath)}' es demasiado grande " +
                $"({baseSnapshotFileInfo.Length / (1024 * 1024)}MB). Máximo permitido: {MaxSnapshotFileSize / (1024 * 1024)}MB");
            return 2;
        }

        var desiredJson = await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        var baseJson = await File.ReadAllTextAsync(baseSnapshotPath, cancellationToken).ConfigureAwait(false);

        var desiredSchema = _serializer.Deserialize(desiredJson);
        var baseSchema = _serializer.Deserialize(baseJson);

        var differ = new SchemaDiffer();

        var plan = differ.Diff(
            baseSchema,
            desiredSchema,
            new DiffOptions(
                AllowDestructiveChanges: allowDestructiveChanges,
                StrictTypeMatching: true));

        if (!plan.Up.Any())
        {
            Console.WriteLine("No se detectaron cambios entre snapshot base y snapshot deseado.");
            return 0;
        }

        // Validar que outDir no sea un archivo existente
        if (File.Exists(outDir))
        {
            Console.Error.WriteLine($"Error: '{outDir}' es un archivo, no un directorio.");
            return 2;
        }

        try
        {
            if (!Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            
            // Verificar permisos de escritura
            var testFile = Path.Combine(outDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: No tiene permisos de escritura en '{outDir}'.");
            return 2;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Error: No se puede escribir en '{outDir}': {ex.Message}");
            return 2;
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"); // Agregar milisegundos
        var safeName = SanitizeName(migrationName);
        var fileName = $"{timestamp}_{safeName}.sql";

        // O alternativamente, agregar GUID:
        // var fileName = $"{timestamp}_{safeName}_{Guid.NewGuid():N}.sql";

        // O verificar unicidad:
        var outputPath = Path.Combine(outDir, fileName);
        var counter = 1;
        while (File.Exists(outputPath))
        {
            fileName = $"{timestamp}_{safeName}_{counter:D3}.sql";
            outputPath = Path.Combine(outDir, fileName);
            counter++;
        }
        
        // VALIDAR PATH FINAL
        outputPath = ValidateAndNormalizePath(outputPath);

        var sql = SqlMigrationEmitter.Emit(plan);

        // Crear backup antes de sobrescribir
        var backupPath = $"{baseSnapshotPath}.backup.{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Copy(baseSnapshotPath, backupPath, overwrite: false);

        try
        {
            await File.WriteAllTextAsync(outputPath, sql, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(baseSnapshotPath, desiredJson, cancellationToken).ConfigureAwait(false);
            
            // Si todo salió bien, eliminar backup
            File.Delete(backupPath);
        }
        catch
        {
            // En caso de error, restaurar backup
            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, baseSnapshotPath, overwrite: true);
                File.Delete(backupPath);
            }
            throw;
        }

        Console.WriteLine($"Migración generada: {outputPath}");
        Console.WriteLine($"Snapshot base actualizado: {baseSnapshotPath}");

        return 0;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Migration";

        // 1. Normalizar Unicode a forma canónica (NFD) y eliminar diacríticos
        var normalized = name.Normalize(System.Text.NormalizationForm.FormD);
        var withoutDiacritics = new string(
            normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                      .ToArray()
        ).Normalize(System.Text.NormalizationForm.FormC);

        // 2. Reemplazar espacios múltiples por uno solo
        var spacesNormalized = System.Text.RegularExpressions.Regex.Replace(
            withoutDiacritics, 
            @"\s+", 
            " "
        );

        // 3. Eliminar espacios al inicio/final
        var trimmed = spacesNormalized.Trim();

        // 4. Remover caracteres inválidos para nombres de archivo
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(trimmed.Where(c => !invalidChars.Contains(c)).ToArray());

        // 5. Reemplazar espacios por guiones bajos
        var withUnderscores = cleaned.Replace(' ', '_');

        // 6. Eliminar puntos (excepto extensión final que se agrega después)
        var withoutDots = withUnderscores.Replace('.', '_');

        // 7. Limitar longitud
        const int MaxLength = 100;
        if (withoutDots.Length > MaxLength)
            withoutDots = withoutDots.Substring(0, MaxLength);

        // 8. Eliminar guiones bajos al inicio/final
        var final = withoutDots.Trim('_');

        return string.IsNullOrWhiteSpace(final) ? "Migration" : final;
    }

    private static string ValidateAndNormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));

        try
        {
            // 1. Normalizar path completo
            var fullPath = Path.GetFullPath(path);
            
            // 2. Obtener directorio raíz del proyecto
            var currentDir = Directory.GetCurrentDirectory();
            var normalizedCurrentDir = Path.GetFullPath(currentDir);
            
            // 3. Asegurar que currentDir termina con separador
            if (!normalizedCurrentDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                normalizedCurrentDir += Path.DirectorySeparatorChar;
            
            // 4. Validar que fullPath está dentro de currentDir
            if (!fullPath.StartsWith(normalizedCurrentDir, StringComparison.Ordinal))
            {
                throw new SecurityException(
                    $"Path traversal detected. Path '{path}' resolves outside project directory.");
            }
            
            // 5. En sistemas que soportan symlinks, validar que no apuntan fuera
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var realPath = ResolvePath(fullPath);
                if (!realPath.StartsWith(normalizedCurrentDir, StringComparison.Ordinal))
                {
                    throw new SecurityException(
                        $"Symlink detected pointing outside project directory: {path}");
                }
            }
            
            return fullPath;
        }
        catch (SecurityException)
        {
            throw; // Re-lanzar excepciones de seguridad
        }
        catch (Exception ex)
        {
            throw new SecurityException($"Invalid path: {path}", ex);
        }
    }

    /// <summary>
    /// Resuelve symlinks en Linux/macOS.
    /// </summary>
    private static string ResolvePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path; // No existe aún, no hay symlink que resolver
        
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.FullName; // En Linux/macOS, FullName resuelve symlinks
        }
        catch
        {
            return path;
        }
    }
}