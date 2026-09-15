using System.Reflection;
using Motorcito.Obd.Signals;

namespace Motorcito.Profiles.Obdb;

/// <summary>
/// Serves the vendored OBDb signalsets through Motorcito's profile boundary.
/// </summary>
public sealed class ObdbProfileSource : ISignalProfileSource
{
    private const string ResourcePrefix = "Motorcito.Profiles.Obdb.Signalsets.";

    /// <summary>Makes whose names contain a hyphen, so "Land-Rover-Defender" splits correctly.</summary>
    private static readonly string[] HyphenatedMakes = ["Alfa-Romeo", "Aston-Martin", "Land-Rover", "Mercedes-Benz", "Rolls-Royce"];

    private readonly IReadOnlyList<Signalset> _signalsets;

    /// <summary>Loads the signalsets vendored into this assembly.</summary>
    public ObdbProfileSource()
        : this(LoadEmbedded())
    {
    }

    /// <summary>Builds a source from signalsets supplied by the caller rather than the vendored ones.</summary>
    /// <param name="signalsets">Repository names (e.g. "Mazda-MX-5") with their signalset JSON.</param>
    public static ObdbProfileSource FromSignalsets(IEnumerable<(string RepositoryName, Stream Json)> signalsets)
        => new(signalsets);

    // Private on purpose. A dependency-injection container picks the public
    // constructor with the most parameters it can satisfy, and it satisfies any
    // IEnumerable<T> with an empty sequence — so a public constructor taking a
    // list of signalsets gets chosen and silently builds a source with no
    // vehicles at all. The parameterless constructor must be the only public one.
    private ObdbProfileSource(IEnumerable<(string RepositoryName, Stream Json)> signalsets)
    {
        _signalsets = signalsets
            .Select(s =>
            {
                using var json = s.Json;
                var (make, model) = SplitRepositoryName(s.RepositoryName);
                return new Signalset(make, model, ObdbSignalsetParser.Parse(json));
            })
            .ToList();

        Models = _signalsets.Select(s => new VehicleModel(s.Make, s.Model, null, null)).ToList();
    }

    public string SourceId => ObdbSignalsetParser.SourceId;

    public string? Attribution => "Vehicle signal definitions from OBDb (obdb.community), CC BY-SA 4.0.";

    public IReadOnlyList<VehicleModel> Models { get; }

    /// <summary>
    /// Commands for every signalset matching what is known about the car. An
    /// unknown make or model widens the set rather than guessing; the car's own
    /// answers at connect narrow it back down.
    /// </summary>
    public IReadOnlyList<SignalCommand> CommandsFor(VehicleIdentity vehicle)
        => _signalsets
            .Where(s => Matches(vehicle.Make, s.Make) && Matches(vehicle.Model, s.Model))
            .SelectMany(s => s.Commands)
            .Where(c => c.AppliesTo(vehicle.Year))
            .Select(c => c.Command)
            .ToList();

    private static bool Matches(string? known, string candidate)
        => known is null || string.Equals(Normalise(known), Normalise(candidate), StringComparison.Ordinal);

    private static string Normalise(string name)
        => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static (string Make, string Model) SplitRepositoryName(string repositoryName)
    {
        foreach (var make in HyphenatedMakes)
        {
            if (repositoryName.StartsWith(make + "-", StringComparison.OrdinalIgnoreCase))
                return (make, repositoryName[(make.Length + 1)..]);
        }

        var dash = repositoryName.IndexOf('-');
        return dash > 0
            ? (repositoryName[..dash], repositoryName[(dash + 1)..])
            : (repositoryName, string.Empty);
    }

    private static IEnumerable<(string, Stream)> LoadEmbedded()
    {
        var assembly = typeof(ObdbProfileSource).Assembly;

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var repositoryName = resource[ResourcePrefix.Length..^".json".Length];
            if (assembly.GetManifestResourceStream(resource) is { } stream)
                yield return (repositoryName, stream);
        }
    }

    private sealed record Signalset(string Make, string Model, IReadOnlyList<ObdbCommand> Commands);
}
