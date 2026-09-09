using Microsoft.Data.Sqlite;

namespace Motorcito.Data;

public sealed class VehicleRepository
{
    private readonly MotorcitoDatabase _db;

    public VehicleRepository(MotorcitoDatabase db) => _db = db;

    /// <summary>
    /// Inserts, or updates the fields a fresh connection can re-learn.
    ///
    /// Keyed on VIN where one is available: the same physical car should not
    /// become a second row because the app was reinstalled and generated a new
    /// vehicle id. Without a VIN we cannot tell cars apart, so the caller's id
    /// is trusted.
    /// </summary>
    public Vehicle Upsert(Vehicle vehicle)
    {
        var existingId = vehicle.Vin is not null ? FindIdByVin(vehicle.Vin, vehicle.UserId) : null;
        var id = existingId ?? vehicle.VehicleId;

        using var command = _db.CreateCommand("""
            INSERT INTO vehicles (vehicle_id, user_id, vin, year, make, model, engine_code, nickname, supported_pids, created_at)
            VALUES ($id, $user, $vin, $year, $make, $model, $engine, $nick, $pids, $created)
            ON CONFLICT(vehicle_id) DO UPDATE SET
                vin            = COALESCE(excluded.vin, vin),
                year           = COALESCE(excluded.year, year),
                make           = COALESCE(excluded.make, make),
                model          = COALESCE(excluded.model, model),
                engine_code    = COALESCE(excluded.engine_code, engine_code),
                nickname       = COALESCE(excluded.nickname, nickname),
                -- Supported PIDs are re-read at every connection and may
                -- legitimately change (a module asleep on the previous scan),
                -- so this one overwrites rather than coalescing.
                supported_pids = excluded.supported_pids;
            """);

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$user", vehicle.UserId);
        command.Parameters.AddWithValue("$vin", (object?)vehicle.Vin ?? DBNull.Value);
        command.Parameters.AddWithValue("$year", (object?)vehicle.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$make", (object?)vehicle.Make ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)vehicle.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("$engine", (object?)vehicle.EngineCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$nick", (object?)vehicle.Nickname ?? DBNull.Value);
        command.Parameters.AddWithValue("$pids", (object?)vehicle.SupportedPidsJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", Timestamps.ToDb(vehicle.CreatedAt));
        command.ExecuteNonQuery();

        return vehicle with { VehicleId = id };
    }

    public string? FindIdByVin(string vin, string userId)
    {
        using var command = _db.CreateCommand("SELECT vehicle_id FROM vehicles WHERE vin = $vin AND user_id = $user LIMIT 1;");
        command.Parameters.AddWithValue("$vin", vin);
        command.Parameters.AddWithValue("$user", userId);
        return command.ExecuteScalar() as string;
    }

    public Vehicle? Get(string vehicleId)
    {
        using var command = _db.CreateCommand("""
            SELECT vehicle_id, user_id, vin, year, make, model, engine_code, nickname, supported_pids, created_at
            FROM vehicles WHERE vehicle_id = $id;
            """);
        command.Parameters.AddWithValue("$id", vehicleId);

        using var r = command.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public List<Vehicle> All(string userId)
    {
        using var command = _db.CreateCommand("""
            SELECT vehicle_id, user_id, vin, year, make, model, engine_code, nickname, supported_pids, created_at
            FROM vehicles WHERE user_id = $user ORDER BY created_at;
            """);
        command.Parameters.AddWithValue("$user", userId);

        var list = new List<Vehicle>();
        using var r = command.ExecuteReader();
        while (r.Read())
            list.Add(Read(r));
        return list;
    }

    private static Vehicle Read(SqliteDataReader r) => new()
    {
        VehicleId = r.GetString(0),
        UserId = r.GetString(1),
        Vin = r.IsDBNull(2) ? null : r.GetString(2),
        Year = r.IsDBNull(3) ? null : r.GetInt32(3),
        Make = r.IsDBNull(4) ? null : r.GetString(4),
        Model = r.IsDBNull(5) ? null : r.GetString(5),
        EngineCode = r.IsDBNull(6) ? null : r.GetString(6),
        Nickname = r.IsDBNull(7) ? null : r.GetString(7),
        SupportedPidsJson = r.IsDBNull(8) ? null : r.GetString(8),
        CreatedAt = Timestamps.FromDb(r.GetString(9)),
    };
}
