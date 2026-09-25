using RedCompute.App.Services;
using RedCompute.Plugin.OpenCode;
using RedCompute.PluginSdk;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class AcceleratorAdmissionCoordinatorTests
{
    private static readonly AcceleratorWorkload Image = new("cuda:0", "comfyui", "image generation");
    private static readonly AcceleratorWorkload Gemma = new("cuda:0", "ollama", "Gemma inference");

    [Fact]
    public async Task Same_residency_stays_warm_and_transition_releases_once()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var releases = 0;
        using var registration = coordinator.RegisterPreemptibleResident(new(
            "cuda:0", "comfyui", "comfyui:test", _ =>
            {
                Interlocked.Increment(ref releases);
                return Task.FromResult(AcceleratorReleaseResult.Released());
            }));

        await using (await coordinator.AcquireAsync(Image)) { }
        await using (await coordinator.AcquireAsync(Image)) { }
        Assert.Equal(0, releases);

        await using (await coordinator.AcquireAsync(Gemma)) { }
        await using (await coordinator.AcquireAsync(Gemma)) { }
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task First_language_admission_releases_registered_incompatible_resident()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var releases = 0;
        using var registration = coordinator.RegisterPreemptibleResident(new(
            "cuda:0", "comfyui", "comfyui:test", _ =>
            {
                Interlocked.Increment(ref releases);
                return Task.FromResult(AcceleratorReleaseResult.Released());
            }));

        await using var languageLease = await coordinator.AcquireAsync(Gemma);

        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task Language_waits_for_active_image_before_releasing_it()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = coordinator.RegisterPreemptibleResident(new(
            "cuda:0", "comfyui", "comfyui:test", _ =>
            {
                released.TrySetResult();
                return Task.FromResult(AcceleratorReleaseResult.Released());
            }));

        var imageLease = await coordinator.AcquireAsync(Image);
        var languageTask = coordinator.AcquireAsync(Gemma).AsTask();

        await Task.Delay(50);
        Assert.False(languageTask.IsCompleted);
        Assert.False(released.Task.IsCompleted);

        await imageLease.DisposeAsync();
        await using var languageLease = await languageTask;
        Assert.True(released.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Refused_release_does_not_admit_language_or_leak_gate()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var allowRelease = false;
        using var registration = coordinator.RegisterPreemptibleResident(new(
            "cuda:0", "comfyui", "comfyui:test", _ => Task.FromResult(
                allowRelease
                    ? AcceleratorReleaseResult.Released()
                    : AcceleratorReleaseResult.Refused("image backend busy"))));

        await using (await coordinator.AcquireAsync(Image)) { }
        var error = await Assert.ThrowsAsync<AcceleratorAdmissionException>(
            () => coordinator.AcquireAsync(Gemma).AsTask());
        Assert.Contains("busy", error.Message);

        allowRelease = true;
        await using var languageLease = await coordinator.AcquireAsync(Gemma);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_leak_gate()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var imageLease = await coordinator.AcquireAsync(Image);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.AcquireAsync(Gemma, cts.Token).AsTask());

        await imageLease.DisposeAsync();
        await using var nextImage = await coordinator.AcquireAsync(Image);
    }

    [Fact]
    public async Task Separate_accelerators_are_independent()
    {
        var coordinator = new AcceleratorAdmissionCoordinator();
        var first = await coordinator.AcquireAsync(Image);

        var secondTask = coordinator.AcquireAsync(
            new AcceleratorWorkload("cuda:1", "ollama", "other GPU")).AsTask();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));

        await first.DisposeAsync();
    }

    [Theory]
    [InlineData("ollama/gemma4-heretic-fast", true)]
    [InlineData("OLLAMA/qwen3", true)]
    [InlineData("anthropic/claude-sonnet-4", false)]
    [InlineData("openai/gpt-5", false)]
    [InlineData(null, false)]
    public void Only_Ollama_models_use_local_accelerator_admission(string? model, bool expected)
    {
        Assert.Equal(expected, OpenCodeSessionService.IsLocalAcceleratorModel(model));
    }
}
