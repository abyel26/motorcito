using Motorcito.Obd;
using Motorcito.Obd.Signals;
using Motorcito.Profiles.Obdb;

namespace Motorcito.Profiles.Obdb.Tests;

public class ObdbProfileSourceTests
{
    private static readonly ObdbProfileSource Source = new();

    private static IReadOnlyList<SignalCommand> Mx5(int? year) =>
        Source.CommandsFor(new VehicleIdentity(null, "Mazda", "MX-5", year));

    private static SignalCommand Find(IEnumerable<SignalCommand> commands, string header, ushort identifier)
        => commands.Single(c => c.Header == header && c.Identifier == identifier);

    /// <summary>
    /// OBDb test cases record replies with headers on: CAN id, then the ISO-TP
    /// length byte. The app runs with headers off, so strip both to get what
    /// the parser actually sees.
    /// </summary>
    private static UdsResponse Reply(string obdbTestResponse, SignalCommand command)
    {
        var withoutHeaderAndLength = obdbTestResponse[5..];
        return UdsResponse.Parse(Elm327Response.Parse(withoutHeaderAndLength), command.ToRequest());
    }

    [Fact]
    public void Dependency_injection_can_only_construct_the_source_that_loads_vendored_data()
    {
        // A DI container picks the public constructor with the most parameters
        // it can satisfy, and satisfies any IEnumerable<T> with an empty one.
        // A second public constructor taking signalsets was chosen in the app
        // and built a source with no vehicles — oil temperature silently
        // became "not available for this car".
        var constructor = Assert.Single(typeof(ObdbProfileSource).GetConstructors());

        Assert.Empty(constructor.GetParameters());
    }

    [Fact]
    public void The_vendored_mx5_signalset_loads()
    {
        Assert.Contains(Source.Models, m => m.Make == "Mazda" && m.Model == "MX-5");
        Assert.NotEmpty(Mx5(null));
    }

    // Expected values are copied from OBDb's own test cases for real MX-5s
    // (tests/test_cases/2016 and 2024, command 7E0.7E8.221310).
    [Theory]
    [InlineData("7E8056213100EE5", -1.87)]
    [InlineData("7E80562131019FF", 26.55)]
    [InlineData("7E8056213103F5D", 122.21)]
    [InlineData("7E805621310360A", 98.34)]
    public void Oil_temperature_decodes_like_the_real_car_replies(string response, double expectedCelsius)
    {
        var oilTemp = Find(Mx5(2018), "7E0", 0x1310);

        var reading = Assert.Single(SignalDecoder.Decode(oilTemp, Reply(response, oilTemp)));

        Assert.Equal(SignalCheck.Plausible, reading.Check);
        Assert.Equal(expectedCelsius, reading.Reading!.Value, precision: 2);
        Assert.Equal("oil_temp", reading.Reading.Key);
    }

    [Fact]
    public void Tire_pressure_from_the_real_car_converts_to_kilopascals()
    {
        // OBDb 2016 test case 720.728.222A05: 0xFF → 3.5 bar.
        var wheel1 = Find(Mx5(2018), "720", 0x2A05);

        var reading = Assert.Single(SignalDecoder.Decode(wheel1, Reply("72804622A05FF", wheel1)));

        Assert.Equal(SignalCheck.Plausible, reading.Check);
        Assert.Equal(350.1, reading.Reading!.Value, precision: 1);
    }

    [Fact]
    public void Numbered_wheels_keep_their_number_rather_than_a_guessed_position()
    {
        var signal = Find(Mx5(2018), "720", 0x2A05).Signals.Single();

        Assert.Equal("tire_pressure", signal.CanonicalKey);
        Assert.Equal(SignalQualifier.WheelNumber(1), signal.Qualifier);
        Assert.False(signal.Qualifier!.Value.IsWheelPositionKnown);
    }

    [Fact]
    public void Named_wheels_map_to_their_position()
    {
        var signals = Find(Mx5(2010), "720", 0xC901).Signals;

        Assert.Equal(SignalQualifier.FrontLeft, signals.Single(s => s.Id == "MX5_TP_FL").Qualifier);
        Assert.Equal(SignalQualifier.RearRight, signals.Single(s => s.Id == "MX5_TP_RR").Qualifier);
    }

    [Fact]
    public void Model_year_filters_select_the_right_generation()
    {
        var nd = Mx5(2018);
        var nc = Mx5(2012);

        Assert.Contains(nd, c => c.Identifier == 0x1310);
        Assert.DoesNotContain(nd, c => c.Identifier == 0xC901);

        Assert.DoesNotContain(nc, c => c.Identifier == 0x1310);
        Assert.Contains(nc, c => c.Identifier == 0xC901);
    }

    [Fact]
    public void An_unknown_year_includes_every_generation_for_the_car_to_decide()
    {
        var all = Mx5(null);

        Assert.Contains(all, c => c.Identifier == 0x1310);
        Assert.Contains(all, c => c.Identifier == 0xC901);
    }

    [Fact]
    public void Another_make_gets_none_of_these_definitions()
        => Assert.Empty(Source.CommandsFor(new VehicleIdentity(null, "Toyota", null, 2018)));

    [Fact]
    public void An_unknown_make_is_offered_every_definition_to_probe()
        => Assert.NotEmpty(Source.CommandsFor(new VehicleIdentity(null, null, null, null)));

    [Fact]
    public void Every_mx5_command_is_a_read_the_app_can_send()
        => Assert.All(Mx5(null), c => Assert.True(c.IsSendable, c.ToString()));

    [Fact]
    public void Bit_flags_decode_to_zero_or_one()
    {
        var fan = Find(Mx5(2018), "7E0", 0x0967);

        Assert.Equal(1, fan.Signals.Single().Decode([0b0000_0100]));
        Assert.Equal(0, fan.Signals.Single().Decode([0b0000_0000]));
    }

    [Theory]
    [InlineData("Mazda-MX-5", "Mazda", "MX-5")]
    [InlineData("Land-Rover-Defender", "Land-Rover", "Defender")]
    [InlineData("Mercedes-Benz-C-Class", "Mercedes-Benz", "C-Class")]
    public void Repository_names_split_into_make_and_model(string repository, string make, string model)
        => Assert.Equal((make, model), ObdbProfileSource.SplitRepositoryName(repository));
}
