namespace Motorcito.Obd.Signals;

/// <summary>
/// One decoded manufacturer-specific value, already converted to the canonical
/// unit of its <see cref="CanonicalSignal"/> and checked for plausibility.
/// </summary>
public sealed record SignalReading(
    CanonicalSignal Signal,
    SignalQualifier? Qualifier,
    double Value,
    SignalCommand Command)
{
    /// <summary>Canonical key plus qualifier, e.g. "oil_temp" or "tire_pressure:FL".</summary>
    public string Key => KeyFor(Signal.Key, Qualifier);

    /// <summary>Which source's definition produced this value.</summary>
    public string SourceId => Command.SourceId;

    public static string KeyFor(string canonicalKey, SignalQualifier? qualifier)
        => qualifier is { } q ? $"{canonicalKey}:{q.Value}" : canonicalKey;
}

public enum SignalCheck
{
    /// <summary>Decoded, converted, and inside the canonical plausible range.</summary>
    Plausible,

    /// <summary>
    /// Decoded, but outside the range a real car can produce — a tire at 30 bar.
    /// The definition is wrong for this car, and the value must not be shown.
    /// </summary>
    Implausible,

    /// <summary>The reply was too short, a sentinel, or outside the source's own range.</summary>
    NoReading,

    /// <summary>The source did not map this signal to Motorcito's vocabulary.</summary>
    Unmapped,

    /// <summary>No defined conversion from the source's unit to the canonical one. Never guessed.</summary>
    UnconvertibleUnit,

    /// <summary>A driver-behaviour signal, which is not collected by default.</summary>
    BlockedForPrivacy
}

public sealed record SignalDecodeResult(
    SignalDefinition Definition,
    SignalCheck Check,
    SignalReading? Reading,
    double? SourceValue);

public static class SignalDecoder
{
    /// <summary>
    /// Decodes every signal a command carries from one positive reply.
    ///
    /// Only <see cref="SignalCheck.Plausible"/> results carry a reading. The
    /// others explain why a value is withheld, which is what the diagnostics
    /// view shows and what a consumer never sees.
    /// </summary>
    public static IReadOnlyList<SignalDecodeResult> Decode(SignalCommand command, UdsResponse response)
    {
        if (!response.IsPositive)
            return [];

        var results = new List<SignalDecodeResult>(command.Signals.Count);

        foreach (var definition in command.Signals)
        {
            var sourceValue = definition.Decode(response.Data);
            var canonical = CanonicalSignals.Find(definition.CanonicalKey);

            if (canonical is null)
            {
                results.Add(new(definition, SignalCheck.Unmapped, null, sourceValue));
                continue;
            }

            if (canonical.Privacy == SignalPrivacy.DriverBehaviour)
            {
                results.Add(new(definition, SignalCheck.BlockedForPrivacy, null, null));
                continue;
            }

            if (sourceValue is null)
            {
                results.Add(new(definition, SignalCheck.NoReading, null, null));
                continue;
            }

            if (SignalUnits.Convert(sourceValue.Value, definition.Unit, canonical.Unit) is not { } value)
            {
                results.Add(new(definition, SignalCheck.UnconvertibleUnit, null, sourceValue));
                continue;
            }

            results.Add(canonical.IsPlausible(value)
                ? new(definition, SignalCheck.Plausible, new SignalReading(canonical, definition.Qualifier, value, command), sourceValue)
                : new(definition, SignalCheck.Implausible, null, sourceValue));
        }

        return results;
    }
}
