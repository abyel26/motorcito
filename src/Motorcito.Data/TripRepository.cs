using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

public sealed class TripRepository
{
    private readonly MotorcitoDatabase _db;

    public TripRepository(MotorcitoDatabase db) => _db = db;

    public void Insert(Trip trip)
    {
        using var command = _db.CreateCommand("""
            INSERT INTO trips (trip_id, vehicle_id, user_id, started_at, ended_at,
                               distance_km, duration_s, ambient_temp_c, start_altitude_m,
                               was_cold_start, synced)
            VALUES ($id, $vehicle, $user, $started, $ended,
                    $distance, $duration, $ambient, $altitude, $cold, $synced);
            """);
        Bind(command, trip);
        command.ExecuteNonQuery();
    }

    /// <summary>Closes a trip out with its computed totals.</summary>
    public void Complete(string tripId, DateTime endedAt, double? distanceKm, int durationS)
    {
        using var command = _db.CreateCommand("""
            UPDATE trips
            SET ended_at = $ended, distance_km = $distance, duration_s = $duration
            WHERE trip_id = $id;
            """);
        command.Parameters.AddWithValue("$id", tripId);
        command.Parameters.AddWithValue("$ended", Timestamps.ToDb(endedAt));
        command.Parameters.AddWithValue("$distance", (object?)distanceKm ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", durationS);
        command.ExecuteNonQuery();
    }

    public Trip? Get(string tripId)
    {
        using var command = _db.CreateCommand($"{SelectSql} WHERE trip_id = $id;");
        command.Parameters.AddWithValue("$id", tripId);
        using var r = command.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<Trip> ForVehicle(string vehicleId, int limit = 100)
    {
        using var command = _db.CreateCommand($"{SelectSql} WHERE vehicle_id = $v ORDER BY started_at DESC LIMIT $n;");
        command.Parameters.AddWithValue("$v", vehicleId);
        command.Parameters.AddWithValue("$n", limit);

        var list = new List<Trip>();
        using var r = command.ExecuteReader();
        while (r.Read())
            list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Trips that ended but were never uploaded. The sync queue drains this on
    /// WiFi; a trip is only marked synced after the server confirms.
    /// </summary>
    public List<Trip> Unsynced(int limit = 50)
    {
        using var command = _db.CreateCommand(
            $"{SelectSql} WHERE synced = 0 AND ended_at IS NOT NULL ORDER BY started_at LIMIT $n;");
        command.Parameters.AddWithValue("$n", limit);

        var list = new List<Trip>();
        using var r = command.ExecuteReader();
        while (r.Read())
            list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Closes trips left open by an unclean shutdown.
    ///
    /// A trip normally ends via disconnect or sustained zero RPM. Neither runs
    /// if iOS terminates the app mid-drive, or the phone dies, leaving
    /// <c>ended_at</c> NULL forever. The exposure grew when the zero-RPM
    /// timeout went to three minutes, so recovery is what keeps that safe.
    ///
    /// Recovers to the last sample actually recorded, which is the last moment
    /// the trip is known to have existed. Distance is recomputed from the
    /// stored samples rather than left NULL, so a recovered trip is complete
    /// rather than merely closed.
    ///
    /// Call once at startup, before recording begins.
    /// </summary>
    /// <returns>How many trips were closed.</returns>
    public int CloseOrphanedTrips(SampleRepository samples)
    {
        var orphans = new List<(string TripId, DateTime StartedAt)>();

        using (var command = _db.CreateCommand("SELECT trip_id, started_at FROM trips WHERE ended_at IS NULL;"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                orphans.Add((reader.GetString(0), Timestamps.FromDb(reader.GetString(1))));
        }

        foreach (var (tripId, startedAt) in orphans)
        {
            // A trip with no samples at all collapses to zero length rather
            // than being deleted: it is evidence the app connected, and
            // silently dropping rows hides bugs.
            var endedAt = samples.LastTimestampForTrip(tripId) ?? startedAt;
            var distance = DistanceCalculator.Total(samples.GetForTrip(tripId));
            var duration = (int)Math.Max(0, (endedAt - startedAt).TotalSeconds);

            Complete(tripId, endedAt, distance > 0 ? distance : null, duration);
        }

        return orphans.Count;
    }

    private const string SelectSql = """
        SELECT trip_id, vehicle_id, user_id, started_at, ended_at,
               distance_km, duration_s, ambient_temp_c, start_altitude_m,
               was_cold_start, synced
        FROM trips
        """;

    private static void Bind(SqliteCommand command, Trip t)
    {
        command.Parameters.AddWithValue("$id", t.TripId);
        command.Parameters.AddWithValue("$vehicle", t.VehicleId);
        command.Parameters.AddWithValue("$user", t.UserId);
        command.Parameters.AddWithValue("$started", Timestamps.ToDb(t.StartedAt));
        command.Parameters.AddWithValue("$ended", t.EndedAt is { } e ? Timestamps.ToDb(e) : DBNull.Value);
        command.Parameters.AddWithValue("$distance", (object?)t.DistanceKm ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)t.DurationS ?? DBNull.Value);
        command.Parameters.AddWithValue("$ambient", (object?)t.AmbientTempC ?? DBNull.Value);
        command.Parameters.AddWithValue("$altitude", (object?)t.StartAltitudeM ?? DBNull.Value);
        command.Parameters.AddWithValue("$cold", t.WasColdStart ? 1 : 0);
        command.Parameters.AddWithValue("$synced", t.Synced ? 1 : 0);
    }

    private static Trip Read(SqliteDataReader r) => new()
    {
        TripId = r.GetString(0),
        VehicleId = r.GetString(1),
        UserId = r.GetString(2),
        StartedAt = Timestamps.FromDb(r.GetString(3)),
        EndedAt = r.IsDBNull(4) ? null : Timestamps.FromDb(r.GetString(4)),
        DistanceKm = r.IsDBNull(5) ? null : r.GetDouble(5),
        DurationS = r.IsDBNull(6) ? null : r.GetInt32(6),
        AmbientTempC = r.IsDBNull(7) ? null : r.GetDouble(7),
        StartAltitudeM = r.IsDBNull(8) ? null : r.GetDouble(8),
        WasColdStart = !r.IsDBNull(9) && r.GetInt32(9) == 1,
        Synced = r.GetInt32(10) == 1,
    };
}
