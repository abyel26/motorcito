namespace Motorcito.Obd;

/// <summary>
/// What can be read out of a VIN on the device, with no network lookup:
/// the manufacturer from the WMI, and the model year from position 10.
///
/// The model is deliberately not decoded. Positions 4–8 are encoded
/// differently by every manufacturer, and a wrong guess would select the wrong
/// signal profile — so the model comes from the user or from probing the car.
/// </summary>
public sealed record VinInfo(string Vin, string? Make, int? ModelYear)
{
    private const string YearCodes = "ABCDEFGHJKLMNPRSTVWXY123456789";

    /// <summary>Returns null unless <paramref name="vin"/> is a well-formed 17-character VIN.</summary>
    /// <param name="currentYear">The year to disambiguate the 30-year model-year cycle against. Defaults to now.</param>
    public static VinInfo? Parse(string? vin, int? currentYear = null)
    {
        if (vin is null)
            return null;

        vin = vin.Trim().ToUpperInvariant();
        if (vin.Length != 17 || !vin.All(IsVinCharacter))
            return null;

        return new VinInfo(vin, Wmi.MakeFor(vin), ModelYearFor(vin, currentYear ?? DateTime.UtcNow.Year));
    }

    private static int? ModelYearFor(string vin, int currentYear)
    {
        var index = YearCodes.IndexOf(vin[9]);
        if (index < 0)
            return null;

        var earlier = 1980 + index;
        var later = earlier + 30;

        // For vehicles built for North America (WMI starting 1–5), position 7
        // being a letter marks the 2010+ cycle. Elsewhere that convention is not
        // reliable, so take the most recent cycle that is not in the future.
        var northAmerican = vin[0] is >= '1' and <= '5';
        if (northAmerican)
            return char.IsLetter(vin[6]) ? later : earlier;

        return later <= currentYear + 1 ? later : earlier;
    }

    private static bool IsVinCharacter(char c)
        => c is >= '0' and <= '9' or >= 'A' and <= 'Z' && c is not ('I' or 'O' or 'Q');
}

/// <summary>
/// World Manufacturer Identifier prefixes for common makes.
///
/// Partial by design and easy to extend. An unrecognised WMI yields no make,
/// which sends the user to the vehicle picker rather than to a wrong profile.
/// Make names are written as catalogs commonly spell them; sources normalise
/// when matching.
/// </summary>
public static class Wmi
{
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        // Mazda
        ["JM1"] = "Mazda", ["JM3"] = "Mazda", ["JMZ"] = "Mazda", ["3MZ"] = "Mazda", ["4F2"] = "Mazda",
        // BMW / Mini
        ["WBA"] = "BMW", ["WBS"] = "BMW", ["WBY"] = "BMW", ["5UX"] = "BMW", ["5YM"] = "BMW", ["4US"] = "BMW",
        ["WMW"] = "Mini",
        // Toyota / Lexus (Lexus prefixes are more specific, so they win)
        ["JT"] = "Toyota", ["4T1"] = "Toyota", ["4T3"] = "Toyota", ["5TD"] = "Toyota", ["5TF"] = "Toyota",
        ["2T1"] = "Toyota", ["2T3"] = "Toyota", ["SB1"] = "Toyota", ["VNK"] = "Toyota", ["NMT"] = "Toyota",
        ["JTH"] = "Lexus", ["JTJ"] = "Lexus", ["2T2"] = "Lexus", ["58A"] = "Lexus",
        // Honda / Acura
        ["JHM"] = "Honda", ["1HG"] = "Honda", ["2HG"] = "Honda", ["5J6"] = "Honda", ["5FN"] = "Honda",
        ["19X"] = "Honda", ["SHH"] = "Honda",
        ["JH4"] = "Acura", ["19U"] = "Acura",
        // Ford
        ["1FA"] = "Ford", ["1FB"] = "Ford", ["1FD"] = "Ford", ["1FM"] = "Ford", ["1FT"] = "Ford",
        ["2FM"] = "Ford", ["3FA"] = "Ford", ["WF0"] = "Ford", ["NM0"] = "Ford",
        // Hyundai / Kia / Genesis
        ["KMH"] = "Hyundai", ["KM8"] = "Hyundai", ["5NP"] = "Hyundai", ["5NM"] = "Hyundai", ["TMA"] = "Hyundai", ["NLH"] = "Hyundai",
        ["KNA"] = "Kia", ["KND"] = "Kia", ["5XY"] = "Kia", ["3KP"] = "Kia", ["U5Y"] = "Kia",
        ["KMT"] = "Genesis",
        // Volkswagen group
        ["WVW"] = "Volkswagen", ["WV1"] = "Volkswagen", ["WV2"] = "Volkswagen", ["WVG"] = "Volkswagen",
        ["3VW"] = "Volkswagen", ["1VW"] = "Volkswagen",
        ["WAU"] = "Audi", ["WA1"] = "Audi", ["TRU"] = "Audi", ["WUA"] = "Audi",
        ["WP0"] = "Porsche", ["WP1"] = "Porsche",
        ["TMB"] = "Skoda", ["VSS"] = "Seat",
        // Mercedes-Benz
        ["WDB"] = "Mercedes-Benz", ["WDC"] = "Mercedes-Benz", ["WDD"] = "Mercedes-Benz",
        ["W1K"] = "Mercedes-Benz", ["W1N"] = "Mercedes-Benz", ["4JG"] = "Mercedes-Benz", ["55S"] = "Mercedes-Benz",
        // GM
        ["1G1"] = "Chevrolet", ["1GC"] = "Chevrolet", ["1GN"] = "Chevrolet", ["2G1"] = "Chevrolet", ["3GN"] = "Chevrolet",
        // Stellantis (North America)
        ["1C4"] = "Jeep", ["1J4"] = "Jeep", ["1J8"] = "Jeep",
        ["1C6"] = "Ram", ["3C6"] = "Ram",
        // Stellantis / Renault group (Europe)
        ["VF1"] = "Renault", ["VF3"] = "Peugeot", ["VF7"] = "Citroen", ["UU1"] = "Dacia",
        ["ZFA"] = "Fiat", ["ZAR"] = "Alfa Romeo",
        // Nissan
        ["JN1"] = "Nissan", ["JN8"] = "Nissan", ["1N4"] = "Nissan", ["1N6"] = "Nissan",
        ["3N1"] = "Nissan", ["5N1"] = "Nissan", ["SJN"] = "Nissan", ["VSK"] = "Nissan",
        // Subaru
        ["JF1"] = "Subaru", ["JF2"] = "Subaru", ["4S3"] = "Subaru", ["4S4"] = "Subaru",
        // Tesla
        ["5YJ"] = "Tesla", ["7SA"] = "Tesla", ["LRW"] = "Tesla", ["XP7"] = "Tesla",
        // Volvo
        ["YV1"] = "Volvo", ["YV4"] = "Volvo", ["7JR"] = "Volvo",
        // Jaguar Land Rover
        ["SAL"] = "Land Rover", ["SAJ"] = "Jaguar",
        // Mitsubishi / Suzuki
        ["JA3"] = "Mitsubishi", ["JA4"] = "Mitsubishi",
        ["JS2"] = "Suzuki", ["JS3"] = "Suzuki", ["TSM"] = "Suzuki",
    };

    /// <summary>The make for a VIN, matching the longest known prefix, or null.</summary>
    public static string? MakeFor(string vin)
    {
        if (vin.Length < 3)
            return null;

        return Prefixes.TryGetValue(vin[..3], out var make) ? make
            : Prefixes.TryGetValue(vin[..2], out make) ? make
            : null;
    }
}
