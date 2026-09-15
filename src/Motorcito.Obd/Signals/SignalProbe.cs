namespace Motorcito.Obd.Signals;

public enum ProbeState
{
    /// <summary>The car answered and at least one value is plausible. Safe to poll and show.</summary>
    Verified,

    /// <summary>The car answered, but every value is outside what a real car produces. Hidden.</summary>
    Implausible,

    /// <summary>Refused or nothing at that address. This car does not offer it.</summary>
    Unsupported,

    /// <summary>Refused with "conditions not correct" — may answer later, e.g. with the engine running.</summary>
    ConditionsNotMet,

    /// <summary>The link failed. Says nothing about the car; worth trying on the next connection.</summary>
    NoResponse,

    /// <summary>The definition needs addressing the app does not implement, so it was not sent.</summary>
    UnsupportedByApp
}

public sealed record ProbeResult(
    SignalCommand Command,
    ProbeState State,
    UdsResponse? Response,
    IReadOnlyList<SignalDecodeResult> Signals)
{
    public string Describe() => State switch
    {
        ProbeState.Verified => "verified",
        ProbeState.Implausible => "answered with implausible values",
        ProbeState.UnsupportedByApp => $"not sent: {Command.UnsupportedReason ?? "unsupported addressing"}",
        _ => Response?.Describe() ?? State.ToString()
    };
}

/// <summary>
/// Asks the connected car, once, whether it answers each candidate command.
///
/// Catalog definitions are candidates, not facts about this car. Every request
/// is a read, so trying one the car does not support costs a refusal and
/// nothing else — which is what makes it safe to try definitions for a whole
/// make when the exact model is unknown.
/// </summary>
public static class SignalProbe
{
    /// <summary>
    /// Link-level failures and garbled replies get one more attempt: a cheap
    /// adapter drops frames, and a candidate should not be written off for this
    /// connection because of a single bad frame.
    /// </summary>
    private const int Attempts = 2;

    public static async Task<IReadOnlyList<ProbeResult>> ProbeAsync(
        Elm327Session session,
        IEnumerable<SignalCommand> commands,
        CancellationToken ct = default)
    {
        var results = new List<ProbeResult>();
        var sentAny = false;

        // Grouped by header so a batch to one module costs a single ATSH.
        foreach (var command in commands.OrderBy(c => c.Header, StringComparer.Ordinal))
        {
            if (!command.IsSendable)
            {
                results.Add(new(command, ProbeState.UnsupportedByApp, null, []));
                continue;
            }

            var request = command.ToRequest();
            UdsResponse response = new(UdsOutcome.NoResponse, [], null, Elm327Status.Empty);

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    response = await session.ReadIdentifierAsync(request, ct: ct).ConfigureAwait(false);
                    sentAny = true;
                }
                catch (ObdException)
                {
                    response = new(UdsOutcome.NoResponse, [], null, Elm327Status.Empty);
                }

                if (response.Outcome is not (UdsOutcome.NoResponse or UdsOutcome.Malformed))
                    break;
            }

            results.Add(Classify(command, response));
        }

        if (sentAny)
        {
            try
            {
                await session.RestoreFunctionalHeaderAsync(ct).ConfigureAwait(false);
            }
            catch (ObdException)
            {
                // The header is then unknown; the next addressed read re-sends it,
                // and a failed restore surfaces as Mode 01 reads failing.
            }
        }

        return results;
    }

    private static ProbeResult Classify(SignalCommand command, UdsResponse response)
    {
        switch (response.Outcome)
        {
            case UdsOutcome.Positive:
                var signals = SignalDecoder.Decode(command, response);
                var state = signals.Any(s => s.Check == SignalCheck.Plausible)
                    ? ProbeState.Verified
                    : ProbeState.Implausible;
                return new(command, state, response, signals);

            case UdsOutcome.Negative when response.NegativeResponseCode == UdsResponse.NrcConditionsNotCorrect:
                return new(command, ProbeState.ConditionsNotMet, response, []);

            case UdsOutcome.Negative:
            case UdsOutcome.NoData:
                return new(command, ProbeState.Unsupported, response, []);

            default:
                return new(command, ProbeState.NoResponse, response, []);
        }
    }
}
