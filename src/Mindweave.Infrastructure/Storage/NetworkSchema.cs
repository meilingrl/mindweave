namespace Mindweave.Infrastructure.Storage;

internal static class NetworkSchema
{
    internal const string Sql = """
        CREATE TABLE node_layout (
            workspace_id TEXT NOT NULL,
            summary_node_id TEXT NOT NULL,
            x REAL NOT NULL CHECK(typeof(x) IN ('integer','real') AND x BETWEEN -1000000000 AND 1000000000),
            y REAL NOT NULL CHECK(typeof(y) IN ('integer','real') AND y BETWEEN -1000000000 AND 1000000000),
            PRIMARY KEY(workspace_id,summary_node_id),
            FOREIGN KEY(summary_node_id,workspace_id) REFERENCES summary_node(id,workspace_id) ON DELETE CASCADE
        );
        """;
}
