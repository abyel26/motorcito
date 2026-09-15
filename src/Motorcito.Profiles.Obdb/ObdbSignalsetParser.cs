using System.Globalization;
using System.Text.Json;
using Motorcito.Obd;
using Motorcito.Obd.Signals;

namespace Motorcito.Profiles.Obdb;

/// <summary>
/// Model years a command applies to. OBDb filters are unions: a command marked
/// <c>{"to": 2018, "years": [2022], "from": 2027}</c> applies to 2018 and
/// earlier, to 2022, and to 2027 onward.
/// </summary>
public sealed record YearFilter(int? From, int? To, IReadOnlyList<int> Years)
{
    public bool Includes(int year)
        => (From is null && To is null && Years.Count == 0)
           || (From is { } from && year >= from)
           || (To is { } to && year <= to)
           || Years.Contains(year);
}

/// <summary>A parsed OBDb command with the model-year filter that selects it.</summary>
public sealed record ObdbCommand(SignalCommand Command, YearFilter? Filter)
{
    /// <summary>An unknown year includes everything: the car's own answers decide.</summary>
    public bool AppliesTo(int? year) => year is null || Filter is null || Filter.Includes(year.Value);
}

/// <summary>
/// Reads an OBDb v3 signalset (<c>signalsets/v3/*.json</c>) into Motorcito's
/// source-neutral signal model.
/// </summary>
public static class ObdbSignalsetParser
{
    public const string SourceId = "obdb";

    public static IReadOnlyList<ObdbCommand> Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var commands = new List<ObdbCommand>();

        if (!document.RootElement.TryGetProperty("commands", out var array) || array.ValueKind != JsonValueKind.Array)
            return commands;

        foreach (var element in array.EnumerateArray())
        {
            if (ParseCommand(element) is { } command)
                commands.Add(command);
        }

