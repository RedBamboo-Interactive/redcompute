using System.Text;
using RedCompute.Node.Runtimes;
using Xunit;

namespace RedCompute.Node.Tests;

/// <summary>
/// The record of the files the server's own prompts produced (review item 5, <c>/view</c>): what a <c>/history/{id}</c>
/// answer contributes, and its bounds (per prompt, per runtime, by age).
/// </summary>
public sealed class ServerOutputsTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static List<OutputFile> FilesOf(string answer, string prompt) => ServerOutputs.FilesOf(Encoding.UTF8.GetBytes(answer), prompt);

    [Fact]
    public void A_history_answer_gives_the_prompts_listed_files_as_view_would_ask_for_them()
    {
        const string answer = """
            {"p1":{"prompt":[],"outputs":{
              "9":{"images":[{"filename":"ComfyUI_00042_.png","subfolder":"","type":"output"},
                             {"filename":"preview.png","subfolder":"previews","type":"temp"},
                             {"filename":"bare.png"},
                             {"filename":"in.png","subfolder":"","type":"input"},
                             {"filename":"../escape.png","subfolder":"","type":"output"},
                             {"filename":"x.png","subfolder":"../up","type":"output"},
                             {"filename":7,"type":"output"},
                             {"filename":"num-type.png","type":3},
                             "not an object"]},
              "10":{"gifs":[{"filename":"clip.mp4","subfolder":"video","type":"output"}],"text":["just text"]},
              "11":"not an object"}},
             "p2":{"outputs":{"9":{"images":[{"filename":"other-prompt.png","subfolder":"","type":"output"}]}}}}
            """;
        Assert.Equal(
            [
                new OutputFile("output", "", "ComfyUI_00042_.png"),
                new OutputFile("temp", "previews", "preview.png"),
                new OutputFile("output", "", "bare.png"),
                new OutputFile("output", "video", "clip.mp4"),
            ],
            FilesOf(answer, "p1"));

        // Only the prompt asked for; nothing from an answer that is not ComfyUI's history.
        Assert.Equal([new OutputFile("output", "", "other-prompt.png")], FilesOf(answer, "p2"));
        Assert.Empty(FilesOf(answer, "p3"));
        foreach (var other in new[] { "not json", "[]", """{"p1":[]}""", """{"p1":{"outputs":[]}}""", "" })
            Assert.Empty(FilesOf(other, "p1"));
    }

    [Fact]
    public void One_answer_records_at_most_PerPrompt_files()
    {
        var items = string.Join(",", Enumerable.Range(0, ServerOutputs.PerPrompt + 10)
            .Select(i => $$"""{"filename":"f{{i}}.png","subfolder":"","type":"output"}"""));
        var files = FilesOf("""{"p":{"outputs":{"9":{"images":[""" + items + "]}}}}", "p");
        Assert.Equal(ServerOutputs.PerPrompt, files.Count);

        var outputs = new ServerOutputs(new ManualTime(DateTimeOffset.UnixEpoch));
        outputs.Add("rt", Enumerable.Range(0, ServerOutputs.PerPrompt + 10).Select(i => new OutputFile("output", "", $"f{i}.png")));
        Assert.True(outputs.Contains("rt", new OutputFile("output", "", $"f{ServerOutputs.PerPrompt - 1}.png")));
        Assert.False(outputs.Contains("rt", new OutputFile("output", "", $"f{ServerOutputs.PerPrompt}.png")));
    }

    [Fact]
    public void A_file_is_served_only_on_its_runtime_and_exactly_as_recorded()
    {
        var outputs = new ServerOutputs(new ManualTime(DateTimeOffset.UnixEpoch));
        outputs.Add("rt-a", [new OutputFile("output", "", "ComfyUI_00042_.png")]);

        Assert.True(outputs.Contains("rt-a", new OutputFile("output", "", "ComfyUI_00042_.png")));
        Assert.False(outputs.Contains("rt-b", new OutputFile("output", "", "ComfyUI_00042_.png")));
        Assert.False(outputs.Contains("rt-a", new OutputFile("temp", "", "ComfyUI_00042_.png")));
        Assert.False(outputs.Contains("rt-a", new OutputFile("output", "sub", "ComfyUI_00042_.png")));
        Assert.False(outputs.Contains("rt-a", new OutputFile("output", "", "comfyui_00042_.png")));
        Assert.False(outputs.Contains("rt-a", new OutputFile("output", "", "ComfyUI_00041_.png")));
    }

    [Fact]
    public void A_runtime_keeps_its_most_recent_PerRuntime_files()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var outputs = new ServerOutputs(time);
        for (var prompt = 0; prompt < ServerOutputs.PerRuntime / 8 + 1; prompt++)
        {
            outputs.Add("rt", Enumerable.Range(0, 8).Select(i => new OutputFile("output", "", $"p{prompt}-{i}.png")));
            time.Now += TimeSpan.FromSeconds(1);
        }
        // 8 over the cap: the first prompt's files went first, the second's stay.
        Assert.False(outputs.Contains("rt", new OutputFile("output", "", "p0-0.png")));
        Assert.False(outputs.Contains("rt", new OutputFile("output", "", "p0-7.png")));
        Assert.True(outputs.Contains("rt", new OutputFile("output", "", "p1-0.png")));
        Assert.True(outputs.Contains("rt", new OutputFile("output", "", $"p{ServerOutputs.PerRuntime / 8}-7.png")));

        // The cap is per runtime: another runtime's files are its own.
        outputs.Add("rt-2", [new OutputFile("output", "", "p1-0.png")]);
        Assert.True(outputs.Contains("rt", new OutputFile("output", "", "p1-0.png")));
    }

    [Fact]
    public void A_file_is_served_for_MaxAge_from_its_latest_record()
    {
        var time = new ManualTime(DateTimeOffset.UnixEpoch);
        var outputs = new ServerOutputs(time);
        var early = new OutputFile("output", "", "early.png");
        var again = new OutputFile("output", "", "again.png");
        outputs.Add("rt", [early, again]);

        time.Now += ServerOutputs.MaxAge - TimeSpan.FromMinutes(1);
        Assert.True(outputs.Contains("rt", early));
        outputs.Add("rt", [again]); // listed again by a later answer: its age starts over

        time.Now += TimeSpan.FromMinutes(2);
        Assert.False(outputs.Contains("rt", early)); // past its age, even with no Add since
        Assert.True(outputs.Contains("rt", again));

        // A later Add drops what aged out; the re-recorded file is kept under its later time.
        outputs.Add("rt", [new OutputFile("output", "", "late.png")]);
        Assert.False(outputs.Contains("rt", early));
        Assert.True(outputs.Contains("rt", again));
        time.Now += ServerOutputs.MaxAge;
        Assert.False(outputs.Contains("rt", again));
    }
}
