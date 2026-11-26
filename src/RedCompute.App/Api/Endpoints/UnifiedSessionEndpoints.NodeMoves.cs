using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Services;
using RedCompute.App.Services.Nodes;
using RedCompute.App.Services.SessionBindings;
using RedCompute.Core.Nodes;
using RedCompute.Core.Providers;
using RedCompute.Core.Sessions;
using RedCompute.NodeLink;
using RedCompute.PluginSdk.Hosting;
using RedCompute.PluginSdk;

namespace RedCompute.App.Api.Endpoints;

public static partial class UnifiedSessionEndpoints
{
    internal sealed partial class RemoteSessions
    {
        private const string NodePlacementRoute = "/ai-session/sessions/{id}/placement";
        private static readonly JsonSerializerOptions NodeMoveJson = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

        internal async Task<bool> IsActiveAsync(string id, CancellationToken ct)
            => (await queue.ReadNodeBindingAsync(id, ct))?.Active.Native.Remote is not null;

        internal void MapNodeMoves(EndpointRegistry endpoints, INodeRegistry? registry,
            Func<string, (ISessionProvider? Provider, UnifiedSessionInfo? Info)> findLocal,
            Func<HttpContext, UnifiedSessionInfo, bool> canRead, Func<bool> draining)
        {
            if (registry is null) return;
            endpoints.MapGet(NodePlacementRoute,
                "Discover the AI Session's active node and generation, eligible Change Node destinations and any held activation. A signed owner or Agent can preview, apply or redirect an idle session.",
                async (HttpContext ctx, string id) =>
                {
                    try
                    {
                        await AuthorizedProvenanceAsync(ctx, NodePlacementRoute);
                        var source = await ReadMoveSourceAsync(ctx, id, registry, findLocal, canRead);
                        var availability = await ReadMoveAvailabilityAsync(ctx, source);
                        return Results.Json(new
                        {
                            generation = source.Active.Generation,
                            active = NodeChoice(source.Active.Native.Target, registry),
                            state = availability.State,
                            reason = availability.Reason,
                            canMove = availability.State is "idle" or "held",
                            destinations = await NodeChoicesAsync(ctx, source, registry),
                        });
                    }
                    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested && IsExpected(ex)) { return Failure(ex); }
                })
                .WithParam("id", "string", required: true, location: ParamLocation.Path);

