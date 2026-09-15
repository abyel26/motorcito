using Motorcito.Obd.Signals;

namespace Motorcito.Obd.Tests;

public class SignalDefinitionTests
{
    private static SignalDefinition Define(int bitLength, int bitIndex = 0) => new()
    {
        Id = "test",
        Name = "test",
        BitIndex = bitIndex,
        BitLength = bitLength,
    };

    [Fact]
    public void Decodes_a_scaled_and_offset_16_bit_value()
    {
        // 0x2AF8 = 11000; 11000 / 100 − 40 = 70.0
        var oilTemp = Define(16) with { Divisor = 100, Offset = -40, Min = -40, Max = 615.35 };

        Assert.Equal(70.0, oilTemp.Decode([0x2A, 0xF8])!.Value, precision: 6);
    }

    [Fact]
    public void A_bit_index_selects_a_later_byte()
    {
        var secondByte = Define(8, bitIndex: 8);

        Assert.Equal(0xAB, secondByte.Decode([0x00, 0xAB, 0x00]));
    }

    [Fact]
    public void A_single_bit_flag_counts_from_the_most_significant_bit()
    {
        var flag = Define(1, bitIndex: 5);

        Assert.Equal(1, flag.Decode([0b0000_0100]));
        Assert.Equal(0, flag.Decode([0b1111_1011]));
    }

    [Fact]
    public void Signed_values_use_twos_complement_over_the_bit_length()
    {
        var pressure = Define(16) with { Signed = true };

        Assert.Equal(-100, pressure.Decode([0xFF, 0x9C]));
    }

    [Fact]
    public void Little_endian_values_reverse_the_byte_order()
    {
        var value = Define(16) with { LittleEndian = true };

        Assert.Equal(11000, value.Decode([0xF8, 0x2A]));
    }

    [Fact]
    public void A_reply_too_short_for_the_signal_is_no_reading_not_zero()
        => Assert.Null(Define(16).Decode([0x2A]));

    [Fact]
    public void A_value_outside_the_sources_own_range_is_rejected()
    {
        var bounded = Define(8) with { Max = 100 };

        Assert.Null(bounded.Decode([0xC8]));
    }

    [Fact]
    public void The_top_raw_value_survives_a_rounded_catalog_maximum()
    {
        // 255 × 1373 / 100000 = 3.50115, written by the catalog as max 3.5.
        var tirePressure = Define(8) with { Multiplier = 1373, Divisor = 100000, Max = 3.5 };

        Assert.NotNull(tirePressure.Decode([0xFF]));
    }

    [Fact]
    public void A_sentinel_value_decodes_to_no_reading()
    {
        // 255 / 5 = 51, which this source uses to mean "no sensor data".
        var sentinel = Define(8) with { Divisor = 5, NullMax = 51, Max = 51 };

        Assert.Null(sentinel.Decode([0xFF]));
        Assert.Equal(40, sentinel.Decode([0xC8]));
    }
}
