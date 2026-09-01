using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

/// <summary>
/// Hand-computed expectations. The simulator encodes DTCs with the inverse of
/// this logic, so a round-trip test would pass even if both halves were wrong —
/// these pin the decoder against the standard directly.
/// </summary>
public class DtcDecoderTests
{
    [Theory]
    [InlineData(0x01, 0x71, "P0171")]   // 00 -> P, the reference doc's example
    [InlineData(0x03, 0x00, "P0300")]   // random misfire
    [InlineData(0x11, 0x23, "P1123")]   // manufacturer-specific first digit
    [InlineData(0x41, 0x23, "C0123")]   // 01 -> C, chassis
    [InlineData(0x81, 0x23, "B0123")]   // 10 -> B, body
    [InlineData(0xC1, 0x23, "U0123")]   // 11 -> U, network
    [InlineData(0x0A, 0xBC, "P0ABC")]   // hex digits preserved
    public void Decodes_code_pairs(byte a, byte b, string expected)
    {
        Assert.Equal(expected, DtcDecoder.DecodePair(a, b));
    }

    [Fact]
    public void Treats_all_zero_as_padding_not_a_fault()
    {
        Assert.Null(DtcDecoder.DecodePair(0x00, 0x00));
    }

    [Fact]
    public void Decodes_a_can_response_with_a_count_byte()
    {
        var response = Elm327Response.Parse("4302017103 00");

        Assert.Equal(["P0171", "P0300"], DtcDecoder.Decode(response, DtcMode.Stored));
    }

    [Fact]
    public void Drops_padding_from_a_partially_filled_frame()
    {
        var response = Elm327Response.Parse("43010171000000000000");

        Assert.Equal(["P0171"], DtcDecoder.Decode(response, DtcMode.Stored));
    }

    [Fact]
    public void Distinguishes_pending_from_stored_by_response_mode()
    {
        var pending = Elm327Response.Parse("4701 0171");

        Assert.Equal(["P0171"], DtcDecoder.Decode(pending, DtcMode.Pending));
        // A Mode 07 reply must not be read as a Mode 03 answer.
        Assert.Empty(DtcDecoder.Decode(pending, DtcMode.Stored));
    }

    [Fact]
    public void Returns_empty_for_status_only_replies()
    {
        Assert.Empty(DtcDecoder.Decode(Elm327Response.Parse("NO DATA"), DtcMode.Stored));
        Assert.Empty(DtcDecoder.Decode(Elm327Response.Parse("SEARCHING..."), DtcMode.Stored));
    }
}
