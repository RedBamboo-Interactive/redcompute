using System.Windows.Media;

namespace RedCompute.App.ViewModels;

internal static class FriezeColors
{
    public static readonly SolidColorBrush Queued = Freeze(0xFF, 0xB7, 0x4D);
    public static readonly SolidColorBrush Running = Freeze(0x43, 0xA2, 0x5A);
    public static readonly SolidColorBrush Completed = Freeze(0x26, 0xA6, 0x9A);
    public static readonly SolidColorBrush Failed = Freeze(0xFF, 0x52, 0x52);
    public static readonly SolidColorBrush Cancelled = Freeze(0x72, 0x76, 0x7D);
    public static readonly SolidColorBrush Idle = Freeze(0x2A, 0x2A, 0x2A);
    public static readonly SolidColorBrush Empty = Freeze(0x2A, 0x2A, 0x2A);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
