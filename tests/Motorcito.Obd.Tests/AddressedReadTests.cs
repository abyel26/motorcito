using Motorcito.Obd;
using Motorcito.Obd.Signals;
using Motorcito.Obd.Testing;

namespace Motorcito.Obd.Tests;

/// <summary>
/// Manufacturer-specific reads through the session, against the simulated
/// MX-5, which answers on its engine (7E0) and TPMS (720) modules.
/// </summary>
public class AddressedReadTests
{
    private static Elm327Options Fast => new()
    {
        CommandTimeout = TimeSpan.FromSeconds(1),
        InitTimeout = TimeSpan.FromSeconds(1),
        RetryDelay = TimeSpan.Zero
    };

    private static async Task<(SimulatedObdAdapter Adapter, Elm327Session Session)> ConnectMx5()
    {
        var adapter = SimulatedObdAdapter.MazdaMx5Nd(new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter, Fast);
        await session.InitializeAsync();
        return (adapter, session);
    }

    [Fact]
    public async Task Initialisation_leaves_the_adapter_on_the_functional_header()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        Assert.Equal(SignalRequest.FunctionalHeader, session.CurrentHeader);
    }

    [Fact]
    public async Task Reads_a_value_standard_obd_does_not_offer()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        // The MX-5 does not report PID 0x5C...
        var supported = await session.ScanSupportedPidsAsync();
        Assert.False(supported.IsSupported(0x5C));

        // ...but its engine module answers the manufacturer read.
        var reply = await session.ReadIdentifierAsync(new SignalRequest("7E0", 0x22, 0x1310, 2));

        Assert.Equal(UdsOutcome.Positive, reply.Outcome);
        var oilTemp = new SignalDefinition { Id = "oil", Name = "oil", BitLength = 16, Divisor = 100, Offset = -40 }
            .Decode(reply.Data);
        Assert.InRange(oilTemp!.Value, 15, 110);
    }

    [Fact]
    public async Task A_module_that_does_not_know_the_identifier_refuses_it()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        var reply = await session.ReadIdentifierAsync(new SignalRequest("7E0", 0x22, 0xBEEF, 2));

        Assert.Equal(UdsOutcome.Negative, reply.Outcome);
        Assert.Equal(UdsResponse.NrcRequestOutOfRange, reply.NegativeResponseCode);
    }

    [Fact]
    public async Task An_address_with_no_module_is_silent()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        var reply = await session.ReadIdentifierAsync(new SignalRequest("7E5", 0x22, 0x1310, 2));

        Assert.Equal(UdsOutcome.NoData, reply.Outcome);
    }

    [Fact]
    public async Task A_batch_to_one_module_sets_the_header_once()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        await session.ReadIdentifierAsync(new SignalRequest("720", 0x22, 0x2A05, 2));
        await session.ReadIdentifierAsync(new SignalRequest("720", 0x22, 0x2A06, 2));
        await session.ReadIdentifierAsync(new SignalRequest("720", 0x22, 0x2A07, 2));

        Assert.Single(adapter.CommandLog, c => c == "ATSH720");
    }

    [Fact]
    public async Task Standard_polling_needs_the_functional_header_restored()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;
        var rpm = PidRegistry.Find(0x0C)!;

        await session.ReadIdentifierAsync(new SignalRequest("720", 0x22, 0x2A05, 2));

        // Still pointed at the TPMS module: nothing there speaks Mode 01.
        Assert.False((await session.ReadAsync(rpm)).Success);

        Assert.True(await session.RestoreFunctionalHeaderAsync());
        Assert.Equal(SignalRequest.FunctionalHeader, session.CurrentHeader);
        Assert.True((await session.ReadAsync(rpm)).Success);
    }

    [Fact]
    public async Task A_29_bit_header_is_refused_rather_than_sent_wrongly()
    {
        var (adapter, session) = await ConnectMx5();
        await using var _ = adapter;

        await Assert.ThrowsAsync<ArgumentException>(() => session.SetHeaderAsync("18DA10F1"));
    }
}
