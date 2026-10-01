using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RedCompute.App.Services;

public sealed record DeploymentVerificationTarget(string Service, string RunId)
{
    internal const string MetadataKey = "deploymentVerificationTarget";
    public void Validate()
    {
        if (Service is not ("redleaf" or "redcompute") || string.IsNullOrWhiteSpace(RunId)
            || RunId.Length > 100 || !Regex.IsMatch(RunId, "^[A-Za-z0-9-]+$"))
            throw new SessionInputQueueStoreException("invalid_deployment_target", "Target requires exact service redleaf/redcompute and a runId without paths");
    }
    public static DeploymentVerificationTarget? FromMetadata(string? metadata)
    {
        if (metadata is null) return null;
        var node = JsonNode.Parse(metadata)?[MetadataKey];
        return node?.Deserialize<DeploymentVerificationTarget>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    public static string WithMetadata(string? metadata, DeploymentVerificationTarget target)
    {
        target.Validate();
        var node = metadata is null ? new JsonObject() : JsonNode.Parse(metadata)!.AsObject();
        node[MetadataKey] = JsonSerializer.SerializeToNode(target, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return node.ToJsonString();
    }
}

public sealed record DeploymentVerificationState(string State, string? ErrorCode = null);

/// <summary>Exact canonical request and receipt identity; later runs never imply success or supersession.</summary>
internal static class DeploymentVerification
{
    internal static DeploymentVerificationState Read(DeploymentVerificationTarget target, string computeRoot, string leafRoot)
    {
        target.Validate();
        var root = target.Service == "redcompute" ? computeRoot : leafRoot;
        var app = target.Service == "redcompute" ? "RedCompute.App" : "RedLeaf.App";
        var receiptRoot = Path.Combine(root, "src", app, "bin", "Release", ".rebuild-receipts");
        var prefix = $"{target.Service}-rebuild-{target.RunId}";
        var requestPath = Path.Combine(receiptRoot, prefix + ".request.json");
        var receiptPath = Path.Combine(receiptRoot, prefix + ".json");
        try
        {
            if (!File.Exists(requestPath)) return new("unknown", "deployment_target_unknown");
            using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
            if (Text(request.RootElement, "runId") != target.RunId
                || !string.Equals(Path.GetFullPath(Text(request.RootElement, "receiptPath") ?? ""),
                    Path.GetFullPath(receiptPath), StringComparison.OrdinalIgnoreCase))
                return new("unknown", "deployment_target_identity_mismatch");
            if (!File.Exists(receiptPath)) return new("pending", "deployment_target_pending");
            using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
            if (Text(receipt.RootElement, "runId") != target.RunId)
                return new("unknown", "deployment_target_identity_mismatch");
            var state = Text(receipt.RootElement, "state");
            if (state is "succeeded" or "failed"
                && (!receipt.RootElement.TryGetProperty("success", out var success)
                    || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || success.GetBoolean() != (state == "succeeded")))
                return new("unknown", "deployment_target_receipt_inconsistent");
            return state switch {
                "succeeded" => new("succeeded"),
                "failed" => new("failed", "deployment_target_failed"),
                _ => new("pending", "deployment_target_pending") };
        }
        catch (IOException) { return new("pending", "deployment_target_unavailable"); }
        catch (JsonException) { return new("pending", "deployment_target_unavailable"); }
        catch (ArgumentException) { return new("unknown", "deployment_target_identity_mismatch"); }
    }
    private static string? Text(JsonElement data, string key) => data.TryGetProperty(key, out var node)
        && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
}
