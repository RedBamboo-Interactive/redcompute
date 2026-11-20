using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedCompute.App.Api.Endpoints;
using RedCompute.App.Services.Nodes;
using RedCompute.Core.Nodes;
using RedCompute.PluginSdk.Hosting.Local;
using Xunit;
using static RedCompute.App.Tests.NodeTestKit;

namespace RedCompute.App.Tests;

/// <summary>
/// A node's look (Laurent, 2026-11-20): the kernel's node entity keeps an icon and a colour, RedCompute
/// carries them through GET /nodes and reports the firmware's chassis hint the kernel draws a default
/// icon from. An older kernel has none of them, and nothing changes then.
/// </summary>
public sealed class NodeLookTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "rc-node-look-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    [Theory]
    [InlineData(3, "desktop")]
    [InlineData(7, "desktop")]
    [InlineData(13, "all-in-one")]
    [InlineData(9, "laptop")]
    [InlineData(10, "laptop")]
    [InlineData(31, "laptop")]
    [InlineData(30, "tablet")]
    [InlineData(35, "mini")]
    [InlineData(23, "server")]
    [InlineData(0x80 | 9, "laptop")] // the lock bit
    [InlineData(1, null)]            // "Other", what many VMs say
    [InlineData(2, null)]            // "Unknown"
    [InlineData(99, null)]
    public void The_smbios_chassis_type_becomes_a_hint(int type, string? hint)
        => Assert.Equal(hint, LocalChassis.FromSmbiosType(type));

    [Fact]
    public void The_chassis_type_is_read_from_the_raw_smbios_table_after_the_structures_before_it()
    {
        // Type 0 (BIOS) with two strings, type 1 (System) with none, then type 3 (System Enclosure), then the end.
        var structures = new List<byte>();
        structures.AddRange([0, 0x18, 0x00, 0x00]);
        structures.AddRange(new byte[0x18 - 4]);
        structures.AddRange("Vendor\0Version\0\0"u8.ToArray());
        structures.AddRange([1, 0x1B, 0x01, 0x00]);
        structures.AddRange(new byte[0x1B - 4]);
        structures.AddRange([0, 0]);
        structures.AddRange([3, 0x16, 0x02, 0x00, 1, 0x80 | 10]);
        structures.AddRange(new byte[0x16 - 6]);
        structures.AddRange("Maker\0\0"u8.ToArray());
        structures.AddRange([127, 4, 0xFF, 0xFF, 0, 0]);
        var raw = Raw(structures);

        Assert.Equal(10, LocalChassis.ChassisTypeFromRawSmbios(raw));
        Assert.Equal("laptop", LocalChassis.FromSmbiosType(LocalChassis.ChassisTypeFromRawSmbios(raw)!.Value));
    }

    [Fact]
    public void This_machines_firmware_is_read_without_throwing_and_gives_a_known_hint_or_none()
    {
        Assert.Contains(LocalChassis.Current, new string?[] { null, "desktop", "all-in-one", "laptop", "tablet", "mini", "server" });
        Assert.Equal(LocalChassis.Current, new LocalExecutionHost("node_test").Platform.Info.Chassis);
    }

    [Fact]
    public void A_table_without_an_enclosure_or_cut_short_has_no_chassis()
    {
        Assert.Null(LocalChassis.ChassisTypeFromRawSmbios([]));
        Assert.Null(LocalChassis.ChassisTypeFromRawSmbios(Raw([0, 4, 0, 0, 0, 0, 127, 4, 0, 0, 0, 0])));
        // A type 3 header whose formatted area runs past the end.
        Assert.Null(LocalChassis.ChassisTypeFromRawSmbios(Raw([3, 0x16, 0, 0, 1])));
        // A formatted length below the header's own 4 bytes.
        Assert.Null(LocalChassis.ChassisTypeFromRawSmbios(Raw([0, 2, 0, 0, 0, 0])));
        // The header claims more table than there is: only what is there is read.
        var claimsMore = Raw([3, 0x16, 0, 0, 1, 9]);
        BinaryPrimitives.WriteUInt32LittleEndian(claimsMore.AsSpan(4), 4096);
        Assert.Null(LocalChassis.ChassisTypeFromRawSmbios(claimsMore));
    }

    [Fact]
    public void The_kernels_icon_colour_and_chassis_are_read_and_anything_but_a_token_is_dropped()
    {
        var item = JsonNode.Parse($$"""
            {"id":"{{RemoteId(1)}}","name":"Studio","kind":"remote","status":"online","ownerId":"{{Owner}}","confirmed":true,
             "platform":{"os":"linux","chassis":"server"},"icon":"hard-drives","color":"violet"}
            """);
        var node = KernelNode.Parse(item)!;
        Assert.Equal(("hard-drives", "violet", "server"), (node.Icon, node.Color, node.Chassis));

        item!["icon"] = "ph-bold ph-x\" onmouseover=\"x";
        item["color"] = "#ff0000";
        ((JsonObject)item["platform"]!)["chassis"] = new string('a', 41);
        node = KernelNode.Parse(item)!;
        Assert.Equal((null, null, null), (node.Icon, node.Color, node.Chassis));

        // An older kernel: no such members.
        var older = KernelNode.Parse(JsonNode.Parse($$"""{"id":"{{RemoteId(2)}}","kind":"remote"}"""))!;
        Assert.Equal((null, null, null), (older.Icon, older.Color, older.Chassis));
    }

    [Fact]
    public async Task Get_nodes_carries_the_kernels_icon_and_colour_for_node_zero_and_remote_nodes()
    {
        var registry = Registry(_cache);
        var updated = new List<string>();
        registry.Updated += updated.Add;
        registry.ApplyKernelNodes([
            Kernel(RemoteId(1), "Studio") with { Icon = "graphics-card", Color = "violet", Chassis = "desktop" },
            new KernelNode(LocalId, "This computer", KernelNode.LocalKind, "online", null, Owner, true, null, null, null, null, null, null,
                null, null, Icon: "desktop-tower", Color: "blue"),
        ]);

        Assert.Contains(LocalId, updated);
        Assert.Equal(("desktop-tower", "blue"), (registry.Local.Icon, registry.Local.Color));
        var studio = registry.Find(RemoteId(1))!;
        Assert.Equal(("graphics-card", "violet", "desktop"), (studio.Icon, studio.Color, studio.Chassis));

        var local = await NodeEndpoints.BuildAsync(registry, registry.Local, new(), null);
        var remote = await NodeEndpoints.BuildAsync(registry, studio, new(), null);
        var json = JsonSerializer.SerializeToNode(new[] { local, remote }, JsonSerializerOptions.Web)!.AsArray();
        Assert.Equal(("desktop-tower", "blue"), (json[0]!["icon"]!.GetValue<string>(), json[0]!["color"]!.GetValue<string>()));
        Assert.Equal(("graphics-card", "violet"), (json[1]!["icon"]!.GetValue<string>(), json[1]!["color"]!.GetValue<string>()));
        Assert.Equal("desktop", json[1]!["platform"]!["chassis"]!.GetValue<string>());

        // The same list again changes nothing; a kernel that drops them (an older one after a rollback) clears them.
        updated.Clear();
        registry.ApplyKernelNodes([
            Kernel(RemoteId(1), "Studio") with { Icon = "graphics-card", Color = "violet", Chassis = "desktop" },
            new KernelNode(LocalId, "This computer", KernelNode.LocalKind, "online", null, Owner, true, null, null, null, null, null, null,
                null, null, Icon: "desktop-tower", Color: "blue"),
        ]);
        Assert.Empty(updated);
        registry.ApplyKernelNodes([Kernel(RemoteId(1), "Studio")]);
        Assert.Equal((null, null), (registry.Find(RemoteId(1))!.Icon, registry.Find(RemoteId(1))!.Color));
        Assert.Equal(("desktop-tower", "blue"), (registry.Local.Icon, registry.Local.Color));
    }

    [Fact]
    public async Task An_older_kernel_gives_null_icon_and_colour_and_the_view_says_so()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        var view = await NodeEndpoints.BuildAsync(registry, registry.Find(RemoteId(1))!, new(), null);
        Assert.Equal((null, null), (view.Icon, view.Color));
        var zero = await NodeEndpoints.BuildAsync(registry, registry.Local, new(), null);
        Assert.Equal((null, null, null), (zero.Icon, zero.Color, zero.Platform.Chassis));
    }

    [Fact]
    public void The_look_survives_a_restart_through_the_cache()
    {
        var first = Registry(_cache);
        first.ApplyKernelNodes([Kernel(RemoteId(1)) with { Icon = "laptop", Color = "amber", Chassis = "laptop" }]);

        var second = Registry(_cache);
        var node = second.Find(RemoteId(1))!;
        Assert.Equal(("laptop", "amber", "laptop"), (node.Icon, node.Color, node.Chassis));
    }

    [Fact]
    public void The_nodes_own_describe_brings_its_chassis()
    {
        var registry = Registry();
        registry.ApplyKernelNodes([Kernel(RemoteId(1))]);
        var describe = NodeHub.ParseDescribe(JsonDocument.Parse("""
            {"platform":{"os":"linux","arch":"x64","osDescription":"Fedora","chassis":"laptop"},"machineName":"leaflinux",
             "agent":{"version":"0.1.0"}}
            """).RootElement);
        Assert.Equal("laptop", describe.Chassis);
        registry.ApplyDescribe(RemoteId(1), describe);
        Assert.Equal("laptop", registry.Find(RemoteId(1))!.Chassis);

        // An older agent says nothing: what was known stays.
        registry.ApplyDescribe(RemoteId(1), NodeHub.ParseDescribe(JsonDocument.Parse("""{"platform":{"os":"linux"}}""").RootElement));
        Assert.Equal("laptop", registry.Find(RemoteId(1))!.Chassis);
    }

    [Fact]
    public async Task Node_zero_and_presence_report_the_chassis_only_when_the_firmware_says()
    {
        var kernel = new NodeRegistryRemoteTests.FakeKernel();
        var withChassis = new NodeRegistry(Identity, NodeRegistryTests.Platform() with { Chassis = "desktop" }, "0.2.0", Now.AddHours(-1),
            () => Now);
        await using (var sync = new KernelNodeSync(withChassis, kernel, _ => { }))
            Assert.True(await sync.PutLocalAsync(CancellationToken.None));
        Assert.Equal("desktop", kernel.Local[^1]["platform"]!["chassis"]!.GetValue<string>());
        // RedCompute never sends an icon or a colour: they are the owner's, kept by the kernel.
        Assert.Null(kernel.Local[^1]["icon"]);
        Assert.Null(kernel.Local[^1]["color"]);

        var without = Registry();
        await using (var sync = new KernelNodeSync(without, kernel, _ => { }))
            Assert.True(await sync.PutLocalAsync(CancellationToken.None));
        Assert.False(((JsonObject)kernel.Local[^1]["platform"]!).ContainsKey("chassis"));

        without.ApplyKernelNodes([Kernel(RemoteId(1)) with { Chassis = "server" }]);
        var presence = KernelNodeSync.PresenceBody(without.Find(RemoteId(1))!)!;
        Assert.Equal("server", presence["platform"]!["chassis"]!.GetValue<string>());
        Assert.Null(presence["icon"]);
    }

    private static byte[] Raw(IReadOnlyCollection<byte> structures)
    {
        var raw = new byte[8 + structures.Count];
        raw[1] = 3; // SMBIOS 3.x
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), (uint)structures.Count);
        structures.ToArray().CopyTo(raw, 8);
        return raw;
    }
}
