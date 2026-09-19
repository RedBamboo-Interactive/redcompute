using System.Diagnostics;
using System.IO;
using System.Text.Json;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;

namespace RedCompute.App.Services;

public sealed record MaintenanceDeploymentAdmission(
    bool Accepted,
    string State,
    string RunId,
    string? Error = null);

/// <summary>
/// Drains interactive turns inside RedCompute, then hands the already-staged file transaction to
/// an Explorer-owned process. No execution bearer is retained or forwarded to the deployer.
/// </summary>
public sealed class MaintenanceDeploymentCoordinator
{
    private static readonly TimeSpan GracefulDrainTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForcedDrainTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HandoffTimeout = TimeSpan.FromMinutes(2);
    private readonly MaintenanceSessionDrain _sessionDrain;
    private readonly Action<string, Guid?> _log;
    private readonly object _gate = new();
    private SessionInputQueueService? _inputQueue;
    private string _state = "idle";
    private string? _runId;

    public MaintenanceDeploymentCoordinator(CapabilityRegistry registry, Action<string, Guid?> log)
        : this(registry, log, GracefulDrainTimeout, ForcedDrainTimeout, DrainPollInterval)
    {
    }

    internal MaintenanceDeploymentCoordinator(
        CapabilityRegistry registry,
        Action<string, Guid?> log,
        TimeSpan gracefulDrainTimeout,
        TimeSpan forcedDrainTimeout,
        TimeSpan drainPollInterval)
    {
        _log = log;
        _sessionDrain = new MaintenanceSessionDrain(
            registry, log, gracefulDrainTimeout, forcedDrainTimeout, drainPollInterval);
    }

    public bool IsDraining
    {
        get { lock (_gate) return _state is "draining" or "launching"; }
    }

    public void AttachInputQueue(SessionInputQueueService inputQueue)
        => _inputQueue = inputQueue;

    public MaintenanceDeploymentAdmission Arm(string requestPath)
    {
        var validated = ValidateRequest(requestPath);
        lock (_gate)
        {
            if (_state is "draining" or "launching")
                return new(false, _state, _runId ?? validated.RunId,
                    $"Deployment {_runId} is already {_state}");
            _state = "draining";
            _runId = validated.RunId;
        }

        _ = DrainAndLaunchAsync(validated);
        return new(true, "draining", validated.RunId);
    }

