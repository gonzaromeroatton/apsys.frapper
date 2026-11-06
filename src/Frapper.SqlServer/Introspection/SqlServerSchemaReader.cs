using Microsoft.Data.SqlClient;
using Frapper.Core.Domain.Schema;
using Frapper.SqlServer.Internal;
using Frapper.SqlServer.Normalization;
using Frapper.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Frapper.Core;

namespace Frapper.SqlServer.Introspection;

/// <summary>
/// Lee el esquema de base de datos de SQL Server usando consultas al catálogo del sistema.
/// Implementa lectura optimizada con bajo consumo de memoria y límites de seguridad.
/// </summary>
public sealed class SqlServerSchemaReader : IDatabaseSchemaReader
{
    private readonly ILogger<SqlServerSchemaReader>? _logger;

    public SqlServerSchemaReader(ILogger<SqlServerSchemaReader>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Lee el esquema completo de la base de datos de forma asíncrona.
    /// </summary>
    /// <param name="connectionString">Connection string de SQL Server</param>
    /// <param name="ct">Token de cancelación</param>
    /// <returns>Esquema completo de la base de datos</returns>
    /// <exception cref="ArgumentException">Si connection string es inválido</exception>
    /// <exception cref="InvalidOperationException">Si se exceden límites de seguridad</exception>
    public async Task<DatabaseSchema> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string is required.", nameof(connectionString));

        _logger?.LogInformation("Iniciando lectura de esquema de base de datos");

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // 1) Leer tablas
        var tables = await ReadTablesAsync(conn, ct).ConfigureAwait(false);
        _logger?.LogDebug("Leídas {TableCount} tablas", tables.Count);

        // 2) Leer columnas agrupadas por ObjectId
        var columnsByObjectId = await ReadColumnsAsync(conn, ct).ConfigureAwait(false);
        _logger?.LogDebug("Leídas columnas para {TableCount} tablas", columnsByObjectId.Count);

        // 3) Leer primary keys agrupadas por ObjectId
        var pkByObjectId = await ReadPrimaryKeysAsync(conn, ct).ConfigureAwait(false);
        _logger?.LogDebug("Leídas {PKCount} primary keys", pkByObjectId.Count);

        // 4) Construir esquema determinístico
        var dbTables = BuildDatabaseTables(tables, columnsByObjectId, pkByObjectId);

        _logger?.LogInformation("Esquema leído exitosamente: {TableCount} tablas", dbTables.Count);

        return new DatabaseSchema(
            Provider: FrapperConstants.Provider,
            Tables: dbTables,
            FormatVersion: FrapperConstants.FormatVersion
        );
    }

    /// <summary>
    /// Lee la lista de tablas de usuario con límite de seguridad.
    /// </summary>
    private async Task<List<(int ObjectId, string Schema, string Name)>> ReadTablesAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        var tables = new List<(int ObjectId, string Schema, string Name)>();
        const int maxTables = FrapperConstants.Limits.MaxTables;

        await using var cmd = new SqlCommand(SysCatalogQueries.Tables, conn)
        {
            CommandTimeout = FrapperConstants.Timeouts.DefaultCommand
        };

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // Cachear ordinales para mejor rendimiento
        var objectIdOrdinal = reader.GetOrdinal("ObjectId");
        var schemaOrdinal = reader.GetOrdinal("SchemaName");
        var tableNameOrdinal = reader.GetOrdinal("TableName");

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (tables.Count >= maxTables)
            {
                _logger?.LogWarning(
                    "Se alcanzó el límite de {MaxTables} tablas. " +
                    "La base de datos contiene más tablas que el límite configurado. " +
                    "Algunas tablas no serán procesadas. " +
                    "Para aumentar este límite, modifique FrapperConstants.Limits.MaxTables.",
                    maxTables);
                break;
            }

