namespace Motorcito.Obd;

public enum UdsOutcome
{
    /// <summary>The ECU answered with data for the identifier.</summary>
    Positive,

    /// <summary>The ECU refused, with a negative response code saying why.</summary>
    Negative,

    /// <summary>NO DATA: nothing at that address answered.</summary>
    NoData,

    /// <summary>A timeout, bus error or other link-level failure. Worth retrying later; says nothing about the identifier.</summary>
    NoResponse,

    /// <summary>Hex came back, but not a reply to this request.</summary>
    Malformed
}

/// <summary>
/// A parsed reply to a manufacturer-specific read (service 0x21 or 0x22), or
/// any other request addressed by <see cref="SignalRequest"/>.
///
/// Exists because <see cref="Elm327Response"/> cannot tell success from
/// refusal: "7F 22 31" is valid hex, so the adapter layer reports it as data.
/// Treating a refusal as a reading would decode the NRC bytes as a value.
/// </summary>
public sealed record UdsResponse(
    UdsOutcome Outcome,
    byte[] Data,
    byte? NegativeResponseCode,
    Elm327Status TransportStatus)
{
    public const byte NegativeResponseService = 0x7F;

    public const byte NrcServiceNotSupported = 0x11;
    public const byte NrcSubFunctionNotSupported = 0x12;
    public const byte NrcConditionsNotCorrect = 0x22;
    public const byte NrcRequestOutOfRange = 0x31;
    public const byte NrcSecurityAccessDenied = 0x33;
    public const byte NrcResponsePending = 0x78;

    public bool IsPositive => Outcome == UdsOutcome.Positive;

    /// <summary>A short human-readable reason, for diagnostics rather than logic.</summary>
    public string Describe() => Outcome switch
    {
        UdsOutcome.Positive => "answered",
        UdsOutcome.NoData => "no answer at this address",
        UdsOutcome.NoResponse => $"link problem ({TransportStatus})",
        UdsOutcome.Malformed => "unrecognised reply",
        _ => NegativeResponseCode switch
        {
            NrcRequestOutOfRange => "not supported (7F 31)",
            NrcConditionsNotCorrect => "not available right now (7F 22)",
            NrcSecurityAccessDenied => "locked (7F 33)",
            NrcServiceNotSupported => "service not supported (7F 11)",
            NrcSubFunctionNotSupported => "not supported (7F 12)",
            NrcResponsePending => "no final answer (7F 78)",
            { } code => $"refused (7F {code:X2})",
            null => "refused"
        }
    };

    public static UdsResponse Parse(Elm327Response response, SignalRequest request)
    {
        if (response.Status == Elm327Status.NoData)
            return new UdsResponse(UdsOutcome.NoData, [], null, response.Status);

        if (!response.IsData)
            return new UdsResponse(UdsOutcome.NoResponse, [], null, response.Status);

        if (!PidDecoder.TryParseHex(response.Payload, out var bytes))
            return new UdsResponse(UdsOutcome.Malformed, [], null, response.Status);

        var positiveService = (byte)(request.Service + 0x40);

        // A positive reply wins wherever it appears. An ECU that needs longer
        // sends "7F 22 78" (response pending) first, and the adapter can hand
        // both back in one reply — the pending notice is not the answer.
        // Offset 0 is checked first so a data byte that happens to equal the
        // positive service id cannot shadow the real header.
        if (TryFindPositive(bytes, 0, positiveService, request, out var data))
            return new UdsResponse(UdsOutcome.Positive, data, null, response.Status);

        for (var i = 1; i < bytes.Length; i++)
        {
            if (TryFindPositive(bytes, i, positiveService, request, out data))
                return new UdsResponse(UdsOutcome.Positive, data, null, response.Status);
        }

        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] == NegativeResponseService && bytes[i + 1] == request.Service)
                return new UdsResponse(UdsOutcome.Negative, [], bytes[i + 2], response.Status);
        }

        return new UdsResponse(UdsOutcome.Malformed, [], null, response.Status);
    }

    private static bool TryFindPositive(byte[] bytes, int offset, byte positiveService, SignalRequest request, out byte[] data)
    {
        data = [];
        var headerEnd = offset + 1 + request.IdentifierLength;

        if (headerEnd > bytes.Length || bytes[offset] != positiveService)
            return false;

        var identifierMatches = request.IdentifierLength == 1
            ? bytes[offset + 1] == request.Identifier
            : bytes[offset + 1] == request.Identifier >> 8 && bytes[offset + 2] == (request.Identifier & 0xFF);

        if (!identifierMatches)
            return false;

        data = bytes[headerEnd..];
        return true;
    }
}
