namespace Motorcito.Obd.Signals;

public enum ProbeState
{
    /// <summary>The car answered and at least one value is plausible. Safe to poll and show.</summary>
    Verified,

    /// <summary>The car answered, but every value is outside what a real car produces. Hidden.</summary>
    Implausible,

    /// <summary>
    /// The car answered, but nothing in the reply maps to a value Motorcito
    /// uses (or it is driver-behaviour data, which is not collected). There is
    /// no plausibility range to pass, so it is shown in diagnostics only and
    /// never polled or used by features.
    /// </summary>
    AnsweredUnmapped,

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
        ProbeState.AnsweredUnmapped => "answered, not used yet",
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
            ProbeResult result = new(command, ProbeState.NoResponse, null, []);

            for (var attempt = 0; attempt < Attempts; attempt++)
            {
                UdsResponse response;
                try
                {
                    response = await session.ReadIdentifierAsync(request, ct: ct).ConfigureAwait(false);
                    sentAny = true;
                }
                catch (ObdException)
                {
                    response = new(UdsOutcome.NoResponse, [], null, Elm327Status.Empty);
                }

                result = Classify(command, response);
                if (!WorthRetrying(result))
                    break;
            }

            results.Add(result);
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

    /// <summary>
    /// Whether a result may be the link's fault rather than the car's answer.
    ///
    /// A positive reply too short for its own definition is almost always a
    /// damaged frame — a cheap adapter dropping the last byte — not the car
    /// genuinely answering with half a value. Seen in practice: oil temperature
    /// written off for a whole connection because one reply lost its final byte.
    /// </summary>
    private static bool WorthRetrying(ProbeResult result) => result.State switch
    {
        ProbeState.NoResponse => true,
        ProbeState.Implausible => result.Signals.Any(s => s.Check == SignalCheck.NoReading),
        _ => false
    };

    private static ProbeResult Classify(SignalCommand command, UdsResponse response)
    {
        switch (response.Outcome)
        {
            case UdsOutcome.Positive:
                var signals = SignalDecoder.Decode(command, response);
                var state = signals.Any(s => s.Check == SignalCheck.Plausible) ? ProbeState.Verified
                    : signals.All(s => s.Check is SignalCheck.Unmapped or SignalCheck.BlockedForPrivacy) ? ProbeState.AnsweredUnmapped
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
