using System.Text;

namespace Motorcito.Obd;

public enum DtcMode
{
    /// <summary>Mode 03 — confirmed, MIL-illuminating codes.</summary>
    Stored,
    /// <summary>Mode 07 — failed once, not yet confirmed. Early warning most apps ignore.</summary>
    Pending,
    /// <summary>Mode 0A — cannot be cleared by disconnecting the battery.</summary>
    Permanent
}

/// <summary>
/// Decodes trouble codes from Modes 03, 07 and 0A.
///
/// Each code is two bytes. The top two bits select the system letter, the next
/// two are the first digit, and the remaining twelve bits are three hex digits.
/// </summary>
public static class DtcDecoder
{
    private static readonly char[] SystemLetters = ['P', 'C', 'B', 'U'];

    public static IReadOnlyList<string> Decode(Elm327Response response, DtcMode mode)
    {
        if (!response.IsData || !PidDecoder.TryParseHex(response.Payload, out var bytes))
            return [];

        var responseMode = mode switch
        {
            DtcMode.Stored => (byte)0x43,
            DtcMode.Pending => (byte)0x47,
            DtcMode.Permanent => (byte)0x4A,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        var codes = new List<string>();

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != responseMode)
                continue;

            var cursor = i + 1;

            // ISO 15765 (CAN) prefixes the list with a code count; the older
            // protocols do not. Distinguish by whether the remaining length is
            // consistent with a count byte followed by that many code pairs.
            if (cursor < bytes.Length)
            {
                var candidateCount = bytes[cursor];
                var remaining = bytes.Length - cursor - 1;
                if (candidateCount > 0 && remaining >= candidateCount * 2)
                    cursor++;
            }

            for (; cursor + 1 < bytes.Length; cursor += 2)
            {
                var code = DecodePair(bytes[cursor], bytes[cursor + 1]);

                // 0000 is the padding an ECU sends to fill a frame when it has
                // fewer codes than the frame holds — not a real fault.
                if (code is not null && !codes.Contains(code))
                    codes.Add(code);
            }

            break;
        }

        return codes;
    }

    /// <summary>Decodes one two-byte code. Returns null for the all-zero padding value.</summary>
    public static string? DecodePair(byte a, byte b)
    {
        if (a == 0 && b == 0)
            return null;

        var letter = SystemLetters[(a & 0xC0) >> 6];
        var firstDigit = (a & 0x30) >> 4;
        var secondDigit = a & 0x0F;
        var lastTwo = b;

        return new StringBuilder(5)
            .Append(letter)
            .Append(firstDigit)
            .Append(secondDigit.ToString("X1"))
            .Append(lastTwo.ToString("X2"))
            .ToString();
    }
}
