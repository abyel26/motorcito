using System.Globalization;

namespace Motorcito.Obd;

/// <summary>One decoded parameter value, with the bytes it came from kept for diagnostics.</summary>
public sealed record PidReading(PidDefinition Definition, double Value, byte[] RawBytes)
{
    public string Name => Definition.Name;
    public string Unit => Definition.Unit;
    public override string ToString() => $"{Name}: {Value:0.##} {Unit}";
}

public enum DecodeFailure
{
    None,
    /// <summary>Reply carried no payload (NO DATA, SEARCHING, bus error).</summary>
    NoPayload,
    /// <summary>Payload never contained the expected mode+PID header — this reply answers a different question.</summary>
    HeaderMismatch,
    /// <summary>Header found but fewer data bytes followed than the PID defines.</summary>
    TooShort,
    /// <summary>Payload had an odd number of hex characters; frame was truncated mid-byte.</summary>
    Malformed
}

public sealed record DecodeResult(PidReading? Reading, DecodeFailure Failure)
{
    public bool Success => Reading is not null;

    public static DecodeResult Ok(PidReading reading) => new(reading, DecodeFailure.None);
    public static DecodeResult Fail(DecodeFailure failure) => new(null, failure);
}

/// <summary>
/// Turns a cleaned adapter reply into a physical value.
///
/// The rule from <c>docs/PID-REFERENCE.md</c>: validate every response against
/// the expected mode+PID prefix and byte count before parsing; reject on
/// mismatch. Never trust length alone. A clone that drops a byte produces a
/// frame that still parses to a number — just the wrong one — and a wrong
/// number silently poisons a baseline for months.
/// </summary>
public static class PidDecoder
{
    /// <summary>Mode 01 (live data) request mode byte.</summary>
    public const byte ModeLiveData = 0x01;

    /// <summary>Mode 02 (freeze frame) request mode byte.</summary>
    public const byte ModeFreezeFrame = 0x02;

    public static DecodeResult Decode(Elm327Response response, PidDefinition definition, byte mode = ModeLiveData)
    {
        if (!response.IsData || response.Payload.Length == 0)
            return DecodeResult.Fail(DecodeFailure.NoPayload);

        if (!TryParseHex(response.Payload, out var bytes))
            return DecodeResult.Fail(DecodeFailure.Malformed);

        // A response echoes the request mode with 0x40 added, then the PID.
        // Mode 02 additionally echoes the frame number.
        var responseMode = (byte)(mode + 0x40);
        var headerLength = mode == ModeFreezeFrame ? 3 : 2;

        // Scan rather than assume position zero: several ECUs may answer the
        // same request, concatenating frames, and some adapters prepend chatter
        // that survived cleaning.
        for (var i = 0; i + headerLength <= bytes.Length; i++)
        {
            if (bytes[i] != responseMode || bytes[i + 1] != definition.Pid)
                continue;

            var dataStart = i + headerLength;
            var available = bytes.Length - dataStart;
            if (available < definition.ByteCount)
                return DecodeResult.Fail(DecodeFailure.TooShort);

            var data = new byte[definition.ByteCount];
            Array.Copy(bytes, dataStart, data, 0, definition.ByteCount);

            return DecodeResult.Ok(new PidReading(definition, definition.Decode(data), data));
        }

        return DecodeResult.Fail(DecodeFailure.HeaderMismatch);
    }

    /// <summary>Parses an even-length hex string. Returns false on odd length — a truncated frame, not a recoverable one.</summary>
    public static bool TryParseHex(string hex, out byte[] bytes)
    {
        if (hex.Length == 0 || hex.Length % 2 != 0)
        {
            bytes = [];
            return false;
        }

        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
            {
                bytes = [];
                return false;
            }
        }

        bytes = result;
        return true;
    }
}
