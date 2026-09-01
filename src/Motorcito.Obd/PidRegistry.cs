namespace Motorcito.Obd;

/// <summary>
/// The Mode 01 parameters Motorcito knows how to decode.
///
/// Formulas are the SAE J1979 standard ones, transcribed from
/// <c>docs/PID-REFERENCE.md</c>. Byte counts are part of the contract: a reply
/// of the wrong length is rejected rather than padded or truncated, because a
/// short frame from a clone decodes to a plausible-looking wrong number, which
/// is worse than no reading at all.
/// </summary>
public static class PidRegistry
{
    private static readonly Dictionary<byte, PidDefinition> _byPid;

    public static IReadOnlyCollection<PidDefinition> All { get; }

    static PidRegistry()
    {
        var defs = new[]
        {
            new PidDefinition(0x04, "Calculated engine load", 1, "%", "engine_load_pct", PollClass.Fast,
                b => b[0] * 100.0 / 255.0),

            new PidDefinition(0x05, "Engine coolant temp", 1, "°C", "coolant_c", PollClass.Slow,
                b => b[0] - 40.0),

            new PidDefinition(0x06, "Short term fuel trim, bank 1", 1, "%", "stft1_pct", PollClass.Medium,
                b => (b[0] - 128.0) * 100.0 / 128.0),

            new PidDefinition(0x07, "Long term fuel trim, bank 1", 1, "%", "ltft1_pct", PollClass.Medium,
                b => (b[0] - 128.0) * 100.0 / 128.0),

            new PidDefinition(0x08, "Short term fuel trim, bank 2", 1, "%", "stft2_pct", PollClass.Medium,
                b => (b[0] - 128.0) * 100.0 / 128.0),

            new PidDefinition(0x09, "Long term fuel trim, bank 2", 1, "%", "ltft2_pct", PollClass.Medium,
                b => (b[0] - 128.0) * 100.0 / 128.0),

            new PidDefinition(0x0B, "Intake manifold pressure", 1, "kPa", "map_kpa", PollClass.Medium,
                b => b[0]),

            new PidDefinition(0x0C, "Engine RPM", 2, "rpm", "rpm", PollClass.Fast,
                b => (256.0 * b[0] + b[1]) / 4.0),

            new PidDefinition(0x0D, "Vehicle speed", 1, "km/h", "speed_kph", PollClass.Fast,
                b => b[0]),

            new PidDefinition(0x0E, "Timing advance", 1, "° BTDC", "timing_adv_deg", PollClass.Medium,
                b => b[0] / 2.0 - 64.0),

            new PidDefinition(0x0F, "Intake air temp", 1, "°C", "iat_c", PollClass.Slow,
                b => b[0] - 40.0),

            new PidDefinition(0x10, "MAF air flow rate", 2, "g/s", "maf_gps", PollClass.Medium,
                b => (256.0 * b[0] + b[1]) / 100.0),

            new PidDefinition(0x11, "Throttle position", 1, "%", "throttle_pct", PollClass.Fast,
                b => b[0] * 100.0 / 255.0),

            new PidDefinition(0x14, "O2 sensor 1 voltage", 2, "V", "o2_s1_v", PollClass.Fast,
                b => b[0] / 200.0),

            // Not listed in PID-REFERENCE.md, but SCHEMA.md defines an o2_s2_v
            // column and the catalyst-efficiency rule needs a downstream sensor.
            // Same encoding as PID 14.
            new PidDefinition(0x15, "O2 sensor 2 voltage", 2, "V", "o2_s2_v", PollClass.Fast,
                b => b[0] / 200.0),

            new PidDefinition(0x1F, "Run time since engine start", 2, "s", "runtime_s", PollClass.Slow,
                b => 256.0 * b[0] + b[1]),

            new PidDefinition(0x21, "Distance with MIL on", 2, "km", null, PollClass.Slow,
                b => 256.0 * b[0] + b[1]),

            new PidDefinition(0x2F, "Fuel tank level", 1, "%", "fuel_level_pct", PollClass.Slow,
                b => b[0] * 100.0 / 255.0),

            new PidDefinition(0x42, "Control module voltage", 2, "V", "voltage_v", PollClass.Slow,
                b => (256.0 * b[0] + b[1]) / 1000.0),

            new PidDefinition(0x43, "Absolute load value", 2, "%", "abs_load_pct", PollClass.Fast,
                b => (256.0 * b[0] + b[1]) * 100.0 / 255.0),

            new PidDefinition(0x44, "Commanded equivalence ratio", 2, "lambda", "lambda_cmd", PollClass.Medium,
                b => (256.0 * b[0] + b[1]) / 32768.0),

            new PidDefinition(0x46, "Ambient air temp", 1, "°C", null, PollClass.Slow,
                b => b[0] - 40.0),

            new PidDefinition(0x5C, "Engine oil temp", 1, "°C", null, PollClass.Slow,
                b => b[0] - 40.0),

            new PidDefinition(0x5E, "Engine fuel rate", 2, "L/h", "fuel_rate_lph", PollClass.Medium,
                b => (256.0 * b[0] + b[1]) / 20.0),
        };

        _byPid = defs.ToDictionary(d => d.Pid);
        All = defs;
    }

    public static PidDefinition? Find(byte pid) => _byPid.GetValueOrDefault(pid);

    public static bool TryGet(byte pid, out PidDefinition definition)
    {
        var found = _byPid.TryGetValue(pid, out var d);
        definition = d!;
        return found;
    }

    /// <summary>
    /// PIDs required for emissions monitoring on essentially every OBD-II
    /// vehicle. Used as a sanity check: a capability scan that reports none of
    /// these almost certainly failed to parse rather than describing a real car.
    /// </summary>
    public static readonly byte[] NearUniversal = [0x04, 0x05, 0x0C, 0x0D, 0x0F, 0x11];

    /// <summary>
    /// PIDs the whole detection plan depends on. ROADMAP.md Phase 0 gates on
    /// these being present: without fuel trims there is nothing to baseline.
    /// </summary>
    public static readonly byte[] PhaseZeroGate = [0x06, 0x07, 0x14];
}
