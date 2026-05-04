using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using RedCompute.App.Helpers;
using RedCompute.Core.Providers;

namespace RedCompute.App.TrayIcon;

public class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private DispatcherTimer? _statusTimer;

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RedCompute",
            Icon = IconHelper.CreateTrayIcon(StatusColors.Gray),
            ContextMenu = BuildContextMenu(),
            MenuActivation = PopupActivationMode.RightClick
        };
        _trayIcon.ForceCreate();

        _trayIcon.TrayMouseDoubleClick += (_, _) => OpenDashboard();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await UpdateIconStatus();
        _statusTimer.Start();
    }

    private static void OpenDashboard()
    {
        var port = App.ConfigManager.Config.ApiPort;
        Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true });
    }

    private System.Windows.Controls.ContextMenu BuildContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Show RedCompute" };
        showItem.Click += (_, _) => OpenDashboard();
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

    private Color _lastIconColor;

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

        var color = anyError ? StatusColors.Red : anyRunning ? StatusColors.Green : StatusColors.Gray;
        if (_trayIcon == null || color == _lastIconColor) return;

        try
        {
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = IconHelper.CreateTrayIcon(color);
            _lastIconColor = color;
            oldIcon?.Dispose();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
    }

    public void Dispose()
    {
        _statusTimer?.Stop();
        _trayIcon?.Dispose();
    }
}

internal static class StatusColors
{
    public static Color Gray => Color.FromArgb(0x72, 0x76, 0x7D);
    public static Color Green => Color.FromArgb(0x43, 0xA2, 0x5A);
    public static Color Red => Color.FromArgb(0xFF, 0x52, 0x52);
}