            tables.Add((
                ObjectId: reader.GetInt32(objectIdOrdinal),
                Schema: reader.GetString(schemaOrdinal),
                Name: reader.GetString(tableNameOrdinal)
            ));
        }

        return tables;
    }

    /// <summary>
    /// Lee todas las columnas agrupadas por ObjectId con límite por tabla.
    /// </summary>
    private async Task<Dictionary<int, List<DbColumn>>> ReadColumnsAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        var columnsByObjectId = new Dictionary<int, List<DbColumn>>();
        const int maxColumnsPerTable = FrapperConstants.Limits.MaxColumnsPerTable;

        await using var cmd = new SqlCommand(SysCatalogQueries.Columns, conn)
        {
            CommandTimeout = FrapperConstants.Timeouts.DefaultCommand
        };

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // Cachear ordinales
        var objectIdOrdinal = reader.GetOrdinal("ObjectId");
        var columnIdOrdinal = reader.GetOrdinal("ColumnId");
        var nameOrdinal = reader.GetOrdinal("ColumnName");
        var typeNameOrdinal = reader.GetOrdinal("TypeName");
        var maxLengthOrdinal = reader.GetOrdinal("MaxLength");
        var precisionOrdinal = reader.GetOrdinal("Precision");
        var scaleOrdinal = reader.GetOrdinal("Scale");
        var isNullableOrdinal = reader.GetOrdinal("IsNullable");
        var isIdentityOrdinal = reader.GetOrdinal("IsIdentity");
        var defaultSqlOrdinal = reader.GetOrdinal("DefaultDefinition");

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var objectId = reader.GetInt32(objectIdOrdinal);

            if (!columnsByObjectId.TryGetValue(objectId, out var columnList))
            {
                columnList = new List<DbColumn>();
                columnsByObjectId[objectId] = columnList;
            }

            // Validar límite de columnas por tabla
            if (columnList.Count >= maxColumnsPerTable)
            {
                _logger?.LogWarning(
                    "Tabla con ObjectId {ObjectId} excede el límite de {MaxColumns} columnas. Columnas adicionales omitidas.",
                    objectId,
                    maxColumnsPerTable);
                continue;
            }

            var columnId = reader.GetInt32(columnIdOrdinal);
            var name = reader.GetString(nameOrdinal);
            var typeName = reader.GetString(typeNameOrdinal);
            var maxLength = reader.GetInt16(maxLengthOrdinal);
            var precision = reader.GetByte(precisionOrdinal);
            var scale = reader.GetByte(scaleOrdinal);
            var isNullable = reader.GetBoolean(isNullableOrdinal);
            var isIdentity = reader.GetBoolean(isIdentityOrdinal);
            var defaultSql = reader.IsDBNull(defaultSqlOrdinal)
                ? null
                : reader.GetString(defaultSqlOrdinal);

            var sqlType = SqlServerTypeNormalizer.Normalize(typeName, maxLength, precision, scale);

            columnList.Add(new DbColumn(
                ColumnId: columnId,
                Name: name,
                Type: sqlType,
                IsNullable: isNullable,
                IsIdentity: isIdentity,
                DefaultSql: defaultSql
            ));
        }

        return columnsByObjectId;
    }

    /// <summary>
    /// Lee las primary keys agrupadas por ObjectId.
    /// </summary>
    private async Task<Dictionary<int, PrimaryKeyAccumulator>> ReadPrimaryKeysAsync(
        SqlConnection conn,
        CancellationToken ct)
    {
        var pkByObjectId = new Dictionary<int, PrimaryKeyAccumulator>();

        await using var cmd = new SqlCommand(SysCatalogQueries.PrimaryKeys, conn)
        {
            CommandTimeout = FrapperConstants.Timeouts.DefaultCommand
        };

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // Cachear ordinales
        var objectIdOrdinal = reader.GetOrdinal("ObjectId");
        var pkNameOrdinal = reader.GetOrdinal("ConstraintName");
        var columnNameOrdinal = reader.GetOrdinal("ColumnName");
        var indexTypeDescOrdinal = reader.GetOrdinal("IndexTypeDesc");

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var objectId = reader.GetInt32(objectIdOrdinal);
            var pkName = reader.GetString(pkNameOrdinal);
            var columnName = reader.GetString(columnNameOrdinal);
            var indexTypeDesc = reader.GetString(indexTypeDescOrdinal);

            var isClustered = indexTypeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase);

            if (!pkByObjectId.TryGetValue(objectId, out var acc))
            {
                acc = new PrimaryKeyAccumulator(pkName, isClustered);
                pkByObjectId[objectId] = acc;
            }

            // Las columnas ya vienen ordenadas por key_ordinal en la query
            acc.Columns.Add(columnName);

            // Preservar clustered si aparece en cualquier fila
            acc.IsClustered |= isClustered;
        }

        return pkByObjectId;
    }

    /// <summary>
    /// Construye la lista final de tablas combinando datos de tablas, columnas y PKs.
    /// </summary>
    private List<DbTable> BuildDatabaseTables(
        List<(int ObjectId, string Schema, string Name)> tables,
        Dictionary<int, List<DbColumn>> columnsByObjectId,
        Dictionary<int, PrimaryKeyAccumulator> pkByObjectId)
    {
        var dbTables = new List<DbTable>(tables.Count);

        // Ordenar de forma determinística
        var orderedTables = tables
            .OrderBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var table in orderedTables)
        {
            // Obtener columnas (o lista vacía si no hay)
            if (!columnsByObjectId.TryGetValue(table.ObjectId, out var columns))
                columns = new List<DbColumn>();

            // Ordenar columnas por ColumnId (orden semántico correcto)
            var orderedColumns = columns.OrderBy(c => c.ColumnId).ToList();

            // Obtener PK si existe
            DbPrimaryKey? pk = null;
            if (pkByObjectId.TryGetValue(table.ObjectId, out var pkAcc))
            {
                pk = new DbPrimaryKey(
                    Name: pkAcc.Name,
                    Columns: pkAcc.Columns,
                    IsClustered: pkAcc.IsClustered
                );
            }

            dbTables.Add(new DbTable(
                Schema: table.Schema,
                Name: table.Name,
                Columns: orderedColumns,
                PrimaryKey: pk
            ));
        }

        return dbTables;
    }

    /// <summary>
    /// Acumulador para construir primary keys.
    /// </summary>
    private sealed class PrimaryKeyAccumulator
    {
        public PrimaryKeyAccumulator(string name, bool isClustered)
        {
            Name = name;
            IsClustered = isClustered;
        }

        public string Name { get; }
        public bool IsClustered { get; set; }
        public List<string> Columns { get; } = new();
    }
}