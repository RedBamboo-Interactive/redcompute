using System.Runtime.CompilerServices;
using RedCompute.App.Services;

namespace RedCompute.App.Tests;

/// <summary>
/// Placement resolution and repository validation run real git under HostGit's per-attempt limit (15 s, one
/// retry in production). A test host that shares the machine with a full suite and other workstreams (a
/// Nightly) can still stall longer, which those tests do not check. They allow for a loaded machine here; the
/// tests of the timeout and its retry pass their own limit.
/// </summary>
internal static class GitTimeoutForTests
{
    [ModuleInitializer]
    internal static void Allow() => HostGit.Timeout = TimeSpan.FromMinutes(2);
}
