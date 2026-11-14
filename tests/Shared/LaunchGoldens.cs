using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.Tests;

/// <summary>
/// Launch goldens for node zero (design S:/RedCompute/node-zero-design.md section 4): what every CLI and
/// backend launch hands the OS (file, argv, cwd, environment, redirects, encodings, containment) is
/// recorded once from the code as it stands and must stay byte-identical while the launch code moves
/// behind an execution host. Goldens live in tests/Goldens/Launch. Set REDCOMPUTE_GOLDEN_RECORD=1 to
/// (re)write them; every other run compares.
/// </summary>
internal static class LaunchGoldens
{
    public const string RecordVariable = "REDCOMPUTE_GOLDEN_RECORD";

    public static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) == "1";

    public static string Directory => Path.Combine(RepoRoot(), "tests", "Goldens", "Launch");

    public static void Verify(string name, JsonNode actual)
    {
        var text = Canonical(actual);
        AssertNoAmbientSecret(text);
        var path = Path.Combine(Directory, name + ".json");
        if (Recording)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }
        Assert.True(File.Exists(path), $"Missing launch golden {name}.json; record with {RecordVariable}=1 on the unchanged code");
        // A checkout with core.autocrlf turns the LF goldens into CRLF: compare content, not line ends.
        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == text) return;
        var dump = Path.Combine(Path.GetTempPath(), "redcompute-launch-goldens-actual", name + ".json");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dump)!);
        File.WriteAllText(dump, text, new UTF8Encoding(false));
        Assert.Fail($"Launch golden {name} changed. Expected {path}, actual written to {dump}.\n{FirstDifference(expected, text)}");
    }

    /// <summary>Sorted keys, two-space indent, LF line ends, trailing newline.</summary>
    public static string Canonical(JsonNode node)
    {
        var sorted = Sort(node);
        var json = sorted?.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) ?? "null";
        return json.Replace("\r\n", "\n") + "\n";
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, Sort(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    /// <summary>The token this test host itself runs with (an agent session) must never reach a golden.</summary>
    private static void AssertNoAmbientSecret(string text)
    {
        var token = Environment.GetEnvironmentVariable("REDLEAF_EXECUTION_TOKEN");
        if (!string.IsNullOrEmpty(token) && token.Length >= 16)
            Assert.False(text.Contains(token, StringComparison.Ordinal), "A launch golden would contain the ambient execution token");
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var left = i < e.Length ? e[i] : "<end>";
            var right = i < a.Length ? a[i] : "<end>";
            if (left != right) return $"line {i + 1}:\n  expected: {left}\n  actual:   {right}";
        }
        return "(no line difference)";
    }

    private static string RepoRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RedCompute.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("RedCompute.sln not found above " + file);
    }

    // ---------------------------------------------------------------------------------------------
    // Layer A: what a launch seam was handed
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The launch as a seam receives it. <paramref name="seam"/> names the seam and
    /// <paramref name="containment"/> its production default (proven by SeamDefaultContainmentTests),
    /// for example "session:claude-code" or "none".
    /// </summary>
    public static JsonObject FromStartInfo(ProcessStartInfo start, string seam, string containment, Normalizer normalizer)
    {
        var touched = EnvironmentTouched(start);
        var capture = new JsonObject
        {
            ["seam"] = seam,
            ["containment"] = containment,
            ["fileName"] = start.FileName,
            ["workingDirectory"] = start.WorkingDirectory,
            ["useShellExecute"] = start.UseShellExecute,
            ["createNoWindow"] = start.CreateNoWindow,
            ["redirect"] = new JsonObject
            {
                ["stdin"] = start.RedirectStandardInput,
                ["stdout"] = start.RedirectStandardOutput,
                ["stderr"] = start.RedirectStandardError,
            },
            ["encoding"] = new JsonObject
            {
                ["stdin"] = Describe(start.StandardInputEncoding),
                ["stdout"] = Describe(start.StandardOutputEncoding),
                ["stderr"] = Describe(start.StandardErrorEncoding),
            },
            ["environmentTouched"] = touched,
        };
        if (start.ArgumentList.Count > 0)
            capture["argumentList"] = new JsonArray(start.ArgumentList.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
        if (!string.IsNullOrEmpty(start.Arguments))
            capture["arguments"] = start.Arguments;
        if (!string.IsNullOrEmpty(start.Verb))
            capture["verb"] = start.Verb;
        if (touched)
            capture["environmentChanges"] = EnvironmentChanges(
                start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value));
        return (JsonObject)normalizer.Apply(capture);
    }

    private static readonly FieldInfo? EnvironmentField =
        typeof(ProcessStartInfo).GetField("_environmentVariables", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Whether the launcher touched <see cref="ProcessStartInfo.Environment"/>. Touching it makes .NET
    /// pass an explicit environment block, so it is part of what a launch is.
    /// </summary>
    public static bool EnvironmentTouched(ProcessStartInfo start)
        => EnvironmentField is null
            ? throw new InvalidOperationException("ProcessStartInfo._environmentVariables not found: update the golden harness")
            : EnvironmentField.GetValue(start) is not null;

    public static JsonNode? Describe(Encoding? encoding)
        => encoding is null ? null : $"{encoding.WebName}/preamble={encoding.GetPreamble().Length}";

    /// <summary>
    /// Keys whose value differs from this test host's own environment (a launch inherits it), with
    /// the new value, and keys removed (null).
    /// </summary>
    public static JsonObject EnvironmentChanges(IReadOnlyDictionary<string, string?> child)
    {
        var host = HostEnvironment();
        var changes = new JsonObject();
        foreach (var (key, value) in child.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (!host.TryGetValue(key, out var hostValue) || hostValue != value)
                changes[key] = value;
        var childKeys = new HashSet<string>(child.Keys, KeyComparer);
        foreach (var key in host.Keys.Where(key => !childKeys.Contains(key)).OrderBy(key => key, StringComparer.Ordinal))
            changes[key] = null;
        return changes;
    }

    private static StringComparer KeyComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static Dictionary<string, string?> HostEnvironment()
    {
        var host = new Dictionary<string, string?>(KeyComparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            host[(string)entry.Key] = (string?)entry.Value;
        return host;
    }
}

/// <summary>Replaces machine- and run-specific values with stable placeholders, longest first.</summary>
internal sealed class Normalizer
{
    private readonly List<(string Value, string Placeholder)> _replacements = [];

    public Normalizer() => AddMachinePaths();

    public Normalizer Add(string? value, string placeholder)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 3) return this;
        var trimmed = value.TrimEnd('\\', '/');
        foreach (var variant in new[] { trimmed, trimmed.Replace("\\", "\\\\"), trimmed.Replace('\\', '/') }.Distinct())
            if (!_replacements.Any(existing => existing.Value == variant))
                _replacements.Add((variant, placeholder));
        return this;
    }

    private void AddMachinePaths()
    {
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<localappdata>");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "<appdata>");
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<userprofile>");
        Add(Path.GetTempPath(), "<temp>");
        Add(Environment.SystemDirectory, "<system>");
        // A launch without a working directory inherits this test host's (the test output folder).
        Add(Environment.CurrentDirectory, "<host-cwd>");
    }

    public string Apply(string text)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (value, placeholder) in _replacements.OrderByDescending(r => r.Value.Length))
            text = text.Replace(value, placeholder, comparison);
        return text;
    }

    public JsonNode? Apply(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(Apply(pair.Key), Apply(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(Apply).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Apply(text)),
        null => null,
        _ => node.DeepClone(),
    };
}

