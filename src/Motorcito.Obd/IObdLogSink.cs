namespace Motorcito.Obd;

/// <summary>One command sent to the adapter and whatever came back.</summary>
/// <param name="Command">Exactly what was sent, e.g. "0902".</param>
/// <param name="RawResponse">The adapter's reply verbatim, before any parsing. Null if the call threw.</param>
/// <param name="Status">Classification after parsing — Data, NoData, BusError, Timeout and so on.</param>
/// <param name="Error">Exception message when the transport itself failed.</param>
public sealed record ObdExchange(
    DateTime TimestampUtc,
    string Command,
    string? RawResponse,
    string Status,
    int DurationMs,
    string? Error);

/// <summary>
/// Receives raw adapter exchanges for diagnosis.
///
/// Implemented in <c>Motorcito.Data</c> so this assembly keeps no persistence
/// knowledge, the same way <see cref="IObdAdapter"/> keeps no protocol
/// knowledge.
/// </summary>
public interface IObdLogSink
{
    /// <summary>
    /// Records one exchange. Must not throw and must not block: this runs on
    /// the poll loop, and a logging problem may never cost a read.
    /// </summary>
    void Record(ObdExchange exchange);
}

/// <summary>How much of the conversation to keep.</summary>
public enum ObdLogMode
{
    /// <summary>
    /// One-off commands, the capability scan, and everything that failed.
    ///
    /// A few hundred rows per drive. Steady-state reads that succeeded are
    /// dropped: at 10–20 reads a second they would be millions of rows an hour
    /// and would dwarf the driving data they are meant to explain.
    /// </summary>
    Diagnostic,

    /// <summary>
    /// Every exchange, for actively chasing a problem. Expensive — intended to
    /// be switched on for minutes, not left on.
    /// </summary>
    Verbose,

    /// <summary>Nothing.</summary>
    Off
}
