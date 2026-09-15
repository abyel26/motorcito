namespace Motorcito.Obd.Signals;

/// <summary>
/// One request a source says the car may answer, and every signal carried in
/// its reply. A single reply often packs several values, so a command is polled
/// once no matter how many signals it feeds.
/// </summary>
public sealed record SignalCommand
{
    /// <summary>Which <see cref="ISignalProfileSource"/> defined this command.</summary>
    public required string SourceId { get; init; }

    /// <summary>CAN transmit header as the source wrote it. Not necessarily sendable — see <see cref="IsSendable"/>.</summary>
    public required string Header { get; init; }

    /// <summary>Expected reply header, if the source specifies one.</summary>
    public string? ReceiveAddress { get; init; }

    public required byte Service { get; init; }

    public required ushort Identifier { get; init; }

    public required int IdentifierLength { get; init; }

    /// <summary>How often the source suggests re-reading, in seconds.</summary>
    public double IntervalSeconds { get; init; } = 1;

    public required IReadOnlyList<SignalDefinition> Signals { get; init; }

    /// <summary>
    /// Why the app cannot send this command correctly (29-bit headers, extended
    /// addressing, flow-control options), or null. Such commands are reported,
    /// never sent with approximate addressing and never silently dropped.
    /// </summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>True when the command can be sent exactly as defined, and only reads.</summary>
    public bool IsSendable
        => UnsupportedReason is null
           && ReadOnlyServicePolicy.IsAllowed(Service)
           && SignalRequest.IsElevenBitHeader(Header)
           && IdentifierLength is 1 or 2;

    /// <exception cref="InvalidOperationException">The command is not sendable.</exception>
    public SignalRequest ToRequest()
    {
        if (!IsSendable)
            throw new InvalidOperationException($"{this} cannot be sent: {UnsupportedReason ?? "not a read-only 11-bit request"}.");

        return new SignalRequest(Header, Service, Identifier, IdentifierLength);
    }

    public override string ToString() => IdentifierLength == 1
        ? $"{Header} {Service:X2}:{Identifier:X2}"
        : $"{Header} {Service:X2}:{Identifier:X4}";
}
