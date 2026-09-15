using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

public class UdsResponseTests
{
    private static readonly SignalRequest OilTemp = new("7E0", 0x22, 0x1310, 2);

    private static UdsResponse Parse(string raw, SignalRequest request)
        => UdsResponse.Parse(Elm327Response.Parse(raw, request.Command), request);

    [Fact]
    public void A_positive_reply_yields_the_bytes_after_the_identifier()
    {
        var reply = Parse("62131096", OilTemp);

        Assert.Equal(UdsOutcome.Positive, reply.Outcome);
        Assert.Equal([0x96], reply.Data);
    }

    [Fact]
    public void A_refusal_is_not_mistaken_for_data()
    {
        // "7F 22 31" is valid hex, so the adapter layer reports it as data.
        // Decoding it as a reading would turn the NRC into a temperature.
        var reply = Parse("7F2231", OilTemp);

        Assert.Equal(UdsOutcome.Negative, reply.Outcome);
        Assert.Equal(UdsResponse.NrcRequestOutOfRange, reply.NegativeResponseCode);
        Assert.Empty(reply.Data);
    }

    [Fact]
    public void A_response_pending_notice_does_not_hide_the_real_answer()
    {
        var reply = Parse("7F2278\r62131096", OilTemp);

        Assert.Equal(UdsOutcome.Positive, reply.Outcome);
        Assert.Equal([0x96], reply.Data);
    }

    [Fact]
    public void No_data_means_nothing_answered_at_that_address()
        => Assert.Equal(UdsOutcome.NoData, Parse("NO DATA", OilTemp).Outcome);

    [Fact]
    public void An_empty_reply_is_a_link_problem_not_a_refusal()
        => Assert.Equal(UdsOutcome.NoResponse, Parse(string.Empty, OilTemp).Outcome);

    [Fact]
    public void A_reply_for_a_different_identifier_is_rejected()
        => Assert.Equal(UdsOutcome.Malformed, Parse("62131196", OilTemp).Outcome);

    [Fact]
    public void Service_21_uses_a_one_byte_local_identifier()
    {
        var request = new SignalRequest("750", 0x21, 0x30, 1);

        var reply = Parse("613001020304", request);

        Assert.Equal(UdsOutcome.Positive, reply.Outcome);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], reply.Data);
    }

    [Fact]
    public void A_multi_frame_positive_reply_is_joined_before_parsing()
    {
        var reply = Parse("008\r0: 62 13 10 01 02 03 04\r1: 05 00 00 00 00 00 00", OilTemp);

        Assert.Equal(UdsOutcome.Positive, reply.Outcome);
        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05], reply.Data.Take(5));
    }

    [Theory]
    [InlineData(0x31, "not supported (7F 31)")]
    [InlineData(0x22, "not available right now (7F 22)")]
    [InlineData(0x33, "locked (7F 33)")]
    public void Refusals_explain_themselves(byte nrc, string expected)
    {
        var reply = Parse($"7F22{nrc:X2}", OilTemp);

        Assert.Equal(expected, reply.Describe());
    }
}
