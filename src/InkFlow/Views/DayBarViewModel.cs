using InkFlow.Core;

namespace InkFlow.Views;

/// <summary>One column in the history bar charts.</summary>
public sealed class DayBarViewModel : ObservableObject
{
    private long _value;
    private double _barHeight;
    private bool _isToday;
    private string _tooltip;

    public DayBarViewModel(string dateKey, string label, string tooltip)
    {
        DateKey = dateKey;
        Label = label;
        _tooltip = tooltip;
    }

    public string DateKey { get; }

    public string Label { get; }

    public string Tooltip
    {
        get => _tooltip;
        set => SetProperty(ref _tooltip, value);
    }

    public long Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(HasValue));
            }
        }
    }

    public double BarHeight
    {
        get => _barHeight;
        set => SetProperty(ref _barHeight, value);
    }

    public bool IsToday
    {
        get => _isToday;
        set => SetProperty(ref _isToday, value);
    }

    public bool HasValue => _value > 0;
}
