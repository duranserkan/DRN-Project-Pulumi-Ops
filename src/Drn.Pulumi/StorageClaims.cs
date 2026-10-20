namespace Drn.Pulumi;

public sealed record StorageClaim(string Key, string Namespace, string ClaimName);

public static class StorageClaims
{
    public const int VolumeSizeInGbs = 50;
    public const int BootSizeInGbs = 64;
    public const string StorageClass = "drn-retained";

    // Chart/operator names are release contracts. Changing them requires explicit data migration.
    public static IReadOnlyList<StorageClaim> All { get; } = Array.AsReadOnly<StorageClaim>(
    [
        new("sample", "drn-project", "sample-data"),
        new("nexus", "drn-project", "nexus-data"),
        new("postgresql", "drn-project", "postgresql-1"),
        new("mongodb-data", "graylog", "data-volume-mongodb-0"),
        new("mongodb-logs", "graylog", "logs-volume-mongodb-0"),
        new("graylog", "graylog", "graylog-data-graylog-0"),
        new("datanode", "graylog", "data-graylog-datanode-0")
    ]);
}
