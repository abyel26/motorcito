using System.Globalization;

namespace Motorcito.Obd;

/// <summary>
/// One addressed diagnostic read: which ECU, which service, which identifier.
///
/// Only 11-bit CAN headers are representable. Catalog entries that need 29-bit
/// headers or extended addressing are kept out of this type on purpose, so
/// they can never be sent with the wrong addressing — see
/// <see cref="Signals.SignalCommand.UnsupportedReason"/>.
/// </summary>
public sealed record SignalRequest
{
    public SignalRequest(string header, byte service, ushort identifier, int identifierLength)
    {
        if (!IsElevenBitHeader(header))
            throw new ArgumentException($"'{header}' is not an 11-bit CAN header (000–7FF).", nameof(header));
        if (identifierLength is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(identifierLength), "Identifiers are one or two bytes.");
        if (identifierLength == 1 && identifier > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(identifier), "A one-byte identifier cannot exceed 0xFF.");

        Header = header.ToUpperInvariant();
        Service = service;
        Identifier = identifier;
        IdentifierLength = identifierLength;
    }

    /// <summary>CAN transmit header, e.g. "7E0".</summary>
    public string Header { get; }

    public byte Service { get; }

    public ushort Identifier { get; }

    /// <summary>1 for service 0x21 local identifiers and Mode 01 PIDs, 2 for service 0x22.</summary>
    public int IdentifierLength { get; }

    /// <summary>The hex command sent after the header is set, e.g. "221310".</summary>
    public string Command => IdentifierLength == 1
        ? $"{Service:X2}{Identifier:X2}"
        : $"{Service:X2}{Identifier:X4}";

    /// <summary>The functional (broadcast) request header every ECU listens to.</summary>
    public const string FunctionalHeader = "7DF";

    public static bool IsElevenBitHeader(string? header)
        => header is { Length: 3 }
           && header.All(Uri.IsHexDigit)
           && int.Parse(header, NumberStyles.HexNumber, CultureInfo.InvariantCulture) <= 0x7FF;

    public override string ToString() => $"{Header} {Command}";
}
