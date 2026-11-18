using Frapper.Core.Abstractions;
using Microsoft.Data.SqlClient;
using System.Data;

namespace Frapper.SqlServer.MigrationExecution;

public sealed class SqlServerMigrationRunner : IMigrationRunner
{
    public async Task EnsureHistoryTableExistsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
        IF OBJECT_ID(N'dbo.__FrapperMigrationsHistory', N'U') IS NULL
        BEGIN
            CREATE TABLE dbo.__FrapperMigrationsHistory
            (
                MigrationId NVARCHAR(255) NOT NULL PRIMARY KEY,
                AppliedUtc DATETIME2 NOT NULL
            );
        END
        """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var cmd = new SqlCommand(sql, connection)
        {
            CommandTimeout = 30 // segundos
        };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAppliedMigrationIdsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
        SELECT MigrationId
        FROM dbo.__FrapperMigrationsHistory
        ORDER BY MigrationId;
        """;

        var result = new List<string>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 30
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task ApplyMigrationAsync(
        string connectionString,
        string migrationId,
        string sql,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(migrationId))
            throw new ArgumentException("Migration id is required.", nameof(migrationId));

        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Migration SQL is required.", nameof(sql));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, 
            cancellationToken);

        try
        {
            await using (var applyCommand = new SqlCommand(sql, connection, (SqlTransaction)transaction))
            {
                applyCommand.CommandTimeout = 300; // 5 minutos para migraciones
                await applyCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string insertHistorySql = """
            INSERT INTO dbo.__FrapperMigrationsHistory (MigrationId, AppliedUtc)
            VALUES (@migrationId, SYSUTCDATETIME());
            """;

            await using (var historyCommand = new SqlCommand(insertHistorySql, connection, (SqlTransaction)transaction))
            {
                historyCommand.CommandTimeout = 30;
                historyCommand.Parameters.Add("@migrationId", SqlDbType.NVarChar, 255).Value = migrationId;
                await historyCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}