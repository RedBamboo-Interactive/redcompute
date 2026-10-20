using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static class CliVersionEndpoints
{
    public static void Map(EndpointRegistry endpoints, CapabilityRegistry registry)
    {
        endpoints.MapGet("/ai-session/providers/{providerId}/cli",
            "Installed CLI version, install eligibility, blocking session count, and current install operation for an opted-in provider.",
            async (string providerId, CancellationToken ct) =>
            {
                var provider = FindProvider(registry, providerId);
                if (provider is null) return ProviderNotFound(providerId);
                return Results.Ok(await provider.CliVersionManager.GetStatusAsync(ct));
            });

        endpoints.MapGet("/ai-session/providers/{providerId}/cli/versions",
            "Published npm versions for an opted-in CLI provider, ordered newest first.",
            async (string providerId, CancellationToken ct) =>
            {
                var provider = FindProvider(registry, providerId);
                if (provider is null) return ProviderNotFound(providerId);
                try
                {
                    var versions = await provider.CliVersionManager.GetAvailableVersionsAsync(ct);
                    return Results.Ok(new
                    {
                        providerId = provider.ProviderId,
                        packageName = provider.CliVersionManager.PackageName,
                        versions,
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Results.Empty;
                }
                catch (Exception ex)
                {
                    return Results.Json(new
                    {
                        error = "npm_registry_failed",
                        message = ex.Message,
                    }, statusCode: StatusCodes.Status502BadGateway);
                }
            });

        endpoints.MapPost("/ai-session/providers/{providerId}/cli/install",
            "Start an asynchronous npm install for latest or a specific published CLI version. Refuses while provider sessions are Starting or Active.",
            async (HttpContext ctx, string providerId) =>
            {
                var provider = FindProvider(registry, providerId);
                if (provider is null) return ProviderNotFound(providerId);

                InstallRequest? body;
                try { body = await ctx.Request.ReadFromJsonAsync<InstallRequest>(ctx.RequestAborted); }
                catch
                {
                    return Results.BadRequest(new { error = "invalid_body", message = "Expected a JSON body" });
                }
                if (string.IsNullOrWhiteSpace(body?.Version))
                    return Results.BadRequest(new { error = "invalid_version", message = "version is required" });

                try
                {
                    var operation = provider.CliVersionManager.StartInstall(body.Version);
                    return Results.Json(new
                    {
                        providerId = provider.ProviderId,
                        operation,
                        poll = $"/ai-session/providers/{provider.ProviderId}/cli/install/{operation.Id}",
                    }, statusCode: StatusCodes.Status202Accepted);
                }
                catch (CliInstallRejectedException ex)
                {
                    return Results.Conflict(new
                    {
                        error = ex.Code,
                        message = ex.Message,
                        activeSessionCount = ex.ActiveSessionCount,
                    });
                }
            })
            .WithParam("version", "string", required: true,
                description: "The npm version to install, or 'latest'", location: ParamLocation.Body);

        endpoints.MapGet("/ai-session/providers/{providerId}/cli/install/{operationId:guid}",
            "Poll one CLI install operation. Terminal statuses are completed, failed, or refused.",
            (string providerId, Guid operationId) =>
            {
                var provider = FindProvider(registry, providerId);
                if (provider is null) return ProviderNotFound(providerId);
                var operation = provider.CliVersionManager.GetOperation(operationId);
                return operation is null
                    ? Results.NotFound(new { error = "operation_not_found", message = "Install operation not found" })
                    : Results.Ok(new { providerId = provider.ProviderId, operation });
            });
    }

    private static ICliVersionManagedProvider? FindProvider(CapabilityRegistry registry, string providerId)
        => registry.FindProviders<ICliVersionManagedProvider>()
            .Distinct<ICliVersionManagedProvider>(ReferenceEqualityComparer.Instance)
            .FirstOrDefault(provider => provider.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    private static IResult ProviderNotFound(string providerId)
        => Results.NotFound(new
        {
            error = "provider_not_found",
            message = $"CLI version management is not available for provider '{providerId}'",
        });

    private sealed class InstallRequest
    {
        public string Version { get; set; } = "";
    }
}
