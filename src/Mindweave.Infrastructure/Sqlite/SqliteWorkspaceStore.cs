using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

internal sealed class SqliteWorkspaceStore(SqliteDatabase database) : IWorkspaceStore
{
    public Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken) =>
        database.ReadAsync<IReadOnlyList<Workspace>>(
            connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, name, created_at FROM workspace ORDER BY created_at, rowid;";
                using var reader = command.ExecuteReader();
                var list = new List<Workspace>();
                while (reader.Read())
                {
                    list.Add(new Workspace(
                        WorkspaceId.Parse(reader.GetString(0)),
                        WorkspaceName.FromStored(reader.GetString(1)),
                        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
                }

                return list;
            },
            cancellationToken);

    public Task AddAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO workspace (id, name, created_at) VALUES ($id, $name, $created);";
                command.Parameters.AddWithValue("$id", workspace.Id.ToString());
                command.Parameters.AddWithValue("$name", workspace.Name.Value);
                command.Parameters.AddWithValue("$created", workspace.CreatedAt.ToUnixTimeMilliseconds());
                return command.ExecuteNonQuery();
            },
            cancellationToken);
    }
}
