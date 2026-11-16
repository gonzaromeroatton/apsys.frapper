using System.CommandLine;
using Frapper.Cli.Configuration;
using Frapper.Core.Snapshot;

namespace Frapper.Cli.Commands.Migrate;

internal static class MigrateAddCommand
{
    internal static readonly Argument<string> MigrationNameArgument =
        new("name")
        {
            Description = "Nombre de la migración."
        };

    internal static readonly Option<string?> SnapshotOption =
        new("--snapshot")
        {
            Description = "Ruta del snapshot deseado."
        };

    internal static readonly Option<string?> BaseSnapshotOption =
        new("--base-snapshot")
        {
            Description = "Ruta del snapshot base."
        };

    internal static readonly Option<string?> OutDirOption =
        new("--out-dir")
        {
            Description = "Directorio de salida de migraciones."
        };

    internal static readonly Option<bool> AllowDestructiveChangesOption =
        new("--allow-destructive-changes")
        {
            Description = "Permite generar operaciones destructivas."
        };

    public static Command Build(FrapperConfiguration configuration)
    {
        var command = new Command(
            name: "add",
            description: "Genera una nueva migración SQL a partir del diff entre snapshot base y snapshot deseado.");

        command.Arguments.Add(MigrationNameArgument);
        command.Add(SnapshotOption);
        command.Add(BaseSnapshotOption);
        command.Add(OutDirOption);
        command.Add(AllowDestructiveChangesOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var projectFile = await new FrapperProjectFileLoader().LoadOrThrowAsync();

            var migrationName = parseResult.GetValue(MigrationNameArgument) ?? string.Empty;
            var snapshotPath = parseResult.GetValue(SnapshotOption) ?? projectFile.Snapshot;
            var baseSnapshotPath = parseResult.GetValue(BaseSnapshotOption) ?? projectFile.BaseSnapshot;
            var outDir = parseResult.GetValue(OutDirOption) ?? projectFile.MigrationsPath;
            var allowDestructiveChanges = parseResult.GetValue(AllowDestructiveChangesOption);

            var handler = new MigrateAddHandler(new SchemaSnapshotSerializer());

            return await handler.RunAsync(
                migrationName,
                snapshotPath,
                baseSnapshotPath,
                outDir,
                allowDestructiveChanges,
                cancellationToken);
        });

        return command;
    }
}