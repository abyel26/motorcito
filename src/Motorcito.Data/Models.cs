using Motorcito.Obd;

namespace Motorcito.Data;

/// <summary>
/// UTC ISO-8601 round-trip format, used for every timestamp column.
///
/// Stored as TEXT rather than a Unix epoch so the database stays readable in
/// any SQLite browser, and because lexical ordering of this format matches
/// chronological ordering — which is what the (vehicle_id, ts) index relies on.
/// </summary>
public static class Timestamps
{
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public static string ToDb(DateTime utc) => utc.ToUniversalTime().ToString(Format);

    public static DateTime FromDb(string value)
        => DateTime.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal
                                     | System.Globalization.DateTimeStyles.AssumeUniversal);
}

public sealed record Vehicle
{
    public required string VehicleId { get; init; }
    public required string UserId { get; init; }
    public string? Vin { get; init; }
    public int? Year { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? EngineCode { get; init; }
    public string? Nickname { get; init; }

    /// <summary>JSON array from the capability scan, as produced by <see cref="SupportedPids.ToJson"/>.</summary>
    public string? SupportedPidsJson { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public sealed record Trip
{
    public required string TripId { get; init; }
    public required string VehicleId { get; init; }
    public required string UserId { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public double? DistanceKm { get; init; }
    public int? DurationS { get; init; }

    /// <summary>IAT at start (before the engine warms the intake), or PID 46 if supported.</summary>
    public double? AmbientTempC { get; init; }

    /// <summary>Altitude only. Coordinates are derived-and-discarded — see the privacy note in ROADMAP.md.</summary>
    public double? StartAltitudeM { get; init; }

    public bool WasColdStart { get; init; }
    public bool Synced { get; init; }
}

/// <summary>
/// One row per timestamp, one column per PID — the wide format from SCHEMA.md.
///
/// Every value is nullable because a car reports only the PIDs it supports, and
/// slow-polled parameters are absent from most cycles. Null means "not read",
/// never "zero".
/// </summary>
public sealed record Sample
{
    public long SampleId { get; init; }
    public required string TripId { get; init; }
    public required string VehicleId { get; init; }
    public required DateTime Timestamp { get; init; }

    public double? Rpm { get; init; }
    public double? SpeedKph { get; init; }
    public double? EngineLoadPct { get; init; }
    public double? AbsLoadPct { get; init; }
    public double? CoolantC { get; init; }
    public double? IatC { get; init; }
    public double? Stft1Pct { get; init; }
    public double? Ltft1Pct { get; init; }
    public double? Stft2Pct { get; init; }
    public double? Ltft2Pct { get; init; }
    public double? MafGps { get; init; }
    public double? MapKpa { get; init; }
    public double? ThrottlePct { get; init; }
    public double? TimingAdvDeg { get; init; }
    public double? O2S1V { get; init; }
    public double? O2S2V { get; init; }
    public double? LambdaCmd { get; init; }
    public double? VoltageV { get; init; }
    public double? FuelLevelPct { get; init; }
    public double? FuelRateLph { get; init; }
    public int? RuntimeS { get; init; }

    /// <summary>
    /// Projects an <see cref="ObdSnapshot"/> into a persistable row.
    ///
    /// The mapping is driven by <see cref="PidDefinition.Column"/>, so adding a
    /// PID to the registry with a column name is all that is needed for it to
    /// start being logged.
    /// </summary>
    public static Sample FromSnapshot(ObdSnapshot snapshot, string tripId, string vehicleId)
    {
        double? V(byte pid) => snapshot.Readings.TryGetValue(pid, out var r) ? r.Value : null;

        return new Sample
        {
            TripId = tripId,
            VehicleId = vehicleId,
            Timestamp = snapshot.TimestampUtc,

            EngineLoadPct = V(0x04),
            CoolantC = V(0x05),
            Stft1Pct = V(0x06),
            Ltft1Pct = V(0x07),
            Stft2Pct = V(0x08),
            Ltft2Pct = V(0x09),
            MapKpa = V(0x0B),
            Rpm = V(0x0C),
            SpeedKph = V(0x0D),
            TimingAdvDeg = V(0x0E),
            IatC = V(0x0F),
            MafGps = V(0x10),
            ThrottlePct = V(0x11),
            O2S1V = V(0x14),
            O2S2V = V(0x15),
            RuntimeS = V(0x1F) is { } rt ? (int)rt : null,
            FuelLevelPct = V(0x2F),
            VoltageV = V(0x42),
            AbsLoadPct = V(0x43),
            LambdaCmd = V(0x44),
            FuelRateLph = V(0x5E),
        };
    }
}

public enum EventType { Dtc, PendingDtc, PermanentDtc, Rule }

public enum EventSeverity { Info, Advisory, Warning }

public sealed record ObdEvent
{
    public required string EventId { get; init; }
    public required string VehicleId { get; init; }
    public required string UserId { get; init; }
    public string? TripId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required EventType Type { get; init; }

    /// <summary>A DTC such as "P0171", or a rule id such as "trim_lean_idle".</summary>
    public string? Code { get; init; }

    public EventSeverity Severity { get; init; } = EventSeverity.Info;

    /// <summary>The ECU's own Mode 02 snapshot. Exists even if the app was not running, but is a single instant.</summary>
    public string? FreezeFrameJson { get; init; }

    /// <summary>Bounds of our own logged window in <c>samples</c> — what fills the gap the ECU's single freeze frame leaves.</summary>
    public DateTime? WindowStart { get; init; }
    public DateTime? WindowEnd { get; init; }

    public string? Notes { get; init; }
    public bool Acknowledged { get; init; }
    public bool Synced { get; init; }
}
