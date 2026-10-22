using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

internal static class SessionExecutionToken
{
    internal static readonly TimeSpan StatelessCompletionGrace = TimeSpan.FromMinutes(5);
    internal const string SessionContextKind = "session";

    public static IDisposable Push(HttpContext context, string providerId, string providerName)
    {
        // A new session's id is allocated inside StartSessionAsync, after this scope opens. Issue the
        // process token only when the provider names the session whose process it is launching.
        var parent = ExecutionContextScope.Current;
        if (parent is null) return EmptyScope.Instance;
        // Resolve request state now; work started inside this scope inherits it beyond the request.
        var user = context.User;
        var issuer = context.RequestServices.GetRequiredService<IExecutionTokenIssuer>();
        var lifetime = context.RequestServices.GetRequiredService<JwtOptions>().SessionExecutionTokenLifetime;
        return SessionScratch.PushExecutionTokenIssuer(sessionId =>
            issuer.Issue(Child(parent, providerId, providerName, sessionId), user, lifetime).AccessToken);
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

    public static Dictionary<string, string> CreateStatelessEnvironmentWithoutSuiteIdentity(
        Dictionary<string, string>? environment)
    {
        var childEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                if (!string.Equals(key, "REDLEAF_EXECUTION_TOKEN", StringComparison.OrdinalIgnoreCase))
                    childEnvironment.Add(key, value);
            }
        }

        // Explicitly shadow any ambient value inherited by the provider process.
        childEnvironment["REDLEAF_EXECUTION_TOKEN"] = "";
        return childEnvironment;
    }

    internal static string? IssueAuthenticatedDirect(HttpContext context, string sessionId, string messageUid, string providerId, string providerName)
    {
        var subject = context.User.FindFirst("sub")?.Value;
        if (context.User.Identity?.IsAuthenticated != true || context.User.Identity.AuthenticationType == "LocalDefault"
            || string.IsNullOrWhiteSpace(subject) || subject is "local-user" or "system" || subject.StartsWith("service:")) return null;
        const string client = "Direct RedCompute client";
        var child = new ExecutionIdentity(ExecutionIdentity.CurrentSchemaVersion, Guid.NewGuid().ToString(),
            new ExecutionAppIdentity("direct-redcompute-api", client), new ExecutionActorIdentity("app", "direct-redcompute-api", client),
            new ExecutionBeneficiaryIdentity("user", subject, context.User.FindFirst("name")?.Value),
            [new ExecutionContextReference("ai-session", sessionId), new ExecutionContextReference(SessionContextKind, sessionId),
                new ExecutionContextReference("accepted-message", messageUid),
                new ExecutionContextReference("ai-session-provider", providerId, Name: providerName)],
            Trace: new ExecutionTrace(context.TraceIdentifier));
        return context.RequestServices.GetRequiredService<IExecutionTokenIssuer>().Issue(child, context.User,
            context.RequestServices.GetRequiredService<JwtOptions>().SessionExecutionTokenLifetime).AccessToken;
    }

    internal static string? Issue(
        HttpContext context,
        string providerId,
        string providerName,
        TimeSpan? lifetime,
        string? sessionId = null)
    {
        var parent = ExecutionContextScope.Current;
        if (parent is null) return null;

        var child = Child(parent, providerId, providerName, sessionId);
        var issuer = context.RequestServices.GetRequiredService<IExecutionTokenIssuer>();
        var options = context.RequestServices.GetRequiredService<JwtOptions>();
        var issued = issuer.Issue(child, context.User, lifetime ?? options.SessionExecutionTokenLifetime);
        return issued.AccessToken;
    }

    private static ExecutionIdentity Child(
        ExecutionIdentity parent,
        string providerId,
        string providerName,
        string? sessionId)
        => parent with
        {
            ExecutionId = Guid.NewGuid().ToString(),
            ParentExecutionId = parent.ExecutionId,
            Context = ChildContext(parent.Context, providerId, providerName, sessionId),
        };

    /// <summary>
    /// A session process names exactly one session: its own. Consumers such as leaf-discord bind
    /// authority to that entry, so a caller's session reference (the delegating parent, or the
    /// same id supplied again on resume) is replaced rather than repeated.
    /// </summary>
    internal static IReadOnlyList<ExecutionContextReference> ChildContext(
        IReadOnlyList<ExecutionContextReference> parent,
        string providerId,
        string providerName,
        string? sessionId)
    {
        var provider = new ExecutionContextReference("ai-session-provider", providerId, Name: providerName);
        if (string.IsNullOrWhiteSpace(sessionId)) return [.. parent.Take(15), provider];
        return
        [
            .. parent.Where(item => !string.Equals(item.Kind, SessionContextKind, StringComparison.OrdinalIgnoreCase)).Take(14),
            new ExecutionContextReference(SessionContextKind, sessionId),
            provider,
        ];
    }

    private sealed class EmptyScope : IDisposable
    {
        public static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
