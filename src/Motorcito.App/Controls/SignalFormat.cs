using System.Globalization;
using Motorcito.Obd.Signals;

namespace Motorcito.App.Controls;

/// <summary>
/// Turns canonical signal values into display text.
///
/// Values arrive in SI units. Display units are chosen here, per signal — tire
/// pressure reads naturally in bar, oil pressure in kPa. A user-selectable unit
/// system (psi and °F for US drivers) belongs here too, when it lands.
/// Formatted with the device culture, so a Spanish phone shows "2,35 bar".
/// </summary>
public static class SignalFormat
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Label(CanonicalSignal signal, SignalQualifier? qualifier)
        => qualifier is { } q ? $"{signal.Name} {QualifierLabel(q)}" : signal.Name;

    /// <summary>
    /// A wheel is named by position only when the source knew it; otherwise by
    /// the source's own number, so a warning never lands on the wrong tire.
    /// </summary>
    public static string QualifierLabel(SignalQualifier qualifier) => qualifier.Kind switch
    {
        QualifierKind.Wheel => qualifier.Value switch
        {
            "FL" => "front left",
            "FR" => "front right",
            "RL" => "rear left",
            "RR" => "rear right",
            "RLI" => "rear left inner",
            "RRI" => "rear right inner",
            "SPARE" => "spare",
            var number => number
        },
        QualifierKind.Cylinder => $"cylinder {qualifier.Value}",
        _ => $"#{qualifier.Value}"
    };

    public static string Value(CanonicalSignal signal, double value) => signal.Unit switch
    {
        SignalUnits.Kilopascal when signal.Key == CanonicalSignals.TirePressure.Key
            => $"{(value / 100).ToString("0.00", Culture)} bar",
        SignalUnits.Kilopascal => $"{value.ToString("0", Culture)} kPa",
        SignalUnits.Celsius => $"{value.ToString("0", Culture)} °C",
        SignalUnits.Volt => $"{value.ToString("0.0", Culture)} V",
        SignalUnits.Degree => $"{value.ToString("0.0", Culture)}°",
        SignalUnits.Percent => $"{value.ToString("0", Culture)} %",
        SignalUnits.Kilometre => $"{value.ToString("#,0", Culture)} km",
        SignalUnits.Litre => $"{value.ToString("0.0", Culture)} L",
        SignalUnits.OnOff => value >= 0.5 ? "On" : "Off",
        SignalUnits.Count => value.ToString("0", Culture),
        _ => value.ToString("0.##", Culture)
    };

    /// <summary>A value in the source's own unit, for signals Motorcito does not map.</summary>
    public static string SourceValue(double value, string unit)
        => unit switch
        {
            SignalUnits.OnOff => value >= 0.5 ? "On" : "Off",
            SignalUnits.None or SignalUnits.Count => value.ToString("0.##", Culture),
            _ => $"{value.ToString("0.##", Culture)} {unit}"
        };
}
