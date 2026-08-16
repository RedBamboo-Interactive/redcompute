using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;

namespace RedCompute.App.Services;

public sealed record RepositoryReference(Guid Id, string Name, string Path);

public sealed record RepositoryValidationResult(
    RepositoryReference? Repository,
    int StatusCode = StatusCodes.Status200OK,
    string? Error = null)
{
    public bool Ok => Repository is not null;
}

/// <summary>
/// Resolves a stable RedLeaf Repository entity and binds it to the exact physical
/// checkout supplied for execution. The entity is identity; the path is a
/// machine-local execution snapshot and must never be accepted from a different
/// repository reference.
/// </summary>
public sealed class RepositoryReferenceValidator
{
    private readonly HttpClient _http;

    public RepositoryReferenceValidator(string redLeafBaseUrl, JwtService jwtService)
    {
        var token = jwtService.GenerateAccessToken("system", "system@redsuite", "System", ["admin"]);
        _http = new HttpClient
        {
            BaseAddress = new Uri(redLeafBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    }

    internal RepositoryReferenceValidator(HttpClient http) => _http = http;

    public async Task<RepositoryValidationResult> ValidateAsync(
        string repositoryId,
        string projectPath,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(repositoryId, out var id))
            return Invalid("repositoryId must be a valid Repository entity UUID");

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync($"api/entities/{id:D}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(null, StatusCodes.Status503ServiceUnavailable,
                $"Repository validation is unavailable: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Invalid("Repository entity not found");
            if (!response.IsSuccessStatusCode)
                return new(null, StatusCodes.Status503ServiceUnavailable,
                    $"Repository validation failed with RedLeaf status {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var entity = document.RootElement;
            if (!string.Equals(Str(entity, "typeSlug"), "repository", StringComparison.OrdinalIgnoreCase))
                return Invalid("repositoryId must reference a Repository entity");

            using var data = ParseData(entity);
            if (!string.Equals(Str(data.RootElement, "status"), "active", StringComparison.OrdinalIgnoreCase))
                return Invalid("Repository entity is not active");

            var repositoryPath = Str(data.RootElement, "local_path");
            if (!PathsEqual(repositoryPath, projectPath))
                return Invalid("projectPath does not match the referenced Repository checkout");
            if (!Directory.Exists(Path.GetFullPath(projectPath)))
                return Invalid("Referenced Repository checkout is unavailable");

            return new(new RepositoryReference(
                id,
                Str(entity, "name") ?? id.ToString("D"),
                Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }
    }

    private static RepositoryValidationResult Invalid(string message) =>
        new(null, StatusCodes.Status422UnprocessableEntity, message);

    private static JsonDocument ParseData(JsonElement entity)
    {
        if (!entity.TryGetProperty("data", out var data)) return JsonDocument.Parse("{}");
        return data.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(data.GetString() ?? "{}")
            : JsonDocument.Parse(data.GetRawText());
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var leftPath = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightPath = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
