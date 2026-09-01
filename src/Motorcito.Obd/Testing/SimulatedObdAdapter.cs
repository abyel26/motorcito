using System.Globalization;

namespace Motorcito.Obd.Testing;

/// <summary>
/// Controls how badly the simulated adapter misbehaves, so the parser can be
/// exercised against clone-grade firmware without owning a clone.
/// </summary>
public sealed class SimulatorQuirks
{
    /// <summary>Echo the command back despite ATE0.</summary>
    public bool EchoCommands { get; init; }

    /// <summary>Emit spaces between bytes despite ATS0.</summary>
    public bool EmitSpaces { get; init; }

    /// <summary>Fraction of replies (0–1) prefixed with "SEARCHING...".</summary>
    public double SearchingRate { get; init; }

    /// <summary>Fraction of replies (0–1) replaced with a bus error.</summary>
    public double BusErrorRate { get; init; }

    /// <summary>Fraction of replies (0–1) truncated by one byte.</summary>
    public double TruncationRate { get; init; }

    /// <summary>Artificial per-command latency. ~10 ms approximates an MX+; ~80 ms a clone.</summary>
    public TimeSpan Latency { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>A well-behaved MFi adapter: fast, clean, no surprises.</summary>
    public static SimulatorQuirks ObdLinkMxPlus => new();

    /// <summary>A cheap clone: slow, echoes, spaces, drops frames. What the parser must survive.</summary>
    public static SimulatorQuirks CheapClone => new()
    {
        EchoCommands = true,
        EmitSpaces = true,
        SearchingRate = 0.10,
        BusErrorRate = 0.05,
        TruncationRate = 0.03,
        Latency = TimeSpan.FromMilliseconds(80)
    };
}

/// <summary>
/// An in-memory OBD-II vehicle.
///
/// Exists so the protocol layer, the polling loop and the rules engine can be
/// built and tested before hardware arrives, and so regressions are reproducible
/// without a car. It implements <see cref="IObdAdapter"/> like any other
/// transport, which is the point of that abstraction.
/// </summary>
public sealed class SimulatedObdAdapter : IObdAdapter
{
    private readonly SimulatorQuirks _quirks;
    private readonly Random _random;
    private readonly HashSet<byte> _supported;
    private readonly List<string> _storedDtcs;
    private readonly string? _vin;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ObdConnectionState _state = ObdConnectionState.Disconnected;

    /// <summary>Engine state the simulation advances over time.</summary>
    private DateTime _startedAt;

    public string Name { get; }

    public ObdConnectionState State => _state;

    public event EventHandler<ObdConnectionState>? StateChanged;

    /// <summary>Every command received, in order. Lets tests assert on query budget and init sequence.</summary>
    public List<string> CommandLog { get; } = [];

    public SimulatedObdAdapter(
        string name = "Simulated Vehicle",
        IEnumerable<byte>? supportedPids = null,
        SimulatorQuirks? quirks = null,
        IEnumerable<string>? storedDtcs = null,
        string? vin = "WBS8M9C50J5K12345",
        int randomSeed = 1234)
    {
        Name = name;
        _quirks = quirks ?? SimulatorQuirks.ObdLinkMxPlus;
        _random = new Random(randomSeed);
        _vin = vin;
        _storedDtcs = storedDtcs?.ToList() ?? [];

        // Default to a well-equipped modern car: everything the registry knows,
        // minus bank-2 trims (inline engine) and oil temp.
        _supported = supportedPids?.ToHashSet()
            ?? PidRegistry.All.Select(d => d.Pid).Where(p => p is not (0x08 or 0x09 or 0x5C)).ToHashSet();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ObdConnectionState.Connecting);
        _startedAt = DateTime.UtcNow;
        SetState(ObdConnectionState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        SetState(ObdConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<string> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_state != ObdConnectionState.Connected)
            throw new ObdNotConnectedException();

        // Serialise like a real adapter: one request/response at a time.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommandLog.Add(command);

            if (_quirks.Latency > TimeSpan.Zero)
                await Task.Delay(_quirks.Latency, cancellationToken).ConfigureAwait(false);

            var body = Respond(command.Replace(" ", string.Empty).ToUpperInvariant());
            return Decorate(command, body);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Respond(string command)
    {
        if (command.StartsWith("AT", StringComparison.Ordinal))
        {
            // ATZ answers with a version banner rather than OK, like the real thing.
            return command == "ATZ" ? "ELM327 v1.5" : "OK";
        }

        if (command is "03" or "07" or "0A")
            return DtcResponse(command);

        if (command == "0902")
            return VinResponse();

        if (command.Length >= 4 && command.StartsWith("01", StringComparison.Ordinal))
        {
            var pid = Convert.ToByte(command.Substring(2, 2), 16);

            if (pid is 0x00 or 0x20 or 0x40 or 0x60)
                return CapabilityMask(pid);

            if (!_supported.Contains(pid))
                return "NO DATA";

            var def = PidRegistry.Find(pid);
            if (def is null)
                return "NO DATA";

            return $"41{pid:X2}" + Hex(SimulateBytes(def));
        }

        return "?";
    }

    /// <summary>Builds the capability bitmask for a range from the configured supported set.</summary>
    private string CapabilityMask(byte maskPid)
    {
        uint mask = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            var pid = (byte)(maskPid + bit + 1);
            if (_supported.Contains(pid))
                mask |= 1u << (31 - bit);
        }

        // Set the continuation flag when anything exists beyond this range.
        var nextBase = (byte)(maskPid + 0x20);
        if (nextBase <= 0x60 && _supported.Any(p => p > nextBase && p <= nextBase + 0x20))
            mask |= 1u << 0;

        return $"41{maskPid:X2}{mask:X8}";
    }

