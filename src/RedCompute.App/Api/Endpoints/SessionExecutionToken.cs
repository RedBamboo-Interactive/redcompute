using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

internal static class SessionExecutionToken
{
    public static IDisposable Push(HttpContext context, string providerId, string providerName)
    {
        var parent = ExecutionContextScope.Current;
        if (parent is null) return EmptyScope.Instance;

        var child = parent with
        {
            ExecutionId = Guid.NewGuid().ToString(),
            ParentExecutionId = parent.ExecutionId,
            Context =
            [
                .. parent.Context.Take(15),
                new ExecutionContextReference(
                    "ai-session-provider",
                    providerId,
                    Name: providerName),
            ],
        };
        var issuer = context.RequestServices.GetRequiredService<IExecutionTokenIssuer>();
        var issued = issuer.Issue(child, context.User, TimeSpan.FromHours(2));
        return SessionScratch.PushExecutionToken(issued.AccessToken);
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
