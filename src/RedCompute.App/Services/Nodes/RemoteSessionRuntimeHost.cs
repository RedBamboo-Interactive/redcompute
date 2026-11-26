using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RedCompute.NodeLink;

namespace RedCompute.App.Services.Nodes;

/// <summary>The owner-run loopback listener reached only by the session runtime forward.</summary>
internal sealed class RemoteSessionRuntimeHost(WebApplication app) : IAsyncDisposable
{
    internal Uri Address => new(app.Urls.Single());

    internal static async Task<RemoteSessionRuntimeHost> StartAsync(RemoteSessionLifecycleRuntime runtime,
        Func<bool> isReady, int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = SessionRuntimeProtocol.MaxExchangeBodyBytes;
            options.AddServerHeader = false;
        });

        var app = builder.Build();
        app.MapGet(SessionRuntimeProtocol.HealthPath, () => isReady() ? Results.Ok() : Results.StatusCode(503));
        foreach (var operation in Enum.GetValues<SessionRuntimeOperation>())
        {
            app.MapPost(SessionRuntimeProtocol.PathFor(operation),
                (HttpContext context) => HandleAsync(context, runtime, operation));
        }

        try
        {
            await app.StartAsync(ct);
            return new(app);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static async Task HandleAsync(HttpContext context, RemoteSessionLifecycleRuntime runtime,
        SessionRuntimeOperation operation)
    {
        var request = context.Request;
        if (!request.HasJsonContentType() || request.Headers.ContentEncoding.Count != 0)
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        if (request.ContentLength > SessionRuntimeProtocol.MaxExchangeBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        try
        {
            var grant = await JsonSerializer.DeserializeAsync<SessionRuntimeGrant>(request.Body,
                cancellationToken: context.RequestAborted);
            if (grant is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var result = await runtime.ApplyAsync(operation, grant, context.RequestAborted);
            context.Response.StatusCode = result.Status;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(result.Body, context.RequestAborted);
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (BadHttpRequestException error) when (!context.Response.HasStarted)
        {
            context.Response.StatusCode = error.StatusCode;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await app.StopAsync(); }
        finally { await app.DisposeAsync(); }
    }
}
