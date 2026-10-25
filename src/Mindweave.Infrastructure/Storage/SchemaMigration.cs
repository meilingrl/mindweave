namespace Mindweave.Infrastructure.Storage;

public sealed record SchemaMigration(int Version, string Sql);