// -------------------------------------------------------------------------------------------------
// Layer B: what the OS actually started, observed by the probe CLI
// -------------------------------------------------------------------------------------------------

internal sealed record ProbeLaunch(int Pid, string Name, string CommandLine, IReadOnlyList<string> Args, string Cwd,
    IReadOnlyDictionary<string, string?> Environment)
{
    public JsonObject Canonical(Normalizer normalizer, string containment) => (JsonObject)normalizer.Apply(new JsonObject
    {
        ["cli"] = Name,
        ["commandLine"] = CommandLine,
        ["args"] = new JsonArray(Args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["cwd"] = Cwd,
        ["environmentChanges"] = LaunchGoldens.EnvironmentChanges(Environment),
        ["containment"] = containment,
    })!;
}

/// <summary>
/// Installs the probe under CLI names in a private directory and observes what it was launched with.
/// It kills nothing it did not see start: only PIDs from its own capture files, still running the
/// probe executable from its own directory.
/// </summary>
internal sealed class ProbeHarness : IDisposable
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProbeLaunch> _launches = [];

    public ProbeHarness(string label)
    {
        Root = Path.Combine(Path.GetTempPath(), "redcompute-launch-goldens", $"{label}-{Guid.NewGuid():N}");
        CliDirectory = Path.Combine(Root, "cli");
        System.IO.Directory.CreateDirectory(CliDirectory);
        System.IO.Directory.CreateDirectory(Path.Combine(CliDirectory, "release"));
        Normalizer = new Normalizer().Add(Root, "<root>");
    }

    public string Root { get; }
    public string CliDirectory { get; }
    public Normalizer Normalizer { get; }
    public IReadOnlyList<ProbeLaunch> Launches => _launches;

    /// <summary>Copies the probe as <c>&lt;name&gt;.exe</c> (or <c>&lt;name&gt;</c> off Windows); returns its path.</summary>
    public string Install(string name, JsonObject? script = null)
    {
        var source = AppContext.BaseDirectory;
        foreach (var file in new[] { "RedCompute.TestProbe.dll", "RedCompute.TestProbe.runtimeconfig.json", "RedCompute.TestProbe.deps.json" })
        {
            var from = Path.Combine(source, file);
            Assert.True(File.Exists(from), $"Probe file {file} missing from the test output; is RedCompute.TestProbe referenced?");
            File.Copy(from, Path.Combine(CliDirectory, file), overwrite: true);
        }
        var host = OperatingSystem.IsWindows() ? "RedCompute.TestProbe.exe" : "RedCompute.TestProbe";
        var target = Path.Combine(CliDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.Copy(Path.Combine(source, host), target, overwrite: true);
        if (script is not null)
            File.WriteAllText(Path.Combine(CliDirectory, $"{name}.probe.json"), script.ToJsonString(), new UTF8Encoding(false));
        return target;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> and, for every probe launch it causes, in arrival order:
    /// observes containment while the probe holds, then releases it. Returns once the operation ends.
    /// </summary>
    /// <param name="expected">
    /// Launches to wait for. An operation that only spawns (a persistent session start) can return
    /// before its probe has written anything.
    /// </param>
    /// <param name="whileHeld">Runs for each launch after containment is observed, before its release.</param>
    public async Task<IReadOnlyList<(ProbeLaunch Launch, string Containment)>> RunAsync(Func<Task> operation,
        int expected = 1, TimeSpan? patience = null, Func<ProbeLaunch, Task>? whileHeld = null)
    {
        var observed = new List<(ProbeLaunch, string)>();
        var task = Task.Run(operation);
        using var timeout = new CancellationTokenSource(patience ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            foreach (var launch in NewLaunches())
            {
                observed.Add((launch, await ObserveContainmentAsync(launch.Pid)));
                if (whileHeld is not null) await whileHeld(launch);
                Release(launch.Pid);
            }
            if (task.IsCompleted && observed.Count >= expected)
            {
                await task;
                return observed;
            }
            if (task.IsFaulted) await task;
            await Task.Delay(20, timeout.Token);
        }
    }

    public void Release(int pid) => File.WriteAllText(Path.Combine(CliDirectory, "release", pid.ToString()), "");

    private IEnumerable<ProbeLaunch> NewLaunches()
    {
        var captures = Path.Combine(CliDirectory, "captures");
        if (!System.IO.Directory.Exists(captures)) yield break;
        foreach (var file in System.IO.Directory.EnumerateFiles(captures, "*.json").OrderBy(File.GetCreationTimeUtc))
        {
            if (!_seen.Add(file)) continue;
            var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var env = node["environment"]!.AsObject()
                .ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>());
            var launch = new ProbeLaunch(
                node["pid"]!.GetValue<int>(),
                Path.GetFileName(file)[..Path.GetFileName(file).LastIndexOf('-')],
                node["commandLine"]!.GetValue<string>(),
                node["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList(),
                node["cwd"]!.GetValue<string>(),
                env);
            _launches.Add(launch);
            yield return launch;
        }
    }

    /// <summary>
    /// "session:&lt;owner&gt;", "kept:&lt;owner&gt;" or "none", read through the containment registry
    /// (the test host itself runs inside a job, so an OS job query proves nothing).
    /// </summary>
    public static async Task<string> ObserveContainmentAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            if (SessionProcessContainment.FindByRoot(pid) is { } tree)
                return $"{(tree.IsKept ? "kept" : "session")}:{tree.Owner}";
            if (DateTime.UtcNow > deadline) return "none";
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        try { File.WriteAllText(Path.Combine(CliDirectory, "release", "all"), ""); } catch { }
        foreach (var launch in _launches)
        {
            try
            {
                using var process = Process.GetProcessById(launch.Pid);
                var path = process.MainModule?.FileName;
                if (path is not null && path.StartsWith(CliDirectory, StringComparison.OrdinalIgnoreCase)
                    && !process.WaitForExit(2000))
                    process.Kill(entireProcessTree: true);
            }
            catch { }
        }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, true); break; }
            catch { Thread.Sleep(100); }
        }
    }
}