            endpoints.MapPut(NodePlacementRoute,
                "Preview an idle Change Node with validateOnly, then apply it with X-Idempotency-Key. Provide the expected generation and a destination from GET. Read input-queue/{itemId} for the move receipt. A held destination can be redirected without reordering accepted prompts.",
                async (HttpContext ctx, string id) =>
                {
                    if (draining()) return Error(503, "maintenance_draining", "RedCompute is draining interactive turns");
                    var ct = ctx.RequestAborted;
                    try
                    {
                        var provenance = await AuthorizedProvenanceAsync(ctx, NodePlacementRoute);
                        var request = await ctx.Request.ReadFromJsonAsync<NodeMoveRequest>(NodeMoveJson, ct);
                        if (request?.ExpectedGeneration is not >= 0 or long.MaxValue
                            || request.Destination is not { } destination || !ControllerRemoteSessions.Identifier(destination.NodeId))
                            return Error(422, "invalid_node_move", "Supply an expected generation and an explicit destination");
                        var key = RequestKey(ctx);
                        if (!request.ValidateOnly && !ControllerRemoteSessions.Identifier(key))
                            return Error(422, "invalid_remote_request_key", "A stable X-Idempotency-Key is required");
                        var owner = NodeAccess.Subject(ctx.User)!;
                        var nodeId = registry.Resolve(registry.Find(destination.NodeId)?.Id ?? destination.NodeId);
                        var normalized = destination with { NodeId = nodeId };
                        var submission = new SessionInputQueueSubmission(id, owner, [], "Change Node", null, provenance,
                            SessionInputDeliveryPolicy.AfterCurrent, Guid.NewGuid().ToString("N"), [], key);
                        IResult? result = null;
                        var signal = false;
                        await queue.WithSessionLockAsync(id, async () =>
                        {
                            var source = await ReadMoveSourceAsync(ctx, id, registry, findLocal, canRead);
                            if (!request.ValidateOnly && await queue.FindExistingNodeMoveAsync(submission,
                                change => change.ExpectedGeneration == request.ExpectedGeneration
                                    && MatchesNodeSelection(change.Destination, normalized), ct) is { } replay)
                            {
                                result = MoveAdmission(replay, true);
                                return;
                            }

                            if (source.Active.Generation != request.ExpectedGeneration)
                            {
                                result = Error(409, "binding_changed", "Read the active node generation before applying a move");
                                return;
                            }
                            var availability = await ReadMoveAvailabilityAsync(ctx, source);
                            if (availability.State is not ("idle" or "held"))
                            {
                                result = Error(409, availability.Reason ?? "binding_source_busy", "The existing source must be idle");
                                return;
                            }
                            var target = await ResolveMoveTargetAsync(ctx, source, normalized, registry, key ?? Guid.NewGuid().ToString("N"));
                            if (request.ValidateOnly)
                            {
                                result = Results.Json(new { eligible = true, generation = source.Active.Generation,
                                    proposed = NodeChoice(target, registry), wouldQueue = true, replacesHeld = availability.State == "held" });
                                return;
                            }
                            var admitted = await queue.AdmitAsync(submission with
                            {
                                BindingChange = new(source.Active.Generation, target, ChangesNode: true, ReplacesHeldBinding: availability.State == "held"),
                            }, ct, waitForDelivery: false);
                            signal = admitted.Item.State == SessionInputQueueState.Pending;
                            result = MoveAdmission(admitted.Item, admitted.Existing);
                        }, ct);
                        if (signal) queue.Signal(id);
                        return result!;
                    }
                    catch (SessionInputQueueStoreException ex) { return QueueError(ex); }
                    catch (Exception ex) when (!ct.IsCancellationRequested && IsExpected(ex)) { return Failure(ex); }
                })
                .WithParam("id", "string", required: true, location: ParamLocation.Path)
                .WithParam("expectedGeneration", "integer", required: true, location: ParamLocation.Body)
                .WithParam("destination", "object", required: true, location: ParamLocation.Body,
                    description: "nodeId and optional sessionRuntime containing id, workspace, revision and providerInstanceId.")
                .WithParam("validateOnly", "boolean", location: ParamLocation.Body,
                    description: "Check the source and exact destination without admitting a move.")
                .WithParam("X-Idempotency-Key", "string", location: ParamLocation.Header,
                    description: "Required for apply. Reuse the same request and key to read the original receipt.");
        }

        private async Task<NodeMoveSource> ReadMoveSourceAsync(HttpContext ctx, string id, INodeRegistry registry,
            Func<string, (ISessionProvider? Provider, UnifiedSessionInfo? Info)> findLocal, Func<HttpContext, UnifiedSessionInfo, bool> canRead)
        {
            var commands = queue.BindingCommands ?? throw new ControllerRemoteSessionException(422, "binding_unsupported");
            var snapshot = await queue.ReadNodeBindingAsync(id, ctx.RequestAborted);
            var (provider, info) = snapshot?.Active.Native.Remote is null ? findLocal(id) : (null, await ReadOwnedAsync(ctx, id));
            if (info is null || info.UserId != NodeAccess.Subject(ctx.User) || !canRead(ctx, info))
                throw new ControllerRemoteSessionException(404, "session_not_found");
            if (info.Confidential || SessionExecutionProfile.IsConversationOnly(info.ExecutionProfile))
                throw new ControllerRemoteSessionException(422, "binding_unsupported");

            var active = snapshot?.Active;
            if (active is null)
            {
                if (provider is not ISessionInferenceSettings || commands.InstanceFor(provider) is not { } instance
                    || info.ProviderSessionId is not { Length: > 0 } native)
                    throw new ControllerRemoteSessionException(422, "binding_unsupported");
                active = new(0, new(id, native, new(info.Provider, instance, registry.Resolve(info.NodeId),
                    info.Model ?? "", info.Effort), info.StartedAt), 0);
            }
            var options = provider is ISessionInferenceSettings local
                ? await local.GetInferenceOptionsAsync(id, ctx.RequestAborted) : null;
            var model = options?.Model ?? active.Native.Remote?.Thread.Model ?? info.Model ?? active.Native.Target.Model;
            var effort = options is not null ? options.Effort : active.Native.Remote?.Thread.Effort ?? info.Effort ?? active.Native.Target.Effort;
            if (string.IsNullOrWhiteSpace(model))
                throw new ControllerRemoteSessionException(422, "binding_settings_unavailable");
            var revision = active.Native.Remote?.Placement.Revision
                ?? await HostGit.ReadCleanHeadAsync(registry.HostFor(registry.LocalNodeId), info.ProjectPath, ctx.RequestAborted);
            return new(info, active, snapshot?.Threads ?? [active], active.Native.Target with { Model = model, Effort = effort }, revision);
        }

