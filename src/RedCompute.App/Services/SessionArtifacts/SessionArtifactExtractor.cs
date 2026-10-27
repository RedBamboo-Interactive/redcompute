using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RedCompute.Core.Sessions;

namespace RedCompute.App.Services.SessionArtifacts;

/// <summary>
/// Pure record -> candidate extraction for the session artifact projection. Artifacts are what a
/// person sees in the conversation: visible user/assistant text, attachments, voice and generated
/// images. Thinking, status, tasks and tool traffic are never read, except the files an agent writes.
///
/// Classification deliberately mirrors the shared chat renderer (<c>@redbamboo/chat</c>
/// <c>MarkdownLink</c>, <c>isImageUrl</c>, <c>canonicalizeChatMediaSrc</c>, <c>parseLocalFileLink</c>,
/// <c>parseEntityEmbedHref</c>/<c>parseEntityHref</c>); <c>tests/Fixtures/artifact-extraction-cases.json</c>
/// is the contract both sides are tested against. Bump <see cref="Version"/> when a rule changes.
/// </summary>
public static class SessionArtifactExtractor
{
    public const int Version = 2;

    private const int MaxTitleLength = 200;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAutoLinks().Build();

    // Same envelopes the chat package strips from user turns (message-content.ts, nova-event.ts),
    // plus the event and task-notification carriers, which are machine traffic and not artifacts.
    private static readonly Regex NovaContext = new(@"<nova-context[\s\S]*?</nova-context>\s*", RegexOptions.Compiled);
    private static readonly Regex NovaPrior = new(@"<nova-prior-messages?[\s\S]*?</nova-prior-messages?>\s*", RegexOptions.Compiled);
    private static readonly Regex NovaEvent = new(@"<nova-event\b[\s\S]*?</nova-event>\s*", RegexOptions.Compiled);
    private static readonly Regex TaskNotification = new(@"<task-notification\b[\s\S]*?</task-notification>\s*", RegexOptions.Compiled);

