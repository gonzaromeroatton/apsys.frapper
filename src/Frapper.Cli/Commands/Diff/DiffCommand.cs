using System.CommandLine;
using Frapper.Cli.Configuration;
using Frapper.Core.Snapshot;
using Frapper.SqlServer;
using Frapper.SqlServer.Introspection;

namespace Frapper.Cli.Commands.Diff;

internal static class DiffCommand
{
    internal static readonly Option<string?> BaseOption =
        new("--base")
        {
            Description = "Ruta del snapshot base."
        };

    internal static readonly Option<string?> TargetOption =
        new("--target")
        {
            Description = "Ruta del snapshot objetivo."
        };

    internal static readonly Option<string?> ConnectionOption =
        new("--connection")
        {
            Description = "Connection string real, nombre lógico (ej: Default) o clave completa (ej: ConnectionStrings:Default)."
        };

    public static Command Build(FrapperConfiguration configuration)
    {
        var command = new Command(
            name: "diff",
            description: "Compara un snapshot base contra otro snapshot o contra la base de datos real.");

        command.Add(BaseOption);
        command.Add(TargetOption);
        command.Add(ConnectionOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var projectFile = await new FrapperProjectFileLoader().LoadOrThrowAsync();

            var basePath = parseResult.GetValue(BaseOption) ?? projectFile.BaseSnapshot;
            var targetPath = parseResult.GetValue(TargetOption);
            var rawConnection = parseResult.GetValue(ConnectionOption);

            var handler = new DiffHandler(
                configuration,
                new SqlServerSchemaReader(),
                new SchemaSnapshotSerializer());

            return await handler.RunAsync(
                basePath,
                targetPath,
                rawConnection,
                cancellationToken);
        });

        return command;
    }
}