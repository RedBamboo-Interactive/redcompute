using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using RedCompute.Core.Providers;

namespace RedCompute.App.TrayIcon;

public class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private DispatcherTimer? _statusTimer;

    public void Initialize(Window mainWindow)
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RedCompute",
            Icon = CreateIcon(Colors.Gray),
            ContextMenu = BuildContextMenu(mainWindow),
            MenuActivation = PopupActivationMode.RightClick
        };

        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowWindow(mainWindow);

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await UpdateIconStatus();
        _statusTimer.Start();
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu(Window mainWindow)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show RedCompute" };
        showItem.Click += (_, _) => ShowWindow(mainWindow);
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var statusItem = new System.Windows.Controls.MenuItem { Header = "Status: checking...", IsEnabled = false };
        statusItem.Tag = "status";
        menu.Items.Add(statusItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) =>
        {
            _trayIcon?.Dispose();
            Application.Current.Shutdown();
        };
        menu.Items.Add(exitItem);

        menu.Opened += async (_, _) => await RefreshMenuStatus(menu);

        return menu;
    }

    private async Task RefreshMenuStatus(System.Windows.Controls.ContextMenu menu)
    {
        var statusLines = new List<string>();
        foreach (var (slug, entry) in App.Registry.Capabilities)
        {
            var status = entry.ActiveProvider != null
                ? await entry.ActiveProvider.GetStatusAsync()
                : BackendStatus.Stopped;
            statusLines.Add($"{entry.Definition.DisplayName}: {status}");
        }

        foreach (System.Windows.Controls.MenuItem item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (item.Tag?.ToString() == "status")
            {
                item.Header = statusLines.Count > 0 ? string.Join("\n", statusLines) : "No capabilities";
                break;
            }
        }
    }

    private async Task UpdateIconStatus()
    {
        var anyRunning = false;
        var anyError = false;

        foreach (var (_, entry) in App.Registry.Capabilities)
        {
            if (entry.ActiveProvider == null) continue;
            var status = await entry.ActiveProvider.GetStatusAsync();
            if (status == BackendStatus.Running) anyRunning = true;
            if (status == BackendStatus.Error) anyError = true;
        }

        var color = anyError ? Colors.Red : anyRunning ? Colors.Green : Colors.Gray;
        if (_trayIcon != null)
            _trayIcon.Icon = CreateIcon(color);
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static Icon CreateIcon(System.Windows.Media.Color wpfColor)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        var color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
        g.Clear(System.Drawing.Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 12, 12);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _statusTimer?.Stop();
        _trayIcon?.Dispose();
    }
}

internal static class Colors
{
    public static System.Windows.Media.Color Gray => System.Windows.Media.Color.FromRgb(0x72, 0x76, 0x7D);
    public static System.Windows.Media.Color Green => System.Windows.Media.Color.FromRgb(0x43, 0xA2, 0x5A);
    public static System.Windows.Media.Color Red => System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52);
}
