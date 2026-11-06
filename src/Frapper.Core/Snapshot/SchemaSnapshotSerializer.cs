using System.Text.Json;
using System.Text.Json.Serialization;
using Frapper.Core.Domain.Schema;

namespace Frapper.Core.Snapshot;

public sealed class SchemaSnapshotSerializer : ISchemaSnapshotSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string Serialize(DatabaseSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var document = SchemaSnapshotMapper.ToDocument(schema);
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public DatabaseSchema Deserialize(string json)
    {
        const int MaxJsonSize = 50 * 1024 * 1024; // 50MB
        
        if (json.Length > MaxJsonSize)
            throw new ArgumentException($"JSON too large: {json.Length} bytes");
        
        SchemaSnapshotDocument? document;
        try 
        {
            document = JsonSerializer.Deserialize<SchemaSnapshotDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Invalid snapshot JSON format", ex);
        }
        
        if (document == null)
            throw new InvalidOperationException("Snapshot JSON is null");
            
        ValidateDocument(document);
        return SchemaSnapshotMapper.ToDomain(document);
    }

    private static void ValidateDocument(SchemaSnapshotDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.FormatVersion))
        {
            throw new NotSupportedException("Snapshot format version is required.");
        }

        if (!string.Equals(document.FormatVersion, "1.0", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Unsupported snapshot format version '{document.FormatVersion}'.");
        }

        if (!string.Equals(document.DatabaseEngine, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Unsupported database engine '{document.DatabaseEngine}'.");
        }
    }
}