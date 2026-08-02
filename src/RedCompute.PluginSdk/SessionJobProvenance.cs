using RedCompute.Core.Jobs;

namespace RedCompute.PluginSdk;

public static class SessionJobProvenance
{
    public static JobProvenance Create(string? source, string? userId, string? userName,
        string? userAvatarUrl, string route, string? sessionId = null, string? sessionName = null)
    {
        var app = string.IsNullOrWhiteSpace(source) ? "Direct RedCompute client" : source;
        var realUser = !string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(userId, "local-user", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(userId, "system", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<JobContextReference> context = sessionId == null
            ? []
            : [new JobContextReference("ai-session", sessionId, NameSnapshot: sessionName)];
        return new JobProvenance(JobProvenance.CurrentSchemaVersion,
            new JobOrigin("redcompute", new JobAppReference("asserted-client", app!, null, app!),
                new JobEntrypoint("http", route)),
            new JobActor("app", app!, Id: app),
            realUser ? new JobBeneficiary("user", userId, userName, userAvatarUrl)
                : new JobBeneficiary("system", Reason: "Legacy session record has no verifiable beneficiary"),
            context, new JobTrace(), realUser ? JobProvenanceAssurance.Asserted : JobProvenanceAssurance.Unknown,
            DateTimeOffset.UtcNow);
    }
}