    // event-image.ts IMAGE_EXT, streaming-text.tsx VIDEO_EXTENSIONS. `.ogg` stays video here because
    // the renderer's single video matcher claims it; audio-only Ogg uses `.oga` or `.opus`.
    private static readonly Regex ImageExt = new(@"\.(png|jpe?g|webp|gif|avif|bmp|svg)(?:[?#]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VideoExt = new(@"\.(webm|mp4|mov|avi|mkv|ogg)(\?.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AudioExt = new(@"\.(mp3|wav|m4a|flac|opus|aac|oga)(\?.*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnsafeScheme = new(@"^\s*(?:javascript|vbscript|data):", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AssetPath = new(@"^/api/(?:assets|redleaf-asset)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AssetId = new(@"^/api/(?:assets|redleaf-asset)/([^/?#]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPath = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly Regex FileUri = new(@"^file:///?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EntityEmbed = new(@"^redleaf://([^/?#]+)/([^?#]+)(?:\?[^#]*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HashLocation = new(@"#L(\d+)(?::(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SuffixLocation = new(@":(\d+)(?::(\d+))?$", RegexOptions.Compiled);
    private static readonly Regex ControlChars = new(@"[\u0000-\u001f]", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp",
        [".gif"] = "image/gif", [".avif"] = "image/avif", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml",
        [".webm"] = "video/webm", [".mp4"] = "video/mp4", [".mov"] = "video/quicktime",
        [".avi"] = "video/x-msvideo", [".mkv"] = "video/x-matroska", [".ogg"] = "video/ogg",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".m4a"] = "audio/mp4", [".flac"] = "audio/flac",
        [".opus"] = "audio/opus", [".aac"] = "audio/aac", [".oga"] = "audio/ogg",
        [".pdf"] = "application/pdf", [".json"] = "application/json", [".zip"] = "application/zip",
        [".md"] = "text/markdown", [".txt"] = "text/plain", [".csv"] = "text/csv", [".html"] = "text/html",
    };

    /// <summary>
    /// <paramref name="projectPath"/> is the session's working folder (<c>UnifiedSessionInfo.ProjectPath</c>).
    /// Agent-written files are artifacts only inside it; without one there are no <c>tool-write</c> artifacts.
    /// </summary>
    public static IReadOnlyList<ArtifactCandidate> Extract(UnifiedMessageRecord record, string? projectPath = null)
    {
        var found = new List<ArtifactCandidate>();
        switch (record.EventType)
        {
            case "text" when record.Role is "user" or "assistant":
                ExtractText(record, found);
                ExtractAttachments(record, found);
                break;
            case "image":
                ExtractAssistantImage(record, found);
                break;
            case "tool_use":
                ExtractToolWrite(record, projectPath, found);
                break;
        }

        // One mention per record per target, however many times the text repeats it.
        if (found.Count < 2) return found;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return found.Where(candidate => seen.Add(candidate.Key)).ToList();
    }

    // ── text ────────────────────────────────────────────────────────────────

    private static void ExtractText(UnifiedMessageRecord record, List<ArtifactCandidate> found)
    {
        var content = record.Content;
        if (string.IsNullOrWhiteSpace(content)) return;

        if (record.Role == "user")
            content = NovaPrior.Replace(NovaContext.Replace(content, ""), "");
        content = TaskNotification.Replace(NovaEvent.Replace(content, ""), "");
        if (string.IsNullOrWhiteSpace(content)) return;

        var origin = record.Role == "user" ? ArtifactOrigins.UserText : ArtifactOrigins.AssistantText;
        var document = Markdown.Parse(content, Pipeline);
        foreach (var inline in document.Descendants<Inline>())
        {
            switch (inline)
            {
                case LinkInline link when !string.IsNullOrWhiteSpace(link.Url):
                    var title = link.IsImage ? ImageAlt(link) : AnchorText(link);
                    if (ClassifyTarget(link.Url, link.IsImage, record.Role, origin, title) is { } candidate)
                        found.Add(candidate);
                    break;
                case AutolinkInline { IsEmail: false } auto when !string.IsNullOrWhiteSpace(auto.Url):
                    if (ClassifyTarget(auto.Url, false, record.Role, origin, null) is { } autoCandidate)
                        found.Add(autoCandidate);
                    break;
            }
        }
    }

    private static string? AnchorText(LinkInline link) => Clean(InlineText(link));

    private static string? ImageAlt(LinkInline link) => Clean(InlineText(link));

    private static string InlineText(ContainerInline container)
    {
        var text = new StringBuilder();
        foreach (var inline in container.Descendants<Inline>())
        {
            if (inline is LiteralInline literal) text.Append(literal.Content.ToString());
            else if (inline is CodeInline code) text.Append(code.Content);
        }
        return text.ToString();
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Length > MaxTitleLength ? collapsed[..MaxTitleLength] : collapsed;
    }

    /// <summary>Classify one Markdown destination; null drops it (unsafe, anchor or unsupported).</summary>
    internal static ArtifactCandidate? ClassifyTarget(
        string rawTarget, bool isImageInline, string role, string origin, string? title,
        string? mediaType = null, string? name = null, long? size = null)
    {
        var target = rawTarget.Trim();
        if (target.Length == 0 || target[0] == '#') return null;
        if (UnsafeScheme.IsMatch(ControlChars.Replace(target, ""))) return null;

        if (ParseEntity(target) is { } entity)
        {
            return new ArtifactCandidate($"entity:{entity.Id}", ArtifactKinds.Entity, origin, role,
                CanonicalHref(target), title, Entity: entity);
        }

        // Absolute loopback RedLeaf URLs become origin-safe relative ones, as the renderer does.
        var canonical = CanonicalHref(target);

        var local = ParseLocalFile(canonical);
        var path = local?.Path ?? PathOnly(canonical);
        var assetId = local is null ? AssetKey(canonical) : null;

        var kind = ImageExt.IsMatch(canonical) ? ArtifactKinds.Image
            : VideoExt.IsMatch(canonical) ? ArtifactKinds.Video
            : AudioExt.IsMatch(canonical) ? ArtifactKinds.Audio
            : (string?)null;

        if (local is not null)
        {
            kind ??= isImageInline ? ArtifactKinds.Image : ArtifactKinds.Document;
            return new ArtifactCandidate(FileKey(local.Path), kind, origin, role, local.Path, title,
                mediaType ?? MediaTypeFor(local.Path), name, size,
                File: new ArtifactFileRef { Path = local.Path, Line = local.Line });
        }

        if (assetId is not null)
        {
            kind ??= isImageInline ? ArtifactKinds.Image : ArtifactKinds.Document;
            return new ArtifactCandidate($"asset:{assetId}", kind, origin, role, canonical, title,
                mediaType ?? MediaTypeFor(path), name, size);
        }

        var internalLink = canonical.StartsWith('/') && !canonical.StartsWith("//", StringComparison.Ordinal);
        var external = Regex.IsMatch(canonical, @"^https?://", RegexOptions.IgnoreCase);
        if (!internalLink && !external) return null;

        if (kind is null && isImageInline) kind = ArtifactKinds.Image;
        if (kind is not null)
        {
            return new ArtifactCandidate(UrlKey(canonical), kind, origin, role, canonical, title,
                mediaType ?? MediaTypeFor(path), name, size);
        }

        // Autolinks and bare URLs echo their own text; the target already says everything.
        if (title is not null && string.Equals(title, canonical, StringComparison.Ordinal)) title = null;
        return new ArtifactCandidate(UrlKey(canonical), ArtifactKinds.Link, origin, role, canonical, title,
            mediaType, name, size, Internal: internalLink);
    }

    // ── targets ─────────────────────────────────────────────────────────────

    private static ArtifactEntityRef? ParseEntity(string href)
    {
        if (EntityEmbed.Match(href) is { Success: true } embed)
        {
            var type = Decode(embed.Groups[1].Value);
            var id = Decode(embed.Groups[2].Value);
            return type.Length > 0 && id.Length > 0 ? new ArtifactEntityRef { TypeSlug = type, Id = id } : null;
        }

        var canonical = CanonicalHref(href);
        if (!canonical.StartsWith('/') || canonical.StartsWith("//", StringComparison.Ordinal)) return null;
        if (canonical.Contains('?') || canonical.Contains('#')) return null;
        var segments = canonical.Split('/');
        if (segments.Length != 5 || segments[0] != "" || segments[1] != "database" || segments[2] != "entities") return null;
        var typeSlug = Decode(segments[3]);
        var entityId = Decode(segments[4]);
        return typeSlug.Length > 0 && entityId.Length > 0
            ? new ArtifactEntityRef { TypeSlug = typeSlug, Id = entityId }
            : null;
    }

    /// <summary>canonicalizeChatMediaSrc + canonicalizeLeafLinkHref: loopback RedLeaf URLs become relative.</summary>
    internal static string CanonicalHref(string href)
    {
        if (!Regex.IsMatch(href, @"^https?://", RegexOptions.IgnoreCase)) return href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) return href;
        if (!IsLoopback(uri.Host)) return href;

        var relative = uri.PathAndQuery + uri.Fragment;
        if (AssetPath.IsMatch(uri.AbsolutePath)) return relative;
        if (uri.Port == 18804 && string.IsNullOrEmpty(uri.UserInfo) && !uri.AbsolutePath.StartsWith("//", StringComparison.Ordinal))
            return relative;
        return href;
    }

    private static bool IsLoopback(string host)
    {
        host = host.Trim('[', ']').ToLowerInvariant();
        return host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host == "::1"
            || Regex.IsMatch(host, @"^127(?:\.\d{1,3}){3}$");
    }

    private sealed record LocalFile(string Path, int? Line);

    /// <summary>local-file-link.ts parseLocalFileLink: Windows drive paths, /C:/, file:///C:/, #L12 and :12:3.</summary>
    private static LocalFile? ParseLocalFile(string href)
    {
        var value = Decode(href.Trim());
        if (FileUri.IsMatch(value)) value = FileUri.Replace(value, "");
        if (Regex.IsMatch(value, @"^/[A-Za-z]:[\\/]")) value = value[1..];

        int? line = null;
        if (HashLocation.Match(value) is { Success: true } hash)
        {
            line = int.Parse(hash.Groups[1].Value);
            value = value[..^hash.Length];
        }
        else if (SuffixLocation.Match(value) is { Success: true } suffix)
        {
            line = int.Parse(suffix.Groups[1].Value);
            value = value[..^suffix.Length];
        }

        return WindowsPath.IsMatch(value) ? new LocalFile(NormalizePath(value), line) : null;
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    internal static string NormalizePath(string path)
    {
        var normalized = Regex.Replace(path.Trim().Replace('\\', '/'), @"(?<!^)/{2,}", "/");
        return normalized.Length > 1 && normalized.EndsWith('/') && !Regex.IsMatch(normalized, @"^[A-Za-z]:/$")
            ? normalized.TrimEnd('/')
            : normalized;
    }

    private static string FileKey(string path)
        => "file:" + (WindowsPath.IsMatch(path) ? path.ToLowerInvariant() : path);

    private static string? AssetKey(string canonical)
        => AssetId.Match(canonical) is { Success: true } match ? Decode(match.Groups[1].Value) : null;

    private static string UrlKey(string canonical)
    {
        if (canonical.StartsWith('/')) return $"url:{canonical}";
        return $"url:{(Uri.TryCreate(canonical, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : canonical)}";
    }

    private static string PathOnly(string target)
    {
        var end = target.IndexOfAny(['?', '#']);
        return end < 0 ? target : target[..end];
    }

    private static string? MediaTypeFor(string path)
    {
        var extension = System.IO.Path.GetExtension(PathOnly(path));
        return MediaTypes.TryGetValue(extension, out var mediaType) ? mediaType : null;
    }

    // ── attachments ─────────────────────────────────────────────────────────

    private static void ExtractAttachments(UnifiedMessageRecord record, List<ArtifactCandidate> found)
    {
        if (string.IsNullOrWhiteSpace(record.AttachmentsJson)) return;
        JsonDocument json;
        try { json = JsonDocument.Parse(record.AttachmentsJson); }
        catch (JsonException) { return; }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (Str(root, "audioUrl") is { } audioUrl
                && ClassifyTarget(audioUrl, false, record.Role, ArtifactOrigins.Voice, null) is { } voice)
            {
                // A voice message is audio whatever its extension says (WebM/Ogg/MP4 voice notes included).
                var mediaType = voice.MediaType is { } type && type.StartsWith("video/", StringComparison.Ordinal)
                    ? "audio/" + type["video/".Length..]
                    : voice.MediaType;
                found.Add(voice with { Kind = ArtifactKinds.Audio, MediaType = mediaType });
            }

            // Legacy base64 `images` are deliberately ignored: they have no durable target, and Nova
            // supplies them as Assets.
            if (!root.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array)
                return;

            foreach (var attachment in attachments.EnumerateArray())
            {
                if (attachment.ValueKind != JsonValueKind.Object) continue;
                var downloadUrl = Str(attachment, "downloadUrl");
                if (downloadUrl is null) continue;

                var name = Str(attachment, "name");
                var mediaType = Str(attachment, "mediaType");
                long? size = attachment.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes)
                    ? bytes : null;
                var isImage = string.Equals(Str(attachment, "kind"), "image", StringComparison.OrdinalIgnoreCase);

                var attached = ClassifyTarget(downloadUrl, false, record.Role, ArtifactOrigins.UserAttachment,
                    null, mediaType, name, size);
                if (attached is null) continue;
                found.Add(attached with
                {
                    Kind = isImage ? ArtifactKinds.Image : ArtifactKinds.Document,
                    Internal = false,
                });
            }
        }
    }

    private static string? Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;

    // ── generated images and agent-written files ────────────────────────────

    private static void ExtractAssistantImage(UnifiedMessageRecord record, List<ArtifactCandidate> found)
    {
        var payload = record.PayloadRef;
        if (payload is null || payload.Kind != "assistant-image") return;

        found.Add(new ArtifactCandidate(
            $"payload:{payload.RecordId}", ArtifactKinds.Image, ArtifactOrigins.AssistantImage,
            record.Role, $"/ai-session/sessions/{Uri.EscapeDataString(record.SessionId)}/messages/{payload.RecordId}/output",
            MediaType: payload.ContentType, PayloadRef: payload));
    }

    private static readonly string[] PathKeys = ["file_path", "path"];
    private static readonly string[] PlanPathKeys = ["planFilePath", "plan_file_path", "file_path", "path"];

    private static void ExtractToolWrite(UnifiedMessageRecord record, string? projectPath, List<ArtifactCandidate> found)
    {
        var tool = record.ToolName;
        if (string.IsNullOrEmpty(tool) || string.IsNullOrWhiteSpace(record.ToolInput)) return;

        var isWrite = string.Equals(tool, "Write", StringComparison.OrdinalIgnoreCase);
        var isPlan = !isWrite && tool.Contains("plan", StringComparison.OrdinalIgnoreCase);
        if (!isWrite && !isPlan) return;

        string? path;
        try
        {
            using var input = JsonDocument.Parse(record.ToolInput);
            if (input.RootElement.ValueKind != JsonValueKind.Object) return;
            path = null;
            foreach (var key in isPlan ? PlanPathKeys : PathKeys)
            {
                if (Str(input.RootElement, key) is { } value) { path = value; break; }
            }
        }
        catch (JsonException)
        {
            return;
        }
        if (path is null) return;

        var normalized = NormalizePath(path);
        if (!IsInside(normalized, projectPath)) return;

        // A written file is what its extension says it is; the origin still says an agent wrote it.
        var kind = ImageExt.IsMatch(normalized) ? ArtifactKinds.Image
            : VideoExt.IsMatch(normalized) ? ArtifactKinds.Video
            : AudioExt.IsMatch(normalized) ? ArtifactKinds.Audio
            : ArtifactKinds.Document;
        found.Add(new ArtifactCandidate(
            FileKey(normalized), kind, ArtifactOrigins.ToolWrite, record.Role, normalized,
            MediaType: MediaTypeFor(normalized), File: new ArtifactFileRef { Path = normalized }));
    }

    /// <summary>True when <paramref name="path"/> is strictly below <paramref name="projectPath"/> on a segment boundary.</summary>
    internal static bool IsInside(string path, string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return false;
        var root = NormalizePath(projectPath);
        var comparison = WindowsPath.IsMatch(root) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!root.EndsWith('/')) root += "/";
        return path.Length > root.Length && path.StartsWith(root, comparison);
    }
}
