using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using RedCompute.Core.Providers;

namespace RedCompute.App.TrayIcon;

public class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "RedCompute",
            Icon = LoadEmbeddedIcon(),
            ContextMenu = BuildContextMenu(),
            MenuActivation = PopupActivationMode.RightClick
        };
        _trayIcon.ForceCreate();

        _trayIcon.TrayMouseDoubleClick += (_, _) => OpenDashboard();
    }

    private static Icon LoadEmbeddedIcon()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("redcompute.ico")!;
        return new Icon(stream);
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

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
}
