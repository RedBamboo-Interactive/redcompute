namespace RedCompute.PluginSdk;

/// <summary>The docker exec argv a containerised CLI launch hands its execution host.</summary>
public static class DockerExecHelper
{
    /// <summary>The program a docker exec launch runs.</summary>
    public const string FileName = "docker";

    /// <summary>
    /// The argv of <c>docker exec -i [-w dir] [-e K=V]... container cli</c>; the CLI's own arguments
    /// follow. Pure: it builds the list and touches nothing.
    /// </summary>
    public static List<string> ExecArguments(string container, string cliBinary, string? workingDir,
        Dictionary<string, string>? env)
    {
        var arguments = new List<string> { "exec", "-i" };

        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            arguments.Add("-w");
            arguments.Add(workingDir);
        }

        if (env != null)
            foreach (var (k, v) in env)
            {
                arguments.Add("-e");
                arguments.Add($"{k}={v}");
            }

        arguments.Add(container);
        arguments.Add(cliBinary);
        return arguments;
    }
}