    private string DtcResponse(string command)
    {
        var responseMode = command switch { "03" => 0x43, "07" => 0x47, _ => 0x4A };
        if (_storedDtcs.Count == 0)
            return $"{responseMode:X2}00";

        var payload = string.Concat(_storedDtcs.Select(EncodeDtc));
        return $"{responseMode:X2}{_storedDtcs.Count:X2}{payload}";
    }

    private static string EncodeDtc(string code)
    {
        var letterBits = code[0] switch { 'P' => 0, 'C' => 1, 'B' => 2, _ => 3 };
        var firstDigit = code[1] - '0';
        var second = Convert.ToInt32(code[2].ToString(), 16);
        var lastTwo = Convert.ToInt32(code[3..5], 16);

        var a = (byte)(letterBits << 6 | firstDigit << 4 | second);
        return $"{a:X2}{lastTwo:X2}";
    }

    private string VinResponse()
    {
        if (_vin is null)
            return "NO DATA";

        var hex = string.Concat(_vin.Select(c => ((byte)c).ToString("X2")));
        return "490201" + hex;
    }

    /// <summary>
    /// Produces plausible, time-varying values so charts and rules have
    /// something realistic to chew on. Not a physical model — just coherent
    /// enough that idle/cruise buckets differ.
    /// </summary>
    private byte[] SimulateBytes(PidDefinition def)
    {
        var elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
        var phase = Math.Sin(elapsed / 20.0);

        double value = def.Pid switch
        {
            0x04 => 25 + 20 * phase,                                   // engine load %
            0x05 => Math.Min(90, 20 + elapsed * 0.6),                  // coolant warming to 90 °C
            0x06 => 2 * phase,                                         // STFT %
            0x07 => 3.5,                                               // LTFT %
            0x0B => 40 + 30 * phase,                                   // MAP kPa
            0x0C => 800 + 1800 * Math.Abs(phase),                      // rpm
            0x0D => Math.Max(0, 60 + 40 * phase),                      // speed kph
            0x0E => 12 + 6 * phase,                                    // timing advance
            0x0F => 25,                                                // IAT °C
            0x10 => 5 + 15 * Math.Abs(phase),                          // MAF g/s
            0x11 => 15 + 25 * Math.Abs(phase),                         // throttle %
            0x14 => 0.45 + 0.35 * Math.Sin(elapsed * 6),               // O2 upstream, switching
            0x15 => 0.6 + 0.02 * Math.Sin(elapsed * 0.4),              // O2 downstream, flat
            0x1F => elapsed,                                           // runtime s
            0x21 => 0,                                                 // distance with MIL on
            0x2F => Math.Max(5, 70 - elapsed * 0.01),                  // fuel level %
            0x42 => 14.2,                                              // module voltage
            0x43 => 30 + 20 * Math.Abs(phase),                         // absolute load %
            0x44 => 1.0,                                               // lambda
            0x46 => 22,                                                // ambient °C
            0x5C => 95,                                                // oil temp °C
            0x5E => 3 + 4 * Math.Abs(phase),                           // fuel rate L/h
            _ => 0
        };

        return EncodeValue(def, value);
    }

    /// <summary>Inverts each registry formula so the simulator emits bytes the decoder will read back as <paramref name="value"/>.</summary>
    private static byte[] EncodeValue(PidDefinition def, double value)
    {
        static byte Clamp(double d) => (byte)Math.Clamp(Math.Round(d), 0, 255);
        static byte[] Word(double d)
        {
            var raw = (int)Math.Clamp(Math.Round(d), 0, 65535);
            return [(byte)(raw >> 8), (byte)(raw & 0xFF)];
        }

        return def.Pid switch
        {
            0x04 or 0x11 or 0x2F => [Clamp(value * 255.0 / 100.0)],
            0x05 or 0x0F or 0x46 or 0x5C => [Clamp(value + 40)],
            0x06 or 0x07 or 0x08 or 0x09 => [Clamp(value * 128.0 / 100.0 + 128)],
            0x0B or 0x0D => [Clamp(value)],
            0x0E => [Clamp((value + 64) * 2)],
            0x0C => Word(value * 4),
            0x10 => Word(value * 100),
            0x14 or 0x15 => [Clamp(value * 200), 0xFF],
            0x1F or 0x21 => Word(value),
            0x42 => Word(value * 1000),
            0x43 => Word(value * 255.0 / 100.0),
            0x44 => Word(value * 32768),
            0x5E => Word(value * 20),
            _ => new byte[def.ByteCount]
        };
    }

    /// <summary>Applies the configured misbehaviour to an otherwise-correct reply.</summary>
    private string Decorate(string command, string body)
    {
        if (_random.NextDouble() < _quirks.BusErrorRate)
            return "CAN ERROR\r";

        if (body.Length > 2 && !body.StartsWith("OK", StringComparison.Ordinal) && _random.NextDouble() < _quirks.TruncationRate)
            body = body[..^2];

        if (_quirks.EmitSpaces && body.Length > 1 && IsHexOnly(body))
            body = string.Join(' ', Enumerable.Range(0, body.Length / 2).Select(i => body.Substring(i * 2, 2)));

        var prefix = string.Empty;
        if (_quirks.EchoCommands)
            prefix += command + "\r";
        if (_random.NextDouble() < _quirks.SearchingRate)
            prefix += "SEARCHING...\r";

        return prefix + body + "\r";
    }

    private static bool IsHexOnly(string s)
        => s.All(c => Uri.IsHexDigit(c));

    private static string Hex(byte[] bytes)
        => string.Concat(bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private void SetState(ObdConnectionState next)
    {
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
