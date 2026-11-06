namespace Frapper.Core;

public static class FrapperConstants
{
    public const string Provider = "SqlServer";
    public const string FormatVersion = "1.0";
    public const string HistoryTableName = "__FrapperMigrationsHistory";
    public const string ConfigFileName = "frapper.config.json";
    
    public static class Timeouts
    {
        public const int DefaultCommand = 30; // segundos
        public const int MigrationCommand = 300; // 5 minutos
        public const int LongRunningCommand = 600; // 10 minutos
    }
    
    public static class Limits
    {
        public const int MaxJsonSize = 50 * 1024 * 1024; // 50MB
        public const int MaxMigrationsPerBatch = 1000;
        public const int MaxIdentifierLength = 128; // SQL Server limit
        public const int MaxConfigFileSize = 1 * 1024 * 1024; // 1MB
        public const int MaxTables = 10000;
        public const int MaxColumnsPerTable = 1024;
        public const int MaxFileSizeBytes = 100 * 1024 * 1024; // 100MB
    }
}