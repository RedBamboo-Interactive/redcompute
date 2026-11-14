using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Records how it was launched, then optionally plays a scripted CLI so the provider under test
// gets far enough to behave normally. Everything it is told comes from files beside its own
// executable, written by the test before the launch:
//
//   <dir>/<name>.probe.json   optional script (see Script below), <name> = own file name sans extension
//   <dir>/captures/           where it writes <name>-<pid>.json
//   <dir>/release/<pid> or <dir>/release/all
//                             it holds (reads nothing, writes nothing) until one of these exists,
//                             so a test can inspect the live process (containment) without racing it
//
// It never reads its own argv or environment for instructions.

var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
var dir = Path.GetDirectoryName(exe)!;
var name = Path.GetFileNameWithoutExtension(exe);
var pid = Environment.ProcessId;

var script = Script.Load(Path.Combine(dir, $"{name}.probe.json"));

WriteCapture(Path.Combine(dir, "captures"), $"{name}-{pid}.json");

if (script.Hold)
{
    var release = Path.Combine(dir, "release");
    var deadline = DateTime.UtcNow.AddMinutes(2);
    while (!File.Exists(Path.Combine(release, pid.ToString())) && !File.Exists(Path.Combine(release, "all")))
    {
        if (DateTime.UtcNow > deadline) return 97; // never released: the test is gone
        Thread.Sleep(20);
    }
}

var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));

foreach (var line in script.OnStart) output.WriteLine(line);

var repliedOnce = false;
while (input.ReadLine() is { } received)
{
    if (script.Rpc is { } rpc && TryRpc(received, rpc, output)) continue;
    if (!repliedOnce && script.ReplyOnce.Count > 0)
    {
        repliedOnce = true;
        foreach (var line in script.ReplyOnce) output.WriteLine(line);
    }
}

foreach (var line in script.OnEof) output.WriteLine(line);
return script.ExitCode;

static bool TryRpc(string received, RpcScript rpc, StreamWriter output)
{
    JsonNode? request;
    try { request = JsonNode.Parse(received.TrimStart('﻿')); }
    catch (JsonException) { return false; }
    if (request is not JsonObject obj || obj["method"] is not JsonValue methodValue) return false;
    var method = methodValue.GetValue<string>();
    if (obj["id"] is not { } id) return true; // a notification: nothing to answer
    if (rpc.Silent.Contains(method)) return true;
    var result = rpc.Results.TryGetValue(method, out var scripted) ? scripted?.DeepClone() : new JsonObject();
    var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    output.WriteLine(response.ToJsonString());
    return true;
}

static void WriteCapture(string directory, string file)
{
    Directory.CreateDirectory(directory);
    var environment = new SortedDictionary<string, string?>(StringComparer.Ordinal);
    foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        environment[(string)entry.Key] = (string?)entry.Value;
    var capture = new JsonObject
    {
        ["pid"] = Environment.ProcessId,
        ["commandLine"] = RawCommandLine(),
        ["args"] = new JsonArray(Environment.GetCommandLineArgs().Skip(1).Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["cwd"] = Environment.CurrentDirectory,
        ["environment"] = new JsonObject(environment.Select(pair =>
            KeyValuePair.Create(pair.Key, (JsonNode?)JsonValue.Create(pair.Value)))),
    };
    var path = Path.Combine(directory, file);
    var temp = path + ".tmp";
    File.WriteAllText(temp, capture.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    File.Move(temp, path, overwrite: true);
}

static string RawCommandLine()
{
    if (OperatingSystem.IsWindows()) return Marshal.PtrToStringUni(Native.GetCommandLineW()) ?? "";
    try
    {
        var raw = File.ReadAllText("/proc/self/cmdline");
        return string.Join(' ', raw.TrimEnd('\0').Split('\0'));
    }
    catch { return Environment.CommandLine; }
}

internal sealed record RpcScript(Dictionary<string, JsonNode?> Results, HashSet<string> Silent);

internal sealed record Script(bool Hold, List<string> OnStart, List<string> ReplyOnce, List<string> OnEof,
    RpcScript? Rpc, int ExitCode)
{
    public static Script Load(string path)
    {
        if (!File.Exists(path)) return new(true, [], [], [], null, 0);
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        RpcScript? rpc = null;
        if (root["rpc"] is JsonObject rpcNode)
        {
            var results = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (rpcNode["results"] is JsonObject scripted)
                foreach (var (method, result) in scripted) results[method] = result?.DeepClone();
            var silent = new HashSet<string>(
                (rpcNode["silent"] as JsonArray ?? []).Select(node => node!.GetValue<string>()), StringComparer.Ordinal);
            rpc = new RpcScript(results, silent);
        }
        return new(
            root["hold"]?.GetValue<bool>() ?? true,
            Lines(root["onStart"]), Lines(root["replyOnce"]), Lines(root["onEof"]),
            rpc,
            root["exitCode"]?.GetValue<int>() ?? 0);
    }

    private static List<string> Lines(JsonNode? node) =>
        node is JsonArray array ? array.Select(line => line!.GetValue<string>()).ToList() : [];
}

internal static class Native
{
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCommandLineW();
}
