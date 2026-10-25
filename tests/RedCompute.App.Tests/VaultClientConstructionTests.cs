using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Auth.Vault;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

/// <summary>
/// With Leaf Vault down at boot, building RedCompute's long-lived RedLeaf clients must not throw: a badge
/// is only needed when a call goes out, and then it fails that call, not the process.
/// </summary>
public class VaultClientConstructionTests
{
    private sealed class DeadVault : IBadgeVault
    {
        public Task<VaultSignedBadge> SignAsync(JsonElement claims, int lifetimeSeconds, CancellationToken ct)
            => throw new VaultUnavailableException("Leaf Vault unavailable");
        public Task<IReadOnlyList<VaultJwk>> JwksAsync(CancellationToken ct)
            => throw new VaultUnavailableException("Leaf Vault unavailable");
        public Task<VaultReachability> ProbeAsync(CancellationToken ct) => Task.FromResult(VaultReachability.Unavailable);
    }

    private static JwtService DeadVaultJwt() => new(new JwtOptions { Vault = new VaultBadgeAuthority(new DeadVault()) });

    [Fact]
    public async Task Repository_validator_is_built_without_a_badge_and_reports_the_outage_as_unavailable()
    {
        var validator = new RepositoryReferenceValidator("http://127.0.0.1:1/", DeadVaultJwt());

        var result = await validator.ValidateAsync(Guid.NewGuid().ToString(), "C:/x");

        Assert.Equal(503, result.StatusCode);
        Assert.Contains("Leaf Vault unavailable", result.Error);
    }

    [Fact]
    public void Session_reader_is_built_without_a_badge()
        => Assert.NotNull(new RedLeafSessionReader("http://127.0.0.1:1/", DeadVaultJwt(), null!));
}
