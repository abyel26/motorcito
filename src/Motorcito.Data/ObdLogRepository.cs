using Microsoft.Data.Sqlite;
using Motorcito.Obd;

namespace Motorcito.Data;

/// <summary>One recorded adapter exchange, as stored.</summary>
public sealed record ObdLogEntry
{
    public long LogId { get; init; }
    public required string SessionId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string Command { get; init; }
    public string? RawResponse { get; init; }
    public string? Status { get; init; }
    public int? DurationMs { get; init; }
    public string? Error { get; init; }
    public string? VehicleId { get; init; }
}

/// <summary>
/// Stores raw adapter exchanges, and is the <see cref="IObdLogSink"/> the
/// decorator writes to.
///
/// Buffered in memory and written in batches for the same reason samples are:
/// this is called from the poll loop, and a synchronous insert per command
/// would put a disk write between every read.
/// </summary>
public sealed class ObdLogRepository : IObdLogSink
{
    private readonly MotorcitoDatabase _db;
    private readonly List<(ObdExchange Exchange, string? VehicleId)> _pending = [];
    private readonly object _lock = new();
    private readonly int _batchSize;

    /// <summary>
    /// Session the current exchanges belong to. Set by whoever owns the
    /// decorator, so rows can be read back one connection at a time.
    /// </summary>
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Vehicle these exchanges belong to, once known. Null during init and the
    /// capability scan, which happen before the car has been identified.
    /// </summary>
    public string? VehicleId { get; set; }

    public string UserId { get; }

    public ObdLogRepository(MotorcitoDatabase db, string userId, int batchSize = 50)
    {
        _db = db;
        UserId = userId;
        _batchSize = batchSize;
    }

    /// <inheritdoc />
    public void Record(ObdExchange exchange)
    {
        lock (_lock)
        {
            _pending.Add((exchange, VehicleId));
            if (_pending.Count < _batchSize)
                return;
        }

        Flush();
    }

    /// <summary>Writes buffered exchanges. Safe to call at any time.</summary>
    public int Flush()
    {
        List<(ObdExchange Exchange, string? VehicleId)> batch;

        lock (_lock)
        {
            if (_pending.Count == 0)
                return 0;

            batch = [.. _pending];
            _pending.Clear();
        }

        using var transaction = _db.BeginTransaction();
        using var command = _db.CreateCommand("""
            INSERT INTO obd_log (user_id, vehicle_id, session_id, ts, command, raw_response, status, duration_ms, error)
            VALUES ($user, $vehicle, $session, $ts, $cmd, $raw, $status, $ms, $error);
            """);
        command.Transaction = transaction;

        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[] { "user", "vehicle", "session", "ts", "cmd", "raw", "status", "ms", "error" })
            p[name] = command.Parameters.Add($"${name}", SqliteType.Text);

        foreach (var (exchange, vehicleId) in batch)
        {
            p["user"].Value = UserId;
            p["vehicle"].Value = (object?)vehicleId ?? DBNull.Value;
            p["session"].Value = SessionId;
            p["ts"].Value = Timestamps.ToDb(exchange.TimestampUtc);
            p["cmd"].Value = exchange.Command;
            p["raw"].Value = (object?)exchange.RawResponse ?? DBNull.Value;
            p["status"].Value = (object?)exchange.Status ?? DBNull.Value;
            p["ms"].Value = exchange.DurationMs;
            p["error"].Value = (object?)exchange.Error ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return batch.Count;
    }

    public int Count()
    {
        using var command = _db.CreateCommand("SELECT COUNT(*) FROM obd_log;");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Exchanges for one command, newest first — "what did 0902 actually return?"</summary>
    public List<ObdLogEntry> ForCommand(string command, int limit = 50)
    {
        using var query = _db.CreateCommand(
            $"{SelectColumns} FROM obd_log WHERE command = $c ORDER BY ts DESC LIMIT $n;");
        query.Parameters.AddWithValue("$c", command);
        query.Parameters.AddWithValue("$n", limit);
        return Read(query);
    }

    /// <summary>Everything recorded, oldest first, for export.</summary>
    public List<ObdLogEntry> All(int limit = 100_000)
    {
        using var query = _db.CreateCommand($"{SelectColumns} FROM obd_log ORDER BY ts LIMIT $n;");
        query.Parameters.AddWithValue("$n", limit);
        return Read(query);
    }

    /// <summary>
    /// Removes all recorded exchanges.
    ///
    /// Worth offering separately from a full erase: these rows contain raw
    /// replies including the VIN, so a user may reasonably want them gone
    /// without discarding their driving history.
    /// </summary>
    public int Clear()
    {
        using var command = _db.CreateCommand("DELETE FROM obd_log;");
        return command.ExecuteNonQuery();
    }

    private const string SelectColumns = """
        SELECT log_id, session_id, ts, command, raw_response, status, duration_ms, error, vehicle_id
        """;

    private static List<ObdLogEntry> Read(SqliteCommand command)
    {
        var results = new List<ObdLogEntry>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(new ObdLogEntry
            {
                LogId = reader.GetInt64(0),
                SessionId = reader.GetString(1),
                Timestamp = Timestamps.FromDb(reader.GetString(2)),
                Command = reader.GetString(3),
                RawResponse = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = reader.IsDBNull(5) ? null : reader.GetString(5),
                DurationMs = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Error = reader.IsDBNull(7) ? null : reader.GetString(7),
                VehicleId = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return results;
    }
}
