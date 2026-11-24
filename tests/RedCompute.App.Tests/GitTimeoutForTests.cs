using System.Runtime.CompilerServices;
using RedCompute.App.Services;

namespace RedCompute.App.Tests;

/// <summary>
/// Placement resolution and repository validation run real git under HostGit's 10 second limit. A test host
/// that shares the machine with a full suite and other workstreams (a Nightly) can stall a git start for
/// longer than that, and the resolver then correctly answers 503 placement_resolution_unavailable, which is
/// not what those tests check. Production keeps its limit; the tests allow for a loaded machine. The one
/// test of the timeout itself passes its own limit.
/// </summary>
internal static class GitTimeoutForTests
{
    [ModuleInitializer]
    internal static void Allow() => HostGit.Timeout = TimeSpan.FromMinutes(2);
}
