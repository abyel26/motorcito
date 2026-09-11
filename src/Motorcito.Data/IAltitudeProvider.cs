namespace Motorcito.Data;

/// <summary>
/// Supplies altitude, and nothing else.
///
/// <para><b>Why altitude is needed.</b> Air density falls with altitude, so the
/// same engine running correctly produces measurably different fuel trims at
/// 2,000 m than at sea level. Without recording it, a drive into the mountains
/// looks exactly like a developing fault — the class of false positive that
/// destroys trust in the whole product.</para>
///
/// <para><b>Why this interface is this narrow.</b> The privacy rule is that
/// coordinates are derived from and immediately discarded: altitude is all the
/// normalisation needs, and never persisting latitude or longitude removes a
/// location trace from the database entirely. Exposing only a single number
/// makes that rule structural rather than a convention someone has to
/// remember — there is no coordinate here to accidentally store.</para>
///
/// <para>It also keeps <c>Motorcito.Data</c> free of any platform reference,
/// the same way <c>IObdAdapter</c> does for transport.</para>
/// </summary>
public interface IAltitudeProvider
{
    /// <summary>
    /// Most recent known altitude in metres above sea level, or null if it has
    /// not been determined — permission refused, no fix yet, or a device
    /// without GPS.
    ///
    /// Deliberately a cached value rather than an async lookup: it is read on
    /// the hot path when a trip opens, and waiting for a location fix there
    /// would stall the first samples of a drive. A stale altitude from a few
    /// minutes ago is accurate enough for banding; a delayed trip start is not
    /// acceptable.
    /// </summary>
    double? LastKnownAltitudeM { get; }
}

/// <summary>
/// Used when altitude is unavailable — permission denied, or a build with no
/// location support.
///
/// Trips still record; <c>start_altitude_m</c> stays NULL, and any later
/// analysis can tell "not measured" from "measured at sea level". The app must
/// work without location, so this is a supported configuration rather than a
/// failure mode.
/// </summary>
public sealed class NullAltitudeProvider : IAltitudeProvider
{
    public static readonly NullAltitudeProvider Instance = new();

    public double? LastKnownAltitudeM => null;
}
