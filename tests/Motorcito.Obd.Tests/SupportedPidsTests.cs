using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

public class SupportedPidsTests
{
    // Mask bytes BE 3F 90 03 over PIDs 01-20:
    //   BE = 1011 1110 -> 01, 03, 04, 05, 06, 07
    //   3F = 0011 1111 -> 0B, 0C, 0D, 0E, 0F, 10
    //   90 = 1001 0000 -> 11, 14
    //   03 = 0000 0011 -> 1F, and the "next range available" flag at 20
    private const string Mask0100 = "4100BE3F9003";

    [Fact]
    public void Decodes_a_capability_bitmask()
    {
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse(Mask0100), 0x00);

        Assert.True(supported.IsSupported(0x0C));   // RPM
        Assert.True(supported.IsSupported(0x06));   // STFT1
        Assert.True(supported.IsSupported(0x14));   // O2 S1
        Assert.False(supported.IsSupported(0x02));
        Assert.False(supported.IsSupported(0x12));
    }

    [Fact]
    public void Reports_whether_the_next_range_is_available()
    {
        var supported = new SupportedPids();

        var hasNext = supported.AddMask(Elm327Response.Parse(Mask0100), 0x00);

        Assert.True(hasNext);
        // 0x20 is the continuation flag, not a real parameter — it must not
        // end up in the poll set.
        Assert.False(supported.IsSupported(0x20));
    }

    [Fact]
    public void Maps_bits_to_the_correct_pids_in_a_higher_range()
    {
        // Byte 0 of the 0140 mask covers PIDs 41-48; MSB set means PID 41.
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse("414080000000"), 0x40);

        Assert.True(supported.IsSupported(0x41));
        Assert.False(supported.IsSupported(0x42));
    }

    [Fact]
    public void Phase_zero_gate_passes_when_trims_and_o2_are_present()
    {
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse(Mask0100), 0x00);

        var gate = supported.EvaluatePhaseZeroGate();

        Assert.True(gate.FuelTrimsPresent);
        Assert.True(gate.O2Present);
        Assert.True(gate.Passed);
        Assert.Empty(gate.Missing);
    }

    [Fact]
    public void Phase_zero_gate_fails_and_names_what_is_missing()
    {
        // Near-universal PIDs present, but no fuel trims and no O2.
        //   30 = 0011 0000 -> 04, 05
        //   3F = 0011 1111 -> 0B..10
        //   80 = 1000 0000 -> 11
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse("4100303F8000"), 0x00);

        var gate = supported.EvaluatePhaseZeroGate();

        Assert.False(gate.FuelTrimsPresent);
        Assert.False(gate.Passed);
        Assert.False(gate.ScanLooksImplausible);
        Assert.Equal([0x06, 0x07, 0x14], gate.Missing);
    }

    [Fact]
    public void Flags_an_empty_scan_as_implausible_rather_than_as_a_limited_car()
    {
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse("410000000000"), 0x00);

        var gate = supported.EvaluatePhaseZeroGate();

        Assert.True(gate.ScanLooksImplausible);
        Assert.False(gate.Passed);
    }

    [Fact]
    public void Ignores_a_mask_reply_that_did_not_arrive()
    {
        var supported = new SupportedPids();

        Assert.False(supported.AddMask(Elm327Response.Parse("NO DATA"), 0x00));
        Assert.Equal(0, supported.Count);
    }

    [Fact]
    public void Separates_decodable_pids_from_unrecognised_ones()
    {
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse(Mask0100), 0x00);

        // PID 01 (monitor status) is reported by the car but has no decoder yet.
        Assert.Contains((byte)0x01, supported.UnknownSupported());
        Assert.DoesNotContain(PidRegistry.Find(0x0C), supported.KnownSupported().Where(d => d.Pid != 0x0C));
        Assert.Contains(supported.KnownSupported(), d => d.Pid == 0x0C);
    }

    [Fact]
    public void Serialises_for_the_vehicles_table()
    {
        var supported = new SupportedPids();
        supported.AddMask(Elm327Response.Parse("4100C0000000"), 0x00);

        Assert.Equal("[\"01\",\"02\"]", supported.ToJson());
    }
}
