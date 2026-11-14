using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedCompute.App.Services.Nodes;

/// <summary>The persisted identity of node zero, as stored in <c>node.json</c>.</summary>
public sealed record NodeIdentity(string Id, DateTimeOffset CreatedAt, string LegacyRowsNodeId);

/// <summary>
/// The identity this run uses. <see cref="Persisted"/> is false when <c>node.json</c> exists but
/// could not be read, or could not be written: the run then uses an id of its own that no later run
/// will share, and <see cref="Problem"/> says why.
/// </summary>
public sealed record NodeIdentityResult(NodeIdentity Identity, bool Persisted, bool Created, string? Problem);

/// <summary>
/// Reads, or on first boot creates, <c>%LOCALAPPDATA%\RedCompute\node.json</c>:
/// <c>{ "id": "node_…", "createdAt": "…", "legacyRowsNodeId": "node_…" }</c>.
/// <list type="bullet">
/// <item>Written once, atomically (temp file, then a move that never overwrites).</item>
/// <item>A file that exists but cannot be read is never rewritten: a new id would re-home every
/// row that names the old one. The run continues with an unpersisted id and reports the problem.</item>
/// <item><c>legacyRowsNodeId</c> equals <c>id</c> at creation and is never changed.</item>
/// </list>
/// </summary>
public static class NodeIdentityStore
{
    public const string FileName = "node.json";

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedCompute");

    public static NodeIdentityResult LoadOrCreate(string directory, Action<string> log,
        Func<DateTimeOffset>? clock = null)
    {
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var path = Path.Combine(directory, FileName);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            var created = new NodeIdentity(NewId(now), now, "");
            created = created with { LegacyRowsNodeId = created.Id };
            try
            {
                if (TryCreate(directory, path, created))
                {
                    log($"[Nodes] Created node identity {created.Id} in {FileName}");
                    return new NodeIdentityResult(created, Persisted: true, Created: true, Problem: null);
                }
                // Another writer created it first: theirs is the identity.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var problem = $"{FileName} could not be written: {ex.Message}";
                log($"[Nodes] {problem}. This run uses the unpersisted id {created.Id}.");
                return new NodeIdentityResult(created, Persisted: false, Created: false, problem);
            }
        }

        if (TryRead(path, out var identity, out var error))
            return new NodeIdentityResult(identity!, Persisted: true, Created: false, Problem: null);

        var transient = new NodeIdentity(NewId(now), now, "");
        transient = transient with { LegacyRowsNodeId = transient.Id };
        var unreadable = $"{FileName} is unreadable and was left as it is: {error}";
        log($"[Nodes] {unreadable}. This run uses the unpersisted id {transient.Id}; fix or remove the file to restore the node identity.");
        return new NodeIdentityResult(transient, Persisted: false, Created: false, unreadable);
    }

    /// <summary>A new node id: <c>node_</c> and a ULID (time-ordered, 128 bits, Crockford base32).</summary>
    public static string NewId(DateTimeOffset now) => "node_" + Ulid(now);

    private static bool TryCreate(string directory, string path, NodeIdentity identity)
    {
        Directory.CreateDirectory(directory);
        var json = new JsonObject
        {
            ["id"] = identity.Id,
            ["createdAt"] = identity.CreatedAt.ToString("O"),
            ["legacyRowsNodeId"] = identity.LegacyRowsNodeId,
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var temp = Path.Combine(directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static bool TryRead(string path, out NodeIdentity? identity, out string? error)
    {
        identity = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "it is not a JSON object";
                return false;
            }
            var id = ReadString(root, "id");
            if (!IsValidId(id))
            {
                error = "it has no usable \"id\"";
                return false;
            }
            var legacy = ReadString(root, "legacyRowsNodeId");
            var createdAt = ReadString(root, "createdAt") is { } text
                && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            identity = new NodeIdentity(id!, createdAt, IsValidId(legacy) ? legacy! : id!);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsValidId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.Length <= 128
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string Ulid(DateTimeOffset now)
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        UInt128 value = (UInt128)(ulong)Math.Max(0, now.ToUnixTimeMilliseconds()) << 80;
        for (var i = 0; i < random.Length; i++)
            value |= (UInt128)random[i] << (8 * (9 - i));

        Span<char> chars = stackalloc char[26];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Crockford[(int)((value >> (125 - 5 * i)) & 31)];
        return new string(chars);
    }
}
