using System.Globalization;
using System.Text;

namespace RedCompute.PluginSdk.Hosting;

/// <summary>
/// What a ComfyUI launch is built from (Phase 4, design 2.4). Node zero fills it from its
/// <c>config.json</c> provider entry; the node agent will fill it from the runtime its owner
/// registered. Values are as configured: the recipe trims and converts them itself.
/// </summary>
public sealed record ComfyUiRecipe
{
    /// <summary><c>--listen</c>: the address ComfyUI binds (config <c>Host</c>).</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary><c>--port</c> (config <c>BackendPort</c>).</summary>
    public int Port { get; init; } = 8188;

    /// <summary>The WSL distribution to run in, or null to run natively.</summary>
    public string? WslDistro { get; init; }

    /// <summary>The ComfyUI folder (holds <c>main.py</c>).</summary>
    public string? ServerPath { get; init; }

    public string? VenvPath { get; init; }

    /// <summary>Extra arguments appended verbatim, trimmed (config <c>ServerArgs</c>).</summary>
    public string ServerArgs { get; init; } = "";

    public string ModelsDirectory { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string InputDirectory { get; init; } = "";
    public string TempDirectory { get; init; } = "";
    public string UserDirectory { get; init; } = "";

    /// <summary><c>--database-url</c>; when empty, derived from <see cref="UserDirectory"/>.</summary>
    public string DatabaseUrl { get; init; } = "";
}

/// <summary>What a LocalWsl backend launch (the speech servers) is built from (design 2.4).</summary>
public sealed record LocalWslRecipe
{
    /// <summary>The WSL distribution to run in, or null for the native <c>cmd.exe</c> launch.</summary>
    public string? WslDistro { get; init; }

    /// <summary>The server folder (holds <c>server.py</c>).</summary>
    public string? ServerPath { get; init; }

    public string? VenvPath { get; init; }
    public string? Model { get; init; }
    public string? ModelRevision { get; init; }
    public int? BackendPort { get; init; }
}

/// <summary>
/// The shared launch recipes (Phase 4, design 2.4): the one place a ComfyUI or LocalWsl command line
/// is built. Node zero's providers launch what these return; the node agent will build its managed
/// runtimes' launches from the same code, so the server never supplies a program or an argument.
/// The specs are byte-identical to what the providers built before the move (RuntimeRecipePinTests,
/// and the Phase 1 launch goldens).
/// </summary>
public static class RuntimeRecipes
{
    /// <summary>The line the LocalWsl lifecycle script prints with the backend's process group and start time.</summary>
    public const string LocalWslProcessMarker = "REDCOMPUTE_LOCALWSL_PROCESS=";

    // ---- ComfyUI ---------------------------------------------------------------------------------

    /// <summary>The ComfyUI server launch: uncontained, as ComfyUI always ran (node-zero design, section 6).</summary>
    public static HostSpawnSpec ComfyUi(ComfyUiRecipe recipe, IExecutionHost host)
    {
        var arguments = ComfyUiArguments(recipe, recipe.WslDistro != null);
        var serverArgs = recipe.ServerArgs;

        if (recipe.WslDistro != null)
        {
            var serverPath = WslPath(recipe.ServerPath ?? ".");
            var command = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(recipe.VenvPath))
            {
                var activatePath = CombinePortablePath(WslPath(recipe.VenvPath), "bin/activate");
                command.Append("source ").Append(QuoteBashArgument(activatePath)).Append(" && ");
            }
            command.Append("cd ").Append(QuoteBashArgument(serverPath)).Append(" && python ");
            command.AppendJoin(' ', arguments.Select(QuoteBashArgument));
            if (!string.IsNullOrWhiteSpace(serverArgs))
                command.Append(' ').Append(serverArgs.Trim());

            return new HostSpawnSpec
            {
                FileName = "wsl.exe",
                ArgumentList = ["-d", recipe.WslDistro, "bash", "-lc", command.ToString()],
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                Containment = ContainmentMode.None,
                OwnerLabel = "comfyui",
            };
        }

        var pythonExe = ComfyUiNativePython(recipe.VenvPath, recipe.ServerPath, host);

