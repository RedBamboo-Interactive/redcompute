using System.Text.Json;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class DeploymentVerificationTests
{
    [Theory]
    [InlineData("redleaf")]
    [InlineData("redcompute")]
    public void Exact_identity_survives_restart_and_newer_runs_do_not_change_failed_target(string service)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(), "Nova", "46682c5a", "recovery-prevention", "run-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = new DeploymentVerificationTarget(service, "run-one");
            var app = service == "redleaf" ? "RedLeaf.App" : "RedCompute.App";
            var receipts = Path.Combine(root, "src", app, "bin", "Release", ".rebuild-receipts");
            Directory.CreateDirectory(receipts);
            string PathFor(string run, string suffix) => Path.Combine(receipts, $"{service}-rebuild-{run}{suffix}.json");
            void Request(string run) => File.WriteAllText(PathFor(run, ".request"), JsonSerializer.Serialize(new { runId = run, receiptPath = PathFor(run, "") }));
            void Receipt(string run, string state) => File.WriteAllText(PathFor(run, ""), JsonSerializer.Serialize(new { runId = run, state, success = state == "succeeded" }));
            Assert.Equal("unknown", DeploymentVerification.Read(target, root, root).State);
            Request("run-one"); Assert.Equal("pending", DeploymentVerification.Read(target, root, root).State);
            Receipt("run-one", "draining"); Assert.Equal("pending", DeploymentVerification.Read(target, root, root).State);
            Receipt("run-one", "failed"); Assert.Equal("failed", DeploymentVerification.Read(target, root, root).State);
            Request("run-two"); Receipt("run-two", "succeeded");
            Assert.Equal("failed", DeploymentVerification.Read(target, root, root).State);
            Assert.Equal("succeeded", DeploymentVerification.Read(new(service, "run-two"), root, root).State);
            File.WriteAllText(PathFor("run-two", ""), JsonSerializer.Serialize(new { runId = "run-two", state = "succeeded", success = false }));
            Assert.Equal("deployment_target_receipt_inconsistent", DeploymentVerification.Read(new(service, "run-two"), root, root).ErrorCode);
            File.WriteAllText(PathFor("run-one", ""), JsonSerializer.Serialize(new { runId = "run-two", state = "succeeded" }));
            Assert.Equal("deployment_target_identity_mismatch", DeploymentVerification.Read(target, root, root).ErrorCode);
            File.WriteAllText(PathFor("run-one", ".request"), JsonSerializer.Serialize(new { runId = "run-one", receiptPath = PathFor("run-two", "") }));
            Assert.Equal("unknown", DeploymentVerification.Read(target, root, root).State);
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("redleaf", "../run")]
    [InlineData("other", "run")]
    [InlineData("redcompute", "C:/request")]
    public void Target_cannot_supply_paths_or_other_service(string service, string run)
        => Assert.Throws<SessionInputQueueStoreException>(() => new DeploymentVerificationTarget(service, run).Validate());
}
