using Motorcito.Obd;
using Motorcito.Obd.Signals;
using Motorcito.Obd.Testing;

namespace Motorcito.Obd.Tests;

public class ReadOnlyServicePolicyTests
{
    [Theory]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x21)]
    [InlineData(0x22)]
    public void Read_services_are_allowed(byte service)
        => Assert.True(ReadOnlyServicePolicy.IsAllowed(service));

    [Theory]
    [InlineData(0x04)] // clear DTCs (OBD)
    [InlineData(0x10)] // diagnostic session control
    [InlineData(0x11)] // ECU reset
    [InlineData(0x14)] // clear diagnostic information
    [InlineData(0x27)] // security access
    [InlineData(0x2E)] // write data by identifier
    [InlineData(0x31)] // routine control
    [InlineData(0x3B)] // write data by local identifier
    public void Anything_that_changes_the_car_is_refused(byte service)
    {
        Assert.False(ReadOnlyServicePolicy.IsAllowed(service));
        Assert.Throws<ServiceNotAllowedException>(() => ReadOnlyServicePolicy.EnsureAllowed(service));
    }

    [Fact]
    public async Task A_write_request_never_reaches_the_adapter()
    {
        await using var adapter = new SimulatedObdAdapter(quirks: new SimulatorQuirks { Latency = TimeSpan.Zero });
        await adapter.ConnectAsync();
        var session = new Elm327Session(adapter);
        await session.InitializeAsync();
        var sentBefore = adapter.CommandLog.Count;

        var write = new SignalRequest("7E0", 0x2E, 0x1310, 2);

        await Assert.ThrowsAsync<ServiceNotAllowedException>(() => session.ReadIdentifierAsync(write));

        // Not even the header change went out.
        Assert.Equal(sentBefore, adapter.CommandLog.Count);
    }

    [Fact]
    public void A_catalog_command_naming_a_write_service_is_not_sendable()
    {
        var command = new SignalCommand
        {
            SourceId = "test",
            Header = "7E0",
            Service = 0x2E,
            Identifier = 0x1310,
            IdentifierLength = 2,
            Signals = [],
        };

        Assert.False(command.IsSendable);
        Assert.Throws<InvalidOperationException>(() => command.ToRequest());
    }
}
