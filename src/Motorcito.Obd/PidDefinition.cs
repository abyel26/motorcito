namespace Motorcito.Obd;

/// <summary>
/// How often a PID is worth re-reading.
///
/// Every read is a request/response round trip, so the poll set is a budget.
/// On a clone (~5–15 queries/sec) the difference between polling coolant at
/// 1 Hz and at 0.1 Hz is most of your RPM resolution.
/// </summary>
public enum PollClass
{
    /// <summary>Changes continuously; poll every cycle. RPM, speed, throttle, load, O2.</summary>
    Fast,
    /// <summary>Changes over seconds. Fuel trims, MAF, MAP, timing.</summary>
    Medium,
    /// <summary>Changes over minutes, or not at all during a trip. Temperatures, fuel level, voltage.</summary>
    Slow
}

/// <summary>
/// One Mode 01 parameter: how to ask for it, how many bytes to expect back,
/// and how to turn those bytes into a physical quantity.
/// </summary>
/// <param name="Pid">Mode 01 PID byte (e.g. 0x0C for RPM).</param>
/// <param name="Name">Display name.</param>
/// <param name="ByteCount">Data bytes expected after the mode+PID echo. Responses of any other length are rejected, not truncated.</param>
/// <param name="Unit">Metric unit. Everything is stored metric; conversion happens at display only.</param>
/// <param name="Column">Matching column in the <c>samples</c> table, or null if not persisted.</param>
/// <param name="Poll">Suggested poll frequency class.</param>
/// <param name="Decode">Applies the formula from the SAE standard to the data bytes.</param>
public sealed record PidDefinition(
    byte Pid,
    string Name,
    int ByteCount,
    string Unit,
    string? Column,
    PollClass Poll,
    Func<byte[], double> Decode)
{
    /// <summary>The Mode 01 request string, e.g. "010C".</summary>
    public string Command => $"01{Pid:X2}";

    /// <summary>The Mode 02 (freeze frame) request for frame 0, e.g. "020C00".</summary>
    public string FreezeFrameCommand => $"02{Pid:X2}00";
}
