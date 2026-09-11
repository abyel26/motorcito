using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

/// <summary>
/// Writes and reads <c>samples</c>.
///
/// Inserts are batched inside a single transaction. At 1 Hz across a one-hour
/// drive that is 3,600 rows; committing each individually means 3,600 fsyncs
/// on a phone that is also driving a Bluetooth session. The batch is held to
/// batch at 100–500 rows, and <see cref="SampleWriter"/> enforces it.
/// </summary>
public sealed class SampleRepository
{
    private readonly MotorcitoDatabase _db;

    public SampleRepository(MotorcitoDatabase db) => _db = db;

    /// <summary>Column list shared by every read, in the order <see cref="Read"/> expects.</summary>
    private const string SelectColumns = """
        SELECT sample_id, trip_id, vehicle_id, ts,
               rpm, speed_kph, engine_load_pct, abs_load_pct, coolant_c, iat_c,
               stft1_pct, ltft1_pct, stft2_pct, ltft2_pct,
               maf_gps, map_kpa, throttle_pct, timing_adv_deg,
               o2_s1_v, o2_s2_v, lambda_cmd, voltage_v,
               fuel_level_pct, fuel_rate_lph, runtime_s
        """;

    private const string InsertSql = """
        INSERT INTO samples (
            trip_id, vehicle_id, ts,
            rpm, speed_kph, engine_load_pct, abs_load_pct, coolant_c, iat_c,
            stft1_pct, ltft1_pct, stft2_pct, ltft2_pct,
            maf_gps, map_kpa, throttle_pct, timing_adv_deg,
            o2_s1_v, o2_s2_v, lambda_cmd, voltage_v,
            fuel_level_pct, fuel_rate_lph, runtime_s
        ) VALUES (
            $trip, $vehicle, $ts,
            $rpm, $speed, $load, $absLoad, $coolant, $iat,
            $stft1, $ltft1, $stft2, $ltft2,
            $maf, $map, $throttle, $timing,
            $o2s1, $o2s2, $lambda, $voltage,
            $fuelLevel, $fuelRate, $runtime
        );
        """;

    /// <summary>
    /// Inserts a batch in one transaction, reusing a single prepared command.
    /// </summary>
    public int InsertBatch(IReadOnlyCollection<Sample> samples)
    {
        if (samples.Count == 0)
            return 0;

        using var transaction = _db.BeginTransaction();
        using var command = _db.CreateCommand(InsertSql);
        command.Transaction = transaction;

        // Declare parameters once, then just reassign values per row.
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[]
                 {
                     "trip", "vehicle", "ts", "rpm", "speed", "load", "absLoad", "coolant", "iat",
                     "stft1", "ltft1", "stft2", "ltft2", "maf", "map", "throttle", "timing",
                     "o2s1", "o2s2", "lambda", "voltage", "fuelLevel", "fuelRate", "runtime"
                 })
        {
            p[name] = command.Parameters.Add($"${name}", SqliteType.Text);
        }

        var written = 0;
        foreach (var s in samples)
        {
            p["trip"].Value = s.TripId;
            p["vehicle"].Value = s.VehicleId;
            p["ts"].Value = Timestamps.ToDb(s.Timestamp);

            p["rpm"].Value = Db(s.Rpm);
            p["speed"].Value = Db(s.SpeedKph);
            p["load"].Value = Db(s.EngineLoadPct);
            p["absLoad"].Value = Db(s.AbsLoadPct);
            p["coolant"].Value = Db(s.CoolantC);
            p["iat"].Value = Db(s.IatC);
            p["stft1"].Value = Db(s.Stft1Pct);
            p["ltft1"].Value = Db(s.Ltft1Pct);
            p["stft2"].Value = Db(s.Stft2Pct);
            p["ltft2"].Value = Db(s.Ltft2Pct);
            p["maf"].Value = Db(s.MafGps);
            p["map"].Value = Db(s.MapKpa);
            p["throttle"].Value = Db(s.ThrottlePct);
            p["timing"].Value = Db(s.TimingAdvDeg);
            p["o2s1"].Value = Db(s.O2S1V);
            p["o2s2"].Value = Db(s.O2S2V);
            p["lambda"].Value = Db(s.LambdaCmd);
            p["voltage"].Value = Db(s.VoltageV);
            p["fuelLevel"].Value = Db(s.FuelLevelPct);
            p["fuelRate"].Value = Db(s.FuelRateLph);
            p["runtime"].Value = Db(s.RuntimeS);

            written += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return written;
    }

