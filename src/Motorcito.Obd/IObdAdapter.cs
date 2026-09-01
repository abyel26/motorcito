namespace Motorcito.Obd;

/// <summary>
/// The single seam between Motorcito and the physical world.
///
/// Nothing above this interface — parsing, logging, rules, UI — may reference a
/// platform Bluetooth API. Adding a transport (BLE clones, WiFi adapters, a
/// replay-from-disk harness) must be a new implementation of this interface and
/// nothing else.
///
/// Implementations own the transport only. They do not know about ELM327
/// commands, PIDs, or framing; <see cref="Elm327Session"/> layers that on top.
/// </summary>
public interface IObdAdapter : IAsyncDisposable
{
    /// <summary>Human-readable adapter name, for UI and logs (e.g. "OBDLink MX+").</summary>
    string Name { get; }

    ObdConnectionState State { get; }

    /// <summary>Raised on every state transition, including transitions the caller did not request (cable pulled, car switched off).</summary>
    event EventHandler<ObdConnectionState>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one command and returns the raw response text, with the trailing
    /// <c>&gt;</c> prompt stripped.
    ///
    /// Implementations must serialise callers: the ELM327 is a single
    /// request/response device and interleaved writes corrupt both responses.
    /// </summary>
    /// <exception cref="ObdTimeoutException">No prompt arrived within <paramref name="timeout"/>.</exception>
    /// <exception cref="ObdNotConnectedException">Called while not connected.</exception>
    Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}

public enum ObdConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>Lost the link and trying to get it back. Cars get switched off mid-trip; this is routine, not exceptional.</summary>
    Reconnecting
}

public class ObdException : Exception
{
    public ObdException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ObdTimeoutException : ObdException
{
    public ObdTimeoutException(string command, TimeSpan timeout)
        : base($"Adapter did not return a prompt for '{command}' within {timeout.TotalMilliseconds:F0} ms.") { }
}

public sealed class ObdNotConnectedException : ObdException
{
    public ObdNotConnectedException() : base("Adapter is not connected.") { }
}
