using Xunit;

namespace RedCompute.NodeLink.Tests;

/// <summary>
/// Tests that measure latency (control round trips under bulk forwards) run alone, after the parallel ones: on a
/// machine busy with the fault families, a scheduler stall would read as a priority failure.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
