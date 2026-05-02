using System.IO;
using System.Text.Json;
using RedCompute.Core.ComfyUI;

namespace RedCompute.Providers.ComfyUI;

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

                        parameters.Add(new WorkflowParameter
                        {
                            Name = p.GetProperty("name").GetString()!,
                            NodeId = p.GetProperty("node_id").ToString(),
                            Field = p.GetProperty("field").GetString()!,
                            Default = defaultVal
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
