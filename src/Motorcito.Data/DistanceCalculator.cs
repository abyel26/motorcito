namespace Motorcito.Data;

/// <summary>
/// Integrates speed over time to produce distance.
///
/// Trapezoidal rather than last-value: at typical sample rates a last-value
/// integration systematically over-reads under acceleration and under-reads
/// under braking. Distance feeds fuel economy (km/L), so a consistent bias
/// would eventually surface as a fuel-economy trend that is really just a
/// change in driving style — the class of false positive that destroys trust
/// in the whole product.
///
/// <see cref="TripRecorder"/> integrates incrementally as samples arrive;
/// orphan recovery integrates a whole stored trip at once. Both call into the
/// same step function here so the two paths cannot drift apart.
/// </summary>
public static class DistanceCalculator
{
    /// <summary>
    /// Distance covered between two consecutive samples, in km.
    /// Returns zero when the interval is missing, zero, negative, or
    /// implausibly long — a stalled or rewound clock must not inject garbage.
    /// </summary>
    public static double Step(double previousSpeedKph, double speedKph, TimeSpan elapsed)
    {
        var hours = elapsed.TotalHours;
        if (hours <= 0 || hours >= 1)
            return 0;

        return (speedKph + previousSpeedKph) / 2.0 * hours;
    }

    /// <summary>
    /// Total distance over an ordered run of samples. Samples without a speed
    /// reading break the chain rather than being treated as zero: a gap in
    /// reporting is not the car standing still.
    /// </summary>
    public static double Total(IEnumerable<Sample> samplesInTimeOrder)
    {
        double total = 0;
        DateTime? lastAt = null;
        double? lastSpeed = null;

        foreach (var sample in samplesInTimeOrder)
        {
            if (sample.SpeedKph is not { } speed)
            {
                lastAt = null;
                lastSpeed = null;
                continue;
            }

            if (lastAt is { } at && lastSpeed is { } previous)
                total += Step(previous, speed, sample.Timestamp - at);

            lastAt = sample.Timestamp;
            lastSpeed = speed;
        }

        return total;
    }
}
