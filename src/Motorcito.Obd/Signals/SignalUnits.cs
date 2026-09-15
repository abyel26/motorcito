namespace Motorcito.Obd.Signals;

/// <summary>
/// Unit symbols used throughout the signal model, and conversion between them.
///
/// Sources describe values in whatever unit their data uses — one catalog
/// entry reports tire pressure in psi, another in bar. Everything is stored in
/// the canonical (SI) unit of its <see cref="CanonicalSignal"/>, and converted
/// to the user's preference only at display.
/// </summary>
public static class SignalUnits
{
    public const string Celsius = "°C";
    public const string Fahrenheit = "°F";
    public const string Kilopascal = "kPa";
    public const string Psi = "psi";
    public const string Bar = "bar";
    public const string Kilometre = "km";
    public const string Mile = "mi";
    public const string KilometresPerHour = "km/h";
    public const string MilesPerHour = "mph";
    public const string Volt = "V";
    public const string Ampere = "A";
    public const string Percent = "%";
    public const string Rpm = "rpm";
    public const string Degree = "°";
    public const string Litre = "L";
    public const string KilowattHour = "kWh";
    public const string Second = "s";
    public const string Count = "count";
    public const string OnOff = "on/off";
    public const string None = "";

    /// <summary>
    /// Converts <paramref name="value"/> between units, or returns null when no
    /// conversion is defined — callers must not guess one.
    /// </summary>
    public static double? Convert(double value, string from, string to)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
            return value;

        // Sources describe plain numbers inconsistently — "scalar", "count", or
        // no unit at all. They are the same thing, and nothing is being converted.
        if (from is None or Count && to is None or Count)
            return value;

        return (from, to) switch
        {
            (Fahrenheit, Celsius) => (value - 32) * 5.0 / 9.0,
            (Celsius, Fahrenheit) => value * 9.0 / 5.0 + 32,
            (Psi, Kilopascal) => value * 6.894757,
            (Kilopascal, Psi) => value / 6.894757,
            (Bar, Kilopascal) => value * 100,
            (Kilopascal, Bar) => value / 100,
            (Mile, Kilometre) => value * 1.609344,
            (Kilometre, Mile) => value / 1.609344,
            (MilesPerHour, KilometresPerHour) => value * 1.609344,
            (KilometresPerHour, MilesPerHour) => value / 1.609344,
            _ => null
        };
    }
}
