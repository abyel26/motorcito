using Motorcito.Obd.Signals;
using Motorcito.Profiles.Obdb;

namespace Motorcito.Profiles.Obdb.Tests;

public class ObdbCanonicalMapTests
{
    [Theory]
    [InlineData("Engine oil temperature", "Engine", "°C", "oil_temp")]
    [InlineData("Engine oil pressure", "Engine", "kPa", "oil_pressure")]
    [InlineData("Transmission fluid temperature", "Transmission", "°C", "trans_temp")]
    [InlineData("Odometer", "Trips", "km", "odometer")]
    [InlineData("HV battery state of health", "Battery", "%", "hv_soh")]
    public void Recognises_consumer_signals(string name, string path, string unit, string expected)
        => Assert.Equal(expected, ObdbCanonicalMap.Resolve(name, path, null, unit)?.Key);

    [Fact]
    public void Valve_timing_oil_is_not_engine_oil()
        => Assert.Null(ObdbCanonicalMap.Resolve("VVT oil temperature", "Engine", null, SignalUnits.Celsius));

    [Fact]
    public void A_tire_warning_lamp_is_not_a_pressure_reading()
        => Assert.Null(ObdbCanonicalMap.Resolve("Tire pressure warning", "Tires", null, SignalUnits.OnOff));

    [Fact]
    public void A_tire_reading_with_no_identifiable_wheel_stays_unmapped()
        => Assert.Null(ObdbCanonicalMap.Resolve("Tire pressure", "Tires", null, SignalUnits.Psi));

    [Theory]
    [InlineData("Rear left inner tire pressure", "RLI")]
    [InlineData("Spare tire pressure", "SPARE")]
    [InlineData("Tire pressure", "FR")] // resolved from the suggested metric below
    public void Wheel_positions_come_from_the_name_or_suggested_metric(string name, string position)
    {
        var suggested = position == "FR" ? "frontRightTirePressure" : null;

        var match = ObdbCanonicalMap.Resolve(name, "Tires", suggested, SignalUnits.Psi);

        Assert.Equal(position, match?.Qualifier?.Value);
    }

    [Theory]
    [InlineData("Driver seatbelt buckle status")]
    [InlineData("Steering angle")]
    [InlineData("Driver door open")]
    public void Driver_behaviour_is_recognised_so_it_can_be_blocked(string name)
    {
        var match = ObdbCanonicalMap.Resolve(name, "Control", null, SignalUnits.OnOff);

        Assert.NotNull(match);
        Assert.Equal(SignalPrivacy.DriverBehaviour, CanonicalSignals.Find(match.Value.Key)!.Privacy);
    }
}