    public int CountForTrip(string tripId)
    {
        using var command = _db.CreateCommand("SELECT COUNT(*) FROM samples WHERE trip_id = $t;");
        command.Parameters.AddWithValue("$t", tripId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Reads a time window for one vehicle. This is what an event detail screen
    /// uses to chart the conditions around a fault.
    /// </summary>
    public List<Sample> GetWindow(string vehicleId, DateTime fromUtc, DateTime toUtc)
    {
        using var command = _db.CreateCommand(
            $"{SelectColumns} FROM samples WHERE vehicle_id = $v AND ts >= $from AND ts <= $to ORDER BY ts;");

        command.Parameters.AddWithValue("$v", vehicleId);
        command.Parameters.AddWithValue("$from", Timestamps.ToDb(fromUtc));
        command.Parameters.AddWithValue("$to", Timestamps.ToDb(toUtc));

        var results = new List<Sample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(Read(reader));

        return results;
    }

    /// <summary>All samples for one trip, oldest first. Used by orphan recovery to rebuild totals.</summary>
    public List<Sample> GetForTrip(string tripId)
    {
        using var command = _db.CreateCommand($"{SelectColumns} FROM samples WHERE trip_id = $t ORDER BY ts;");
        command.Parameters.AddWithValue("$t", tripId);

        var results = new List<Sample>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(Read(reader));

        return results;
    }

    /// <summary>Timestamp of the last sample recorded for a trip, or null if it has none.</summary>
    public DateTime? LastTimestampForTrip(string tripId)
    {
        using var command = _db.CreateCommand("SELECT MAX(ts) FROM samples WHERE trip_id = $t;");
        command.Parameters.AddWithValue("$t", tripId);
        return command.ExecuteScalar() is string ts ? Timestamps.FromDb(ts) : null;
    }

    private static Sample Read(SqliteDataReader r) => new()
    {
        SampleId = r.GetInt64(0),
        TripId = r.GetString(1),
        VehicleId = r.GetString(2),
        Timestamp = Timestamps.FromDb(r.GetString(3)),
        Rpm = Nd(r, 4),
        SpeedKph = Nd(r, 5),
        EngineLoadPct = Nd(r, 6),
        AbsLoadPct = Nd(r, 7),
        CoolantC = Nd(r, 8),
        IatC = Nd(r, 9),
        Stft1Pct = Nd(r, 10),
        Ltft1Pct = Nd(r, 11),
        Stft2Pct = Nd(r, 12),
        Ltft2Pct = Nd(r, 13),
        MafGps = Nd(r, 14),
        MapKpa = Nd(r, 15),
        ThrottlePct = Nd(r, 16),
        TimingAdvDeg = Nd(r, 17),
        O2S1V = Nd(r, 18),
        O2S2V = Nd(r, 19),
        LambdaCmd = Nd(r, 20),
        VoltageV = Nd(r, 21),
        FuelLevelPct = Nd(r, 22),
        FuelRateLph = Nd(r, 23),
        RuntimeS = Nd(r, 24) is { } v ? (int)v : null,
    };

    private static double? Nd(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    /// <summary>Null means "not read", so it must reach SQLite as NULL rather than 0.</summary>
    private static object Db(double? value) => value.HasValue ? value.Value : DBNull.Value;

    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;
}
