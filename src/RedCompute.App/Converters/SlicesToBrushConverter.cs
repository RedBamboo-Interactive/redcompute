using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RedCompute.App.ViewModels;

namespace RedCompute.App.Converters;

public class SlicesToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not List<ColorSlice> slices || slices.Count == 0)
            return Brushes.Transparent;

        if (slices.Count == 1)
            return slices[0].Brush;

        var group = new DrawingGroup();
        double y = 0;
        foreach (var slice in slices)
        {
            group.Children.Add(new GeometryDrawing(
                slice.Brush,
                null,
                new RectangleGeometry(new Rect(0, y, 1, slice.Proportion))));
            y += slice.Proportion;
        }

        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
