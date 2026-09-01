using System.Text;

namespace Motorcito.Obd;

public enum Elm327Status
{
    /// <summary>Response carried usable hex payload.</summary>
    Data,
    /// <summary>PID unsupported, or the ECU did not answer in time. Expected and common — not an error.</summary>
    NoData,
    /// <summary>Adapter is still negotiating the bus protocol. Retry.</summary>
    Searching,
    /// <summary>Adapter could not reach the bus at all. Usually ignition off.</summary>
    UnableToConnect,
    /// <summary>Bus initialisation chatter. Transient.</summary>
    BusInit,
    /// <summary>Adapter did not understand the command.</summary>
    NotUnderstood,
    /// <summary>Transfer interrupted.</summary>
    Stopped,
    /// <summary>Bus-level error (CAN ERROR, BUFFER FULL, etc.).</summary>
    BusError,
    /// <summary>Adapter acknowledgement of a configuration command.</summary>
    Ok,
    /// <summary>Nothing recognisable survived cleaning.</summary>
    Empty
}

/// <summary>
/// A parsed ELM327 reply.
///
/// The contract this type exists to enforce: <see cref="Payload"/> is trustworthy
/// hex, or <see cref="Status"/> is not <see cref="Elm327Status.Data"/>. Callers
/// never have to inspect raw text.
/// </summary>
public sealed class Elm327Response
{
    /// <summary>Exactly what came off the wire, kept for logging and bug reports.</summary>
    public required string Raw { get; init; }

    public required Elm327Status Status { get; init; }

    /// <summary>Uppercase hex with all whitespace removed. Empty unless <see cref="Status"/> is <see cref="Elm327Status.Data"/>.</summary>
    public required string Payload { get; init; }

    public bool IsData => Status == Elm327Status.Data;

    /// <summary>
    /// True for conditions that may succeed if the same command is sent again.
    /// <see cref="Elm327Status.NoData"/> is deliberately excluded: an unsupported
    /// PID returns NO DATA forever, and retrying it burns the query budget.
    /// </summary>
    public bool IsTransient => Status is Elm327Status.Searching
        or Elm327Status.BusInit
        or Elm327Status.Stopped
        or Elm327Status.BusError
        or Elm327Status.Empty;

    /// <summary>
    /// Cleans and classifies a raw adapter reply.
    ///
    /// Deliberately tolerant: clones echo despite ATE0, emit spaces despite ATS0,
    /// split replies across lines, and interleave status words with payload. All
    /// of that is normalised here so no downstream code has to know about it.
    /// </summary>
    /// <param name="raw">Response text with the trailing prompt already stripped.</param>
    /// <param name="command">The command sent, so an echoed copy can be recognised and dropped.</param>
    public static Elm327Response Parse(string raw, string? command = null)
    {
        raw ??= string.Empty;

        // Split on both CR and LF: adapters disagree about line endings, and ATL0
        // is not always honoured.
        var lines = raw.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var payload = new StringBuilder();
        var status = Elm327Status.Empty;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            // Drop a command echo. Compared without spaces because ATS0 is not
            // always honoured either.
            if (command is not null && StripWhitespace(trimmed).Equals(StripWhitespace(command), StringComparison.OrdinalIgnoreCase))
                continue;

            var upper = trimmed.ToUpperInvariant();

            // Status words are matched with Contains rather than equality: clones
            // append them to payload lines and pad them unpredictably.
            if (upper.Contains("NO DATA"))
            {
                status = Escalate(status, Elm327Status.NoData);
                continue;
            }
            if (upper.Contains("SEARCHING"))
            {
                // "SEARCHING..." is a progress notice, not a result. The real
                // answer often follows on the next line, so keep reading.
                status = Escalate(status, Elm327Status.Searching);
                continue;
            }
            if (upper.Contains("UNABLE TO CONNECT"))
                return Terminal(raw, Elm327Status.UnableToConnect);
            if (upper.Contains("BUS INIT") || upper.Contains("BUSINIT"))
            {
                status = Escalate(status, Elm327Status.BusInit);
                continue;
            }
            if (upper.Contains("STOPPED"))
            {
                status = Escalate(status, Elm327Status.Stopped);
                continue;
            }
            if (upper.Contains("CAN ERROR") || upper.Contains("BUFFER FULL")
                || upper.Contains("DATA ERROR") || upper.Contains("BUS ERROR")
                || upper.Contains("FB ERROR") || upper.Contains("RX ERROR"))
            {
                status = Escalate(status, Elm327Status.BusError);
                continue;
            }
            if (upper == "?")
                return Terminal(raw, Elm327Status.NotUnderstood);
            if (upper == "OK")
            {
                status = Escalate(status, Elm327Status.Ok);
                continue;
            }

            // Anything left should be hex payload. Multi-frame CAN replies are
            // prefixed with a frame index and a colon ("0:", "1:") — strip it.
            var body = StripWhitespace(trimmed);
            var colon = body.IndexOf(':');
            if (colon >= 0 && colon <= 2)
                body = body[(colon + 1)..];

            if (body.Length > 0 && IsHex(body))
                payload.Append(body.ToUpperInvariant());
        }

        if (payload.Length > 0)
        {
            return new Elm327Response
            {
                Raw = raw,
                Status = Elm327Status.Data,
                Payload = payload.ToString()
            };
        }

        return Terminal(raw, status);
    }

    /// <summary>
    /// Keeps the most actionable status when a reply mixes several. A hard fault
    /// outranks NO DATA, which outranks progress chatter, which outranks nothing.
    /// </summary>
    private static Elm327Status Escalate(Elm327Status current, Elm327Status candidate)
    {
        static int Rank(Elm327Status s) => s switch
        {
            Elm327Status.Empty => 0,
            Elm327Status.Ok => 1,
            Elm327Status.Searching => 2,
            Elm327Status.BusInit => 2,
            Elm327Status.NoData => 3,
            Elm327Status.Stopped => 4,
            Elm327Status.BusError => 5,
            _ => 6
        };

        return Rank(candidate) > Rank(current) ? candidate : current;
    }

    private static Elm327Response Terminal(string raw, Elm327Status status) => new()
    {
        Raw = raw,
        Status = status,
        Payload = string.Empty
    };

    private static string StripWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var isHexDigit = c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
            if (!isHexDigit)
                return false;
        }
        return true;
    }
}
