using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

/// <summary>
/// Every case here is behaviour a cheap ELM327 clone exhibits in the field.
/// None of it may crash, and none of it may produce a reading.
/// </summary>
public class Elm327ResponseTests
{
    [Fact]
    public void Parses_clean_response()
    {
        var r = Elm327Response.Parse("410C1AF8");

        Assert.Equal(Elm327Status.Data, r.Status);
        Assert.Equal("410C1AF8", r.Payload);
    }

    [Fact]
    public void Strips_spaces_when_ATS0_is_ignored()
    {
        var r = Elm327Response.Parse("41 0C 1A F8");

        Assert.True(r.IsData);
        Assert.Equal("410C1AF8", r.Payload);
    }

    [Fact]
    public void Drops_command_echo_when_ATE0_is_ignored()
    {
        var r = Elm327Response.Parse("010C\r410C1AF8", command: "010C");

        Assert.True(r.IsData);
        Assert.Equal("410C1AF8", r.Payload);
    }

    [Fact]
    public void Drops_spaced_command_echo()
    {
        var r = Elm327Response.Parse("01 0C\r41 0C 1A F8", command: "010C");

        Assert.True(r.IsData);
        Assert.Equal("410C1AF8", r.Payload);
    }

    [Fact]
    public void Keeps_data_that_follows_SEARCHING()
    {
        var r = Elm327Response.Parse("SEARCHING...\r410C1AF8");

        Assert.True(r.IsData);
        Assert.Equal("410C1AF8", r.Payload);
    }

    [Fact]
    public void Reports_SEARCHING_alone_as_transient()
    {
        var r = Elm327Response.Parse("SEARCHING...");

        Assert.Equal(Elm327Status.Searching, r.Status);
        Assert.True(r.IsTransient);
    }

    [Fact]
    public void NoData_is_not_retried()
    {
        var r = Elm327Response.Parse("NO DATA");

        Assert.Equal(Elm327Status.NoData, r.Status);
        // An unsupported PID answers NO DATA forever; retrying wastes the budget.
        Assert.False(r.IsTransient);
    }

    [Theory]
    [InlineData("UNABLE TO CONNECT", Elm327Status.UnableToConnect)]
    [InlineData("?", Elm327Status.NotUnderstood)]
    [InlineData("STOPPED", Elm327Status.Stopped)]
    [InlineData("CAN ERROR", Elm327Status.BusError)]
    [InlineData("BUFFER FULL", Elm327Status.BusError)]
    [InlineData("BUS INIT: ...", Elm327Status.BusInit)]
    [InlineData("OK", Elm327Status.Ok)]
    [InlineData("", Elm327Status.Empty)]
    public void Classifies_adapter_status_words(string raw, Elm327Status expected)
    {
        Assert.Equal(expected, Elm327Response.Parse(raw).Status);
    }

    [Fact]
    public void Joins_multiframe_lines_and_strips_frame_indices()
    {
        // Mode 09 VIN replies arrive as indexed frames.
        var r = Elm327Response.Parse("0:49020157\r1:4D42413331\r2:4A353637");

        Assert.True(r.IsData);
        Assert.Equal("490201574D42413331" + "4A353637", r.Payload);
    }

    [Fact]
    public void Bus_error_outranks_no_data_in_a_mixed_reply()
    {
        var r = Elm327Response.Parse("NO DATA\rCAN ERROR");

        Assert.Equal(Elm327Status.BusError, r.Status);
    }

    [Fact]
    public void Null_and_whitespace_do_not_throw()
    {
        Assert.Equal(Elm327Status.Empty, Elm327Response.Parse(null!).Status);
        Assert.Equal(Elm327Status.Empty, Elm327Response.Parse("   \r\n  ").Status);
    }
}

public class MultiFrameTests
{
    /// <summary>
    /// The layout a real CAN vehicle returns for Mode 09 PID 02: an ISO-TP
    /// total-length line, then indexed frames.
    /// </summary>
    private const string RealVinReply = """
        0902
        014
        0: 49 02 01 57 42 53
        1: 38 4D 39 43 35 30 4A
        2: 35 4B 31 32 33 34 35
        """;

    [Fact]
    public void The_iso_tp_length_line_is_not_treated_as_payload()
    {
        var response = Elm327Response.Parse(RealVinReply, "0902");

        // "014" is three characters. Appending it shifts every following byte
        // by a nibble, which is why a car that answered correctly reported its
        // VIN as unavailable.
        Assert.Equal(Elm327Status.Data, response.Status);
        Assert.StartsWith("490201", response.Payload);
        Assert.Equal(0, response.Payload.Length % 2);
    }

    [Fact]
    public void A_multi_frame_vin_decodes()
    {
        var vin = VinDecoder.Decode(Elm327Response.Parse(RealVinReply, "0902"));

        Assert.Equal("WBS8M9C50J5K12345", vin);
    }

    [Fact]
    public void A_short_single_frame_payload_is_still_kept()
    {
        // The length line is only dropped in replies that actually have frames,
        // so a genuinely short answer must survive untouched.
        var response = Elm327Response.Parse("41 0D 3C", "010D");

        Assert.Equal(Elm327Status.Data, response.Status);
        Assert.Equal("410D3C", response.Payload);
    }

    [Fact]
    public void Multi_frame_replies_still_work_without_a_length_line()
    {
        // Some adapters omit it; the frames alone must decode.
        var vin = VinDecoder.Decode(Elm327Response.Parse("""
            0: 49 02 01 57 42 53
            1: 38 4D 39 43 35 30 4A
            2: 35 4B 31 32 33 34 35
            """, "0902"));

        Assert.Equal("WBS8M9C50J5K12345", vin);
    }
}
