using Motorcito.Data;

namespace Motorcito.App;

/// <summary>
/// Reads altitude from the device's location services, and keeps nothing else.
///
/// The coordinates come back in the same <see cref="Location"/> object as the
/// altitude and are dropped on the spot: only the single <c>double</c> is
/// retained, and it is the only thing this class can hand to anything else.
/// That is the privacy rule made structural — there is no latitude or
/// longitude held anywhere for a later change to accidentally persist.
/// </summary>
public sealed class GeolocationAltitudeProvider : IAltitudeProvider
{
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public double? LastKnownAltitudeM { get; private set; }

    /// <summary>Set when location is unavailable, for surfacing why altitude is missing.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>
    /// Updates the cached altitude. Safe to call often; failures are recorded
    /// rather than thrown.
    ///
    /// Called before a drive begins rather than at trip start, because a
    /// location fix can take seconds and the trip must open the moment the
    /// engine runs.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

            if (status != PermissionStatus.Granted)
            {
                // Logging must continue without location. Altitude stays NULL,
                // which analysis can distinguish from "measured at sea level".
                UnavailableReason = "Location permission not granted";
                return;
            }

            // Last known first: it is instant, and for altitude banding a fix
            // from a few minutes ago is as good as a fresh one.
            var location = await Geolocation.Default.GetLastKnownLocationAsync()
                           ?? await Geolocation.Default.GetLocationAsync(
                               new GeolocationRequest(GeolocationAccuracy.Medium, _timeout), cancellationToken);

            if (location is null)
            {
                UnavailableReason = "No location fix available";
                return;
            }

            // The only value taken from the fix. Latitude and longitude go out
            // of scope with the Location object and are never stored.
            LastKnownAltitudeM = location.Altitude;
            UnavailableReason = location.Altitude is null ? "Device reported no altitude" : null;
        }
        catch (FeatureNotSupportedException)
        {
            UnavailableReason = "Location not supported on this device";
        }
        catch (Exception ex)
        {
            // Never let a location problem stop a drive being logged.
            UnavailableReason = ex.Message;
        }
    }
}
