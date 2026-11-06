using Frapper.Cli.Configuration;
using Frapper.Core.Abstractions;

namespace Frapper.Cli.Commands.Migrate;

internal sealed class MigrateApplyHandler
{
    private readonly FrapperConfiguration _configuration;
    private readonly IMigrationRunner _migrationRunner;

    public MigrateApplyHandler(
        FrapperConfiguration configuration,
        IMigrationRunner migrationRunner)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _migrationRunner = migrationRunner ?? throw new ArgumentNullException(nameof(migrationRunner));
    }

    public async Task<int> RunAsync(
        string rawConnection,
        string migrationsDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            await Console.Error.WriteLineAsync("Error: --connection es requerido.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(migrationsDirectory))
        {
            await Console.Error.WriteLineAsync("Error: --dir es requerido.");
            return 2;
        }

        if (!Directory.Exists(migrationsDirectory))
        {
            await Console.Error.WriteLineAsync($"Error: no existe el directorio de migraciones '{migrationsDirectory}'.");
            return 2;
        }

        try
        {
            var connectionString = _configuration.RequireConnectionString(rawConnection);

            await _migrationRunner.EnsureHistoryTableExistsAsync(connectionString, cancellationToken);

            var appliedMigrationIds = await _migrationRunner.GetAppliedMigrationIdsAsync(connectionString, cancellationToken);
            var appliedMigrationSet = appliedMigrationIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

            const int MaxMigrationsPerBatch = 1000;
            const long MaxSqlFileSize = 100 * 1024 * 1024; // 100MB

            var migrationFiles = Directory
                .EnumerateFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Take(MaxMigrationsPerBatch)
                .ToArray();

            if (migrationFiles.Length == MaxMigrationsPerBatch)
            {
                Console.WriteLine($"ADVERTENCIA: Se encontraron más de {MaxMigrationsPerBatch} migraciones. Solo se procesarán las primeras {MaxMigrationsPerBatch}.");
            }

            if (migrationFiles.Length == 0)
            {
                Console.WriteLine("No se encontraron migraciones SQL para aplicar.");
                return 0;
            }

            var pendingFiles = migrationFiles
                .Where(file => !appliedMigrationSet.Contains(Path.GetFileName(file)))
                .ToArray();

            if (pendingFiles.Length == 0)
            {
                Console.WriteLine("No hay migraciones pendientes.");
                return 0;
            }

            foreach (var file in pendingFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var fileInfo = new FileInfo(file);
                
                if (fileInfo.Length > MaxSqlFileSize)
                {
                    await Console.Error.WriteLineAsync(
                        $"Error: El archivo de migración '{Path.GetFileName(file)}' es demasiado grande ({fileInfo.Length / (1024 * 1024)}MB). Máximo permitido: {MaxSqlFileSize / (1024 * 1024)}MB");
                    return 1;
                }
                
                var migrationId = Path.GetFileName(file);
                var sql = await File.ReadAllTextAsync(file, cancellationToken);

                Console.WriteLine($"Aplicando migración: {migrationId}");

                await _migrationRunner.ApplyMigrationAsync(
                    connectionString,
                    migrationId,
                    sql,
                    cancellationToken);
            }

            Console.WriteLine($"Migraciones aplicadas correctamente: {pendingFiles.Length}");

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error: migrate apply failed.");
            return 1;
        }
    }
}