        return commands;
    }

    private static ObdbCommand? ParseCommand(JsonElement element)
    {
        if (!element.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.Object)
            return null;

        using var cmdProperties = cmd.EnumerateObject();
        if (!cmdProperties.MoveNext())
            return null;

        var serviceText = cmdProperties.Current.Name;
        var identifierText = cmdProperties.Current.Value.ValueKind == JsonValueKind.String
            ? cmdProperties.Current.Value.GetString() ?? string.Empty
            : string.Empty;

        if (!byte.TryParse(serviceText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var service))
            return null;

        // Standard Mode 01 PIDs come from the built-in registry, which is the
        // one place their formulas live. Catalog copies would only duplicate
        // them and spend the poll budget twice.
        if (service == 0x01)
            return null;

        var header = String(element, "hdr") ?? string.Empty;
        var reasons = new List<string>();

        if (!SignalRequest.IsElevenBitHeader(header))
            reasons.Add(header.Length > 3 ? "29-bit header" : "invalid header");
        if (element.TryGetProperty("eax", out _))
            reasons.Add("extended addressing");
        if (element.TryGetProperty("tst", out _))
            reasons.Add("tester address");
        if (element.TryGetProperty("fcm1", out var fcm1) && fcm1.ValueKind == JsonValueKind.True)
            reasons.Add("flow control option");
        if (!ReadOnlyServicePolicy.IsAllowed(service))
            reasons.Add("not a read-only service");

        ushort identifier = 0;
        var identifierLength = identifierText.Length / 2;
        if (identifierText.Length is not (2 or 4)
            || !ushort.TryParse(identifierText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out identifier))
        {
            reasons.Add("unrecognised identifier");
            identifierLength = 2;
        }

        var signals = new List<SignalDefinition>();
        if (element.TryGetProperty("signals", out var signalArray) && signalArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var signal in signalArray.EnumerateArray())
            {
                if (ParseSignal(signal) is { } definition)
                    signals.Add(definition);
            }
        }

        if (signals.Count == 0)
            return null;

        var command = new SignalCommand
        {
            SourceId = SourceId,
            Header = header.ToUpperInvariant(),
            ReceiveAddress = String(element, "rax"),
            Service = service,
            Identifier = identifier,
            IdentifierLength = identifierLength,
            IntervalSeconds = Number(element, "freq") is { } freq and > 0 ? freq : 1,
            Signals = signals,
            UnsupportedReason = reasons.Count == 0 ? null : string.Join(", ", reasons),
        };

        return new ObdbCommand(command, ParseFilter(element));
    }

    private static SignalDefinition? ParseSignal(JsonElement signal)
    {
        var id = String(signal, "id");
        if (id is null || !signal.TryGetProperty("fmt", out var fmt) || fmt.ValueKind != JsonValueKind.Object)
            return null;

        if (Number(fmt, "len") is not { } length)
            return null;

        var name = String(signal, "name") ?? id;
        var path = String(signal, "path");
        var unit = MapUnit(String(fmt, "unit"));
        var match = ObdbCanonicalMap.Resolve(name, path, String(signal, "suggestedMetric"), unit);

        return new SignalDefinition
        {
            Id = id,
            Name = name,
            Category = path,
            BitIndex = (int)(Number(fmt, "bix") ?? 0),
            BitLength = (int)length,
            Signed = Bool(fmt, "sign"),
            LittleEndian = Bool(fmt, "blsb"),
            Multiplier = Number(fmt, "mul") ?? 1,
            Divisor = Number(fmt, "div") ?? 1,
            Offset = Number(fmt, "add") ?? 0,
            Min = Number(fmt, "min"),
            Max = Number(fmt, "max"),
            NullMin = Number(fmt, "nullmin"),
            NullMax = Number(fmt, "nullmax"),
            Unit = unit,
            ValueMap = ParseMap(fmt),
            CanonicalKey = match?.Key,
            Qualifier = match?.Qualifier,
        };
    }

    private static YearFilter? ParseFilter(JsonElement element)
    {
        if (!element.TryGetProperty("filter", out var filter) || filter.ValueKind != JsonValueKind.Object)
            return null;

        var years = new List<int>();
        if (filter.TryGetProperty("years", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var year in list.EnumerateArray())
            {
                if (year.ValueKind == JsonValueKind.Number && year.TryGetInt32(out var y))
                    years.Add(y);
            }
        }

        return new YearFilter((int?)Number(filter, "from"), (int?)Number(filter, "to"), years);
    }

    /// <summary>
    /// Enumerated values, e.g. gear positions. Entries whose shape is not
    /// recognised are skipped rather than guessed at.
    /// </summary>
    private static IReadOnlyDictionary<long, string>? ParseMap(JsonElement fmt)
    {
        if (!fmt.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.Object)
            return null;

        var result = new Dictionary<long, string>();
        foreach (var entry in map.EnumerateObject())
        {
            if (!long.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw))
                continue;

            var label = entry.Value.ValueKind switch
            {
                JsonValueKind.String => entry.Value.GetString(),
                JsonValueKind.Object => String(entry.Value, "description") ?? String(entry.Value, "value"),
                _ => null
            };

            if (label is not null)
                result[raw] = label;
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// OBDb unit names to Motorcito unit symbols. Unknown names pass through
    /// unchanged, so they can never be converted — a value in a unit the app
    /// does not understand is withheld, not shown in the wrong unit.
    /// </summary>
    internal static string MapUnit(string? unit) => unit switch
    {
        null or "" => SignalUnits.None,
        "celsius" => SignalUnits.Celsius,
        "fahrenheit" => SignalUnits.Fahrenheit,
        "kilopascal" => SignalUnits.Kilopascal,
        "psi" => SignalUnits.Psi,
        "bars" => SignalUnits.Bar,
        "kilometers" => SignalUnits.Kilometre,
        "miles" => SignalUnits.Mile,
        "kilometersPerHour" => SignalUnits.KilometresPerHour,
        "milesPerHour" => SignalUnits.MilesPerHour,
        "volts" => SignalUnits.Volt,
        "amps" => SignalUnits.Ampere,
        "percent" => SignalUnits.Percent,
        "rpm" => SignalUnits.Rpm,
        "degrees" => SignalUnits.Degree,
        "liters" => SignalUnits.Litre,
        "kilowattHours" => SignalUnits.KilowattHour,
        "seconds" => SignalUnits.Second,
        "offon" => SignalUnits.OnOff,
        "scalar" => SignalUnits.Count,
        _ => unit
    };

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
