using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.WebSockets;
using RedCompute.App.Services;
using RedCompute.App.Services.Jobs;
using RedCompute.App.Data;
using Microsoft.EntityFrameworkCore;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;
using RedCompute.Core.Jobs;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class SessionRecoveryTests
{
    [Theory]
    [InlineData("maintenance_restart")]
    [InlineData("orphaned_on_restart")]
    public async Task Accepted_pending_input_survives_reopen_and_delivers_once_without_second_message(string reason)
    {
        using var f = new Fixture(reason);
        var accepted = await f.Store.EnqueueAsync(f.Submission());
        f.Store = new(f.Config, f.Attachments);
        var queue = f.Service();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => queue.ProcessSessionAsync("session", default)));
        await queue.ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Resumes); Assert.Equal(1, f.Runtime.Deliveries);
        Assert.Equal(2, f.AuthorityReads);
        Assert.True(f.Runtime.ResumeHadFreshIdentity); Assert.True(f.Runtime.DeliveryHadFreshIdentity);
        var item = await f.Store.GetAsync("session", accepted.Item.Id, "owner");
        Assert.Equal(SessionInputQueueState.Delivered, item!.State);
        Assert.Equal("prompt", item.DeliveredMessageUid);
        Assert.Null(SessionScratch.Environment(null));
    }

    [Theory]
    [InlineData("user_stopped")]
    [InlineData("usage_limit")]
    [InlineData("terminated")]
    public async Task Other_stops_never_auto_resume(string reason)
    {
        using var f = new Fixture(reason); await f.Store.EnqueueAsync(f.Submission());
        await f.Service().ProcessSessionAsync("session", default);
        Assert.Equal(0, f.Runtime.Resumes); Assert.Equal(0, f.AuthorityReads);
    }

    [Theory]
    [InlineData("recovery_owner_changed", false)]
    [InlineData("recovery_agent_revoked", false)]
    [InlineData("recovery_permission_revoked", false)]
    [InlineData("recovery_host_not_ready", true)]
    public async Task Authority_blocks_are_visible_durable_and_back_off_without_provider_attempts(string error, bool transient)
    {
        using var f = new Fixture("maintenance_restart");
        var item = (await f.Store.EnqueueAsync(f.Submission())).Item;
        f.Authority = (_, _, _) => Task.FromResult(new SessionRecoveryAuthority(null, error, transient));
        var service = f.Service();
        await service.ProcessSessionAsync("session", default); await service.ProcessSessionAsync("session", default);
        var pending = (await f.Store.GetAsync("session", item.Id, "owner"))!;
        Assert.Equal(SessionInputQueueState.Pending, pending.State); Assert.Equal(0, pending.AttemptCount);
        Assert.Equal(error, (await service.GetSummaryAsync("session", "owner")).ErrorCode);
        Assert.True(pending.NextAttemptAt > DateTimeOffset.UtcNow);
        Assert.Equal(0, f.Runtime.Resumes); Assert.Equal(0, f.Runtime.Deliveries);
    }

    [Fact]
    public async Task Maintenance_pause_and_concurrent_manual_stop_win_before_resume()
    {
        using var f = new Fixture("maintenance_restart"); await f.Store.EnqueueAsync(f.Submission());
        f.Paused = true; var service = f.Service();
        await service.ProcessSessionAsync("session", default); Assert.Equal(0, f.AuthorityReads);
        f.Paused = false;
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        f.Authority = async (_, _, _) => { entered.SetResult(); await release.Task; return f.Fresh(); };
        var processing = service.ProcessSessionAsync("session", default); await entered.Task;
        var stopping = service.StopSessionAsync("session", () => { f.Runtime.Info.StopReason = "user_stopped"; return Task.CompletedTask; }, default);
        release.SetResult(); await Task.WhenAll(processing, stopping);
        Assert.Equal(0, f.Runtime.Resumes); Assert.Equal(0, f.Runtime.Deliveries);
    }

    [Fact]
    public async Task Revocation_after_startup_keeps_accepted_input_pending()
    {
        using var f = new Fixture("maintenance_restart"); await f.Store.EnqueueAsync(f.Submission());
        var calls = 0;
        f.Authority = (_, _, _) => Task.FromResult(++calls == 1 ? f.Fresh() : new(null, "recovery_permission_revoked", false));
        await f.Service().ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Resumes); Assert.Equal(0, f.Runtime.Deliveries);
        Assert.Equal("recovery_permission_revoked", (await f.Store.GetHeadAsync("session"))!.ErrorCode);
    }

    [Fact]
    public async Task Busy_retry_keeps_recovery_authorization_requirement_across_reopen()
    {
        using var f = new Fixture("maintenance_restart");
        var item = (await f.Store.EnqueueAsync(f.Submission())).Item;
        f.Runtime.Result = SessionInputDeliveryResult.Busy();
        var service = f.Service();
        await service.ProcessSessionAsync("session", default);
        f.Store = new(f.Config, f.Attachments);
        Assert.True(await f.Store.RequiresRecoveryAsync(item.Id, default));
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Authority = (_, _, _) => Task.FromResult(new SessionRecoveryAuthority(null, "recovery_permission_revoked", false));
        await f.Service().ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Deliveries);
        Assert.Equal("recovery_permission_revoked", (await f.Store.GetHeadAsync("session"))!.ErrorCode);
    }

    [Theory]
    [InlineData(SessionStatus.Idle)]
    [InlineData(SessionStatus.Starting)]
    public async Task Provider_resume_and_later_delivery_start_only_one_real_job_invocation(SessionStatus resumedStatus)
    {
        using var f = new Fixture("maintenance_restart");
        var dbPath = Path.Combine(f.Root, "jobs.db");
        RedComputeDbContext Db() => new(dbPath);
        using (var db = Db()) db.Database.EnsureCreated();
        var jobs = new JobTrackingService(Db);
        var submission = f.Submission();
        var job = jobs.CreateJob(new JobSubmission("ai-session", "test", "{}", submission.Provenance));
        f.Jobs = jobs; f.Runtime.Info.JobId = job.Id; f.Runtime.ResumedStatus = resumedStatus;
        f.Runtime.OnResume = p => jobs.StartInvocation(job.Id, p, JobEventKind.Resumed);
        await f.Store.EnqueueAsync(submission);
        var service = f.Service(); await service.ProcessSessionAsync("session", default);
        if (resumedStatus == SessionStatus.Starting) { f.Runtime.Info.Status = SessionStatus.Idle; await service.ProcessSessionAsync("session", default); }
        Assert.Single(jobs.GetJobEvents(job.Id), e => e.Kind == JobEventKind.Resumed);
        Assert.Equal(1, f.Runtime.Deliveries);
    }

    [Fact]
    public async Task Busy_batch_retains_recovery_requirement_when_original_head_is_cancelled()
    {
        using var f = new Fixture("maintenance_restart");
        var first = (await f.Store.EnqueueAsync(f.Submission())).Item;
        var second = (await f.Store.EnqueueAsync(f.Submission("second"))).Item;
        f.Runtime.Result = SessionInputDeliveryResult.Busy();
        var service = f.Service();
        await service.ProcessSessionAsync("session", default);
        Assert.True(await f.Store.RequiresRecoveryAsync(second.Id, default));
        await service.CancelAsync("session", first.Id, "owner", default);
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Authority = (_, _, _) => Task.FromResult(new SessionRecoveryAuthority(null, "recovery_permission_revoked", false));
        await service.ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Deliveries);
        Assert.Equal("recovery_permission_revoked", (await f.Store.GetHeadAsync("session"))!.ErrorCode);
    }

    [Theory]
    [InlineData("valid", null)]
    [InlineData("wrong_owner", "recovery_identity_invalid")]
    [InlineData("wrong_item", "recovery_identity_invalid")]
    [InlineData("wrong_actor", "recovery_identity_invalid")]
    [InlineData("unsigned", "recovery_identity_invalid")]
    [InlineData("external", "recovery_host_untrusted")]
    public async Task Recovery_transport_accepts_only_signed_exact_child_from_loopback(string variant, string? error)
    {
        using var f = new Fixture("maintenance_restart");
        var actorId = Guid.NewGuid().ToString();
        var submission = f.Submission();
        submission = submission with { Provenance = submission.Provenance with { Actor = submission.Provenance.Actor with { EntityId = actorId } } };
        var item = (await f.Store.EnqueueAsync(submission)).Item;
        var options = new JwtOptions { SigningKey = "recovery-transport-test-secret-1234567890123456789" };
        var jwt = new JwtService(options);
        var identity = new ExecutionIdentity(1, Guid.NewGuid().ToString(), new("nova", "Nova"),
            new("agent", "nova", "Nova", variant == "wrong_actor" ? Guid.NewGuid().ToString() : actorId),
            new("user", variant == "wrong_owner" ? "other" : "owner"),
            [new("accepted-input-recovery", variant == "wrong_item" ? "other" : item.Id, Route: item.MessageUid), new("ai-session", "session")]);
        var token = variant == "unsigned" ? "invalid" : new ExecutionTokenIssuer(jwt, options)
            .Issue(identity, new ExecutionPrincipal(identity.Beneficiary.Id!, "owner@test", "Owner", ["operator"])).AccessToken;
        var handler = new RecoveryHandler(token);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(variant == "external" ? "https://example.com/" : "http://127.0.0.1:1234/") };
        var reader = new RedLeafSessionReader(http, null!, jwt);
        var authority = await reader.RecoverAuthorityAsync(f.Runtime.Info, item, default);
        Assert.Equal(error, authority.ErrorCode);
        Assert.Equal(error is null, authority.AccessToken is not null);
        Assert.Equal(variant == "external" ? 0 : 1, handler.Calls);
    }

    private sealed class RecoveryHandler(string token) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("/api/auth/session-recovery-token", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(new { accessToken = token })) });
        }
    }

    [Fact]
    public async Task Later_fifo_scope_still_requires_current_authority_after_first_recovery()
    {
        using var f = new Fixture("maintenance_restart");
        await f.Store.EnqueueAsync(f.Submission());
        var later = f.Submission("later");
        later = later with { Provenance = later.Provenance with { Actor = later.Provenance.Actor with { EntityId = Guid.NewGuid().ToString() } } };
        var second = (await f.Store.EnqueueAsync(later)).Item;
        var service = f.Service(); await service.ProcessSessionAsync("session", default);
        Assert.True(await f.Store.RequiresRecoveryAsync(second.Id, default));
        f.Runtime.Info.Status = SessionStatus.Idle;
        f.Authority = (_, _, _) => Task.FromResult(new SessionRecoveryAuthority(null, "recovery_agent_revoked", false));
        await service.ProcessSessionAsync("session", default);
        Assert.Equal(1, f.Runtime.Deliveries);
        Assert.Equal("recovery_agent_revoked", (await f.Store.GetHeadAsync("session"))!.ErrorCode);
    }

    [Fact]
    public async Task Expired_uncertain_delivery_lease_is_never_resumed_or_replayed()
    {
        using var f = new Fixture("orphaned_on_restart"); await f.Store.EnqueueAsync(f.Submission());
        await f.Store.ClaimBatchAsync("session", "dead-process", TimeSpan.FromSeconds(-1));
        await f.Store.RecoverExpiredLeasesAsync();
        await f.Service().ProcessSessionAsync("session", default);
        var head = await f.Store.GetHeadAsync("session");
        Assert.Equal("delivery_outcome_unknown", head!.ErrorCode);
        Assert.Equal(0, f.Runtime.Resumes); Assert.Equal(0, f.Runtime.Deliveries);
    }

    [Theory]
    [InlineData("unknown", "deployment_target_unknown")]
    [InlineData("pending", "deployment_target_pending")]
    [InlineData("failed", "deployment_target_failed")]
    public async Task Exact_deployment_target_gates_recovery(string state, string error)
    {
        using var f = new Fixture("maintenance_restart");
        var target = new DeploymentVerificationTarget("redleaf", "run-one");
        await f.Store.EnqueueAsync(f.Submission() with { MetadataJson = DeploymentVerificationTarget.WithMetadata(null, target) });
        f.TargetState = new(state, error);
        await f.Service().ProcessSessionAsync("session", default);
        Assert.Equal(0, f.Runtime.Resumes); Assert.Equal(0, f.AuthorityReads);
        var head = await f.Store.GetHeadAsync("session");
        Assert.Equal(0, head!.AttemptCount); Assert.Equal(error, head.ErrorCode);
    }

    [Fact]
    public async Task Explicit_supersession_preserves_audit_and_rejects_ordinary_or_attempted_input()
    {
        using var f = new Fixture("maintenance_restart");
        var target = new DeploymentVerificationTarget("redleaf", "run-one");
        var replacement = new DeploymentVerificationTarget("redleaf", "run-two");
        var input = (await f.Store.EnqueueAsync(f.Submission() with { MetadataJson = DeploymentVerificationTarget.WithMetadata(null, target) })).Item;
        var service = f.Service();
        Assert.Null(await service.SupersedeVerificationAsync("session", input.Id, "other-owner", target, replacement, default));
        var cancelled = await service.SupersedeVerificationAsync("session", input.Id, "owner", target, replacement, default);
        Assert.Equal(SessionInputQueueState.Cancelled, cancelled!.State);
        Assert.Equal("deployment_target_superseded", cancelled.ErrorCode); Assert.Contains("run-two", cancelled.ErrorMessage);
        Assert.Equal(target, DeploymentVerificationTarget.FromMetadata(cancelled.MetadataJson));
        await f.Store.CleanupTerminalAsync(TimeSpan.FromSeconds(-1));
        Assert.Equal("deployment_target_superseded", (await f.Store.GetAsync("session", input.Id, "owner"))!.ErrorCode);
        var ordinary = (await f.Store.EnqueueAsync(f.Submission("ordinary"))).Item;
        await Assert.ThrowsAsync<SessionInputQueueStoreException>(() => service.SupersedeVerificationAsync("session", ordinary.Id, "owner", target, replacement, default));
        await f.Store.CancelAsync("session", ordinary.Id, "owner");
        var attempted = (await f.Store.EnqueueAsync(f.Submission("attempted") with { MetadataJson = DeploymentVerificationTarget.WithMetadata(null, target) })).Item;
        var batch = await f.Store.ClaimBatchAsync("session", "provider", TimeSpan.FromMinutes(1));
        await f.Store.RequeueBusyAsync(batch);
        await Assert.ThrowsAsync<SessionInputQueueStoreException>(() => service.SupersedeVerificationAsync("session", attempted.Id, "owner", target, replacement, default));
    }

    [Fact]
    public async Task Cancelled_or_failed_stop_does_not_leave_hidden_intent_and_failed_resume_retains_real_stop()
    {
        using var f = new Fixture("maintenance_restart");
        f.Runtime.Info.Status = SessionStatus.Idle;
        await f.Store.EnqueueAsync(f.Submission());
        var service = f.Service();
        var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var held = service.WithSessionLockAsync("session", async () => { entered.SetResult(); await release.Task; });
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var stopping = service.StopSessionAsync("session", () => Task.CompletedTask, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        release.SetResult(); await held;
        Assert.Equal("ready", (await service.GetSummaryAsync("session", "owner")).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopSessionAsync("session", () => throw new InvalidOperationException(), default));
        Assert.Equal("ready", (await service.GetSummaryAsync("session", "owner")).State);
        await service.StopSessionAsync("session", () => Task.CompletedTask, default);
        Assert.False(await service.ExplicitResumeAsync("session", () => Task.FromResult(false), default));
        Assert.Equal("user_stopped", (await service.GetSummaryAsync("session", "owner")).BlockedReason);
        await service.ProcessSessionAsync("session", default); Assert.Equal(0, f.Runtime.Deliveries);
        Assert.True(await service.ExplicitResumeAsync("session", () => Task.FromResult(true), default));
        await service.ProcessSessionAsync("session", default); Assert.Equal(1, f.Runtime.Deliveries);
    }

    [Fact]
    public async Task Recovery_refreshes_expired_startup_service_header_for_each_request()
    {
        using var f = new Fixture("maintenance_restart");
        var item = (await f.Store.EnqueueAsync(f.Submission())).Item;
        const string key = "recovery-service-refresh-secret-1234567890123456789";
        var options = new JwtOptions { SigningKey = key, ClockSkew = TimeSpan.Zero };
        var jwt = new JwtService(options);
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
        var expired = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            options.Issuer, options.Audience, [new System.Security.Claims.Claim("sub", "service:redcompute"), new System.Security.Claims.Claim("client_id", "redcompute")],
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-1), credentials));
        Assert.Null(jwt.ValidateToken(expired));
        var identity = new ExecutionIdentity(1, Guid.NewGuid().ToString(), new("nova", "Nova"), new("agent", "nova", "Nova"), new("user", "owner"),
            [new("accepted-input-recovery", item.Id, Route: item.MessageUid), new("ai-session", "session")]);
        var child = new ExecutionTokenIssuer(jwt, options).Issue(identity, new ExecutionPrincipal("owner", "owner@test", "Owner", ["operator"])).AccessToken;
        var calls = 0;
        using var http = new HttpClient(new TransportHandler(request => {
            var token = request.Headers.Authorization!.Parameter;
            Assert.True(token != expired); var service = jwt.ValidateToken(token!); Assert.NotNull(service);
            Assert.Equal("service:redcompute", service!.FindFirst("sub")!.Value); Assert.Equal("redcompute", service.FindFirst("client_id")!.Value);
            calls++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { accessToken = child })) });
        })) { BaseAddress = new Uri("http://127.0.0.1:1234/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", expired);
        var reader = new RedLeafSessionReader(http, null!, jwt);
        Assert.Null((await reader.RecoverAuthorityAsync(f.Runtime.Info, item, default)).ErrorCode);
        Assert.Null((await reader.RecoverAuthorityAsync(f.Runtime.Info, item, default)).ErrorCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Production_recovery_http_rejects_redirect_instead_of_following_credentials()
    {
        using var f = new Fixture("maintenance_restart"); var item = (await f.Store.EnqueueAsync(f.Submission())).Item;
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start(); var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () => {
            using var connection = await listener.AcceptTcpClientAsync();
            using var stream = connection.GetStream(); using var reader = new StreamReader(stream, leaveOpen: true);
            string? line; while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }
            var response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: https://example.com/credential-trap\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response); await stream.FlushAsync();
        });
        var jwt = new JwtService(new JwtOptions { SigningKey = "recovery-no-redirect-secret-1234567890123456789" });
        var reader = new RedLeafSessionReader($"http://127.0.0.1:{port}", jwt, null!);
        var authority = await reader.RecoverAuthorityAsync(f.Runtime.Info, item, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("recovery_host_redirect", authority.ErrorCode); Assert.Null(authority.AccessToken);
        await server; listener.Stop();
    }
    private sealed class TransportHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    internal sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("REDLEAF_SCRATCH_DIR") ?? Path.GetTempPath(), "Nova", "46682c5a", "recovery-prevention", "test-data", Guid.NewGuid().ToString("N"));
        internal readonly RedComputeConfig Config = new();
        internal readonly InputAttachmentStore Attachments;
        internal SessionInputQueueStore Store;
        internal readonly RuntimeProvider Runtime;
        internal readonly CapabilityRegistry Registry = new();
        internal bool Paused;
        internal int AuthorityReads;
        internal IJobTracker? Jobs;
        internal DeploymentVerificationState TargetState = new("succeeded");
        internal Func<UnifiedSessionInfo, SessionInputQueueItem, CancellationToken, Task<SessionRecoveryAuthority>> Authority;
        internal Fixture(string reason)
        {
            Directory.CreateDirectory(Root);
            Attachments = new(Config, Path.Combine(Root, "bytes"), Path.Combine(Root, "input.db")); Store = new(Config, Attachments);
            var provider = DispatchProxy.Create<ITestProvider, RuntimeProvider>(); Runtime = (RuntimeProvider)(object)provider;
            Runtime.Info.StopReason = reason;
            Registry.Register("ai-session", new CapabilityDefinition { Slug = "ai-session", DisplayName = "Test" }, new CapabilityConfig(), new Dictionary<string, IBackendProvider> { ["test"] = provider }, "test");
            Authority = (_, _, _) => { AuthorityReads++; return Task.FromResult(Fresh()); };
        }
        internal SessionRecoveryAuthority Fresh()
        {
            var options = new JwtOptions { SigningKey = "recovery-provider-test-secret-1234567890123456789" };
            var identity = new ExecutionIdentity(1, Guid.NewGuid().ToString(), new("nova", "Nova"), new("agent", "nova", "Nova"), new("user", "owner"), [new("accepted-input-recovery", "q_fixture")]);
            return new(new ExecutionTokenIssuer(new(options), options).Issue(identity, new ExecutionPrincipal("owner", "owner@test", "Owner", ["operator"])).AccessToken, null, false);
        }
        internal SessionInputQueueService Service() => new(Store, Attachments, Registry, Jobs!, new WebSocketBroadcaster(), (_, _) => { },
            deliveryPaused: () => Paused, recoverAuthority: (s, q, ct) => Authority(s, q, ct), deploymentTarget: _ => TargetState);
        internal SessionInputQueueSubmission Submission(string uid = "prompt") => new("session", "owner", [new("text", uid)], uid, null,
            new(1, new("redcompute", new("app", "nova", null, "Nova"), new("http", "/accepted", "POST")), new("agent", "Nova", Id: "nova"), new("user", "owner"), [], new(), JobProvenanceAssurance.Verified, DateTimeOffset.UtcNow), SessionInputDeliveryPolicy.AfterCurrent, uid, [], uid);
        public void Dispose()
        {
            using var connection = new SqliteConnection($"Data Source={Path.Combine(Root, "jobs.db")}");
            SqliteConnection.ClearPool(connection);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
    public interface ITestProvider : ISessionProvider, IBackendProvider { }
    public class RuntimeProvider : DispatchProxy
    {
        internal UnifiedSessionInfo Info = new() { Id = "session", Provider = "test", ProjectName = "Test", ProjectPath = ".", UserId = "owner", Status = SessionStatus.Stopped };
        internal int Resumes, Deliveries;
        internal SessionStatus ResumedStatus = SessionStatus.Idle;
        internal SessionInputDeliveryResult Result = SessionInputDeliveryResult.Accepted();
        internal Action<JobProvenance>? OnResume;
        internal Action? OnDelivery;
        internal bool ResumeHadFreshIdentity, DeliveryHadFreshIdentity;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_Capabilities": return SessionCapabilities.Resume | SessionCapabilities.SendMessage;
                case "get_ProviderId": return "test";
                case "get_ProviderDisplayName": return "Test";
                case nameof(ISessionProvider.GetSession): return (Info, new List<UnifiedMessageRecord>());
                case nameof(ISessionProvider.ResumeSessionAsync):
                    Resumes++; ResumeHadFreshIdentity = SessionScratch.Environment(null)?.ContainsKey("REDLEAF_EXECUTION_TOKEN") == true;
                    OnResume?.Invoke((JobProvenance)args![1]!);
                    Info.Status = ResumedStatus; Info.StopReason = null; return Task.FromResult<UnifiedSessionInfo?>(Info);
                case nameof(ISessionProvider.TrySendInputAsync):
                    Deliveries++; DeliveryHadFreshIdentity = SessionScratch.Environment(null)?.ContainsKey("REDLEAF_EXECUTION_TOKEN") == true;
                    OnDelivery?.Invoke();
                    Info.Status = SessionStatus.Active; return Task.FromResult(Result);
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
