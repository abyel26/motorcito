using System.Text;
using ExternalAccessory;
using Foundation;
using Motorcito.Obd;

namespace Motorcito.App.Platforms.iOS;

/// <summary>
/// Talks to an MFi-certified OBD adapter (the OBDLink MX+) over the
/// ExternalAccessory framework.
///
/// This is the only file in the solution that references iOS Bluetooth APIs.
/// Everything above <see cref="IObdAdapter"/> — protocol, decoding, rules, UI —
/// is unaware this class exists, which is what makes the deferred CoreBluetooth
/// adapter a sibling of this file rather than a rewrite.
///
/// Why ExternalAccessory rather than CoreBluetooth: the MX+ speaks Bluetooth
/// Classic, which iOS exposes only to MFi-licensed accessories through this
/// framework. CoreBluetooth sees BLE devices only and cannot see the MX+ at all.
///
/// Requires <c>UISupportedExternalAccessoryProtocols</c> in Info.plist listing
/// the accessory's protocol string.
/// </summary>
public sealed class ExternalAccessoryObdAdapter : IObdAdapter
{
    /// <summary>
    /// OBDLink's registered MFi protocol string. An accessory is only visible
    /// to an app that declares the protocol it speaks, so this must also appear
    /// in Info.plist under UISupportedExternalAccessoryProtocols.
    /// </summary>
    public const string ObdLinkProtocol = "com.obdlink";

    /// <summary>The ELM327 marks the end of every reply with this prompt character.</summary>
    private const char Prompt = '>';

    private readonly string _protocolString;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private EASession? _session;
    private EAAccessory? _accessory;
    private ObdConnectionState _state = ObdConnectionState.Disconnected;
    private NSObject? _disconnectObserver;

    public string Name => _accessory?.Name ?? "OBDLink (not connected)";

    public ObdConnectionState State => _state;

    public event EventHandler<ObdConnectionState>? StateChanged;

    public ExternalAccessoryObdAdapter(string protocolString = ObdLinkProtocol)
    {
        _protocolString = protocolString;
    }

    /// <summary>
    /// Accessories already paired in iOS Settings that speak our protocol.
    ///
    /// iOS has no in-app pairing for MFi accessories: the user pairs the adapter
    /// in Settings > Bluetooth, and only then does it appear here. The UI must
    /// say so rather than showing an empty scan.
    /// </summary>
    public IReadOnlyList<EAAccessory> AvailableAccessories()
        => EAAccessoryManager.SharedAccessoryManager.ConnectedAccessories
            .Where(a => a.ProtocolStrings.Contains(_protocolString))
            .ToList();

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ObdConnectionState.Connected)
            return Task.CompletedTask;

        SetState(ObdConnectionState.Connecting);

        var accessory = AvailableAccessories().FirstOrDefault();
        if (accessory is null)
        {
            SetState(ObdConnectionState.Disconnected);
            throw new ObdException(
                $"No paired accessory advertising '{_protocolString}'. Pair the adapter in Settings > Bluetooth first.");
        }

        var session = new EASession(accessory, _protocolString);
        if (session.InputStream is null || session.OutputStream is null)
        {
            session.Dispose();
            SetState(ObdConnectionState.Disconnected);
            throw new ObdException(
                "Accessory session opened without streams. This usually means the app is not authorised for this accessory's protocol.");
        }

        session.InputStream.Schedule(NSRunLoop.Current, NSRunLoopMode.Default);
        session.OutputStream.Schedule(NSRunLoop.Current, NSRunLoopMode.Default);
        session.InputStream.Open();
        session.OutputStream.Open();

        _accessory = accessory;
        _session = session;

        // Cars get switched off mid-trip. Surface that as Reconnecting rather
        // than letting the next read fail with a confusing stream error.
        _disconnectObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            new NSString("EAAccessoryDidDisconnectNotification"),
            _ => SetState(ObdConnectionState.Reconnecting));

        EAAccessoryManager.SharedAccessoryManager.RegisterForLocalNotifications();

        SetState(ObdConnectionState.Connected);
        return Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_state != ObdConnectionState.Connected || _session is null)
            throw new ObdNotConnectedException();

        // The ELM327 is strictly one request/response at a time; interleaved
        // writes corrupt both replies.
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DrainInput();

            var payload = Encoding.ASCII.GetBytes(command + "\r");
            var written = _session.OutputStream!.Write(payload, (nuint)payload.Length);
            if (written < 0)
                throw new ObdException($"Write failed for '{command}'.");

            return await ReadUntilPromptAsync(command, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Reads until the <c>&gt;</c> prompt arrives or the budget expires.
    ///
    /// The prompt is the only reliable end-of-reply marker, but clones drop it,
    /// so a timeout returns whatever arrived rather than throwing when there is
    /// usable text — a reply missing only its prompt still decodes.
    /// </summary>
    private async Task<string> ReadUntilPromptAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var received = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_session?.InputStream?.HasBytesAvailable() == true)
            {
                var read = _session.InputStream.Read(buffer, (nuint)buffer.Length);
                if (read > 0)
                {
                    received.Append(Encoding.ASCII.GetString(buffer, 0, (int)read));

                    var text = received.ToString();
                    var prompt = text.IndexOf(Prompt);
                    if (prompt >= 0)
                        return text[..prompt];
                }
            }
            else
            {
                // Poll rather than block: EAAccessory streams deliver on the run
                // loop, and a blocking read here would deadlock the UI thread.
                await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            }
        }

        var partial = received.ToString();
        if (partial.Trim().Length > 0)
            return partial;

        throw new ObdTimeoutException(command, timeout);
    }

    /// <summary>Discards unread bytes so a late reply cannot be misread as the answer to the next command.</summary>
    private void DrainInput()
    {
        if (_session?.InputStream is not { } input)
            return;

        var scratch = new byte[256];
        while (input.HasBytesAvailable())
        {
            if (input.Read(scratch, (nuint)scratch.Length) <= 0)
                break;
        }
    }

    public Task DisconnectAsync()
    {
        if (_disconnectObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_disconnectObserver);
            _disconnectObserver = null;
        }

        if (_session is not null)
        {
            _session.InputStream?.Close();
            _session.OutputStream?.Close();
            _session.InputStream?.Unschedule(NSRunLoop.Current, NSRunLoopMode.Default);
            _session.OutputStream?.Unschedule(NSRunLoop.Current, NSRunLoopMode.Default);
            _session.Dispose();
            _session = null;
        }

        _accessory = null;
        SetState(ObdConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    private void SetState(ObdConnectionState next)
    {
        if (_state == next)
            return;

        _state = next;
        StateChanged?.Invoke(this, next);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _commandGate.Dispose();
    }
}
