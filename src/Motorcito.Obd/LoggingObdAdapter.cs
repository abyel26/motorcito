using System.Diagnostics;

namespace Motorcito.Obd;

/// <summary>
/// Wraps another adapter and records what actually crosses the wire.
///
/// A decorator rather than a change to <see cref="Elm327Session"/>, for two
/// reasons. It sees the reply <em>before</em> any parsing, which is the only
/// place a parser bug is still visible. And it leaves the session, poller and
/// decoders untouched, so switching logging on cannot alter the behaviour it is
/// meant to observe.
/// </summary>
public sealed class LoggingObdAdapter : IObdAdapter
{
    private readonly IObdAdapter _inner;
    private readonly IObdLogSink _sink;
    private readonly HashSet<string> _seenSuccessfully = [];
    private readonly object _lock = new();

    /// <summary>Identifies one connection, so a drive's exchanges can be read together.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public ObdLogMode Mode { get; set; }

    public LoggingObdAdapter(IObdAdapter inner, IObdLogSink sink, ObdLogMode mode = ObdLogMode.Diagnostic)
    {
        _inner = inner;
        _sink = sink;
        Mode = mode;
    }

    public string Name => _inner.Name;
    public ObdConnectionState State => _inner.State;

    public event EventHandler<ObdConnectionState>? StateChanged
    {
        add => _inner.StateChanged += value;
        remove => _inner.StateChanged -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _inner.ConnectAsync(cancellationToken);

    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var at = DateTime.UtcNow;

        try
        {
            var raw = await _inner.SendCommandAsync(command, timeout, cancellationToken).ConfigureAwait(false);
            var status = Elm327Response.Parse(raw, command).Status;

            if (ShouldRecord(command, status))
                Safely(new ObdExchange(at, command, raw, status.ToString(), Elapsed(started), null));

            return raw;
        }
        catch (Exception ex)
        {
            // Failures are always worth keeping — they are the reason this
            // exists — and the exception must propagate unchanged.
            Safely(new ObdExchange(at, command, null, "Exception", Elapsed(started), ex.Message));
            throw;
        }
    }

    public Task DisconnectAsync() => _inner.DisconnectAsync();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    /// <summary>
    /// Decides whether an exchange earns a row.
    ///
    /// Keeps anything that is one-off or went wrong, and the first success for
    /// each command so the healthy shape of every reply is on record. Drops
    /// repeat successes, which are the overwhelming majority and carry no
    /// information the first one did not.
    /// </summary>
    private bool ShouldRecord(string command, Elm327Status status)
    {
        if (Mode == ObdLogMode.Off)
            return false;

        if (Mode == ObdLogMode.Verbose)
            return true;

        if (status != Elm327Status.Data)
            return true;                       // every failure, always

        if (IsOneOff(command))
            return true;

        lock (_lock)
            return _seenSuccessfully.Add(command);   // first success only
    }

    /// <summary>
    /// Commands issued once per connection rather than in the poll rotation:
    /// adapter setup, the capability scan, VIN and trouble codes. Low volume,
    /// and between them they describe everything about how a car introduced
    /// itself.
    /// </summary>
    private static bool IsOneOff(string command)
    {
        if (command.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            return true;

        if (command is "03" or "07" or "0A")
            return true;

        if (command.StartsWith("09", StringComparison.OrdinalIgnoreCase))
            return true;

        return command is "0100" or "0120" or "0140" or "0160";
    }

    private static int Elapsed(long started)
        => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>A logging failure must never cost a read.</summary>
    private void Safely(ObdExchange exchange)
    {
        try
        {
            _sink.Record(exchange);
        }
        catch
        {
            // Intentionally swallowed.
        }
    }
}
