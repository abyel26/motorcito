namespace Motorcito.Obd;

public sealed class Elm327Options
{
    /// <summary>Per-command timeout. The MX+ answers in low tens of ms; clones can take most of a second.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Longer budget for the reset and protocol-detection commands, which genuinely take seconds.</summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Budget for the first request that actually reaches the vehicle bus.
    ///
    /// <c>ATSP0</c> only selects automatic detection; it does not negotiate.
    /// The adapter tries each protocol in turn on the first real OBD request —
    /// normally <c>0100</c> — and that search takes seconds, sometimes more
    /// than ten on a first connection. Judging it by
    /// <see cref="CommandTimeout"/> makes the capability scan time out on a
    /// perfectly healthy car and report that no PIDs are supported.
    ///
    /// Generous on purpose: this applies once per connection, and only while
    /// the bus has yet to answer.
    /// </summary>
    public TimeSpan FirstRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How many times to re-send a command that failed transiently.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Pause between retries, letting a mid-negotiation adapter settle.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(150);
}

/// <summary>
/// Per-call override of timeout and retries.
///
/// An identifier sweep sends tens of thousands of requests, most of which the
/// ECU refuses or ignores. With the session defaults a silent identifier costs
/// three attempts at 1.5 s each; a sweep wants one short attempt instead.
/// </summary>
public sealed record RequestOptions(TimeSpan Timeout, int MaxRetries);

/// <summary>
/// Speaks ELM327 over any <see cref="IObdAdapter"/>.
///
/// This is where protocol knowledge lives — command strings, the init sequence,
/// retry policy, capability scanning. It holds no transport code, so it is
/// identical for the MX+ today and for a BLE adapter later.
/// </summary>
public sealed class Elm327Session
{
    private readonly IObdAdapter _adapter;
    private readonly Elm327Options _options;

    /// <summary>
    /// Whether the vehicle bus has replied at least once on this session.
    ///
    /// Gates the protocol-negotiation timeout allowance: generous until the
    /// link is proven, tight afterwards so a dead PID cannot cost fifteen
    /// seconds per read.
    /// </summary>
    private bool _busHasAnswered;

    public Elm327Session(IObdAdapter adapter, Elm327Options? options = null)
    {
        _adapter = adapter;
        _options = options ?? new Elm327Options();
    }

    /// <summary>
    /// The CAN header the adapter is currently transmitting on, or null when it
    /// is not known — before initialisation, or after an <c>ATSH</c> the
    /// adapter did not acknowledge. Unknown forces the next header change to be
    /// sent rather than assumed.
    /// </summary>
    public string? CurrentHeader { get; private set; }

    /// <summary>
    /// The initialisation sequence from <c>docs/PID-REFERENCE.md</c>, in order.
    ///
    /// Failures here are not fatal on their own: clones routinely NAK a command
    /// and then behave correctly anyway, and the response parser is built to
    /// cope with echo and spaces regardless of whether ATE0/ATS0 took effect.
    /// </summary>
    public async Task<InitResult> InitializeAsync(CancellationToken ct = default)
    {
        var steps = new (string Command, string Purpose)[]
        {
            ("ATZ",   "reset"),
            ("ATE0",  "echo off"),
            ("ATL0",  "linefeeds off"),
            ("ATS0",  "spaces off"),
            ("ATSP0", "auto-detect protocol"),
        };

        var acknowledged = new List<string>();
        var ignored = new List<string>();
        var adapterReset = false;

        foreach (var (command, _) in steps)
        {
            // ATZ reboots the adapter and answers with a version banner rather
            // than OK, so it gets the long timeout and no OK expectation.
            var timeout = command is "ATZ" or "ATSP0" ? _options.InitTimeout : _options.CommandTimeout;

            try
            {
                var raw = await _adapter.SendCommandAsync(command, timeout, ct).ConfigureAwait(false);
                var response = Elm327Response.Parse(raw, command);

                // The banner is not hex or OK, so ATZ never counts as
                // acknowledged — but any reply at all means the reset happened.
                if (command == "ATZ" && !string.IsNullOrWhiteSpace(raw))
                    adapterReset = true;

                if (response.Status is Elm327Status.Ok or Elm327Status.Data)
                    acknowledged.Add(command);
                else
                    ignored.Add(command);
            }
            catch (ObdTimeoutException)
            {
                ignored.Add(command);
            }
        }

        // A reset restores the adapter's default functional header. Without one
        // the header is unknown, so the next addressed batch sends ATSH rather
        // than assuming.
        CurrentHeader = adapterReset ? SignalRequest.FunctionalHeader : null;

        return new InitResult(acknowledged, ignored);
    }

