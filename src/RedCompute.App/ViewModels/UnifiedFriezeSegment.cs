using System.ComponentModel;
using System.Windows.Media;

namespace RedCompute.App.ViewModels;

public record ColorSlice(SolidColorBrush Brush, double Proportion);

public class UnifiedFriezeSegment : INotifyPropertyChanged
{
    private List<ColorSlice> _slices;
    private string _tooltip;

    public UnifiedFriezeSegment(List<ColorSlice> slices, string tooltip)
    {
        _slices = slices;
        _tooltip = tooltip;
    }

    public List<ColorSlice> Slices
    {
        get => _slices;
        set { if (_slices != value) { _slices = value; PropertyChanged?.Invoke(this, new(nameof(Slices))); } }
    }

    public string Tooltip
    {
        get => _tooltip;
        set { if (_tooltip != value) { _tooltip = value; PropertyChanged?.Invoke(this, new(nameof(Tooltip))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
