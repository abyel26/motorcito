using System.ComponentModel;

namespace Motorcito.App;

/// <summary>One line in the Vehicle signals card: a signal, its value, and what the car said about it.</summary>
public sealed class SignalRow : INotifyPropertyChanged
{
    private string _value = "—";

    public required string Name { get; init; }

    /// <summary>Where the value comes from, or why there is none — e.g. "live · 720 22:2A05" or "not supported (7F 31)".</summary>
    public required string Status { get; init; }

    /// <summary>The snapshot key to refresh <see cref="Value"/> from, or null for rows that are not polled.</summary>
    public string? LiveKey { get; init; }

    /// <summary>Verified rows first, then answered-but-unused, then everything the car did not offer.</summary>
    public int Order { get; init; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
