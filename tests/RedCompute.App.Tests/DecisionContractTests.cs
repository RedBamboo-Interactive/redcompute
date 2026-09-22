using System.Text;
using System.Text.Json;
using RedCompute.Core.Decisions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class DecisionContractTests
{
    [Fact]
    public void Mixed_typed_request_is_accepted()
    {
        var result = Validate("""
        {
          "state":{"subject":"Charged twice","daysLate":14},
          "questions":{
            "route":{"type":"choice","instructions":"Which team?","criteria":{"billing":"Payments","shipping":null}},
            "urgent":{"type":"noul","instructions":"Is urgent?","criteria":{"true":"Needs today","false":"Can wait"}},
            "severity":{"type":"score","instructions":"How severe?","criteria":["low","medium","high"]}
          }
        }
        """);

        Assert.True(result.IsValid, string.Join(", ", result.Errors));
        Assert.IsType<ChoiceQuestion>(result.Request!.Questions["route"]);
        Assert.IsType<NoulQuestion>(result.Request.Questions["urgent"]);
        Assert.IsType<ScoreQuestion>(result.Request.Questions["severity"]);
    }

    [Theory]
    [InlineData("""{"state":"x","questions":{"q":{"type":"other","instructions":"x"}}}""",
        "questions.q.type")]
    [InlineData("""{"state":"x","questions":{"q":{"type":"score","instructions":"x","criteria":["only"]}}}""",
        "questions.q.criteria")]
    [InlineData("""{"state":"x","questions":{"q":{"type":"choice","instructions":"x","criteria":[]}}}""",
        "questions.q.criteria")]
    [InlineData("""{"state":42,"questions":{"q":{"type":"noul","instructions":"x"}}}""",
        "state")]
    [InlineData("""{"state":"x","questions":{"q":{"type":"noul","instructions":"x","extra":true}}}""",
        "questions.q.extra")]
    public void Invalid_types_and_limits_have_precise_paths(string json, string path)
    {
        var result = Validate(json);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.ContainsKey(path),
            $"Expected {path}; got {string.Join(", ", result.Errors.Keys)}");
    }

    [Fact]
    public void Question_count_is_bounded()
    {
        var questions = string.Join(",", Enumerable.Range(0, DecisionContract.MaxQuestions + 1)
            .Select(i => "\"q" + i + "\":{\"type\":\"noul\",\"instructions\":\"x\"}"));
        var result = Validate("{\"state\":\"x\",\"questions\":{" + questions + "}}");

        Assert.Equal($"must contain 1..{DecisionContract.MaxQuestions} questions",
            result.Errors["questions"]);
    }

    [Fact]
    public void Schemas_expose_discriminators_distributions_and_no_authority()
    {
        var request = JsonSerializer.Serialize(DecisionContract.RequestSchema);
        var response = JsonSerializer.Serialize(DecisionContract.ResponseSchema);

        var requestElement = JsonSerializer.SerializeToElement(DecisionContract.RequestSchema);
        Assert.True(requestElement.TryGetProperty(((char)36) + "schema", out _));
        Assert.Contains("\"oneOf\"", request);
        Assert.Contains("\"discriminator\"", request);
        Assert.Contains("\"choice\"", request);
        Assert.Contains("\"score\"", request);
        Assert.Contains("\"noul\"", request);
        Assert.Contains("\"probabilities\"", response);
        Assert.Contains("Distribution concentration", response);
        Assert.Contains("\"actionAuthority\"", response);
        Assert.Contains("\"const\":false", response);
    }

    private static DecisionValidationResult Validate(string json)
        => DecisionContract.Validate(
            JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!);
}
