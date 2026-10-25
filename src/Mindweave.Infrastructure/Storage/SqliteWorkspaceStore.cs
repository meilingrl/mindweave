using Mindweave.Application.Storage;
using Mindweave.Application.Navigation;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Runtime;
using Microsoft.Data.Sqlite;
using System.Threading.Channels;
using System.Globalization;

namespace Mindweave.Infrastructure.Storage;

public sealed class SqliteWorkspaceStore : IWorkspaceStore, IAsyncDisposable
{
    private static readonly SchemaMigration[] Migrations = [new(1, """
        CREATE TABLE workspace (
            id TEXT PRIMARY KEY NOT NULL,
            name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 160),
            created_at TEXT NOT NULL
        );
        CREATE TABLE app_selection (
            singleton INTEGER PRIMARY KEY CHECK(singleton=1),
            workspace_id TEXT NOT NULL REFERENCES workspace(id) ON DELETE CASCADE,
            area TEXT NOT NULL CHECK(area IN ('Explore','Network','Settings'))
        );
        """)];

    private readonly SqliteConnection writer;
    private readonly string readConnectionString;
    private readonly Channel<Action<SqliteConnection>> writes = Channel.CreateUnbounded<Action<SqliteConnection>>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task writeLoop;
    private int disposed;

    private SqliteWorkspaceStore(SqliteConnection writer, string databasePath)
    {
        this.writer = writer;
        readConnectionString = ConnectionString(databasePath, SqliteOpenMode.ReadOnly);
        writeLoop = Task.Run(async () =>
        {
            await foreach (var write in writes.Reader.ReadAllAsync()) write(writer);
        });
    }

    public static Task<SqliteWorkspaceStore> OpenAsync(AppPaths paths, IReadOnlyList<SchemaMigration>? migrations = null)
    {
        var steps = (migrations ?? Migrations).ToArray();
        if (steps.Length == 0 || steps.Where((step, index) => step.Version != index + 1).Any())
            throw new ArgumentException("Migrations must be sequential, starting at version 1.", nameof(migrations));
        // Microsoft.Data.Sqlite's async calls execute synchronously. Keep all database IO off the UI thread.
        return Task.Run(() =>
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var existed = File.Exists(paths.DatabasePath);
            var connection = new SqliteConnection(ConnectionString(paths.DatabasePath, SqliteOpenMode.ReadWriteCreate));
            try
            {
                connection.Open();
                var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
                if (version > steps.Length)
                    throw new InvalidOperationException($"数据库结构版本为 {version}，当前程序仅支持 {steps.Length}。请使用较新程序打开。");
                CheckIntegrity(connection);
                if (version < steps.Length)
                {
                    if (existed) Backup(connection, paths, version);
                    using var transaction = connection.BeginTransaction();
                    foreach (var step in steps.Where(step => step.Version > version))
                    {
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = step.Sql;
                        command.ExecuteNonQuery();
                    }
                    CheckIntegrity(connection, transaction);
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = $"PRAGMA user_version={steps.Length}";
                    update.ExecuteNonQuery();
                    transaction.Commit();
                }
                using var pragmas = connection.CreateCommand();
                pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
                pragmas.ExecuteNonQuery();
                return new SqliteWorkspaceStore(connection, paths.DatabasePath);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        });
    }

    public Task<IReadOnlyList<Workspace>> ListAsync() => ReadAsync<IReadOnlyList<Workspace>>(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,created_at FROM workspace ORDER BY created_at,id";
        using var reader = command.ExecuteReader();
        var result = new List<Workspace>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        return result;
    });

    public Task<Workspace> CreateAsync(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 160) return Task.FromException<Workspace>(new ArgumentException("主题名称需为 1–160 个字符。", nameof(name)));
        var workspace = new Workspace(Guid.NewGuid(), trimmed, DateTimeOffset.UtcNow);
        return WriteAsync((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO workspace(id,name,created_at) VALUES($id,$name,$created)";
            command.Parameters.AddWithValue("$id", workspace.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", workspace.Name);
            command.Parameters.AddWithValue("$created", workspace.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            SaveSelection(connection, transaction, new(workspace.Id, MainArea.Explore));
            return workspace;
        });
    }

    public Task<WorkspaceSelection?> LoadSelectionAsync() => ReadAsync(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT workspace_id,area FROM app_selection WHERE singleton=1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new WorkspaceSelection(Guid.Parse(reader.GetString(0)), Enum.Parse<MainArea>(reader.GetString(1))) : null;
    });

    public Task SaveSelectionAsync(WorkspaceSelection selection) => WriteAsync((connection, transaction) =>
    {
        if (!Enum.IsDefined(selection.Area)) throw new ArgumentException("Unknown main area.", nameof(selection));
        SaveSelection(connection, transaction, selection);
        return true;
    });

    private static void SaveSelection(SqliteConnection connection, SqliteTransaction transaction, WorkspaceSelection selection)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO app_selection VALUES(1,$workspace,$area) ON CONFLICT(singleton) DO UPDATE SET workspace_id=$workspace,area=$area";
        command.Parameters.AddWithValue("$workspace", selection.WorkspaceId.ToString("D"));
        command.Parameters.AddWithValue("$area", selection.Area.ToString());
        command.ExecuteNonQuery();
    }

    private Task<T> ReadAsync<T>(Func<SqliteConnection, T> read)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return Task.Run(() =>
        {
            using var connection = new SqliteConnection(readConnectionString);
            connection.Open();
            return read(connection);
        });
    }

    private Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!writes.Writer.TryWrite(connection =>
        {
            try
            {
                using var transaction = connection.BeginTransaction();
                var result = write(connection, transaction);
                transaction.Commit();
                completion.SetResult(result);
            }
            catch (Exception error) { completion.SetException(error); }
        })) completion.SetException(new ObjectDisposedException(nameof(SqliteWorkspaceStore)));
        return completion.Task;
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, ForeignKeys = true, Pooling = false, DefaultTimeout = 5
    }.ToString();

    private static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void CheckIntegrity(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        if (!Equals(Scalar(connection, "PRAGMA integrity_check", transaction), "ok") ||
            Scalar(connection, "PRAGMA foreign_key_check", transaction) is not null)
            throw new InvalidDataException("数据库完整性或关系检查失败。原库已保留，请勿删除。");
    }

    private static void Backup(SqliteConnection connection, AppPaths paths, int version)
    {
        Directory.CreateDirectory(paths.BackupDirectory);
        var final = Path.Combine(paths.BackupDirectory, $"before-v{version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        var temporary = final + ".partial";
        using (var backup = new SqliteConnection(ConnectionString(temporary, SqliteOpenMode.ReadWriteCreate)))
        {
            backup.Open();
            connection.BackupDatabase(backup);
            CheckIntegrity(backup);
        }
        File.Move(temporary, final);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        writes.Writer.TryComplete();
        await writeLoop.ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }
}