        private async Task<NodeMoveAvailability> ReadMoveAvailabilityAsync(HttpContext ctx, NodeMoveSource source)
        {
            var ct = ctx.RequestAborted;
            var logical = source.Info.Id;
            if (source.Revision is null) return new("busy", "binding_workspace_unverified");
            if (source.Active.Native.Remote is { } remote)
            {
                if (await queue.HasUnsettledRemoteDispatchAsync(logical, ct))
                    return new("busy", "binding_source_busy");
                if (remote.ActivationPending)
                {
                    var operation = await queue.GetAsync(logical, remote.ActivationMoveId!, remote.Scope.OwnerId, ct);
                    return new("held", operation?.ErrorCode ?? "binding_activation_pending");
                }
                try
                {
                    if (!await controller.IsMoveSourceIdleAsync(ctx.User, remote, Guid.NewGuid().ToString("N"), ct))
                        return new("busy", "binding_source_busy");
                }
                catch (NodeHubException ex) when (ex.Code is "node_not_found" or "node_unavailable" or "session_runtime_unavailable"
                    or "secure_node_link_required")
                {
                    return new("held", ex.Code);
                }
                catch (ControllerRemoteSessionException ex) when (ex.Status is 404 or >= 500
                    || ex.Code == "runtime_identity_mismatch")
                {
                    return new("held", ex.Code);
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested)
                {
                    return new("held", "remote_runtime_unavailable");
                }
            }
            else if (queue.BindingCommands is not { } commands
                || !NodeSessionBindingRuntime.LocalSourceIdle(commands, source.Active.Native, source.Info.UserId!))
                return new("busy", "binding_source_busy");

            return await queue.HasQueuedInputAsync(logical, ct) ? new("busy", "input_queued") : new("idle");
        }

