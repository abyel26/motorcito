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
