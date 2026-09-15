namespace Motorcito.Obd.Signals;

/// <summary>What a signal describes, which decides whether it may be collected at all.</summary>
public enum SignalPrivacy
{
    /// <summary>A property of the vehicle: temperatures, pressures, wear. Collected normally.</summary>
    VehicleHealth,

    /// <summary>
    /// A property of how someone drives: seatbelt use, pedal and steering
    /// inputs, g-forces, door state. Not polled, stored or uploaded by default —
    /// under GDPR this is behavioural data about a person, which needs its own
    /// purpose and consent rather than riding along with vehicle health.
    /// </summary>
    DriverBehaviour
}

/// <summary>
/// One entry in Motorcito's own signal vocabulary.
///
/// Features are built against these keys, never against a particular car's
/// identifiers: a tire pressure view asks for <c>tire_pressure</c>, and any
/// source that can supply it for the connected car does. The plausibility range
/// is the guard that keeps a mis-mapped or mis-scaled catalog entry from
/// showing a consumer a confident wrong number.
/// </summary>
public sealed record CanonicalSignal(
    string Key,
    string Name,
    string Unit,
    double? PlausibleMin,
    double? PlausibleMax,
    SignalPrivacy Privacy = SignalPrivacy.VehicleHealth)
{
    public bool IsPlausible(double value)
        => (PlausibleMin is not { } min || value >= min)
           && (PlausibleMax is not { } max || value <= max);
}

public static class CanonicalSignals
{
    // Engine
    public static readonly CanonicalSignal OilTemp = new("oil_temp", "Oil temperature", SignalUnits.Celsius, -40, 160);
    public static readonly CanonicalSignal OilPressure = new("oil_pressure", "Oil pressure", SignalUnits.Kilopascal, 0, 1000);
    public static readonly CanonicalSignal OilLife = new("oil_life", "Oil life remaining", SignalUnits.Percent, 0, 100);
    public static readonly CanonicalSignal HeadTemp = new("head_temp", "Cylinder head temperature", SignalUnits.Celsius, -40, 200);
    public static readonly CanonicalSignal CatalystTemp = new("cat_temp", "Catalyst temperature", SignalUnits.Celsius, 0, 1100);
    public static readonly CanonicalSignal MisfireCount = new("misfire_count", "Misfire count", SignalUnits.Count, 0, 65535);
    public static readonly CanonicalSignal ThrottleDesired = new("throttle_desired", "Throttle desired", SignalUnits.Degree, 0, 90);
    public static readonly CanonicalSignal ThrottleActual = new("throttle_actual", "Throttle actual", SignalUnits.Degree, 0, 90);
    public static readonly CanonicalSignal FanOn = new("fan_on", "Cooling fan", SignalUnits.OnOff, 0, 1);
    public static readonly CanonicalSignal DtcCount = new("dtc_count", "Trouble codes stored", SignalUnits.Count, 0, 255);

    // Driveline
    public static readonly CanonicalSignal TransmissionTemp = new("trans_temp", "Transmission temperature", SignalUnits.Celsius, -40, 150);
    public static readonly CanonicalSignal TransmissionGear = new("trans_gear", "Current gear", SignalUnits.None, -1, 10);

    // Tires (qualified by wheel)
    public static readonly CanonicalSignal TirePressure = new("tire_pressure", "Tire pressure", SignalUnits.Kilopascal, 100, 450);
    public static readonly CanonicalSignal TireTemp = new("tire_temp", "Tire temperature", SignalUnits.Celsius, -40, 120);

    // Trip and fuel
    public static readonly CanonicalSignal Odometer = new("odometer", "Odometer", SignalUnits.Kilometre, 0, 2_000_000);
    public static readonly CanonicalSignal FuelLevelVolume = new("fuel_level_volume", "Fuel level", SignalUnits.Litre, 0, 200);
    public static readonly CanonicalSignal DistanceToEmpty = new("distance_to_empty", "Distance to empty", SignalUnits.Kilometre, 0, 2000);

    // 12 V system
    public static readonly CanonicalSignal AlternatorVoltage = new("alternator_voltage", "Alternator output", SignalUnits.Volt, 8, 16);
    public static readonly CanonicalSignal Battery12VVoltage = new("battery_12v_voltage", "12 V battery voltage", SignalUnits.Volt, 8, 16);
    public static readonly CanonicalSignal Battery12VCharge = new("battery_12v_charge", "12 V battery charge", SignalUnits.Percent, 0, 100);
    public static readonly CanonicalSignal Battery12VTemp = new("battery_12v_temp", "12 V battery temperature", SignalUnits.Celsius, -40, 100);

    // High-voltage battery (EV / hybrid)
    public static readonly CanonicalSignal HvStateOfCharge = new("hv_soc", "Battery charge", SignalUnits.Percent, 0, 100);
    public static readonly CanonicalSignal HvStateOfHealth = new("hv_soh", "Battery health", SignalUnits.Percent, 0, 100);
    public static readonly CanonicalSignal HvBatteryTempMin = new("hv_battery_temp_min", "Battery temperature (min)", SignalUnits.Celsius, -40, 80);
    public static readonly CanonicalSignal HvBatteryTempMax = new("hv_battery_temp_max", "Battery temperature (max)", SignalUnits.Celsius, -40, 80);
    public static readonly CanonicalSignal ChargingState = new("charging_state", "Charging", SignalUnits.None, null, null);

    // Cabin
    public static readonly CanonicalSignal CabinTemp = new("cabin_temp", "Cabin temperature", SignalUnits.Celsius, -40, 80);

    // Driver behaviour — recognised so sources can map to them and the policy
    // can block them, never so features can use them by default.
    public static readonly CanonicalSignal SeatbeltBuckled = new("seatbelt_buckled", "Seatbelt", SignalUnits.OnOff, 0, 1, SignalPrivacy.DriverBehaviour);
    public static readonly CanonicalSignal SteeringAngle = new("steering_angle", "Steering angle", SignalUnits.Degree, -900, 900, SignalPrivacy.DriverBehaviour);
    public static readonly CanonicalSignal BrakePedal = new("brake_pedal", "Brake pedal", SignalUnits.None, null, null, SignalPrivacy.DriverBehaviour);
    public static readonly CanonicalSignal LateralAcceleration = new("lateral_accel", "Lateral acceleration", SignalUnits.None, null, null, SignalPrivacy.DriverBehaviour);
    public static readonly CanonicalSignal DoorOpen = new("door_open", "Door open", SignalUnits.OnOff, 0, 1, SignalPrivacy.DriverBehaviour);

    public static IReadOnlyList<CanonicalSignal> All { get; } =
    [
        OilTemp, OilPressure, OilLife, HeadTemp, CatalystTemp, MisfireCount,
        ThrottleDesired, ThrottleActual, FanOn, DtcCount,
        TransmissionTemp, TransmissionGear,
        TirePressure, TireTemp,
        Odometer, FuelLevelVolume, DistanceToEmpty,
        AlternatorVoltage, Battery12VVoltage, Battery12VCharge, Battery12VTemp,
        HvStateOfCharge, HvStateOfHealth, HvBatteryTempMin, HvBatteryTempMax, ChargingState,
        CabinTemp,
        SeatbeltBuckled, SteeringAngle, BrakePedal, LateralAcceleration, DoorOpen,
    ];

    private static readonly Dictionary<string, CanonicalSignal> ByKey =
        All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public static CanonicalSignal? Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out var signal) ? signal : null;
}
