using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Auth;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using RedCompute.Plugin.ComfyUI;
using Xunit;

namespace RedCompute.App.Tests;

// Defaults that name a Leaf service on this machine use 127.0.0.1, never localhost (IPv6-first on Windows,
// two seconds per fresh connection). Configured addresses are used exactly as configured.
public sealed class LoopbackDefaultsTests
{
    [Fact]
    public void The_kernel_reads_AppHost_registers_resolve_to_the_IPv4_address()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PermissionDataSourceOptions()); // what AddAppHostAuth registers
        Assert.Equal("http://localhost:18804", new PermissionDataSourceOptions().RedLeafBaseUrl); // AppHost's default today

        LoopbackDefaults.ReplaceKernelDefault(services);

        using var provider = services.BuildServiceProvider();
        Assert.Equal("http://127.0.0.1:18804", provider.GetRequiredService<PermissionDataSourceOptions>().RedLeafBaseUrl);
    }

    [Theory]
    [InlineData("http://127.0.0.1:18804")]
    [InlineData("http://localhost:19999")]
    [InlineData("http://kernel.internal:18804")]
    public void A_kernel_address_that_is_not_the_localhost_default_is_kept(string configured)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new PermissionDataSourceOptions { RedLeafBaseUrl = configured });

        LoopbackDefaults.ReplaceKernelDefault(services);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(configured, provider.GetRequiredService<PermissionDataSourceOptions>().RedLeafBaseUrl);
        Assert.Single(services, d => d.ServiceType == typeof(PermissionDataSourceOptions));
    }

    [Fact]
    public void The_suite_defaults_name_127_0_0_1()
    {
        var config = new RedComputeConfig();
        Assert.Equal("http://127.0.0.1:18804", config.RedLeafUrl);
        Assert.Equal("http://127.0.0.1", LoopbackDefaults.BaseAddress);
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:8188")]
    [InlineData("localhost", "http://localhost:8188")]
    [InlineData("172.20.1.5", "http://172.20.1.5:8188")]
    public void ComfyUI_defaults_to_127_0_0_1_and_keeps_a_configured_host(string? host, string expected)
    {
        var config = new ProviderConfig { Type = "ComfyUI" };
        if (host is not null)
            config.Extra = new() { ["Host"] = host };
        var provider = new ComfyUIProvider(config, "image-gen", _ => { });

        var baseUrl = (string)typeof(ComfyUIProvider).GetProperty("BaseUrl", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(provider)!;
        Assert.Equal(expected, baseUrl);
    }
}
