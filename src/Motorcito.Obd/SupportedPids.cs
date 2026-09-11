namespace Motorcito.Obd;

/// <summary>
/// The set of Mode 01 PIDs an ECU says it supports, built from the
/// <c>0100</c>/<c>0120</c>/<c>0140</c>/<c>0160</c> capability bitmasks.
///
/// Queried at every connection, never cached across vehicles and never assumed.
/// The gauge layout is built from this. Never
/// assume a PID exists, degrade gracefully, and tell the user what their car
/// does not report.
/// </summary>
public sealed class SupportedPids
{
    private readonly HashSet<byte> _pids = [];

    public IReadOnlySet<byte> Pids => _pids;

    public int Count => _pids.Count;

    public bool IsSupported(byte pid) => _pids.Contains(pid);

    public bool IsSupported(PidDefinition definition) => _pids.Contains(definition.Pid);

    /// <summary>The capability-scan commands, in the order they must be issued.</summary>
    public static readonly string[] ScanCommands = ["0100", "0120", "0140", "0160"];

    /// <summary>
    /// Builds a set directly from known PIDs, bypassing the mask scan.
    ///
    /// For rehydrating a scan persisted on a previous connection (see
    /// <see cref="ToJson"/>) and for tests that need a specific capability set
    /// without hand-assembling bitmasks. A live connection must always use
    /// <see cref="AddMask"/> against the car itself — never assume a PID exists.
    /// </summary>
    public static SupportedPids FromPids(IEnumerable<byte> pids)
    {
        var set = new SupportedPids();
        foreach (var pid in pids)
            set._pids.Add(pid);
        return set;
    }

    /// <summary>
    /// Merges one capability bitmask response into the set.
    /// </summary>
    /// <param name="maskPid">The PID the mask was requested with: 0x00, 0x20, 0x40 or 0x60.</param>
    /// <returns>
    /// True if the mask indicated the <em>next</em> range is also supported.
    /// The lowest bit of each mask is the "range N+1 available" flag, so a
    /// scanner can stop early instead of issuing all four queries blindly.
    /// </returns>
    public bool AddMask(Elm327Response response, byte maskPid)
    {
        if (!PidRegistry.TryGet(maskPid, out _) && maskPid is not (0x00 or 0x20 or 0x40 or 0x60))
            throw new ArgumentOutOfRangeException(nameof(maskPid), maskPid, "Capability masks live at PID 0x00, 0x20, 0x40 and 0x60.");

        if (!response.IsData || !PidDecoder.TryParseHex(response.Payload, out var bytes))
            return false;

        // Locate the "41 <maskPid>" header, then take the four mask bytes.
        for (var i = 0; i + 6 <= bytes.Length; i++)
        {
            if (bytes[i] != 0x41 || bytes[i + 1] != maskPid)
                continue;

            var mask = (uint)(bytes[i + 2] << 24 | bytes[i + 3] << 16 | bytes[i + 4] << 8 | bytes[i + 5]);

            // Bit 31 (MSB) is maskPid + 1; bit 0 is maskPid + 32.
            for (var bit = 0; bit < 32; bit++)
            {
                if ((mask & (1u << (31 - bit))) != 0)
                    _pids.Add((byte)(maskPid + bit + 1));
            }

            // maskPid + 32 is the flag for the next range, not a real parameter.
            var nextRangePid = (byte)(maskPid + 0x20);
            var hasNext = _pids.Remove(nextRangePid);
            return hasNext;
        }

        return false;
    }

    /// <summary>Definitions Motorcito can decode that this vehicle actually reports.</summary>
    public IEnumerable<PidDefinition> KnownSupported()
        => PidRegistry.All.Where(IsSupported).OrderBy(d => d.Pid);

    /// <summary>PIDs the vehicle reports that Motorcito has no decoder for. Useful for spotting gaps in the registry.</summary>
    public IEnumerable<byte> UnknownSupported()
        => _pids.Where(p => PidRegistry.Find(p) is null).OrderBy(p => p);

    /// <summary>
    /// Evaluates the ROADMAP Phase 0 gate: fuel trims (06/07) and O2 (14).
    /// If these are absent the detection plan changes fundamentally, so this is
    /// checked and surfaced rather than discovered later.
    /// </summary>
    public GateResult EvaluatePhaseZeroGate()
    {
        var missing = PidRegistry.PhaseZeroGate.Where(p => !IsSupported(p)).ToArray();
        var missingUniversal = PidRegistry.NearUniversal.Where(p => !IsSupported(p)).ToArray();

        return new GateResult(
            FuelTrimsPresent: IsSupported(0x06) && IsSupported(0x07),
            O2Present: IsSupported(0x14),
            Missing: missing,
            // A scan reporting none of the near-universal PIDs is far more likely
            // a parse failure than a car that genuinely reports no RPM.
            ScanLooksImplausible: missingUniversal.Length == PidRegistry.NearUniversal.Length);
    }

    /// <summary>Serialises to the JSON array stored in <c>vehicles.supported_pids</c>.</summary>
    public string ToJson()
        => "[" + string.Join(",", _pids.OrderBy(p => p).Select(p => $"\"{p:X2}\"")) + "]";

    public override string ToString()
        => $"{_pids.Count} PIDs supported ({KnownSupported().Count()} decodable)";
}

/// <param name="ScanLooksImplausible">True when the scan reported none of the near-universal PIDs — treat as a failed scan, not a limited vehicle.</param>
public sealed record GateResult(
    bool FuelTrimsPresent,
    bool O2Present,
    byte[] Missing,
    bool ScanLooksImplausible)
{
    public bool Passed => FuelTrimsPresent && !ScanLooksImplausible;
}
