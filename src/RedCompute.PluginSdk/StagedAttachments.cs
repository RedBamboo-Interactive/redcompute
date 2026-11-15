using RedCompute.Core.Sessions;

namespace RedCompute.PluginSdk;

/// <summary>
/// The bytes of an attachment in RedCompute's staged attachment store. The store is server state: its
/// files live with RedCompute, wherever the provider's CLI executes, so a provider that inlines them
/// (base64 image blocks on stdin) reads them here and never through its execution host.
/// </summary>
public static class StagedAttachments
{
    public static byte[] ReadAllBytes(InputAttachment attachment) => File.ReadAllBytes(attachment.StoredPath);
}
