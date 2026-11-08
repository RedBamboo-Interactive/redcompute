using Xunit;

namespace RedCompute.App.Tests;

/// <summary>A fact that runs only on Windows; elsewhere it is skipped with the given reason.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {reason}";
    }
}

/// <summary>A theory that runs only on Windows; elsewhere it is skipped with the given reason.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute(string reason)
    {
        if (!OperatingSystem.IsWindows())
            Skip = $"Windows-only: {reason}";
    }
}

/// <summary>A fact about behaviour that exists only off Windows; skipped on Windows.</summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute(string reason)
    {
        if (OperatingSystem.IsWindows())
            Skip = $"Non-Windows only: {reason}";
    }
}