    /// <summary>
    /// Sends a command, retrying only on transient conditions.
    ///
    /// NO DATA is never retried: an unsupported PID answers NO DATA every time,
    /// and on a clone doing ~10 queries/sec, retrying dead PIDs is the
    /// difference between a usable sample rate and an unusable one.
    /// </summary>
    public Task<Elm327Response> SendAsync(string command, CancellationToken ct = default)
        => SendAsync(command, options: null, ct);

    /// <inheritdoc cref="SendAsync(string, CancellationToken)"/>
    /// <param name="options">Overrides the session's timeout and retry count for this call.</param>
    public async Task<Elm327Response> SendAsync(string command, RequestOptions? options, CancellationToken ct = default)
    {
        Elm327Response response = Elm327Response.Parse(string.Empty);
        var maxRetries = options?.MaxRetries ?? _options.MaxRetries;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);

            // Until the bus has answered once, allow for protocol negotiation.
            // ATSP0 selects auto-detection but does not perform it; the search
            // happens on this first real request and takes far longer than a
            // steady-state read. A short per-call timeout does not override
            // that — it would fail the very first request on a healthy car.
            var timeout = _busHasAnswered
                ? options?.Timeout ?? _options.CommandTimeout
                : Max(options?.Timeout ?? TimeSpan.Zero, _options.FirstRequestTimeout);

            try
            {
                var raw = await _adapter.SendCommandAsync(command, timeout, ct).ConfigureAwait(false);
                response = Elm327Response.Parse(raw, command);
            }
            catch (ObdTimeoutException)
            {
                response = Elm327Response.Parse(string.Empty);
                continue;
            }

            // Any real reply means a protocol is established. NO DATA counts:
            // it is the ECU declining a specific PID, which it can only do once
            // the link is up.
            if (response.Status is Elm327Status.Data or Elm327Status.NoData)
                _busHasAnswered = true;

