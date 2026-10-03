using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;
public sealed class MaintenanceStatusEndpointTests
{
    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 200)]
    public async Task Status_requires_authentication_and_has_no_mutation_authority(bool authenticated, int expected)
    {
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        var registry = new EndpointRegistry(app);
        MaintenanceEndpoints.Map(registry, new MaintenanceDeploymentCoordinator(new CapabilityRegistry(), (_, _) => {}));
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>();
        var status = routes.Single(e => e.RoutePattern.RawText == "/maintenance/status");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        if (authenticated) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "reader")], "signed"));
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        await status.RequestDelegate!(context);
        Assert.Equal(expected, context.Response.StatusCode);
        if (authenticated)
        {
            context.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.Equal("idle", json.RootElement.GetProperty("state").GetString());
            Assert.False(json.RootElement.GetProperty("paused").GetBoolean());
            Assert.Equal(6, json.RootElement.EnumerateObject().Count());
        }
        var arm = routes.Single(e => e.RoutePattern.RawText == "/maintenance/deploy-staged");
        context.Response.Body = new MemoryStream(); context.Request.Method = "POST";
        await arm.RequestDelegate!(context);
        Assert.Equal(403, context.Response.StatusCode); // A status reader cannot pause delivery.
        Assert.Contains(registry.GetEndpoints(), e => e.Path == "/maintenance/status" && e.Method == "GET");
    }
}
