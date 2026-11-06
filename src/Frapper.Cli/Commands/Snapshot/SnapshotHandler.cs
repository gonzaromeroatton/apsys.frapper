using Frapper.Cli.Configuration;
using Frapper.Core.Abstractions;
using Frapper.Core.Snapshot;
using System.Security;

namespace Frapper.Cli.Commands.Snapshot;

internal sealed class SnapshotHandler
{
    private readonly FrapperConfiguration _configuration;
    private readonly IDatabaseSchemaReader _schemaReader;
    private readonly ISchemaSnapshotSerializer _serializer;

    public SnapshotHandler(
        FrapperConfiguration configuration,
        IDatabaseSchemaReader schemaReader,
        ISchemaSnapshotSerializer serializer)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _schemaReader = schemaReader ?? throw new ArgumentNullException(nameof(schemaReader));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public async Task<int> RunAsync(
        string rawConnection,
        string outPath,
        string? baseOutPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawConnection))
        {
            await Console.Error.WriteLineAsync("Connection is required.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            await Console.Error.WriteLineAsync("Output snapshot path is required.");
            return 1;
        }

        try
        {
            var connectionString = _configuration.RequireConnectionString(rawConnection);
            var schema = await _schemaReader.ReadAsync(connectionString, cancellationToken);
            var json = _serializer.Serialize(schema);

            outPath = ValidateAndNormalizePath(outPath);
            EnsureDirectoryExists(outPath);
            await File.WriteAllTextAsync(outPath, json, cancellationToken);

            if (!string.IsNullOrWhiteSpace(baseOutPath))
            {
                baseOutPath = ValidateAndNormalizePath(baseOutPath);
                EnsureDirectoryExists(baseOutPath);
                await File.WriteAllTextAsync(baseOutPath, json, cancellationToken);
            }

            Console.WriteLine("Snapshot generation completed successfully.");
            Console.WriteLine($"Provider: {schema.Provider}");
            Console.WriteLine($"FormatVersion: {schema.FormatVersion}");
            Console.WriteLine($"Tables: {schema.Tables.Count}");
            Console.WriteLine($"Snapshot: {Path.GetFullPath(outPath)}");

            if (!string.IsNullOrWhiteSpace(baseOutPath))
            {
                Console.WriteLine($"Base snapshot: {Path.GetFullPath(baseOutPath)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync("Snapshot generation failed.");
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    private static void EnsureDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ValidateAndNormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var currentDir = Directory.GetCurrentDirectory();

        if (!fullPath.StartsWith(currentDir, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException($"Path traversal detected: {path}");

        return fullPath;
    }
}