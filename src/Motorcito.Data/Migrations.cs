using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

/// <summary>
/// Versioned schema, applied in order and tracked by SQLite's own
/// <c>user_version</c> pragma.
///
/// Migrations are append-only: never edit a shipped migration, add a new one.
/// A device that has already run migration 1 will only ever apply 2 onward, so
/// editing 1 silently produces two different schemas in the wild.
/// </summary>
public static class Migrations
{
    /// <summary>Each entry is one migration; index + 1 is its version number.</summary>
    private static readonly string[] Scripts =
    [
        // ---- v1: initial schema, mirroring docs/SCHEMA.md ----
        """
        CREATE TABLE vehicles (
            vehicle_id      TEXT PRIMARY KEY,
            user_id         TEXT NOT NULL,
            vin             TEXT,
            year            INTEGER,
            make            TEXT,
            model           TEXT,
            engine_code     TEXT,
            nickname        TEXT,
            supported_pids  TEXT,
            created_at      TEXT NOT NULL
        );

        CREATE TABLE trips (
            trip_id           TEXT PRIMARY KEY,
            vehicle_id        TEXT NOT NULL,
            user_id           TEXT NOT NULL,
            started_at        TEXT NOT NULL,
            ended_at          TEXT,
            distance_km       REAL,
            duration_s        INTEGER,
            ambient_temp_c    REAL,
            start_altitude_m  REAL,
            was_cold_start    INTEGER,
            synced            INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (vehicle_id) REFERENCES vehicles(vehicle_id)
        );

        CREATE INDEX idx_trips_vehicle_started ON trips(vehicle_id, started_at);

        CREATE TABLE samples (
            sample_id       INTEGER PRIMARY KEY AUTOINCREMENT,
            trip_id         TEXT NOT NULL,
            vehicle_id      TEXT NOT NULL,
            ts              TEXT NOT NULL,

            rpm             REAL,
            speed_kph       REAL,
            engine_load_pct REAL,
            abs_load_pct    REAL,
            coolant_c       REAL,
            iat_c           REAL,
            stft1_pct       REAL,
            ltft1_pct       REAL,
            stft2_pct       REAL,
            ltft2_pct       REAL,
            maf_gps         REAL,
            map_kpa         REAL,
            throttle_pct    REAL,
            timing_adv_deg  REAL,
            o2_s1_v         REAL,
            o2_s2_v         REAL,
            lambda_cmd      REAL,
            voltage_v       REAL,
            fuel_level_pct  REAL,
            fuel_rate_lph   REAL,
            runtime_s       INTEGER,

            FOREIGN KEY (trip_id) REFERENCES trips(trip_id)
        );

        CREATE INDEX idx_samples_vehicle_ts ON samples(vehicle_id, ts);
        CREATE INDEX idx_samples_trip ON samples(trip_id);

        CREATE TABLE events (
            event_id          TEXT PRIMARY KEY,
            vehicle_id        TEXT NOT NULL,
            user_id           TEXT NOT NULL,
            trip_id           TEXT,
            ts                TEXT NOT NULL,
            event_type        TEXT NOT NULL,
            code              TEXT,
            severity          TEXT,
            freeze_frame_json TEXT,
            window_start_ts   TEXT,
            window_end_ts     TEXT,
            notes             TEXT,
            acknowledged      INTEGER NOT NULL DEFAULT 0,
            synced            INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX idx_events_vehicle_ts ON events(vehicle_id, ts);

        CREATE TABLE baselines (
            baseline_id     TEXT PRIMARY KEY,
            vehicle_id      TEXT NOT NULL,
            user_id         TEXT NOT NULL,
            metric          TEXT NOT NULL,
            coolant_band    TEXT NOT NULL,
            load_band       TEXT NOT NULL,
            rpm_band        TEXT NOT NULL,
            speed_band      TEXT NOT NULL,
            window_start    TEXT NOT NULL,
            window_end      TEXT NOT NULL,
            mean_value      REAL NOT NULL,
            stddev_value    REAL,
            sample_count    INTEGER NOT NULL,
            trip_count      INTEGER NOT NULL,
            is_sufficient   INTEGER NOT NULL
        );

        CREATE INDEX idx_baselines_lookup
            ON baselines(vehicle_id, metric, coolant_band, load_band, rpm_band, speed_band);

        CREATE TABLE maintenance_log (
            entry_id     TEXT PRIMARY KEY,
            vehicle_id   TEXT NOT NULL,
            user_id      TEXT NOT NULL,
            performed_at TEXT NOT NULL,
            category     TEXT,
            odometer_km  REAL,
            notes        TEXT
        );

        CREATE TABLE sync_queue (
            queue_id      INTEGER PRIMARY KEY AUTOINCREMENT,
            entity_type   TEXT NOT NULL,
            entity_id     TEXT NOT NULL,
            user_id       TEXT NOT NULL,
            attempts      INTEGER NOT NULL DEFAULT 0,
            last_attempt  TEXT,
            last_error    TEXT
        );

        CREATE INDEX idx_sync_queue_user ON sync_queue(user_id);
        """,

        // ---- v2: raw adapter exchange log ----
        //
        // A genuine migration rather than an edit to v1: a device is already
        // carrying a v1 database with real driving data, so v1 is now shipped
        // and immutable.
        //
        // Exists because the rest of the schema stores *decoded* values. When a
        // reply fails to parse there is no row, no error and nothing to
        // inspect — a VIN that would not decode left no trace at all, and the
        // cause had to be guessed at from source code instead of read from the
        // car's own bytes.
        //
        // vehicle_id is nullable: the most diagnostically valuable exchanges —
        // the init sequence, the capability scan, the VIN read itself — all
        // happen before the vehicle has been identified.
        """
        CREATE TABLE obd_log (
            log_id        INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id       TEXT NOT NULL,
            vehicle_id    TEXT,
            session_id    TEXT NOT NULL,
            ts            TEXT NOT NULL,
            command       TEXT NOT NULL,
            raw_response  TEXT,
            status        TEXT,
            duration_ms   INTEGER,
            error         TEXT
        );

        CREATE INDEX idx_obd_log_session ON obd_log(session_id, ts);
        CREATE INDEX idx_obd_log_command ON obd_log(command);
        """,
    ];

    public static int LatestVersion => Scripts.Length;

    /// <summary>
    /// Brings a database up to <see cref="LatestVersion"/>. Safe to call on
    /// every startup; already-applied migrations are skipped.
    /// </summary>
    public static int Apply(SqliteConnection connection)
    {
        var current = GetVersion(connection);

        for (var version = current; version < Scripts.Length; version++)
        {
            // Each migration and its version bump commit together, so an
            // interrupted upgrade cannot leave a half-migrated database.
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Scripts[version];
                command.ExecuteNonQuery();
            }

            using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                // PRAGMA does not accept parameters, and the value is an int
                // from our own array index, never user input.
                bump.CommandText = $"PRAGMA user_version = {version + 1};";
                bump.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return GetVersion(connection);
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
