using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;
using RedCompute.Core.Discovery;

namespace RedCompute.App.ViewModels;

public partial class QueueJobDialogViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string CapabilitySlug { get; }
    public string CapabilityDisplayName { get; }

    public ObservableCollection<EndpointManifest> PostEndpoints { get; } = new();

    [ObservableProperty]
    private EndpointManifest? _selectedEndpoint;

    public ObservableCollection<ParameterFieldViewModel> Fields { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _resultJobId;

    [ObservableProperty]
    private bool _isSuccess;

    public bool HasMultipleEndpoints => PostEndpoints.Count > 1;

    public QueueJobDialogViewModel(string slug, string displayName)
    {
        CapabilitySlug = slug;
        CapabilityDisplayName = displayName;
    }

    partial void OnSelectedEndpointChanged(EndpointManifest? value)
    {
        Fields.Clear();
        ErrorMessage = null;

        if (value?.Parameters == null) return;

        var sorted = value.Parameters
            .OrderByDescending(p => p.Value.Required)
            .ThenBy(p => p.Key);

        foreach (var (name, schema) in sorted)
            Fields.Add(new ParameterFieldViewModel(name, schema));
    }

    public async Task LoadManifestAsync()
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var port = App.ConfigManager.Config.ApiPort;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync($"http://localhost:{port}/discover");
            var manifest = JsonSerializer.Deserialize<ServiceManifest>(json, JsonOpts);

            var cap = manifest?.Capabilities.FirstOrDefault(
                c => c.Slug.Equals(CapabilitySlug, StringComparison.OrdinalIgnoreCase));

            if (cap == null)
            {
                ErrorMessage = "Capability not found in manifest";
                return;
            }

            var posts = cap.Endpoints.Where(e =>
                e.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)).ToList();

            PostEndpoints.Clear();
            foreach (var ep in posts)
                PostEndpoints.Add(ep);

            OnPropertyChanged(nameof(HasMultipleEndpoints));

            if (PostEndpoints.Count == 0)
            {
                ErrorMessage = "No job endpoints for this capability";
                return;
            }

            SelectedEndpoint = PostEndpoints[0];
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not load API: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task Submit()
    {
        ErrorMessage = null;

        var allValid = true;
        foreach (var f in Fields)
        {
            if (!f.Validate())
                allValid = false;
        }

        if (!allValid) return;
        if (SelectedEndpoint == null) return;

        var body = new Dictionary<string, object>();
        foreach (var f in Fields)
        {
            var val = f.GetTypedValue();
            if (val != null)
                body[f.Name] = val;
        }

        IsSubmitting = true;

        try
        {
            var port = App.ConfigManager.Config.ApiPort;
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
            http.DefaultRequestHeaders.Add("X-Caller-Info", "redcompute-ui");
            http.DefaultRequestHeaders.Add("X-Async", "true");

            var jsonBody = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var url = $"http://localhost:{port}{SelectedEndpoint.Path}?async=true";
            var resp = await http.PostAsync(url, content);

            if ((int)resp.StatusCode is 200 or 202)
            {
                var respJson = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(respJson);
                if (doc.RootElement.TryGetProperty("jobId", out var jid))
                    ResultJobId = jid.GetString();
                else
                    ResultJobId = "(submitted)";

                IsSuccess = true;
            }
            else
            {
                var errBody = await resp.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(errBody);
                    if (doc.RootElement.TryGetProperty("message", out var msg))
                        ErrorMessage = msg.GetString();
                    else
                        ErrorMessage = $"HTTP {(int)resp.StatusCode}";
                }
                catch
                {
                    ErrorMessage = $"HTTP {(int)resp.StatusCode}: {errBody}";
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Request failed: {ex.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogHost.Close("RootDialog");
    }

    [RelayCommand]
    private void GoToJobs()
    {
        App.MainViewModel.SelectedTabIndex = 1;
        DialogHost.Close("RootDialog");
    }
}
