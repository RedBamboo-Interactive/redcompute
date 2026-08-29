using RedCompute.App.Services.Jobs;
using RedCompute.Core.Jobs;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class JobArtifactStoreTests
{
    [Fact]
    public void NamedArtifactsResolveThroughDurableSidecarManifest()
    {
        using var temp = new TemporaryDirectory();
        var jobId = Guid.NewGuid();
        var primary = Path.Combine(temp.Path, $"{jobId}.mp3");
        File.WriteAllBytes(primary, [1, 2, 3]);
        JobArtifactStore.Save(jobId, temp.Path, primary, "audio/mpeg", "clip-0", "source.mp3",
        [
            new JobOutputPart
            {
                Suffix = "_artifact1",
                Name = "cover-0",
                FileName = "cover.jpg",
                ContentType = "image/jpeg",
                Data = new MemoryStream([4, 5]),
            },
            new JobOutputPart
            {
                Suffix = "_artifact2",
                Name = "stem-drums",
                FileName = "drums.mp3",
                ContentType = "audio/mpeg",
                Data = new MemoryStream([6, 7, 8]),
            },
        ]);

        var primaryByName = JobArtifactStore.Resolve(jobId, temp.Path, primary, "audio/mpeg", "clip-0", null);
        var cover = JobArtifactStore.Resolve(jobId, temp.Path, primary, "audio/mpeg", "cover-0", null);
        var drums = JobArtifactStore.Resolve(jobId, temp.Path, primary, "audio/mpeg", "stem-drums", null);

        Assert.NotNull(primaryByName);
        Assert.Equal("source.mp3", primaryByName!.FileName);
        Assert.Equal([4, 5], File.ReadAllBytes(cover!.Path));
        Assert.Equal("image/jpeg", cover.ContentType);
        Assert.Equal([6, 7, 8], File.ReadAllBytes(drums!.Path));
        Assert.Null(JobArtifactStore.Resolve(jobId, temp.Path, primary, "audio/mpeg", "missing", null));
        Assert.Throws<InvalidDataException>(() =>
            JobArtifactStore.Resolve(jobId, temp.Path, primary, "audio/mpeg", "../escape", null));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"redcompute-artifacts-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
