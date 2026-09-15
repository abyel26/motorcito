using Motorcito.Obd;

namespace Motorcito.Obd.Tests;

public class VinInfoTests
{
    [Fact]
    public void Reads_make_and_model_year_from_a_japanese_built_vin()
    {
        var info = VinInfo.Parse("JM1NDAD75J0100001", currentYear: 2026);

        Assert.NotNull(info);
        Assert.Equal("Mazda", info.Make);
        Assert.Equal(2018, info.ModelYear);
    }

    [Fact]
    public void A_north_american_vin_with_a_digit_in_position_7_is_the_earlier_cycle()
    {
        var info = VinInfo.Parse("1HGCM82633A004352", currentYear: 2026);

        Assert.Equal("Honda", info!.Make);
        Assert.Equal(2003, info.ModelYear);
    }

    [Fact]
    public void A_north_american_vin_with_a_letter_in_position_7_is_the_later_cycle()
    {
        var info = VinInfo.Parse("5YJ3E1EA7KF000316", currentYear: 2026);

        Assert.Equal("Tesla", info!.Make);
        Assert.Equal(2019, info.ModelYear);
    }

    [Fact]
    public void A_more_specific_prefix_wins()
        => Assert.Equal("Lexus", VinInfo.Parse("JTHBA1D20L5100001")!.Make);

    [Fact]
    public void An_unknown_manufacturer_gives_no_make_rather_than_a_guess()
        => Assert.Null(VinInfo.Parse("XTA21099033000001")!.Make);

    [Theory]
    [InlineData(null)]
    [InlineData("JM1NDAD75J010000")]    // 16 characters
    [InlineData("JM1NDAD75J01000011")]  // 18 characters
    [InlineData("JM1NDAD75J01000O1")]   // O is never used in a VIN
    public void A_malformed_vin_is_not_parsed(string? vin)
        => Assert.Null(VinInfo.Parse(vin));
}
