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
/// A manufacturer-specific identifier a simulated ECU answers.
/// </summary>
/// <param name="Header">The request header that reaches this ECU, e.g. "7E0".</param>
/// <param name="Service">0x21 or 0x22.</param>
/// <param name="Identifier">The local identifier or DID.</param>
/// <param name="IdentifierLength">1 for service 0x21, 2 for 0x22.</param>
/// <param name="Bytes">Data bytes to return, given seconds since connect.</param>
public sealed record SimulatedIdentifier(
    string Header,
    byte Service,
    ushort Identifier,
    int IdentifierLength,
    Func<double, byte[]> Bytes);

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
    /// <summary>The engine ECU's physical request header. Mode 01 answers here as well as on the functional header.</summary>
    private const string EngineHeader = "7E0";

    private readonly SimulatorQuirks _quirks;
    private readonly Random _random;
    private readonly HashSet<byte> _supported;
    private readonly List<string> _storedDtcs;
    private readonly string? _vin;
    private readonly IReadOnlyList<SimulatedIdentifier> _extended;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ObdConnectionState _state = ObdConnectionState.Disconnected;

    /// <summary>The header set by the last <c>ATSH</c>; reset by <c>ATZ</c> and <c>ATD</c>.</summary>
    private string _header = SignalRequest.FunctionalHeader;

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
        int randomSeed = 1234,
        IEnumerable<SimulatedIdentifier>? extendedIdentifiers = null)
    {
        Name = name;
        _quirks = quirks ?? SimulatorQuirks.ObdLinkMxPlus;
        _random = new Random(randomSeed);
        _vin = vin;
        _storedDtcs = storedDtcs?.ToList() ?? [];
        _extended = extendedIdentifiers?.ToList() ?? [];

        // Default to a well-equipped modern car: everything the registry knows,
        // minus bank-2 trims (inline engine).
        //
        // Oil temp (0x5C) used to be excluded here too, which meant the one
        // parameter the app most wants to show could never be seen without a
        // real car. Cars that report it exist and are the interesting case, so
        // the default profile now includes it; pass supportedPids explicitly to
        // simulate a car that does not.
        _supported = supportedPids?.ToHashSet()
            ?? PidRegistry.All.Select(d => d.Pid).Where(p => p is not (0x08 or 0x09)).ToHashSet();
    }

    /// <summary>
    /// A 2018 MX-5 (ND) as observed on the real car: no standard oil temp
    /// (0x5C) or narrowband O2 (0x14), so the interesting values are only
    /// reachable through manufacturer-specific reads.
    ///
    /// The identifiers below are the simulator's own fixtures, written here
    /// rather than loaded from any catalog, so protocol tests never depend on a
    /// removable data source. They are chosen to match what catalogs describe
    /// for this car so the profile path can be exercised end to end.
    /// </summary>
    public static SimulatedObdAdapter MazdaMx5Nd(SimulatorQuirks? quirks = null, int randomSeed = 1234)
    {
        static byte TirePressure(double bar) => Clamp8(bar * 100000.0 / 1373.0);
        static byte TireTemp(double celsius) => Clamp8(celsius + 50);

        // Per-wheel base pressures differ slightly, as real tires do, and warm
        // up a little over the drive.
        double[] basePressures = [2.30, 2.32, 2.20, 2.25];

        var identifiers = new List<SimulatedIdentifier>
        {
            // Engine oil temperature: 16-bit, value / 100 − 40 °C. Warms more
            // slowly than coolant and settles hotter.
            new(EngineHeader, 0x22, 0x1310, 2, t => Word16((Math.Min(102, 20 + t * 0.42) + 40) * 100)),

            // Engine oil pressure: 16-bit kPa, rising with engine speed.
            new(EngineHeader, 0x22, 0x0415, 2, t => Word16(250 + 150 * Math.Abs(Math.Sin(t / 20.0)))),

            // Throttle desired and actual: 16-bit degrees × 512, actual lagging desired.
            new(EngineHeader, 0x22, 0x091A, 2, t => Word16((8 + 6 * Math.Abs(Math.Sin(t / 20.0))) * 512)),
            new(EngineHeader, 0x22, 0x093C, 2, t => Word16((7.8 + 6 * Math.Abs(Math.Sin((t - 0.3) / 20.0))) * 512)),

            // Alternator output: 16-bit volts × 8.
            new(EngineHeader, 0x22, 0x16E9, 2, _ => Word16(14.1 * 8)),

            // Alternator field coil duty: 16-bit, × 200 / 65535 %. Catalogs define
            // it but Motorcito does not use it, so it exercises the unmapped path.
            new(EngineHeader, 0x22, 0x16E8, 2, _ => Word16(35 * 65535 / 200.0)),

            // Cooling fan relay: bit 5 of the first byte, cycling once warm.
            new(EngineHeader, 0x22, 0x0967, 2, t => [(byte)(t > 120 && (int)(t / 30) % 2 == 0 ? 0x04 : 0x00)]),

            // Trouble codes stored: none.
            new(EngineHeader, 0x22, 0x0202, 2, _ => [0x00]),
        };

        for (var wheel = 0; wheel < 4; wheel++)
        {
            var basePressure = basePressures[wheel];
            var offset = wheel;
            identifiers.Add(new("720", 0x22, (ushort)(0x2A05 + wheel), 2,
                t => [TirePressure(basePressure + Math.Min(0.15, t * 0.0005))]));
            identifiers.Add(new("720", 0x22, (ushort)(0x2A0A + wheel), 2,
                t => [TireTemp(Math.Min(45, 20 + t * 0.05) + offset)]));
        }

        return new SimulatedObdAdapter(
            name: "Simulated Vehicle (Mazda MX-5 ND 2018)",
            supportedPids: PidRegistry.All.Select(d => d.Pid).Where(p => p is not (0x08 or 0x09 or 0x14 or 0x5C)),
            quirks: quirks,
            vin: "JM1NDAD75J0100001",
            randomSeed: randomSeed,
            extendedIdentifiers: identifiers);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ObdConnectionState.Connecting);
        _startedAt = DateTime.UtcNow;
        _header = SignalRequest.FunctionalHeader;
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
            return AtResponse(command);

        // Standard services reach the engine ECU on the functional header and
        // on its own physical header. Pointed at any other module, nothing that
        // speaks Mode 01 is listening.
        var engineListening = _header is SignalRequest.FunctionalHeader or EngineHeader;

        if (command is "03" or "07" or "0A")
            return engineListening ? DtcResponse(command) : "NO DATA";

        if (command == "0902")
            return engineListening ? VinResponse() : "NO DATA";

        if (command.Length >= 4 && command.StartsWith("01", StringComparison.Ordinal))
        {
            if (!engineListening)
                return "NO DATA";

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

        if (command.Length is 4 or 6 && IsHexOnly(command) && command[..2] is "21" or "22")
            return ExtendedResponse(command);

        return "?";
    }

    private string AtResponse(string command)
    {
        // ATZ answers with a version banner rather than OK, like the real thing,
        // and both it and ATD restore the default functional header.
        if (command == "ATZ")
        {
            _header = SignalRequest.FunctionalHeader;
            return "ELM327 v1.5";
        }

        if (command == "ATD")
        {
            _header = SignalRequest.FunctionalHeader;
            return "OK";
        }

        if (command.StartsWith("ATSH", StringComparison.Ordinal))
        {
            var header = command[4..];
            if (!SignalRequest.IsElevenBitHeader(header))
                return "?";

            _header = header;
            return "OK";
        }

        return "OK";
    }

    /// <summary>
    /// Answers a service 0x21/0x22 read the way a real bus does: silence on the
    /// functional header or at an address with no module, a 7F 31 refusal from
    /// a module that exists but does not know the identifier, and data otherwise.
    /// </summary>
    private string ExtendedResponse(string command)
    {
        if (_header == SignalRequest.FunctionalHeader)
            return "NO DATA";

        var atThisAddress = _extended.Where(e => e.Header == _header).ToList();
        if (atThisAddress.Count == 0)
            return "NO DATA";

        var service = Convert.ToByte(command[..2], 16);
        var identifierHex = command[2..];
        var identifierLength = identifierHex.Length / 2;
        var identifier = Convert.ToUInt16(identifierHex, 16);

        var match = atThisAddress.FirstOrDefault(e =>
            e.Service == service && e.Identifier == identifier && e.IdentifierLength == identifierLength);

        if (match is null)
            return $"7F{service:X2}31";

        var elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
        return $"{service + 0x40:X2}{identifierHex}" + Hex(match.Bytes(elapsed));
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
            // Oil warms more slowly than coolant and settles hotter. Modelling
            // that rather than a constant makes the gauge move during a demo,
            // and the lag is real: oil temp trailing coolant on a cold start is
            // what a healthy engine looks like.
            0x5C => Math.Min(102, 20 + elapsed * 0.42),                // oil temp °C
            0x5E => 3 + 4 * Math.Abs(phase),                           // fuel rate L/h
            _ => 0
        };

        return EncodeValue(def, value);
    }

    /// <summary>Inverts each registry formula so the simulator emits bytes the decoder will read back as <paramref name="value"/>.</summary>
    private static byte[] EncodeValue(PidDefinition def, double value)
    {
        return def.Pid switch
        {
            0x04 or 0x11 or 0x2F => [Clamp8(value * 255.0 / 100.0)],
            0x05 or 0x0F or 0x46 or 0x5C => [Clamp8(value + 40)],
            0x06 or 0x07 or 0x08 or 0x09 => [Clamp8(value * 128.0 / 100.0 + 128)],
            0x0B or 0x0D => [Clamp8(value)],
            0x0E => [Clamp8((value + 64) * 2)],
            0x0C => Word16(value * 4),
            0x10 => Word16(value * 100),
            0x14 or 0x15 => [Clamp8(value * 200), 0xFF],
            0x1F or 0x21 => Word16(value),
            0x42 => Word16(value * 1000),
            0x43 => Word16(value * 255.0 / 100.0),
            0x44 => Word16(value * 32768),
            0x5E => Word16(value * 20),
            _ => new byte[def.ByteCount]
        };
    }

    private static byte Clamp8(double d) => (byte)Math.Clamp(Math.Round(d), 0, 255);

    private static byte[] Word16(double d)
    {
        var raw = (int)Math.Clamp(Math.Round(d), 0, 65535);
        return [(byte)(raw >> 8), (byte)(raw & 0xFF)];
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
