using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

/// <summary>
/// Produces files that can leave the device for analysis.
///
/// Two kinds of unwritten data make a naive export silently incomplete, and
/// both bite hardest on the most recent drive — the one you actually wanted to
/// look at:
///
/// <list type="number">
/// <item>Samples still buffered in memory by <see cref="TripRecorder"/>, up to
/// <see cref="TripRecorderOptions.BatchSize"/> rows. <see cref="LoggingService"/>
/// flushes before exporting.</item>
/// <item>Committed rows living in the write-ahead log rather than the main
/// database file. Copying <c>motorcito.db</c> alone would omit them — on a real
/// device the WAL has been seen holding more than the database file itself.</item>
/// </list>
/// </summary>
public static class DataExporter
{
    /// <summary>
    /// Writes a self-contained copy of the database to <paramref name="destinationPath"/>.
    ///
    /// Uses SQLite's online backup API rather than copying files. Backup folds
    /// the WAL into the destination and takes a consistent snapshot even while
    /// the app is still logging, so the result is one portable file with no
    /// <c>-wal</c> or <c>-shm</c> sidecars to forget.
    /// </summary>
    /// <returns>The path written.</returns>
    public static string SnapshotDatabase(MotorcitoDatabase db, string destinationPath)
    {
        // A stale destination would otherwise be merged into rather than replaced.
        foreach (var stale in new[] { destinationPath, $"{destinationPath}-wal", $"{destinationPath}-shm" })
        {
            if (File.Exists(stale))
                File.Delete(stale);
        }

        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooling keeps the file handle open after Dispose, so a later
            // export to the same path fails to replace the file. Exports are
            // one-shot and infrequent; there is nothing for a pool to save.
            Pooling = false
        }.ToString());

        destination.Open();
        db.Connection.BackupDatabase(destination);

        return destinationPath;
    }

    /// <summary>
    /// Writes every sample as CSV, newest trips last, with trip context on each
    /// row so the file stands alone in a spreadsheet or dataframe.
    /// </summary>
    public static int WriteSamplesCsv(MotorcitoDatabase db, TextWriter writer, string? vehicleId = null)
    {
        writer.WriteLine(string.Join(',', SampleColumns));

        var sql = """
            SELECT s.trip_id, s.vehicle_id, s.ts, t.was_cold_start,
                   s.rpm, s.speed_kph, s.engine_load_pct, s.abs_load_pct, s.coolant_c, s.iat_c,
                   s.stft1_pct, s.ltft1_pct, s.stft2_pct, s.ltft2_pct,
                   s.maf_gps, s.map_kpa, s.throttle_pct, s.timing_adv_deg,
                   s.o2_s1_v, s.o2_s2_v, s.lambda_cmd, s.voltage_v,
                   s.fuel_level_pct, s.fuel_rate_lph, s.runtime_s
            FROM samples s
            JOIN trips t ON t.trip_id = s.trip_id
            """
            + (vehicleId is null ? string.Empty : " WHERE s.vehicle_id = $v")
            + " ORDER BY s.ts;";

        using var command = db.CreateCommand(sql);
        if (vehicleId is not null)
            command.Parameters.AddWithValue("$v", vehicleId);

        return WriteRows(command, writer);
    }

    /// <summary>Writes one row per trip: the summary table for a first look at the data.</summary>
    public static int WriteTripsCsv(MotorcitoDatabase db, TextWriter writer, string? vehicleId = null)
    {
        writer.WriteLine(string.Join(',', TripColumns));

        var sql = """
            SELECT t.trip_id, t.vehicle_id, t.started_at, t.ended_at,
                   t.distance_km, t.duration_s, t.ambient_temp_c, t.start_altitude_m, t.was_cold_start,
                   (SELECT COUNT(*) FROM samples s WHERE s.trip_id = t.trip_id)
            FROM trips t
            """
            + (vehicleId is null ? string.Empty : " WHERE t.vehicle_id = $v")
            + " ORDER BY t.started_at;";

        using var command = db.CreateCommand(sql);
        if (vehicleId is not null)
            command.Parameters.AddWithValue("$v", vehicleId);

        return WriteRows(command, writer);
    }

    /// <summary>
    /// Writes the raw adapter exchange log as CSV.
    ///
    /// Kept separate from the driving data because it answers a different
    /// question — not "what was the car doing" but "what did the adapter
    /// actually say" — and because it contains raw replies including the VIN,
    /// so it should be shared deliberately rather than by default.
    /// </summary>
    public static int WriteObdLogCsv(MotorcitoDatabase db, TextWriter writer)
    {
        writer.WriteLine("ts,session_id,command,status,duration_ms,raw_response,error");

        using var command = db.CreateCommand("""
            SELECT ts, session_id, command, status, duration_ms, raw_response, error
            FROM obd_log ORDER BY ts;
            """);

        return WriteRows(command, writer);
    }

    private static readonly string[] SampleColumns =
    [
        "trip_id", "vehicle_id", "ts", "was_cold_start",
        "rpm", "speed_kph", "engine_load_pct", "abs_load_pct", "coolant_c", "iat_c",
        "stft1_pct", "ltft1_pct", "stft2_pct", "ltft2_pct",
        "maf_gps", "map_kpa", "throttle_pct", "timing_adv_deg",
        "o2_s1_v", "o2_s2_v", "lambda_cmd", "voltage_v",
        "fuel_level_pct", "fuel_rate_lph", "runtime_s"
    ];

    private static readonly string[] TripColumns =
    [
        "trip_id", "vehicle_id", "started_at", "ended_at",
        "distance_km", "duration_s", "ambient_temp_c", "start_altitude_m",
        "was_cold_start", "sample_count"
    ];

    private static int WriteRows(SqliteCommand command, TextWriter writer)
    {
        var rows = 0;
        var values = Array.Empty<string>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (values.Length != reader.FieldCount)
                values = new string[reader.FieldCount];

            for (var i = 0; i < reader.FieldCount; i++)
                values[i] = Format(reader, i);

            writer.WriteLine(string.Join(',', values));
            rows++;
        }

        return rows;
    }

    /// <summary>
    /// Formats one value for CSV.
    ///
    /// Numbers are written with <see cref="CultureInfo.InvariantCulture"/>
    /// deliberately. Under a Spanish or French locale the default formatter
    /// renders 3.5 as "3,5", which in a comma-separated file silently becomes
    /// two columns and shifts every field after it. The export must not depend
    /// on the phone's regional settings.
    /// </summary>
    private static string Format(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return string.Empty;          // null means "not read" — leave the cell empty, never 0

        return reader.GetFieldType(ordinal) switch
        {
            var t when t == typeof(double) => reader.GetDouble(ordinal).ToString("R", CultureInfo.InvariantCulture),
            var t when t == typeof(long) => reader.GetInt64(ordinal).ToString(CultureInfo.InvariantCulture),
            _ => Escape(reader.GetValue(ordinal).ToString() ?? string.Empty)
        };
    }

    /// <summary>
    /// RFC 4180 quoting.
    ///
    /// Carriage return matters as much as line feed here: ELM327 replies are
    /// CR-delimited, so a multi-frame response carries bare CRs. Left unquoted
    /// they split one reply across several CSV rows and the evidence is
    /// destroyed by the export meant to deliver it.
    /// </summary>
    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"')
            && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
