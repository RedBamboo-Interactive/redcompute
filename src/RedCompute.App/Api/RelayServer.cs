using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Api;

public class RelayServer
{
    private WebApplication? _app;
    private readonly RedComputeConfig _config;
    private readonly CapabilityRegistry _registry;
    private readonly JobTrackingService _jobTracker;
    private readonly Action<string, Guid?> _log;

    public RelayServer(RedComputeConfig config, CapabilityRegistry registry, JobTrackingService jobTracker, Action<string, Guid?> log)
    {
        _config = config;
        _registry = registry;
        _jobTracker = jobTracker;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{_config.ApiPort}");
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        _app = builder.Build();

        GlobalEndpoints.Initialize();
        GlobalEndpoints.Map(_app, _registry, _jobTracker);
        DiscoverEndpoints.Map(_app, _config, _registry);
        OpenApiEndpoints.Map(_app, _config, _registry);
        ImageGenEndpoints.Map(_app, _registry, _jobTracker, _log);
        MusicGenEndpoints.Map(_app, _registry, _jobTracker, _log);
        CapabilityEndpoints.Map(_app, _registry, _jobTracker, _log);

        _log($"[Relay] Starting on port {_config.ApiPort}", null);
        await _app.StartAsync(ct);
        _log($"[Relay] Listening at http://localhost:{_config.ApiPort}", null);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            _log("[Relay] Shutting down", null);
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
