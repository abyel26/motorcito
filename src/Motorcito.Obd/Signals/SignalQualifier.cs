using System.Globalization;

namespace Motorcito.Obd.Signals;

public enum QualifierKind
{
    Wheel,
    Cylinder,
    Sensor
}

/// <summary>
/// Which instance of a signal a reading belongs to: which wheel, which
/// cylinder, which battery sensor.
///
/// Wheel position is not always known. Some catalogs name tires "Wheel 1–4"
/// with no mapping to a corner, and guessing "front-left" would put a warning
/// on the wrong tire. <see cref="WheelNumber"/> keeps that honest.
/// </summary>
public readonly record struct SignalQualifier(QualifierKind Kind, string Value)
{
    public static SignalQualifier FrontLeft => new(QualifierKind.Wheel, "FL");
    public static SignalQualifier FrontRight => new(QualifierKind.Wheel, "FR");
    public static SignalQualifier RearLeft => new(QualifierKind.Wheel, "RL");
    public static SignalQualifier RearRight => new(QualifierKind.Wheel, "RR");

    /// <summary>Inner rear wheel on a dual-rear-wheel truck.</summary>
    public static SignalQualifier RearLeftInner => new(QualifierKind.Wheel, "RLI");

    /// <summary>Inner rear wheel on a dual-rear-wheel truck.</summary>
    public static SignalQualifier RearRightInner => new(QualifierKind.Wheel, "RRI");

    public static SignalQualifier Spare => new(QualifierKind.Wheel, "SPARE");

    /// <summary>A wheel identified only by the source's numbering, position unknown.</summary>
    public static SignalQualifier WheelNumber(int number)
        => new(QualifierKind.Wheel, number.ToString(CultureInfo.InvariantCulture));

    public static SignalQualifier Cylinder(int number)
        => new(QualifierKind.Cylinder, number.ToString(CultureInfo.InvariantCulture));

    public static SignalQualifier Sensor(int number)
        => new(QualifierKind.Sensor, number.ToString(CultureInfo.InvariantCulture));

    /// <summary>True for a wheel whose physical position is known.</summary>
    public bool IsWheelPositionKnown
        => Kind == QualifierKind.Wheel && !int.TryParse(Value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()}:{Value}";
}
