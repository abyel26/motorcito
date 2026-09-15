namespace Motorcito.Obd.Signals;

/// <summary>
/// How to extract one value from the data bytes of a reply.
///
/// Source-neutral on purpose: a catalog adapter translates its own format into
/// this, and nothing downstream knows which catalog a definition came from.
/// </summary>
public sealed record SignalDefinition
{
    /// <summary>The source's own identifier for the signal, kept for diagnostics.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The source's grouping, e.g. "Engine" or "Tires".</summary>
    public string? Category { get; init; }

    /// <summary>
    /// First bit of the value, counted from the most significant bit of the
    /// first data byte — the byte after the service and identifier echo.
    /// </summary>
    public int BitIndex { get; init; }

    public required int BitLength { get; init; }

    /// <summary>Byte order for multi-byte values. Big-endian unless set.</summary>
    public bool LittleEndian { get; init; }

    /// <summary>Two's complement over <see cref="BitLength"/> bits.</summary>
    public bool Signed { get; init; }

    public double Multiplier { get; init; } = 1;

    public double Divisor { get; init; } = 1;

    public double Offset { get; init; }

    /// <summary>Lowest valid scaled value. Anything below decodes to null.</summary>
    public double? Min { get; init; }

    /// <summary>Highest valid scaled value. Anything above decodes to null.</summary>
    public double? Max { get; init; }

    /// <summary>Scaled values at or below this are the source's "no reading" sentinel.</summary>
    public double? NullMin { get; init; }

    /// <summary>Scaled values at or above this are the source's "no reading" sentinel.</summary>
    public double? NullMax { get; init; }

    /// <summary>Unit of the decoded value, one of <see cref="SignalUnits"/>.</summary>
    public string Unit { get; init; } = SignalUnits.None;

    /// <summary>Labels for enumerated raw values, e.g. gear positions.</summary>
    public IReadOnlyDictionary<long, string>? ValueMap { get; init; }

    /// <summary>The Motorcito vocabulary key this maps to, or null if unmapped.</summary>
    public string? CanonicalKey { get; init; }

    public SignalQualifier? Qualifier { get; init; }

    /// <summary>
    /// Decodes the value from <paramref name="data"/>, or returns null when the
    /// reply is too short, the value is a sentinel, or it falls outside the
    /// source's own valid range. Null means "no reading" — never zero.
    /// </summary>
    public double? Decode(ReadOnlySpan<byte> data)
    {
        if (BitLength is < 1 or > 64 || BitIndex < 0)
            return null;

        if ((long)BitIndex + BitLength > (long)data.Length * 8)
            return null;

        ulong raw = 0;
        for (var i = 0; i < BitLength; i++)
        {
            var bit = BitIndex + i;
            var set = (data[bit / 8] >> (7 - bit % 8)) & 1;
            raw = raw << 1 | (uint)set;
        }

        if (LittleEndian && BitLength % 8 == 0 && BitLength > 8)
            raw = ReverseBytes(raw, BitLength / 8);

        long integer;
        if (Signed && BitLength < 64 && (raw & (1UL << (BitLength - 1))) != 0)
            integer = (long)(raw | ~((1UL << BitLength) - 1));
        else
            integer = (long)raw;

        var divisor = Divisor == 0 ? 1 : Divisor;
        var value = integer * Multiplier / divisor + Offset;

        if (NullMin is { } nullMin && value <= nullMin)
            return null;
        if (NullMax is { } nullMax && value >= nullMax)
            return null;

        // Catalog ranges are often written as the rounded top of the scale
        // (255 × 1373 / 100000 = 3.501 against a stated max of 3.5), so allow a
        // hair of tolerance rather than rejecting the top raw value.
        if (Min is { } min && value < min - Tolerance(min))
            return null;
        if (Max is { } max && value > max + Tolerance(max))
            return null;

        return value;
    }

    private static double Tolerance(double bound) => Math.Abs(bound) * 1e-3 + 1e-9;

    private static ulong ReverseBytes(ulong value, int byteCount)
    {
        ulong reversed = 0;
        for (var i = 0; i < byteCount; i++)
        {
            reversed = reversed << 8 | (value & 0xFF);
            value >>= 8;
        }
        return reversed;
    }
}
