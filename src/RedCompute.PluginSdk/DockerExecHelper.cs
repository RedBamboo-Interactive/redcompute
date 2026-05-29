using System.Diagnostics;

namespace RedCompute.PluginSdk;

public static class DockerExecHelper
{
    public static void ConfigureForDockerExec(ProcessStartInfo startInfo, string container,
        string cliBinary, string? workingDir, Dictionary<string, string>? env)
    {
        startInfo.FileName = "docker";
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("-i");

        if (!string.IsNullOrWhiteSpace(workingDir))
        {
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(workingDir);
        }

        if (env != null)
            foreach (var (k, v) in env)
            {
                startInfo.ArgumentList.Add("-e");
                startInfo.ArgumentList.Add($"{k}={v}");
            }

        startInfo.ArgumentList.Add(container);
        startInfo.ArgumentList.Add(cliBinary);
    }
}
