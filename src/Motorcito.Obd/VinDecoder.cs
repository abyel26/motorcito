using System.Text;

namespace Motorcito.Obd;

/// <summary>
/// Decodes the VIN from a Mode 09 PID 02 reply.
///
/// The VIN is 17 ASCII characters and does not fit one CAN frame, so it arrives
/// multi-frame. <see cref="Elm327Response"/> has already stripped frame indices
/// and joined the payload by the time this runs.
/// </summary>
public static class VinDecoder
{
    /// <summary>Characters excluded from the VIN standard because they are confusable with 0/1.</summary>
    private const string Invalid = "IOQ";

    public static string? Decode(Elm327Response response)
    {
        if (!response.IsData || !PidDecoder.TryParseHex(response.Payload, out var bytes))
            return null;

        for (var i = 0; i + 2 < bytes.Length; i++)
        {
            if (bytes[i] != 0x49 || bytes[i + 1] != 0x02)
                continue;

            // Byte after the PID is the number of data items; some ECUs omit it.
            var cursor = i + 2;
            if (cursor < bytes.Length && bytes[cursor] <= 0x05)
                cursor++;

            var sb = new StringBuilder(17);
            for (; cursor < bytes.Length && sb.Length < 17; cursor++)
            {
                var c = (char)bytes[cursor];

                // Leading 0x00 bytes pad the first frame; skip until real text starts.
                if (bytes[cursor] == 0x00 && sb.Length == 0)
                    continue;

                if (IsValidVinChar(c))
                    sb.Append(c);
            }

            return sb.Length == 17 ? sb.ToString() : null;
        }

        return null;
    }

    private static bool IsValidVinChar(char c)
    {
        var upper = char.ToUpperInvariant(c);
        if (upper is >= '0' and <= '9')
            return true;
        return upper is >= 'A' and <= 'Z' && !Invalid.Contains(upper);
    }
}
