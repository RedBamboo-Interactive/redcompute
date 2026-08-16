using System.Net;
using System.Text;
using System.Text.Json;
using RedCompute.App.Services;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class RepositoryReferenceValidatorTests
{
    private static readonly Guid RepositoryId = Guid.Parse("1910ac53-d68a-4ccc-883d-0541c0091d9b");

    [Fact]
    public async Task ValidateAsync_AcceptsActiveRepositoryWithExactNormalizedPath()
    {
        var directory = CreateDirectory();
        try
        {
            var validator = CreateValidator("repository", "active", directory.Replace('\\', '/'));

            var result = await validator.ValidateAsync(
                RepositoryId.ToString("D").ToUpperInvariant(),
                directory + Path.DirectorySeparatorChar);

            Assert.True(result.Ok);
            Assert.Equal(RepositoryId, result.Repository?.Id);
            Assert.Equal("RedCompute", result.Repository?.Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("provider", "active", "repositoryId must reference a Repository entity")]
    [InlineData("repository", "inactive", "Repository entity is not active")]
    public async Task ValidateAsync_RejectsWrongTypeOrInactiveEntity(
        string typeSlug,
        string status,
        string expectedError)
    {
        var directory = CreateDirectory();
        try
        {
            var result = await CreateValidator(typeSlug, status, directory)
                .ValidateAsync(RepositoryId.ToString("D"), directory);

            Assert.False(result.Ok);
            Assert.Equal(422, result.StatusCode);
            Assert.Equal(expectedError, result.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ValidateAsync_RejectsPathMismatch()
    {
        var directory = CreateDirectory();
        var otherDirectory = CreateDirectory();
        try
        {
            var result = await CreateValidator("repository", "active", directory)
                .ValidateAsync(RepositoryId.ToString("D"), otherDirectory);

            Assert.False(result.Ok);
            Assert.Equal(422, result.StatusCode);
            Assert.Equal("projectPath does not match the referenced Repository checkout", result.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(otherDirectory, recursive: true);
        }
    }

    private static RepositoryReferenceValidator CreateValidator(
        string typeSlug,
        string status,
        string repositoryPath)
    {
        var handler = new EntityHandler(typeSlug, status, repositoryPath);
        return new RepositoryReferenceValidator(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://redleaf/"),
        });
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"redcompute-repository-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class EntityHandler(string typeSlug, string status, string repositoryPath) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = new
            {
                id = RepositoryId,
                typeSlug,
                name = "RedCompute",
                data = JsonSerializer.Serialize(new
                {
                    status,
                    local_path = repositoryPath,
                }),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            });
        }
    }
}
