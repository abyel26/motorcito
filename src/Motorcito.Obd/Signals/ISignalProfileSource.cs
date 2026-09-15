namespace Motorcito.Obd.Signals;

/// <summary>A vehicle a source has definitions for, as offered in the "Is this your car?" picker.</summary>
public sealed record VehicleModel(string Make, string Model, int? YearFrom, int? YearTo)
{
    public bool Covers(int? year)
        => year is null
           || ((YearFrom is null || year >= YearFrom) && (YearTo is null || year <= YearTo));
}

/// <summary>
/// What is known about the connected car. Every part may be missing: many cars
/// do not return a VIN, and the model is only known once the user picks it.
/// </summary>
public sealed record VehicleIdentity(string? Vin, string? Make, string? Model, int? Year);

/// <summary>
/// Supplies manufacturer-specific signal definitions.
///
/// This is the boundary that keeps any one catalog removable. The protocol
/// layer, poller, storage and features consume this interface and work with
/// zero implementations registered — a car then simply gets standard OBD data.
/// </summary>
public interface ISignalProfileSource
{
    /// <summary>Stable id stored with every reading this source defined, e.g. "obdb".</summary>
    string SourceId { get; }

    /// <summary>Text the app must display when this source's data is used, or null if none is required.</summary>
    string? Attribution { get; }

    IReadOnlyList<VehicleModel> Models { get; }

    /// <summary>
    /// Commands that may apply to <paramref name="vehicle"/>. Candidates, not
    /// promises: every one is verified against the car before it is trusted.
    /// </summary>
    IReadOnlyList<SignalCommand> CommandsFor(VehicleIdentity vehicle);
}