    private async Task DrainAndLaunchAsync(ValidatedRequest request)
    {
        try
        {
            _log($"[Maintenance] Deployment {request.RunId} armed; queue delivery paused while active turns drain", null);
            if (_inputQueue is null)
                throw new InvalidOperationException("The durable input queue is not attached");
            var sessions = await _sessionDrain.DrainAsync(_inputQueue.WaitForDeliveryQuiescenceAsync);
            var checkpoint = new PlannedRestartData(request.RunId, DateTimeOffset.UtcNow, sessions);
            PlannedRestartCheckpoint.Write(checkpoint);
            lock (_gate) _state = "launching";
            await LaunchDesktopHandoffAsync(request);
            _log($"[Maintenance] Deployment {request.RunId} handed to the Windows desktop", null);
            using var handoffTimeout = new CancellationTokenSource(HandoffTimeout);
            try
            {
                await ObserveHandoffAsync(request, handoffTimeout.Token);
            }
            catch (OperationCanceledException) when (handoffTimeout.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Desktop deployment handoff did not stop RedCompute within {HandoffTimeout.TotalMinutes:0.##} minutes");
            }
            throw new InvalidOperationException(
                "Desktop handoff returned without stopping the running RedCompute process");
        }
        catch (Exception ex)
        {
            PlannedRestartCheckpoint.Consume();
            lock (_gate) _state = "failed";
            WriteFailureReceipt(request, ex);
            _log($"[Maintenance] Deployment {request.RunId} cancelled without stopping RedCompute: {ex.Message}", null);
        }
    }

    private static async Task LaunchDesktopHandoffAsync(ValidatedRequest request)
    {
        var powerShell = Path.Combine(Environment.SystemDirectory,
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo
        {
            FileName = powerShell,
            WorkingDirectory = request.RepositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(request.HandoffScript);
        start.ArgumentList.Add("-RequestPath");
        start.ArgumentList.Add(request.RequestPath);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the desktop deployment handoff");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Desktop deployment handoff exited with code {process.ExitCode}");
    }

    private static async Task ObserveHandoffAsync(ValidatedRequest request, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (File.Exists(request.ReceiptPath))
            {
                try
                {
                    using var receipt = JsonDocument.Parse(File.ReadAllText(request.ReceiptPath));
                    var state = receipt.RootElement.TryGetProperty("state", out var value)
                        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    if (state == "failed")
                    {
                        var error = receipt.RootElement.TryGetProperty("error", out var detail)
                            && detail.ValueKind == JsonValueKind.String ? detail.GetString() : null;
                        throw new InvalidOperationException(error ?? "Desktop deployment failed before restart");
                    }
                }
                catch (JsonException) { }
            }
            await Task.Delay(250, ct);
        }
    }

    private static void WriteFailureReceipt(ValidatedRequest request, Exception exception)
    {
        try
        {
            var temporaryPath = $"{request.ReceiptPath}.writing-{Environment.ProcessId}";
            var receipt = JsonSerializer.Serialize(new
            {
                runId = request.RunId,
                state = "failed",
                phase = "maintenance-coordinator",
                success = false,
                updatedAt = DateTimeOffset.Now,
                error = exception.Message,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, receipt);
            File.Move(temporaryPath, request.ReceiptPath, overwrite: true);
        }
        catch
        {
            // Preserve the original deployment failure. Logging still reports it even if the
            // terminal receipt cannot be written.
        }
    }

    private static ValidatedRequest ValidateRequest(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            throw new ArgumentException("requestPath is required", nameof(requestPath));

        var fullRequestPath = Path.GetFullPath(requestPath);
        var computeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var leafRoot = Path.GetFullPath(Path.Combine(computeRoot, "..", "redleaf"));
        var computeReceiptRoot = Path.Combine(computeRoot,
            "src", "RedCompute.App", "bin", "Release", ".rebuild-receipts");
        var leafReceiptRoot = Path.Combine(leafRoot,
            "src", "RedLeaf.App", "bin", "Release", ".rebuild-receipts");
        var isComputeRequest = IsWithin(fullRequestPath, computeReceiptRoot);
        var isLeafRequest = IsWithin(fullRequestPath, leafReceiptRoot);
        if ((!isComputeRequest && !isLeafRequest) || !File.Exists(fullRequestPath))
            throw new InvalidOperationException(
                "Deployment request must be an existing RedCompute or RedLeaf rebuild receipt request");

        using var document = JsonDocument.Parse(File.ReadAllText(fullRequestPath));
        var runId = document.RootElement.TryGetProperty("runId", out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var prefix = isComputeRequest ? "redcompute" : "redleaf";
        if (string.IsNullOrWhiteSpace(runId)
            || Path.GetFileName(fullRequestPath) != $"{prefix}-rebuild-{runId}.request.json")
            throw new InvalidOperationException("Deployment request identity does not match its filename");

        var receiptPath = document.RootElement.TryGetProperty("receiptPath", out var receipt)
            && receipt.ValueKind == JsonValueKind.String ? receipt.GetString() : null;
        var owningReceiptRoot = isComputeRequest ? computeReceiptRoot : leafReceiptRoot;
        if (string.IsNullOrWhiteSpace(receiptPath)
            || !IsWithin(Path.GetFullPath(receiptPath), owningReceiptRoot))
            throw new InvalidOperationException("Deployment receipt path is outside the owning rebuild receipt root");

        var repositoryRoot = isComputeRequest ? computeRoot : leafRoot;
        var handoffScript = Path.Combine(repositoryRoot, "handoff-staged.ps1");
        if (!File.Exists(handoffScript))
            throw new FileNotFoundException("Desktop handoff script is missing", handoffScript);

        return new(fullRequestPath, Path.GetFullPath(receiptPath), runId, repositoryRoot, handoffScript);
    }

    private static bool IsWithin(string path, string directory)
        => path.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed record ValidatedRequest(
        string RequestPath,
        string ReceiptPath,
        string RunId,
        string RepositoryRoot,
        string HandoffScript);
}
