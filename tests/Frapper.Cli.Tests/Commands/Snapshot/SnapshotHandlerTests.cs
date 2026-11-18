using Microsoft.Extensions.Configuration;
using Frapper.Cli.Commands.Snapshot;
using Frapper.Cli.Configuration;
using Frapper.Core.Abstractions;
using Frapper.Core.Domain.Schema;
using Frapper.Core.Snapshot;
using Xunit;

namespace Frapper.Cli.Tests.Commands.Snapshot;

public sealed class SnapshotHandlerTests
{
    [Fact]
    public async Task RunAsync_ShouldWriteSnapshotAndBaseSnapshot()
    {
        var tempDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var outPath = Path.Combine(tempDirectory, "schema.snapshot.json");
            var baseOutPath = Path.Combine(tempDirectory, "schema.snapshot.base.json");

            var configuration = BuildConfiguration(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Server=test;Database=Frapper;Trusted_Connection=True;"
            });

            var schemaReader = new FakeDatabaseSchemaReader(BuildSchema());
            var serializer = new SchemaSnapshotSerializer();

            var handler = new SnapshotHandler(configuration, schemaReader, serializer);

            var exitCode = await handler.RunAsync("Default", outPath, baseOutPath, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outPath));
            Assert.True(File.Exists(baseOutPath));

            var outJson = await File.ReadAllTextAsync(outPath);
            var baseJson = await File.ReadAllTextAsync(baseOutPath);

            Assert.Equal(outJson, baseJson);
            Assert.Contains(@"""databaseEngine"": ""SqlServer""", outJson, StringComparison.Ordinal);
            Assert.Contains(@"""name"": ""Orders""", outJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_ShouldReturnOne_WhenConnectionCannotBeResolved()
    {
        var tempDirectory = Path.Combine(Directory.GetCurrentDirectory(), "tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var outPath = Path.Combine(tempDirectory, "schema.snapshot.json");
            var baseOutPath = Path.Combine(tempDirectory, "schema.snapshot.base.json");

            var configuration = BuildConfiguration(new Dictionary<string, string?>());
            var schemaReader = new FakeDatabaseSchemaReader(BuildSchema());
            var serializer = new SchemaSnapshotSerializer();

            var handler = new SnapshotHandler(configuration, schemaReader, serializer);

            var exitCode = await handler.RunAsync("MissingConnection", outPath, baseOutPath, CancellationToken.None);

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(baseOutPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static FrapperConfiguration BuildConfiguration(Dictionary<string, string?> values)
    {
        var configurationRoot = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new FrapperConfiguration(configurationRoot);
    }

    private static DatabaseSchema BuildSchema()
    {
        return new DatabaseSchema(
            "SqlServer",
            new[]
            {
                new DbTable(
                    "dbo",
                    "Orders",
                    new[]
                    {
                        new DbColumn(1, "Id", new SqlType("int"), false, true, null),
                        new DbColumn(2, "Status", new SqlType("nvarchar", 20), false, false, "'Pending'")
                    },
                    new DbPrimaryKey("PK_Orders", new[] { "Id" }, true))
            },
            "1.0");
    }

    private sealed class FakeDatabaseSchemaReader : IDatabaseSchemaReader
    {
        private readonly DatabaseSchema _schema;

        public FakeDatabaseSchemaReader(DatabaseSchema schema)
        {
            _schema = schema;
        }

        public Task<DatabaseSchema> ReadAsync(string connectionString, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_schema);
        }
    }
}