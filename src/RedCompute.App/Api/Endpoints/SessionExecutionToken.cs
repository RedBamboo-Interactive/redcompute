using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

internal static class SessionExecutionToken
{
    internal static readonly TimeSpan StatelessCompletionGrace = TimeSpan.FromMinutes(5);

    public static IDisposable Push(HttpContext context, string providerId, string providerName)
    {
        var token = Issue(context, providerId, providerName, lifetime: null);
        return token is null ? EmptyScope.Instance : SessionScratch.PushExecutionToken(token);
    }

    public static Dictionary<string, string>? CreateStatelessEnvironment(
        HttpContext context,
        string providerId,
        string providerName,
        int timeoutSeconds,
        Dictionary<string, string>? environment)
    {
        var lifetime = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)) + StatelessCompletionGrace;
        var token = Issue(context, providerId, providerName, lifetime);
        if (token is null) return environment;

        // Preserve the caller's environment exactly, including case-sensitive keys used by Linux
        // containers, but never allow it to replace the signed child identity.
        var childEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (!string.Equals(key, "REDLEAF_EXECUTION_TOKEN", StringComparison.OrdinalIgnoreCase))
                    childEnvironment.Add(key, value);
            }
        }

        childEnvironment["REDLEAF_EXECUTION_TOKEN"] = token;
        return childEnvironment;
    }

    private static string? Issue(
        HttpContext context,
        string providerId,
        string providerName,
        TimeSpan? lifetime)
    {
        var parent = ExecutionContextScope.Current;
        if (parent is null) return null;

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
        var options = context.RequestServices.GetRequiredService<JwtOptions>();
        var issued = issuer.Issue(child, context.User, lifetime ?? options.SessionExecutionTokenLifetime);
        return issued.AccessToken;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
