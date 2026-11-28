using RedCompute.Core.Jobs;

namespace RedCompute.Core.Sessions;

/// <summary>In-process delivery scope of an input whose admitted execution has no responder.
/// Providers capture the policy before writing the input, since later protocol messages are read
/// on another task. The queue restores the scope from the input's durable provenance.</summary>
public static class SessionTurnQuestions
{
    public const string NoResponder =
        "This turn cannot wait for an answer. Continue where possible and state any missing input.";

    public const string ContextKind = "session-questions";
    public const string DisabledValue = "disabled";
    public const string InteractiveValue = "interactive";

    public static bool RequiresNonInteractive(JobProvenance provenance)
        => provenance.Assurance == JobProvenanceAssurance.Verified
            && provenance.Context.Any(reference =>
                reference.Kind == ContextKind && reference.Id == DisabledValue);

    private static readonly AsyncLocal<bool> DisabledScope = new();

    public static bool Disabled => DisabledScope.Value;

    public static IDisposable Suppress()
    {
        var previous = DisabledScope.Value;
        DisabledScope.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => DisabledScope.Value = previous;
    }
}
