using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RedCompute.Core.Providers;

namespace RedCompute.App.ViewModels;

public partial class CapabilityCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _slug = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private BackendStatus _status;

    [ObservableProperty]
    private string _providerName = "";

    public string StatusColor => Status switch
    {
        BackendStatus.Running => "#43A25A",
        BackendStatus.Starting => "#FFB74D",
        BackendStatus.Error => "#FF5252",
        BackendStatus.Draining => "#26A69A",
        _ => "#72767D"
    };

    [RelayCommand]
    private async Task Start()
    {
        var entry = App.Registry.Get(Slug);
        if (entry?.ActiveProvider == null) return;
        Status = BackendStatus.Starting;
        var success = await entry.ActiveProvider.StartAsync();
        Status = success ? BackendStatus.Running : BackendStatus.Error;
    }

    [RelayCommand]
    private async Task Stop()
    {
        var entry = App.Registry.Get(Slug);
        if (entry?.ActiveProvider == null) return;
        await entry.ActiveProvider.StopAsync();
        Status = BackendStatus.Stopped;
    }
}