        return new HostSpawnSpec
        {
            FileName = pythonExe,
            RawArguments = string.Join(' ', arguments.Select(QuoteWindowsArgument))
                + (string.IsNullOrWhiteSpace(serverArgs) ? "" : $" {serverArgs.Trim()}"),
            WorkingDirectory = recipe.ServerPath is { } workingDirectory ? host.Path(workingDirectory) : null,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "comfyui",
        };
    }

    /// <summary>
    /// The Python a native ComfyUI runs on: the venv's (<c>Scripts\python.exe</c> on Windows,
    /// <c>bin/python</c> on Linux and macOS, as <c>python -m venv</c> lays them out), else a portable
    /// install's <c>python_embeded</c> beside the server folder, else <c>python</c> on PATH.
    /// </summary>
    public static string ComfyUiNativePython(string? venvPath, string? serverPath, IExecutionHost host)
    {
        if (!string.IsNullOrWhiteSpace(venvPath))
            return host.Platform.IsWindows
                ? host.PathRules.Combine(venvPath, "Scripts", "python.exe")
                : host.PathRules.Combine(venvPath, "bin", "python");

        if (!string.IsNullOrWhiteSpace(serverPath))
        {
            var portablePython = host.PathRules.GetFullPath(host.PathRules.Combine(
                serverPath, "..", "python_embeded", "python.exe"));
            if (host.Files.FileExists(host.Path(portablePython))) return portablePython;
        }

        return "python";
    }

    private static List<string> ComfyUiArguments(ComfyUiRecipe recipe, bool forWsl)
    {
        var arguments = new List<string>
        {
            "main.py", "--listen", recipe.Host, "--port", recipe.Port.ToString(CultureInfo.InvariantCulture)
        };

        AddPathArgument(arguments, recipe.ModelsDirectory, "--models-directory", forWsl);
        AddPathArgument(arguments, recipe.OutputDirectory, "--output-directory", forWsl);
        AddPathArgument(arguments, recipe.InputDirectory, "--input-directory", forWsl);
        AddPathArgument(arguments, recipe.TempDirectory, "--temp-directory", forWsl);
        AddPathArgument(arguments, recipe.UserDirectory, "--user-directory", forWsl);

        var databaseUrl = recipe.DatabaseUrl.Trim();
        if (databaseUrl.Length == 0)
        {
            var userDirectory = recipe.UserDirectory.Trim();
            if (userDirectory.Length > 0)
            {
                var databasePath = CombinePortablePath(userDirectory, "comfyui.db");
                if (forWsl) databasePath = WslPath(databasePath);
                databaseUrl = "sqlite:///" + databasePath.Replace('\\', '/');
            }
        }
        else if (forWsl)
        {
            databaseUrl = ConvertDatabaseUrlToWsl(databaseUrl);
        }

        if (databaseUrl.Length > 0)
        {
            arguments.Add("--database-url");
            arguments.Add(databaseUrl);
        }

        return arguments;
    }

    private static void AddPathArgument(List<string> arguments, string configured, string option, bool forWsl)
    {
        var value = configured.Trim();
        if (value.Length == 0) return;
        arguments.Add(option);
        arguments.Add(forWsl ? WslPath(value) : value);
    }

    private static string CombinePortablePath(string directory, string child)
    {
        var separator = directory.Contains('\\') || (directory.Length >= 2 && directory[1] == ':')
            ? '\\'
            : '/';
        return directory.TrimEnd('\\', '/') + separator + child;
    }

    private static string ConvertDatabaseUrlToWsl(string databaseUrl)
    {
        const string sqlitePrefix = "sqlite:///";
        if (!databaseUrl.StartsWith(sqlitePrefix, StringComparison.OrdinalIgnoreCase))
            return databaseUrl;

        var path = databaseUrl[sqlitePrefix.Length..];
        return path.Length >= 2 && path[1] == ':'
            ? sqlitePrefix + WslPath(path)
            : databaseUrl;
    }