        private async Task<BindingTarget> ResolveMoveTargetAsync(HttpContext ctx, NodeMoveSource source, NodeMoveDestination destination,
            INodeRegistry registry, string key)
        {
            var ct = ctx.RequestAborted;
            var live = source.Settings;
            if (destination.NodeId == live.NodeId
                && destination.SessionRuntime?.Id == (live.Runtime?.RuntimeId ?? source.Active.Native.Remote?.Scope.RuntimeId))
                throw new ControllerRemoteSessionException(422, "binding_already_active");
            if (destination.NodeId == registry.LocalNodeId)
            {
                if (destination.SessionRuntime is not null)
                    throw new ControllerRemoteSessionException(422, "invalid_node_move");
                var prior = source.Threads.LastOrDefault(thread => thread.Native.Remote is null
                    && thread.Native.Target.NodeId == registry.LocalNodeId && thread.Native.Target.ProviderId == live.ProviderId);
                if (prior is null || queue.BindingCommands is not { } commands
                    || !NodeSessionBindingRuntime.LocalSourceIdle(commands, prior.Native, source.Info.UserId!)
                    || await LocalMoveRevisionAsync(commands, prior.Native, registry, ct) != source.Revision)
                    throw new ControllerRemoteSessionException(422, "binding_destination_unavailable");
                return prior.Native.Target with { Model = live.Model, Effort = live.Effort };
            }
            if (destination.SessionRuntime is not { } runtime)
                throw new ControllerRemoteSessionException(422, "invalid_remote_placement");
            if (runtime.Revision != source.Revision)
                throw new ControllerRemoteSessionException(422, "binding_workspace_mismatch");
            if (registry is NodeRegistry nodes && !nodes.RuntimesOf(destination.NodeId).Any(candidate =>
                candidate.Id == runtime.Id && candidate.Capabilities.Contains(SessionRuntimeProtocol.BindingMoveCapability)))
                throw new ControllerRemoteSessionException(422, "session_runtime_move_unsupported");
            var target = new RemoteSessionPlacement(destination.NodeId, runtime.Id, runtime.Workspace, runtime.Revision,
                live.ProviderId, runtime.ProviderInstanceId, live.Model, live.Effort);
            var preview = await controller.ValidateMoveAsync(ctx.User, source.Info.Id, checked(source.Active.Generation + 1), key,
                target, source.Info.MaxAccessLevel ?? SessionAccessLevels.Ask, ct);
            return new(live.ProviderId, runtime.ProviderInstanceId, destination.NodeId, live.Model, live.Effort,
                new(runtime.Id, runtime.Workspace, runtime.Revision, preview.AccessLevel, preview.MaxAccessLevel));
        }

        private async Task<object[]> NodeChoicesAsync(HttpContext ctx, NodeMoveSource source, INodeRegistry registry)
        {
            var ct = ctx.RequestAborted;
            var live = source.Settings;
            if (source.Revision is null) return [];
            var choices = new List<BindingTarget>();
            if (live.NodeId != registry.LocalNodeId && queue.BindingCommands is { } commands
                && source.Threads.LastOrDefault(thread => thread.Native.Remote is null
                    && thread.Native.Target.NodeId == registry.LocalNodeId
                    && thread.Native.Target.ProviderId == live.ProviderId) is { } earlier
                && NodeSessionBindingRuntime.LocalSourceIdle(commands, earlier.Native, source.Info.UserId!)
                && await LocalMoveRevisionAsync(commands, earlier.Native, registry, ct) == source.Revision)
                choices.Add(earlier.Native.Target);
            if (registry is not NodeRegistry nodes) return choices.Select(target => NodeChoice(target, registry)).ToArray();

            var candidates = nodes.Nodes.Where(node => node.Kind == NodeKind.Remote && NodeAccess.Allows(ctx.User, node, NodeUse.Place)
                    && nodes.Placement(node).Eligible)
                .SelectMany(node => nodes.RuntimesOf(node.Id)
                    .Where(runtime => runtime is { Kind: SessionRuntimeProtocol.Kind, Managed: HostRuntimeManagement.Attach,
                            Enabled: true, Status: BackendStatus.Running, Ports.Count: 1 }
                        && runtime.Capabilities.Contains(SessionRuntimeProtocol.BindingMoveCapability, StringComparer.Ordinal))
                    .Select(runtime => (Node: node, Runtime: runtime)))
                .Where(candidate => candidate.Node.Id != live.NodeId
                    || candidate.Runtime.Id != (live.Runtime?.RuntimeId ?? source.Active.Native.Remote?.Scope.RuntimeId))
                .Take(12).ToArray();
            var remote = await Task.WhenAll(candidates.Select(async candidate =>
            {
                try
                {
                    var workspaces = await controller.ListMoveWorkspacesAsync(ctx.User, source.Info.Id,
                        checked(source.Active.Generation + 1), candidate.Node.Id, candidate.Runtime.Id, ct);
                    return workspaces.Where(workspace => workspace.ProviderId == live.ProviderId && workspace.Revision == source.Revision
                            && SessionAccessLevels.Rank(workspace.MaxAccessLevel)
                                <= SessionAccessLevels.Rank(source.Info.MaxAccessLevel ?? SessionAccessLevels.Ask))
                        .Select(workspace => new BindingTarget(live.ProviderId, workspace.ProviderInstanceId, candidate.Node.Id,
                            live.Model, live.Effort, new(candidate.Runtime.Id, workspace.Workspace, workspace.Revision,
                                workspace.AccessLevel, workspace.MaxAccessLevel)))
                        .ToArray();
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsExpected(ex)) { return []; }
            }));
            choices.AddRange(remote.SelectMany(targets => targets));
            return choices.Select(target => NodeChoice(target, registry)).ToArray();
        }

