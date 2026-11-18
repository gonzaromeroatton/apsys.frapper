using System.Text.Json;
using Frapper.Cli.Commands.Snapshot;
using Frapper.Cli.Configuration;
using Frapper.Core;

namespace Frapper.Cli.Commands.Init;

internal sealed class InitHandler
{
    private readonly FrapperConfiguration _configuration;
    private readonly SnapshotHandler _snapshotHandler;

    public InitHandler(
        FrapperConfiguration configuration,
        SnapshotHandler snapshotHandler)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _snapshotHandler = snapshotHandler ?? throw new ArgumentNullException(nameof(snapshotHandler));
    }

    public async Task<int> RunAsync(
        string rawConnection,
        string migrationsPath,
        string baseSnapshotPath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            await Console.Error.WriteLineAsync("Error: --connection es requerido.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(migrationsPath))
        {
            await Console.Error.WriteLineAsync("Error: --migrations-path es requerido.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(baseSnapshotPath))
        {
            await Console.Error.WriteLineAsync("Error: --base-snapshot es requerido.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            await Console.Error.WriteLineAsync("Error: --snapshot es requerido.");
            return 2;
        }

        try
        {
            _configuration.RequireConnectionString(rawConnection);

            Directory.CreateDirectory(migrationsPath);

            // Escribir config UNA SOLA VEZ con protección TOCTOU
            await WriteConfigFileAsync(
                rawConnection,
                migrationsPath,
                baseSnapshotPath,
                snapshotPath,
                cancellationToken);

            var snapshotResult = await _snapshotHandler.RunAsync(
                rawConnection,
                snapshotPath,
                baseSnapshotPath,
                cancellationToken);

            if (snapshotResult != 0)
            {
                await Console.Error.WriteLineAsync("Error: no fue posible generar los snapshots iniciales.");
                return snapshotResult;
            }

            await EnsureMigrationsReadmeAsync(migrationsPath, cancellationToken);

            Console.WriteLine("Proyecto Frapper inicializado correctamente.");
            Console.WriteLine($"Config: {Path.GetFullPath(FrapperConstants.ConfigFileName)}");
            Console.WriteLine($"Base snapshot: {Path.GetFullPath(baseSnapshotPath)}");
            Console.WriteLine($"Desired snapshot: {Path.GetFullPath(snapshotPath)}");
            Console.WriteLine($"Migrations: {Path.GetFullPath(migrationsPath)}");

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Error: initialization failed.");
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static async Task WriteConfigFileAsync(
        string rawConnection,
        string migrationsPath,
        string baseSnapshotPath,
        string snapshotPath,
        CancellationToken cancellationToken)
    {
        var config = new FrapperProjectConfig
        {
            Provider = "SqlServer",
            Connection = rawConnection,
            MigrationsPath = migrationsPath,
            BaseSnapshot = baseSnapshotPath,
            Snapshot = snapshotPath
        };

        var options = new JsonSerializerOptions { WriteIndented = true };

        try
        {
            await using var stream = new FileStream(
                FrapperConstants.ConfigFileName,
                FileMode.CreateNew,  // Falla si existe
                FileAccess.Write,
                FileShare.None);

            await JsonSerializer.SerializeAsync(stream, config, options, cancellationToken);
        }
        catch (IOException ex) when (ex.HResult == -2147024816) // ERROR_FILE_EXISTS
        {
            throw new InvalidOperationException($"Ya existe '{FrapperConstants.ConfigFileName}' en el directorio actual.", ex);
        }
    }

    private static async Task EnsureMigrationsReadmeAsync(
        string migrationsPath,
        CancellationToken cancellationToken)
    {
        var readmePath = Path.Combine(migrationsPath, "README.md");

        if (File.Exists(readmePath))
            return;

        var content =
            "# Migrations\r\n\r\n" +
            "Esta carpeta contiene las migraciones SQL generadas por Frapper.\r\n";

        await File.WriteAllTextAsync(readmePath, content, cancellationToken);
    }

    private sealed class FrapperProjectConfig
    {
        public string Provider { get; init; } = "SqlServer";
        public string Connection { get; init; } = string.Empty;
        public string MigrationsPath { get; init; } = "migrations";
        public string BaseSnapshot { get; init; } = "schema.snapshot.base.json";
        public string Snapshot { get; init; } = "schema.snapshot.json";
    }
}