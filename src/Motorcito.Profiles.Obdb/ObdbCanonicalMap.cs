using System.Text.RegularExpressions;
using Motorcito.Obd.Signals;

namespace Motorcito.Profiles.Obdb;

public readonly record struct CanonicalMatch(string Key, SignalQualifier? Qualifier);

/// <summary>
/// Maps OBDb signal names onto Motorcito's canonical vocabulary.
///
/// Deliberately conservative. A signal that is not confidently recognised stays
/// unmapped — visible in diagnostics, never on a consumer screen — because a
/// wrong mapping would show a real number under the wrong label. Unit
/// conversion and plausibility checks downstream are a second line of defence,
/// not a licence to guess here.
/// </summary>
public static partial class ObdbCanonicalMap
{
    public static CanonicalMatch? Resolve(string name, string? path, string? suggestedMetric, string unit)
    {
        var text = $"{name} {suggestedMetric}".ToLowerInvariant();
        var compact = Compact(text);
        var words = WordSplitter().Split(text).Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);

        bool Has(string phrase) => compact.Contains(Compact(phrase), StringComparison.Ordinal);
        bool Word(string word) => words.Contains(word);

        // Driver behaviour is recognised first, so it is always identified — and
        // therefore always blocked by the privacy policy — however it is named.
        if (Has("seatbelt") || Has("seat belt"))
            return new(CanonicalSignals.SeatbeltBuckled.Key, null);
        if (Has("steering angle") || Has("steering wheel angle"))
            return new(CanonicalSignals.SteeringAngle.Key, null);
        if (Has("lateral acceleration") || Has("longitudinal acceleration") || Word("g-force"))
            return new(CanonicalSignals.LateralAcceleration.Key, null);
        if (Word("door") || Word("doors"))
            return new(CanonicalSignals.DoorOpen.Key, null);
        if (Has("brake pedal"))
            return new(CanonicalSignals.BrakePedal.Key, null);

        var isTire = path == "Tires" || Word("tire") || Word("tyre") || Word("tires") || Word("wheel");
        if (isTire && !Has("warning") && !Word("speed") && !Word("size") && !Has("revolutions"))
        {
            string? key = Has("pressure") ? CanonicalSignals.TirePressure.Key
                : Has("temperature") || Word("temp") ? CanonicalSignals.TireTemp.Key
                : null;

            // Without a wheel, readings from four sensors would collide on one key.
            return key is not null && Wheel(text, compact, words) is { } wheel
                ? new(key, wheel)
                : null;
        }

        if (Has("transmission") && (Has("temperature") || Word("temp")))
            return new(CanonicalSignals.TransmissionTemp.Key, null);

        // "VVT oil temperature" is the variable valve timing actuator's oil, not the engine sump.
        if ((Has("oil temperature") || Has("oil temp")) && !Word("vvt") && !Has("transmission"))
            return new(CanonicalSignals.OilTemp.Key, null);

        if (Has("oil pressure") && !Has("transmission"))
            return new(CanonicalSignals.OilPressure.Key, null);

        if (Has("oil life"))
            return new(CanonicalSignals.OilLife.Key, null);

        if (Has("odometer"))
            return new(CanonicalSignals.Odometer.Key, null);

        if (Has("distance to empty"))
            return new(CanonicalSignals.DistanceToEmpty.Key, null);

        if (Has("fuel level") && unit == SignalUnits.Litre)
            return new(CanonicalSignals.FuelLevelVolume.Key, null);

        if (Has("cylinder head temperature"))
            return new(CanonicalSignals.HeadTemp.Key, null);

        if (Has("generator output voltage") || Has("alternator voltage"))
            return new(CanonicalSignals.AlternatorVoltage.Key, null);

        var highVoltage = Word("hv") || Has("high voltage") || Has("traction battery");
        if (highVoltage && Has("state of health"))
            return new(CanonicalSignals.HvStateOfHealth.Key, null);
        if (highVoltage && (Has("battery charge") || Has("state of charge")))
            return new(CanonicalSignals.HvStateOfCharge.Key, null);

        if (Has("cooling fan") && unit == SignalUnits.OnOff)
            return new(CanonicalSignals.FanOn.Key, null);

        if (Word("throttle") && unit == SignalUnits.Degree)
        {
            if (Word("desired"))
                return new(CanonicalSignals.ThrottleDesired.Key, null);
            if (Word("actual"))
                return new(CanonicalSignals.ThrottleActual.Key, null);
        }

        if (Has("number of dtcs") || Has("dtc count"))
            return new(CanonicalSignals.DtcCount.Key, null);

        return null;
    }

    private static SignalQualifier? Wheel(string text, string compact, HashSet<string> words)
    {
        if (compact.Contains("spare", StringComparison.Ordinal))
            return SignalQualifier.Spare;

        // Inner duals before plain rear, so "rear left inner" is not read as "rear left".
        if (compact.Contains("rearleftinner", StringComparison.Ordinal) || words.Contains("rli"))
            return SignalQualifier.RearLeftInner;
        if (compact.Contains("rearrightinner", StringComparison.Ordinal) || words.Contains("rri"))
            return SignalQualifier.RearRightInner;
        if (compact.Contains("frontleft", StringComparison.Ordinal) || words.Contains("fl"))
            return SignalQualifier.FrontLeft;
        if (compact.Contains("frontright", StringComparison.Ordinal) || words.Contains("fr"))
            return SignalQualifier.FrontRight;
        if (compact.Contains("rearleft", StringComparison.Ordinal) || words.Contains("rl"))
            return SignalQualifier.RearLeft;
        if (compact.Contains("rearright", StringComparison.Ordinal) || words.Contains("rr"))
            return SignalQualifier.RearRight;

        // "Wheel 1 pressure": numbered, position unknown. Kept honest rather than guessed.
        var numbered = NumberedWheel().Match(text);
        return numbered.Success ? SignalQualifier.WheelNumber(int.Parse(numbered.Groups[1].Value)) : null;
    }

    private static string Compact(string text)
        => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"[^a-z0-9\-]+")]
    private static partial Regex WordSplitter();

    [GeneratedRegex(@"\b(?:wheel|tire|tyre)\s*(\d)\b")]
    private static partial Regex NumberedWheel();
}
