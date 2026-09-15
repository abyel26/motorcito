namespace Motorcito.Obd;

/// <summary>
/// The diagnostic services Motorcito will send when a request is driven by
/// profile data rather than written in code.
///
/// Signal definitions come from outside this codebase — a community catalog
/// today, possibly a downloaded one later — and data is not trusted to be
/// benign. An entry naming service 0x2E would write to an ECU, 0x31 would run
/// a routine, 0x11 would reset a module. Every profile-driven request passes
/// through <see cref="EnsureAllowed"/>, so the worst a bad catalog entry can do
/// is ask a question the car declines.
/// </summary>
public static class ReadOnlyServicePolicy
{
    private static readonly HashSet<byte> Allowed =
    [
        0x01, // current data
        0x02, // freeze frame
        0x03, // stored DTCs
        0x07, // pending DTCs
        0x09, // vehicle information
        0x0A, // permanent DTCs
        0x21, // KWP2000 ReadDataByLocalIdentifier (Toyota and others)
        0x22, // UDS ReadDataByIdentifier
    ];

    public static IReadOnlySet<byte> AllowedServices => Allowed;

    public static bool IsAllowed(byte service) => Allowed.Contains(service);

    /// <exception cref="ServiceNotAllowedException">The service is not a read.</exception>
    public static void EnsureAllowed(byte service)
    {
        if (!Allowed.Contains(service))
            throw new ServiceNotAllowedException(service);
    }
}

/// <summary>
/// Thrown instead of sending a non-read service.
///
/// Deliberately not an <see cref="ObdException"/>: the poll loop treats those
/// as ordinary link failures and carries on. A catalog asking for a write is a
/// defect to surface, not a flaky PID to quietly drop.
/// </summary>
public sealed class ServiceNotAllowedException(byte service)
    : InvalidOperationException($"OBD service 0x{service:X2} is not a read-only service and will not be sent.")
{
    public byte Service { get; } = service;
}
