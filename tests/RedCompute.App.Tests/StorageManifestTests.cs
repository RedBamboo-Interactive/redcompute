using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Storage;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// Data relocation phase 3A: RedCompute declares the class of every location it owns
/// (<c>GET /storage/manifest</c>), so the kernel never guesses what a move may copy.
/// </summary>
public sealed class StorageManifestTests
{
    private static ComputeStorage Storage(string root)
        => ComputeStorage.FromEnvironment(
            name => name == ComputeStorage.DataRootVariable ? root : null, Environment.GetFolderPath);

    private static string Root => Path.Combine(Path.GetTempPath(), "redcompute-manifest-root");

    [Fact]
    public void EveryLocationIsRelativeSlashSeparatedAndClassified()
    {
        var locations = Storage(Root).Locations();

        Assert.NotEmpty(locations);
        Assert.Equal(locations.Count, locations.Select(l => l.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(locations.Count, locations.Select(l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var location in locations)
        {
            Assert.False(Path.IsPathRooted(location.Path), location.Path);
            Assert.DoesNotContain((char)92, location.Path);
            Assert.DoesNotContain("..", location.Path.Split('/'));
            Assert.NotEmpty(location.Path);
            Assert.Contains(location.Class, new[] { StorageClass.Durable, StorageClass.Rebuildable, StorageClass.Disposable });
            Assert.Contains(location.Mutability, new[] { StorageMutability.Immutable, StorageMutability.Mutable });
        }
    }

    [Fact]
    public void EveryPathPropertyOfComputeStorageIsDeclared_SoANewLocationCannotBeForgotten()
    {
        var storage = Storage(Root);
        var declared = storage.Locations().Select(l => Path.GetFullPath(Path.Combine(storage.DataRoot, l.Path))).ToList();
        var properties = typeof(ComputeStorage).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.Name is not (nameof(ComputeStorage.DataRoot) or nameof(ComputeStorage.SuiteRoot)));

        foreach (var property in properties)
        {
            var path = Path.GetFullPath((string)property.GetValue(storage)!);
            if (string.Equals(path, Path.GetFullPath(storage.DataRoot), StringComparison.OrdinalIgnoreCase)) continue; // a folder holding a declared file
            Assert.True(
                declared.Any(d => string.Equals(path, d, StringComparison.OrdinalIgnoreCase)
                                  || path.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                $"ComputeStorage.{property.Name} ({path}) is not declared in Locations(): give it a class.");
        }
        Assert.Contains(declared, d => d.EndsWith(Path.Combine("plugins"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheFileNamesTheManifestPinsAreTheOnesTheStoresUse()
    {
        Assert.Equal(NodeIdentityStore.FileName, ComputeStorage.NodeIdentityFileName);
        Assert.Equal("long-lived-processes.json", ComputeStorage.LongLivedProcessesFileName);
    }

    [Fact]
    public void ProcessStateAndMachineBoundFilesAreDisposable_AndTheBulkDataIsImmutable()
    {
        var byName = Storage(Root).Locations().ToDictionary(l => l.Name);

        Assert.Equal(StorageClass.Disposable, byName["session-processes"].Class);
        Assert.Equal(StorageClass.Disposable, byName["planned-restart"].Class);
        Assert.Equal(StorageClass.Disposable, byName["tunnel-state"].Class);
        Assert.Equal(StorageClass.Disposable, byName["defender-exclusions-marker"].Class);
        Assert.Equal(StorageClass.Rebuildable, byName["telemetry"].Class);
        Assert.Equal((StorageClass.Durable, StorageMutability.Immutable), (byName["outputs"].Class, byName["outputs"].Mutability));
        Assert.Equal((StorageClass.Durable, StorageMutability.Mutable), (byName["database"].Class, byName["database"].Mutability));
        Assert.Equal("input-attachments/attachments.db", byName["attachments-database"].Path);
    }

    [Fact]
    public async Task TheManifestEndpoint_AnswersAbsolutePathFree_AndSchemaVersion1()
    {
        var root = Root;
        var storage = Storage(root);
        var builder = WebApplication.CreateSlimBuilder();
        await using var app = builder.Build();
        StorageEndpoints.Map(new EndpointRegistry(app), storage: () => storage);
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == StorageEndpoints.ManifestPath);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();

        await route.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(StorageEndpoints.ManifestSchemaVersion, body.RootElement.GetProperty("schemaVersion").GetInt32());
        var locations = body.RootElement.GetProperty("locations");
        Assert.Equal(storage.Locations().Count, locations.GetArrayLength());
        var outputs = locations.EnumerateArray().Single(l => l.GetProperty("name").GetString() == "outputs");
        Assert.Equal("outputs", outputs.GetProperty("path").GetString());
        Assert.Equal("durable", outputs.GetProperty("class").GetString());
        Assert.Equal("immutable", outputs.GetProperty("mutability").GetString());
        var text = body.RootElement.GetRawText();
        Assert.DoesNotContain(Path.GetFileName(root), text);
        Assert.DoesNotContain(":" + (char)92, text);
        Assert.DoesNotContain(":/", text);
    }

    [Fact]
    public void The_placement_shadow_evidence_is_declared_durable_under_its_real_file_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "rc-manifest-" + Guid.NewGuid().ToString("N"));
        var storage = new ComputeStorage(root, Path.Combine(root, "suite"), isRelocated: true);
        var shadow = Assert.Single(storage.Locations(), l => l.Name == "placement-shadow");
        Assert.Equal(RedCompute.App.Services.Placement.PlacementShadowStore.FileName, shadow.Path);
        Assert.Equal(StorageClass.Durable, shadow.Class);
        Assert.Equal(StorageMutability.Mutable, shadow.Mutability);
    }
}
