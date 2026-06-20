using System.IO;
using System.Text.Json;

namespace RedCompute.Plugin.ComfyUI;

public class WorkflowParameter
{
    public required string Name { get; init; }
    public required string NodeId { get; init; }
    public required string Field { get; init; }
    public object? Default { get; init; }

    public string Type { get; init; } = "string";
    public string? Description { get; init; }
    public bool Required { get; init; } = false;
    public string[]? Enum { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public double? Step { get; init; }
    public string? Widget { get; init; }
    public string? Placeholder { get; init; }
    public string? Suffix { get; init; }
}

public class WorkflowDefinition
{
    public required string Name { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public string Description { get; init; } = "";
    public string MediaType { get; init; } = "image";
    public required string OutputNode { get; init; }
    public required List<WorkflowParameter> Parameters { get; init; }
}

public class WorkflowLoader
{
    private readonly string _workflowsDir;
    private readonly Action<string> _log;
    private Dictionary<string, WorkflowDefinition> _workflows = new();

    public IReadOnlyDictionary<string, WorkflowDefinition> Workflows => _workflows;

    public WorkflowLoader(string workflowsDir, Action<string> log)
    {
        _workflowsDir = workflowsDir;
        _log = log;
    }

    public void Reload()
    {
        var result = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_workflowsDir))
        {
            _log($"[WorkflowLoader] Directory not found: {_workflowsDir}");
            _workflows = result;
            return;
        }

        foreach (var path in Directory.GetFiles(_workflowsDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("_axl", out var axl))
                    continue;

                var name = Path.GetFileNameWithoutExtension(path);
                var parameters = new List<WorkflowParameter>();

                if (axl.TryGetProperty("parameters", out var paramArray))
                {
                    foreach (var p in paramArray.EnumerateArray())
                    {
                        object? defaultVal = null;
                        if (p.TryGetProperty("default", out var def))
                        {
                            defaultVal = def.ValueKind switch
                            {
                                JsonValueKind.String => def.GetString(),
                                JsonValueKind.Number => def.TryGetInt64(out var l) ? l : def.GetDouble(),
                                JsonValueKind.Null => null,
                                _ => def.ToString()
                            };
                        }

                        string[]? enumValues = null;
                        if (p.TryGetProperty("enum", out var enumProp) && enumProp.ValueKind == JsonValueKind.Array)
                            enumValues = enumProp.EnumerateArray().Select(e => e.GetString()!).ToArray();

                        parameters.Add(new WorkflowParameter
                        {
                            Name = p.GetProperty("name").GetString()!,
                            NodeId = p.GetProperty("node_id").ToString(),
                            Field = p.GetProperty("field").GetString()!,
                            Default = defaultVal,
                            Type = p.TryGetProperty("type", out var tp) ? tp.GetString() ?? "string" : "string",
                            Description = p.TryGetProperty("description", out var dp) ? dp.GetString() : null,
                            Required = p.TryGetProperty("required", out var rp) && rp.GetBoolean(),
                            Enum = enumValues,
                            Minimum = p.TryGetProperty("minimum", out var minP) ? minP.GetDouble() : null,
                            Maximum = p.TryGetProperty("maximum", out var maxP) ? maxP.GetDouble() : null,
                            Step = p.TryGetProperty("step", out var stepP) ? stepP.GetDouble() : null,
                            Widget = p.TryGetProperty("widget", out var wp) ? wp.GetString() : null,
                            Placeholder = p.TryGetProperty("placeholder", out var pp) ? pp.GetString() : null,
                            Suffix = p.TryGetProperty("suffix", out var sp) ? sp.GetString() : null,
                        });
                    }
                }

                result[name] = new WorkflowDefinition
                {
                    Name = name,
                    FileName = Path.GetFileName(path),
                    FilePath = path,
                    Description = axl.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                    MediaType = axl.TryGetProperty("media_type", out var mt) ? mt.GetString() ?? "image" : "image",
                    OutputNode = axl.TryGetProperty("output_node", out var on) ? on.ToString() : "9",
                    Parameters = parameters
                };

                _log($"[WorkflowLoader] Loaded workflow: {name} ({parameters.Count} params, {result[name].MediaType})");
            }
            catch (Exception ex)
            {
                _log($"[WorkflowLoader] Failed to load {path}: {ex.Message}");
            }
        }

        _workflows = result;
        _log($"[WorkflowLoader] {result.Count} workflow(s) loaded from {_workflowsDir}");
    }

    public WorkflowDefinition? Get(string name) =>
        _workflows.TryGetValue(name, out var wf) ? wf : null;

    public Dictionary<string, JsonElement>? LoadWorkflowJson(string name)
    {
        var wf = Get(name);
        if (wf == null) return null;

        try
        {
            var json = File.ReadAllText(wf.FilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            dict?.Remove("_axl");
            return dict;
        }
        catch (Exception ex)
        {
            _log($"[WorkflowLoader] Failed to load JSON for {name}: {ex.Message}");
            return null;
        }
    }
}
