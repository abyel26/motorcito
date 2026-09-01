using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

public class PidDecoderTests
{
    private static DecodeResult Decode(string raw, byte pid, byte mode = PidDecoder.ModeLiveData)
        => PidDecoder.Decode(Elm327Response.Parse(raw), PidRegistry.Find(pid)!, mode);

    [Fact]
    public void Decodes_the_worked_example_from_the_reference_doc()
    {
        // docs/PID-REFERENCE.md: 410C1AF8 -> (256*26 + 248) / 4 = 6904 / 4 = 1726 rpm.
        // The doc originally stated 1728; that was an arithmetic slip, corrected
        // in the doc. This test pins the real value so it does not drift back.
        var result = Decode("410C1AF8", 0x0C);

        Assert.True(result.Success);
        Assert.Equal(1726, result.Reading!.Value);
    }

    [Theory]
    // pid,  response,      expected value
    [InlineData(0x05, "41057B", 83.0)]      // coolant: 123 - 40
    [InlineData(0x0D, "410D50", 80.0)]      // speed: 0x50 km/h
    [InlineData(0x0F, "410F32", 10.0)]      // IAT: 50 - 40
    [InlineData(0x0B, "410B62", 98.0)]      // MAP: raw kPa
    [InlineData(0x06, "410680", 0.0)]       // STFT1: centred at 0x80 = no correction
    [InlineData(0x10, "411009C4", 25.0)]    // MAF: 2500 / 100 g/s
    [InlineData(0x1F, "411F0E10", 3600.0)]  // runtime: 3600 s
    [InlineData(0x42, "4142376A", 14.186)]  // module voltage: 14186 / 1000
    [InlineData(0x5E, "415E012C", 15.0)]    // fuel rate: 300 / 20 L/h
    public void Applies_standard_formulas(byte pid, string response, double expected)
    {
        var result = Decode(response, pid);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Reading!.Value, precision: 3);
    }

    [Fact]
    public void Decodes_negative_fuel_trim()
    {
        // 0x70 = 112 -> (112 - 128) * 100 / 128 = -12.5 %  (running rich)
        var result = Decode("410670", 0x06);

        Assert.True(result.Success);
        Assert.Equal(-12.5, result.Reading!.Value, precision: 3);
    }

    [Fact]
    public void Rejects_a_reply_for_a_different_pid()
    {
        // Asked for RPM (0C), adapter answered speed (0D). Length is plausible;
        // only the header catches it.
        var result = Decode("410D50", 0x0C);

        Assert.False(result.Success);
        Assert.Equal(DecodeFailure.HeaderMismatch, result.Failure);
    }

    [Fact]
    public void Rejects_a_truncated_two_byte_frame()
    {
        // RPM needs two data bytes; a clone dropped one. Decoding what arrived
        // would yield a plausible but wrong rpm.
        var result = Decode("410C1A", 0x0C);

        Assert.False(result.Success);
        Assert.Equal(DecodeFailure.TooShort, result.Failure);
    }

    [Fact]
    public void Rejects_odd_length_hex()
    {
        var result = Decode("410C1AF", 0x0C);

        Assert.False(result.Success);
        Assert.Equal(DecodeFailure.Malformed, result.Failure);
    }

    [Fact]
    public void Reports_no_payload_for_status_only_replies()
    {
        Assert.Equal(DecodeFailure.NoPayload, Decode("NO DATA", 0x0C).Failure);
        Assert.Equal(DecodeFailure.NoPayload, Decode("UNABLE TO CONNECT", 0x0C).Failure);
    }

    [Fact]
    public void Finds_the_frame_when_several_ecus_answer()
    {
        // Two modules replied; both frames are concatenated after cleaning.
        var result = Decode("410C1AF8410C1AF8", 0x0C);

        Assert.True(result.Success);
        Assert.Equal(1726, result.Reading!.Value);
    }

    [Fact]
    public void Decodes_a_freeze_frame_reply()
    {
        // Mode 02 echoes mode 42, the PID, then the frame number, then data.
        var result = Decode("420C001AF8", 0x0C, PidDecoder.ModeFreezeFrame);

        Assert.True(result.Success);
        Assert.Equal(1726, result.Reading!.Value);
    }

    [Fact]
    public void Does_not_accept_a_live_data_frame_as_a_freeze_frame()
    {
        var result = Decode("410C1AF8", 0x0C, PidDecoder.ModeFreezeFrame);

        Assert.False(result.Success);
    }

    [Fact]
    public void Every_registry_entry_round_trips_its_own_byte_count()
    {
        // Guards against a registry entry declaring a byte count its formula
        // does not actually index — that would throw at runtime, mid-drive.
        foreach (var def in PidRegistry.All)
        {
            var data = new byte[def.ByteCount];
            var payload = $"41{def.Pid:X2}" + string.Concat(data.Select(b => b.ToString("X2")));

            var result = Decode(payload, def.Pid);

            Assert.True(result.Success, $"PID {def.Pid:X2} ({def.Name}) failed to decode a well-formed frame.");
        }
    }
}
