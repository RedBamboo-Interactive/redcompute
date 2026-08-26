using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;

namespace RedCompute.App.Api.Endpoints;

public static class MaintenanceEndpoints
{
    public static void Map(EndpointRegistry endpoints, MaintenanceDeploymentCoordinator coordinator)
    {
        endpoints.MapPost("/maintenance/deploy-staged",
            "Drain interactive turns and hand one validated staged deployment to the desktop",
            async (HttpContext context) =>
        {
            var identity = ConfidentialResourcePolicy.ExecutionIdentity(context);
            var roles = ExecutionIdentityClaims.ReadRoles(context.User);
            if (identity is null || !roles.Contains("admin", StringComparer.OrdinalIgnoreCase))
                return Results.Json(new { error = "forbidden", message = "Signed admin execution identity required" },
                    statusCode: StatusCodes.Status403Forbidden);

            JsonElement body;
            try { body = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body", message = "Request body must be JSON" }); }
            var requestPath = body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("requestPath", out var path)
                && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
            if (string.IsNullOrWhiteSpace(requestPath))
                return Results.BadRequest(new { error = "invalid_body", message = "requestPath is required" });

            try
            {
                var admission = coordinator.Arm(requestPath);
                return Results.Json(admission,
                    statusCode: admission.Accepted
                        ? StatusCodes.Status202Accepted
                        : StatusCodes.Status409Conflict);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "invalid_deployment_request", message = ex.Message });
            }
        });
    }
}
