using System.Security.Cryptography;
using RedCompute.App.Services;
using RedCompute.Core.Configuration;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class InputAttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redcompute-attachment-tests", Guid.NewGuid().ToString("N"));

    private InputAttachmentStore Store(int ttlMinutes = 60, long maxFile = 1024, int maxCount = 4, long maxTurn = 2048)
    {
        Directory.CreateDirectory(_root);
        return new InputAttachmentStore(new RedComputeConfig
        {
            InputAttachmentTtlMinutes = ttlMinutes,
            InputAttachmentMaxFileSizeBytes = maxFile,
            InputAttachmentMaxCount = maxCount,
            InputAttachmentMaxTurnSizeBytes = maxTurn,
        }, Path.Combine(_root, "bytes"), Path.Combine(_root, "attachments.db"));
    }

    [Fact]
    public async Task UploadUsesGeneratedPathAndSanitizesHostileFilename()
    {
        var store = Store();
        var bytes = "proposal"u8.ToArray();

        var attachment = await store.UploadBytesAsync(bytes, "../../..\\secret.txt", "text/plain", "owner-a");

        Assert.Equal("secret.txt", attachment.Name);
        Assert.StartsWith("att_", attachment.Id);
        Assert.DoesNotContain("secret", attachment.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".txt", attachment.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), attachment.Sha256);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "bytes")), Path.GetFullPath(store.Root));
        Assert.True(Path.GetRelativePath(store.Root, attachment.StoredPath).Split(Path.DirectorySeparatorChar)[0] != "..");
    }

    [Fact]
    public async Task StorageExtensionComesFromValidatedMediaTypeNotUserFilename()
    {
        var store = Store();

        var attachment = await store.UploadBytesAsync("{}"u8.ToArray(), "malware.exe", "application/json", "owner");

        Assert.EndsWith(".json", attachment.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("malware", attachment.StoredPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsOversizeInvalidMimeAndSpoofedImage()
    {
        var store = Store(maxFile: 3);
        await AssertCode("attachment_too_large", () => store.UploadBytesAsync("four"u8.ToArray(), "a.txt", "text/plain", "owner"));
        var validatingStore = Store();
        await AssertCode("invalid_media_type", () => validatingStore.UploadBytesAsync("x"u8.ToArray(), "a.txt", "not a mime", "owner"));
        await AssertCode("media_type_mismatch", () => validatingStore.UploadBytesAsync("not png"u8.ToArray(), "a.png", "image/png", "owner"));
    }

    [Fact]
    public async Task ClaimIsAuthorizedAtomicAndCannotBeReused()
    {
        var store = Store();
        var attachment = await store.UploadBytesAsync("data"u8.ToArray(), "a.txt", "text/plain", "owner-a");

        await AssertCode("forbidden", () => store.ClaimAsync([attachment.Id], "owner-b", "s1", "m1"));
        var claimed = await store.ClaimAsync([attachment.Id], "owner-a", "s1", "m1");
        Assert.Single(claimed);
        Assert.Null(claimed[0].ExpiresAt);
        await AssertCode("attachment_already_claimed", () => store.ClaimAsync([attachment.Id], "owner-a", "s2", "m2"));
        await AssertCode("attachment_claimed", () => store.DeleteDraftAsync(attachment.Id, "owner-a"));
    }

    [Fact]
    public async Task FailedTurnCanReleaseThenReclaim()
    {
        var store = Store();
        var attachment = await store.UploadBytesAsync("data"u8.ToArray(), "a.txt", "text/plain", "owner");
        await store.ClaimAsync([attachment.Id], "owner", "s1", "m1");

        await store.ReleaseClaimAsync("s1", "m1");
        var reclaimed = await store.ClaimAsync([attachment.Id], "owner", "s1", "m2");

        Assert.Equal("m2", reclaimed[0].ClaimedMessageUid);
    }

    [Fact]
    public async Task DeleteAndExpiryRemoveUnclaimedBytes()
    {
        var store = Store();
        var deleted = await store.UploadBytesAsync("data"u8.ToArray(), "a.txt", "text/plain", "owner");
        await AssertCode("forbidden", () => store.DeleteDraftAsync(deleted.Id, "someone-else"));
        await store.DeleteDraftAsync(deleted.Id, "owner");
        Assert.False(File.Exists(deleted.StoredPath));

        var expiringStore = Store(ttlMinutes: 0);
        var expired = await expiringStore.UploadBytesAsync("data"u8.ToArray(), "b.txt", "text/plain", "owner");
        await AssertCode("attachment_expired", () => expiringStore.GetAuthorizedAsync(expired.Id, "owner"));
        Assert.Equal(1, await expiringStore.CleanupExpiredAsync());
        Assert.False(File.Exists(expired.StoredPath));
    }

    [Fact]
    public async Task EnforcesCountTotalAndDuplicateLimitsBeforeClaim()
    {
        var store = Store(maxCount: 2, maxTurn: 5);
        var a = await store.UploadBytesAsync("aaa"u8.ToArray(), "a.txt", "text/plain", "owner");
        var b = await store.UploadBytesAsync("bbb"u8.ToArray(), "b.txt", "text/plain", "owner");
        var c = await store.UploadBytesAsync("c"u8.ToArray(), "c.txt", "text/plain", "owner");

        await AssertCode("duplicate_attachment", () => store.ClaimAsync([a.Id, a.Id], "owner", "s", "m"));
        await AssertCode("too_many_attachments", () => store.ClaimAsync([a.Id, b.Id, c.Id], "owner", "s", "m"));
        await AssertCode("attachments_too_large", () => store.ClaimAsync([a.Id, b.Id], "owner", "s", "m"));
    }

    private static async Task AssertCode(string expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<AttachmentStoreException>(action);
        Assert.Equal(expected, error.Code);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
