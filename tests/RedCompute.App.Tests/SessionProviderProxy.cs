using System.Reflection;
using System.Runtime.CompilerServices;
using RedCompute.PluginSdk;

namespace RedCompute.App.Tests;

/// <summary>
/// DispatchProxy fakes intercept default interface members too, so a fake written before
/// <see cref="ISessionProvider.GetSessionInfo"/> existed would never reach its default. This gives
/// every fake exactly that default: the Info of its own <see cref="ISessionProvider.GetSession"/>.
/// </summary>
internal static class SessionProviderProxy
{
    private static readonly MethodInfo GetSession = typeof(ISessionProvider).GetMethod(nameof(ISessionProvider.GetSession))!;

    public static object? Route(MethodInfo? method, object?[]? args, Func<MethodInfo?, object?[]?, object?> invoke)
    {
        if (method?.Name == nameof(ISessionProvider.GetSessionInfo) && method.DeclaringType == typeof(ISessionProvider))
            return invoke(GetSession, args) is ITuple session ? session[0] : null;
        return invoke(method, args);
    }
}
