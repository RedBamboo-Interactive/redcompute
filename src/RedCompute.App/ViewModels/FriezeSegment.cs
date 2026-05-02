using System.ComponentModel;
using System.Windows.Media;

namespace RedCompute.App.ViewModels;

public class FriezeSegment : INotifyPropertyChanged
{
    private SolidColorBrush _color;
    private string _tooltip;

    public FriezeSegment(SolidColorBrush color, string tooltip)
    {
        _color = color;
        _tooltip = tooltip;
    }

    public SolidColorBrush Color
    {
        get => _color;
        set { if (_color != value) { _color = value; PropertyChanged?.Invoke(this, new(nameof(Color))); } }
    }

    public string Tooltip
    {
        get => _tooltip;
        set { if (_tooltip != value) { _tooltip = value; PropertyChanged?.Invoke(this, new(nameof(Tooltip))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