        private static Task<string?> LocalMoveRevisionAsync(SessionBindingCommands commands, NativeSessionBinding binding,
            INodeRegistry registry, CancellationToken ct)
            => HostGit.ReadCleanHeadAsync(registry.HostFor(registry.LocalNodeId),
                commands.GetNative(binding.Target)?.GetSessionInfo(binding.RuntimeSessionId)?.ProjectPath, ct);

        private static object NodeChoice(BindingTarget target, INodeRegistry registry) => new
        {
            nodeId = target.NodeId,
            name = registry.Find(target.NodeId)?.Name ?? target.NodeId,
            sessionRuntime = target.Runtime is { } runtime
                ? new NodeMoveRuntime(runtime.RuntimeId, runtime.Workspace, runtime.Revision, target.ProviderInstanceId) : null,
        };

        private static bool MatchesNodeSelection(BindingTarget target, NodeMoveDestination destination)
            => target.NodeId == destination.NodeId && (target.Runtime is { } runtime
                ? destination.SessionRuntime == new NodeMoveRuntime(runtime.RuntimeId, runtime.Workspace, runtime.Revision, target.ProviderInstanceId)
                : destination.SessionRuntime is null);

        private static IResult MoveAdmission(SessionInputQueueItem item, bool existing)
            => Results.Json(new { existing, operation = PublicQueueItem(item) }, statusCode: StatusCodes.Status202Accepted);

        internal static UnifiedSessionInfo ProjectRemoteBinding(UnifiedSessionInfo info, SessionBindingSnapshot snapshot)
        {
            var binding = snapshot.Active.Native.Remote!;
            var view = new UnifiedSessionInfo
            {
                Id = info.Id, Provider = binding.Placement.ProviderId, ProjectName = info.ProjectName, ProjectPath = info.ProjectPath,
                NodeId = binding.Scope.NodeId, ProviderEntity = binding.Placement.ProviderInstanceId,
                ProviderSessionId = binding.Thread.NativeThreadId, Model = binding.Thread.Model ?? info.Model,
                Effort = binding.Thread.Effort ?? info.Effort, JobId = info.JobId, Status = info.Status,
                StopReason = info.StopReason, StoppedAt = info.StoppedAt, QualityTier = info.QualityTier,
            }.AsLogicalSession(info.Id, snapshot.Threads[0].Native.Target.ProviderId);
            var projected = info.WithActiveBinding(view);
            projected.Placement = JsonSerializer.SerializeToNode(new
            {
                nodeId = binding.Scope.NodeId,
                sessionRuntime = new NodeMoveRuntime(binding.Scope.RuntimeId, binding.Placement.Workspace,
                    binding.Placement.Revision, binding.Placement.ProviderInstanceId),
            }, NodeMoveJson)!.AsObject();
            projected.AccessLevel = binding.Preview.AccessLevel;
            projected.MaxAccessLevel = binding.Preview.MaxAccessLevel;
            return projected;
        }

        private sealed record NodeMoveSource(UnifiedSessionInfo Info, SessionBinding Active,
            IReadOnlyList<SessionBinding> Threads, BindingTarget Settings, string? Revision);
        private sealed record NodeMoveAvailability(string State, string? Reason = null);
        private sealed record NodeMoveRequest(long? ExpectedGeneration, NodeMoveDestination? Destination, bool ValidateOnly = false);
        private sealed record NodeMoveDestination(string NodeId, NodeMoveRuntime? SessionRuntime);
        private sealed record NodeMoveRuntime(string Id, string Workspace, string Revision, string ProviderInstanceId);
    }
}