            if (!response.IsTransient)
                return response;
        }

        return response;
    }

    /// <summary>
    /// Points subsequent requests at one ECU, e.g. "7E0" for the engine.
    ///
    /// Skips the round trip when the adapter is already on that header, so a
    /// batch of reads to one ECU costs a single <c>ATSH</c>.
    /// </summary>
    /// <returns>False if the adapter did not acknowledge; the header is then unknown.</returns>
    public async Task<bool> SetHeaderAsync(string header, CancellationToken ct = default)
    {
        if (!SignalRequest.IsElevenBitHeader(header))
            throw new ArgumentException($"'{header}' is not an 11-bit CAN header (000–7FF).", nameof(header));

        header = header.ToUpperInvariant();
        if (header == CurrentHeader)
            return true;

        var command = $"ATSH{header}";
        try
        {
            var raw = await _adapter.SendCommandAsync(command, _options.CommandTimeout, ct).ConfigureAwait(false);
            if (Elm327Response.Parse(raw, command).Status == Elm327Status.Ok)
            {
                CurrentHeader = header;
                return true;
            }
        }
        catch (ObdTimeoutException)
        {
        }

        CurrentHeader = null;
        return false;
    }

    /// <summary>
    /// Returns to the functional broadcast header. Must follow any addressed
    /// batch: standard Mode 01 polling expects every ECU to hear its requests.
    /// </summary>
    public Task<bool> RestoreFunctionalHeaderAsync(CancellationToken ct = default)
        => SetHeaderAsync(SignalRequest.FunctionalHeader, ct);

    /// <summary>
    /// Sends one addressed read and classifies the reply as an answer, a
    /// refusal, or silence.
    ///
    /// Leaves the adapter on <see cref="SignalRequest.Header"/>; callers
    /// batching reads restore the functional header once at the end.
    /// </summary>
    /// <exception cref="ServiceNotAllowedException">The request is not a read.</exception>
    public async Task<UdsResponse> ReadIdentifierAsync(SignalRequest request, RequestOptions? options = null, CancellationToken ct = default)
    {
        ReadOnlyServicePolicy.EnsureAllowed(request.Service);

        if (!await SetHeaderAsync(request.Header, ct).ConfigureAwait(false))
            return new UdsResponse(UdsOutcome.NoResponse, [], null, Elm327Status.NotUnderstood);

        var response = await SendAsync(request.Command, options, ct).ConfigureAwait(false);
        return UdsResponse.Parse(response, request);
    }

    /// <summary>Reads one live parameter.</summary>
    public async Task<DecodeResult> ReadAsync(PidDefinition definition, CancellationToken ct = default)
    {
        var response = await SendAsync(definition.Command, ct).ConfigureAwait(false);
        return PidDecoder.Decode(response, definition);
    }

    /// <summary>Reads one parameter from the ECU's stored freeze frame (Mode 02, frame 0).</summary>
    public async Task<DecodeResult> ReadFreezeFrameAsync(PidDefinition definition, CancellationToken ct = default)
    {
        var response = await SendAsync(definition.FreezeFrameCommand, ct).ConfigureAwait(false);
        return PidDecoder.Decode(response, definition, PidDecoder.ModeFreezeFrame);
    }

    /// <summary>
    /// Queries the four capability bitmasks and returns what the ECU reports.
    ///
    /// Run at every connection — never assume a PID exists. Stops early when a mask says
    /// the next range is unavailable, saving round trips on simpler ECUs.
    /// </summary>
    public async Task<SupportedPids> ScanSupportedPidsAsync(CancellationToken ct = default)
    {
        var supported = new SupportedPids();
        byte[] maskPids = [0x00, 0x20, 0x40, 0x60];

        foreach (var maskPid in maskPids)
        {
            var response = await SendAsync($"01{maskPid:X2}", ct).ConfigureAwait(false);
            var hasNextRange = supported.AddMask(response, maskPid);

            if (!hasNextRange)
                break;
        }

        return supported;
    }

    /// <summary>
    /// Reads the VIN (Mode 09 PID 02). Captured at first connection because
    /// year/make/model/engine is what makes cross-fleet baselines possible.
    /// </summary>
    public async Task<string?> ReadVinAsync(CancellationToken ct = default)
    {
        var response = await SendAsync("0902", ct).ConfigureAwait(false);
        return VinDecoder.Decode(response);
    }

    /// <summary>Reads stored (Mode 03), pending (Mode 07) or permanent (Mode 0A) trouble codes.</summary>
    public async Task<IReadOnlyList<string>> ReadDtcsAsync(DtcMode mode, CancellationToken ct = default)
    {
        var command = mode switch
        {
            DtcMode.Stored => "03",
            DtcMode.Pending => "07",
            DtcMode.Permanent => "0A",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        var response = await SendAsync(command, ct).ConfigureAwait(false);
        return DtcDecoder.Decode(response, mode);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <param name="Ignored">Init commands the adapter did not acknowledge. Informational — clones frequently ignore some and still work.</param>
public sealed record InitResult(IReadOnlyList<string> Acknowledged, IReadOnlyList<string> Ignored)
{
    /// <summary>True if the adapter answered nothing at all — the link is not usable.</summary>
    public bool Failed => Acknowledged.Count == 0;
}
