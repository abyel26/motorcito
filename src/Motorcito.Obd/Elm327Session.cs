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

        foreach (var (command, _) in steps)
        {
            // ATZ reboots the adapter and answers with a version banner rather
            // than OK, so it gets the long timeout and no OK expectation.
            var timeout = command is "ATZ" or "ATSP0" ? _options.InitTimeout : _options.CommandTimeout;

            try
            {
                var raw = await _adapter.SendCommandAsync(command, timeout, ct).ConfigureAwait(false);
                var response = Elm327Response.Parse(raw, command);

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

        return new InitResult(acknowledged, ignored);
    }

    /// <summary>
    /// Sends a command, retrying only on transient conditions.
    ///
    /// NO DATA is never retried: an unsupported PID answers NO DATA every time,
    /// and on a clone doing ~10 queries/sec, retrying dead PIDs is the
    /// difference between a usable sample rate and an unusable one.
    /// </summary>
    public async Task<Elm327Response> SendAsync(string command, CancellationToken ct = default)
    {
        Elm327Response response = Elm327Response.Parse(string.Empty);

        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(_options.RetryDelay, ct).ConfigureAwait(false);

            // Until the bus has answered once, allow for protocol negotiation.
            // ATSP0 selects auto-detection but does not perform it; the search
            // happens on this first real request and takes far longer than a
            // steady-state read.
            var timeout = _busHasAnswered ? _options.CommandTimeout : _options.FirstRequestTimeout;

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
}

/// <param name="Ignored">Init commands the adapter did not acknowledge. Informational — clones frequently ignore some and still work.</param>
public sealed record InitResult(IReadOnlyList<string> Acknowledged, IReadOnlyList<string> Ignored)
{
    /// <summary>True if the adapter answered nothing at all — the link is not usable.</summary>
    public bool Failed => Acknowledged.Count == 0;
}
