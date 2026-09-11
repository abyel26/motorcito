using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

/// <summary>How many rows were removed from each table by an erasure.</summary>
public sealed record ErasureReport
{
    public int Samples { get; init; }
    public int Trips { get; init; }
    public int Events { get; init; }
    public int Baselines { get; init; }
    public int MaintenanceEntries { get; init; }
    public int SyncQueueEntries { get; init; }
    public int ObdLogEntries { get; init; }
    public int Vehicles { get; init; }

    public int Total => Samples + Trips + Events + Baselines
                      + MaintenanceEntries + SyncQueueEntries + ObdLogEntries + Vehicles;
}

/// <summary>
/// Right-to-erasure support: removes everything held about one user.
///
/// Built before there is a server, deliberately. Once data lives in two places
/// this becomes two implementations that must agree, and proving "nothing
/// survived" gets much harder. Writing it against a single database now means
/// the on-device half is already correct and tested when sync arrives.
///
/// Note this erases the *user's* data. Aggregate fleet statistics — the
/// anonymous tier described in docs/SCHEMA.md — are deliberately out of scope:
/// once a contribution has been aggregated beyond the point of singling anyone
/// out, it is no longer personal data, and erasure requests should not gut the
/// analysis model.
/// </summary>
public static class DataErasure
{
    /// <summary>
    /// Deletes every row belonging to <paramref name="userId"/>, in one
    /// transaction so a failure cannot leave a partial erasure behind.
    /// </summary>
    public static ErasureReport EraseUser(MotorcitoDatabase db, string userId)
    {
        using var transaction = db.BeginTransaction();

        // Samples carry vehicle_id but not user_id — they reach the user
        // through their trip. Both routes are checked so a sample orphaned by
        // an earlier bug cannot survive an erasure it should not have.
        var samples = Execute(db, transaction, """
            DELETE FROM samples
            WHERE trip_id IN (SELECT trip_id FROM trips WHERE user_id = $u)
               OR vehicle_id IN (SELECT vehicle_id FROM vehicles WHERE user_id = $u);
            """, userId);

        var trips = Execute(db, transaction, "DELETE FROM trips WHERE user_id = $u;", userId);
        var events = Execute(db, transaction, "DELETE FROM events WHERE user_id = $u;", userId);
        var baselines = Execute(db, transaction, "DELETE FROM baselines WHERE user_id = $u;", userId);
        var maintenance = Execute(db, transaction, "DELETE FROM maintenance_log WHERE user_id = $u;", userId);
        var queue = Execute(db, transaction, "DELETE FROM sync_queue WHERE user_id = $u;", userId);

        // Raw exchanges include the Mode 09 reply, which carries the VIN in
        // plaintext hex. These rows are personal data in their own right and
        // must not survive an erasure.
        var log = Execute(db, transaction, "DELETE FROM obd_log WHERE user_id = $u;", userId);

        // Vehicles last: samples and trips reference them, and foreign keys are
        // enforced, so removing them first would be rejected.
        var vehicles = Execute(db, transaction, "DELETE FROM vehicles WHERE user_id = $u;", userId);

        transaction.Commit();

        return new ErasureReport
        {
            Samples = samples,
            Trips = trips,
            Events = events,
            Baselines = baselines,
            MaintenanceEntries = maintenance,
            SyncQueueEntries = queue,
            ObdLogEntries = log,
            Vehicles = vehicles
        };
    }

    /// <summary>
    /// Counts rows still held for a user. Zero after <see cref="EraseUser"/>.
    ///
    /// Exists so erasure can be *verified* rather than assumed — by a test, and
    /// by the app itself if it ever needs to show a user that their data is
    /// gone.
    /// </summary>
    public static int CountRowsForUser(MotorcitoDatabase db, string userId)
    {
        var total = 0;

        foreach (var sql in new[]
                 {
                     "SELECT COUNT(*) FROM vehicles WHERE user_id = $u;",
                     "SELECT COUNT(*) FROM trips WHERE user_id = $u;",
                     "SELECT COUNT(*) FROM events WHERE user_id = $u;",
                     "SELECT COUNT(*) FROM baselines WHERE user_id = $u;",
                     "SELECT COUNT(*) FROM maintenance_log WHERE user_id = $u;",
                     "SELECT COUNT(*) FROM sync_queue WHERE user_id = $u;",
                     "SELECT COUNT(*) FROM obd_log WHERE user_id = $u;",
                     """
                     SELECT COUNT(*) FROM samples
                     WHERE trip_id IN (SELECT trip_id FROM trips WHERE user_id = $u)
                        OR vehicle_id IN (SELECT vehicle_id FROM vehicles WHERE user_id = $u);
                     """
                 })
        {
            using var command = db.CreateCommand(sql);
            command.Parameters.AddWithValue("$u", userId);
            total += Convert.ToInt32(command.ExecuteScalar());
        }

        return total;
    }

    private static int Execute(MotorcitoDatabase db, SqliteTransaction transaction, string sql, string userId)
    {
        using var command = db.CreateCommand(sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$u", userId);
        return command.ExecuteNonQuery();
    }
}
