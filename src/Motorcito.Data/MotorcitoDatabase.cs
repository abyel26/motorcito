using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

/// <summary>
/// Owns the SQLite connection and applies migrations.
///
/// One long-lived connection rather than a pool: this is a single-writer,
/// on-device store, and SQLite's own locking is the coordination mechanism.
/// A pool would buy nothing and invite "database is locked" during a drive.
/// </summary>
public sealed class MotorcitoDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public string Path { get; }

    /// <summary>Schema version actually present after construction.</summary>
    public int SchemaVersion { get; }

    /// <param name="path">File path, or ":memory:" for a transient database in tests.</param>
    public MotorcitoDatabase(string path)
    {
        Path = path;

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = path == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            // Private cache deliberately. A shared cache would make every
            // ":memory:" database in the process the *same* database, so
            // concurrent tests would collide on each other's schema and rows.
            // Private is also correct in production: this class holds one
            // connection open for its lifetime, which is what keeps an
            // in-memory database alive.
            Cache = SqliteCacheMode.Private
        }.ToString());

        _connection.Open();

        using (var pragma = _connection.CreateCommand())
        {
            // WAL survives power loss mid-write far better than the default
            // journal, and a car being switched off is exactly that.
            // NORMAL synchronous is the accepted pairing: durable to crashes,
            // and it avoids an fsync on every batch.
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                """;
            pragma.ExecuteNonQuery();
        }

        SchemaVersion = Migrations.Apply(_connection);
    }

    public SqliteConnection Connection => _connection;

    public SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public SqliteTransaction BeginTransaction() => (SqliteTransaction)_connection.BeginTransaction();

    /// <summary>The conventional on-device location, alongside the app's other data.</summary>
    public static string DefaultPath(string appDataDirectory)
        => System.IO.Path.Combine(appDataDirectory, "motorcito.db");

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
