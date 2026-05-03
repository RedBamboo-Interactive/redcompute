using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;

namespace RedCompute.App.Helpers;

public static class IconHelper
{
    public static Icon CreateTrayIcon(Color mainColor, int size = 32)
    {
        using var bmp = DrawServerIcon(mainColor, size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static BitmapSource CreateWindowIcon(Color mainColor, int size = 256)
    {
        using var bmp = DrawServerIcon(mainColor, size);
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Bitmap DrawServerIcon(Color mainColor, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 256f;
        var white = Color.White;

        float margin = 12 * s;
        using var circleBrush = new SolidBrush(mainColor);
        g.FillEllipse(circleBrush, margin, margin, size - 2 * margin, size - 2 * margin);

        float bodyX = 58 * s;
        float bodyW = 140 * s;
        float bayH = 38 * s;
        float gap = 14 * s;
        float topY = 68 * s;
        float botY = topY + bayH + gap;

        using var whiteBrush = new SolidBrush(white);
        g.FillRectangle(whiteBrush, bodyX, topY, bodyW, bayH);
        g.FillRectangle(whiteBrush, bodyX, botY, bodyW, bayH);

        float ledR = 11 * s;
        float ledX = (58 + 140 - 11 - 14) * s;
        g.FillEllipse(circleBrush, ledX, topY + (bayH - ledR) / 2, ledR, ledR);
        g.FillEllipse(circleBrush, ledX, botY + (bayH - ledR) / 2, ledR, ledR);

        using var linePen = new Pen(mainColor, 2 * s);
        float lineX1 = 68 * s;
        float lineX2 = ledX - 8 * s;
        g.DrawLine(linePen, lineX1, topY + bayH * 0.35f, lineX2, topY + bayH * 0.35f);
        g.DrawLine(linePen, lineX1, topY + bayH * 0.65f, lineX2, topY + bayH * 0.65f);
        g.DrawLine(linePen, lineX1, botY + bayH * 0.35f, lineX2, botY + bayH * 0.35f);
        g.DrawLine(linePen, lineX1, botY + bayH * 0.65f, lineX2, botY + bayH * 0.65f);

        float cx = size / 2f;
        float connTop = botY + bayH + 4 * s;
        float connBot = connTop + 20 * s;
        using var connPen = new Pen(white, 4 * s);
        g.DrawLine(connPen, cx, connTop, cx, connBot);

        float nodeR = 10 * s;
        float nodeY = connBot + 2 * s;
        float n1 = cx - 36 * s;
        float n3 = cx + 36 * s;

        g.DrawLine(connPen, n1, connBot, n3, connBot);
        g.DrawLine(connPen, n1, connBot, n1, nodeY + nodeR / 2);
        g.DrawLine(connPen, n3, connBot, n3, nodeY + nodeR / 2);

        g.FillEllipse(whiteBrush, n1 - nodeR / 2, nodeY, nodeR, nodeR);
        g.FillEllipse(whiteBrush, cx - nodeR / 2, nodeY, nodeR, nodeR);
        g.FillEllipse(whiteBrush, n3 - nodeR / 2, nodeY, nodeR, nodeR);

        return bmp;
    }
}