    private static string QuoteBashArgument(string value)
    {
        if (value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':'))
            return value;
        if (value == "~") return "\"$HOME\"";
        if (value.StartsWith("~/", StringComparison.Ordinal))
            return "\"$HOME\"/" + QuoteBashLiteral(value[2..]);
        if (value.StartsWith("sqlite:///~/", StringComparison.OrdinalIgnoreCase))
            return QuoteBashLiteral("sqlite:///") + "\"$HOME\"/" + QuoteBashLiteral(value[12..]);
        return QuoteBashLiteral(value);
    }

    private static string QuoteBashLiteral(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"')) return value;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    // ---- LocalWsl (the speech servers) -----------------------------------------------------------

    /// <summary>The backend launch: uncontained, as local backends always ran (node-zero design, section 6).</summary>
    public static HostSpawnSpec LocalWsl(LocalWslRecipe recipe)
    {
        var extraArgs = LocalWslBackendArguments(recipe);

        if (recipe.WslDistro != null)
        {
            var command = LocalWslBackendCommand(recipe);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            var dollar = ((char)36).ToString();
            var lifecycleScript =
                "set -euo pipefail; " +
                $"command={dollar}(printf '%s' '{encoded}' | base64 -d); " +
                $"setsid bash -lc \"{dollar}command\" & child={dollar}!; " +
                $"start={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}child/stat\"); " +
                $"printf '{LocalWslProcessMarker}%s:%s\\n' \"{dollar}child\" \"{dollar}start\"; " +
                "cleanup() { " +
                $"current={dollar}(awk '{{print {dollar}22}}' \"/proc/{dollar}child/stat\" 2>/dev/null || true); " +
                $"[ \"{dollar}current\" = \"{dollar}start\" ] && kill -TERM -- \"-{dollar}child\" 2>/dev/null || true; }}; " +
                $"trap cleanup TERM INT EXIT; wait \"{dollar}child\"";
            // wsl.exe reparses the command tail through the distribution shell. Passing a
            // lifecycle script containing '$' directly therefore expands its variables before
            // bash -lc receives it. Base64-wrap the whole script so that the reparse-safe outer
            // command contains no shell variables; the inner bash owns every expansion.
            var lifecycleEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(lifecycleScript));
            var wrapper = $"printf '%s' '{lifecycleEncoded}' | base64 -d | bash";

            return new HostSpawnSpec
            {
                FileName = "wsl.exe",
                ArgumentList = ["-d", recipe.WslDistro, "--", "bash", "-lc", wrapper],
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutput = StdioMode.Pipe,
                StandardError = StdioMode.Pipe,
                Containment = ContainmentMode.None,
                OwnerLabel = "localwsl",
            };
        }

        return new HostSpawnSpec
        {
            FileName = "cmd.exe",
            RawArguments = $"/c cd /d \"{recipe.ServerPath}\" && python3 server.py{extraArgs}",
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutput = StdioMode.Pipe,
            StandardError = StdioMode.Pipe,
            Containment = ContainmentMode.None,
            OwnerLabel = "localwsl",
        };
    }

    /// <summary>The command the WSL lifecycle script runs: the venv, the server folder, <c>server.py</c>.</summary>
    public static string LocalWslBackendCommand(LocalWslRecipe recipe)
    {
        var venvActivate = recipe.VenvPath != null
            ? $"if [ -f {recipe.VenvPath}/bin/activate ]; then source {recipe.VenvPath}/bin/activate; fi; "
            : "";
        var serverPath = WslPath(recipe.ServerPath ?? ".");
        return $"{venvActivate}cd '{ShellLiteral(serverPath)}' && exec python3 server.py{LocalWslBackendArguments(recipe)}";
    }

    private static string LocalWslBackendArguments(LocalWslRecipe recipe)
    {
        var extraArgs = "";
        if (!string.IsNullOrEmpty(recipe.Model))
            extraArgs += $" --model {recipe.Model}";
        if (!string.IsNullOrEmpty(recipe.ModelRevision))
            extraArgs += $" --revision {recipe.ModelRevision}";
        if (recipe.BackendPort > 0)
            extraArgs += $" --port {recipe.BackendPort}";
        return extraArgs;
    }

    /// <summary>A value for inside single quotes in a bash command (each quote closed, escaped, reopened).</summary>
    public static string ShellLiteral(string value)
        => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    // ---- shared ----------------------------------------------------------------------------------

    /// <summary>A Windows path as WSL sees it (<c>C:\x</c> → <c>/mnt/c/x</c>); any other path with forward slashes.</summary>
    public static string WslPath(string windowsPath)
    {
        if (string.IsNullOrEmpty(windowsPath)) return windowsPath;
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var drive = char.ToLower(windowsPath[0]);
            var rest = windowsPath[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }
        return windowsPath.Replace('\\', '/');
    }
}
