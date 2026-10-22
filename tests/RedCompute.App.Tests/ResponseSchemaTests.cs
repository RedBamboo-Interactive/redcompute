using System.Text.Json;
using RedCompute.Core.Sessions;
using Xunit;

namespace RedCompute.App.Tests;

public sealed class ResponseSchemaTests
{
    private const string Verdict = """
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["allow", "block"] },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "reasons": { "type": "array", "items": { "type": "string" }, "maxItems": 3 },
            "count": { "type": "integer" },
            "code": { "type": ["string", "null"], "pattern": "^[A-Z]{3}$" }
          },
          "required": ["verdict", "confidence", "reasons", "count", "code"],
          "additionalProperties": false
        }
        """;

    [Fact]
    public void Valid_output_has_no_errors()
    {
        var schema = Parse(Verdict);

        var errors = schema.Validate(Json("""{"verdict":"block","confidence":0.9,"reasons":["a"],"count":2,"code":"ABC"}"""));

        Assert.Empty(errors);
        Assert.True(schema.IsStrictCompatible);
    }

    [Fact]
    public void Every_violation_is_reported_with_its_path()
    {
        var schema = Parse(Verdict);

        var errors = schema.Validate(Json("""
            {"verdict":"maybe","confidence":1.5,"reasons":["a","b","c",4],"count":2.5,"code":"abc","extra":true}
            """));

        Assert.Contains(errors, e => e.StartsWith("$.verdict: must be one of"));
        Assert.Contains("$.confidence: greater than maximum 1", errors);
        Assert.Contains("$.reasons: more than maxItems 3", errors);
        Assert.Contains("$.reasons[3]: expected string, got integer", errors);
        Assert.Contains("$.count: expected integer, got number", errors);
        Assert.Contains(errors, e => e.StartsWith("$.code: does not match pattern"));
        Assert.Contains("$.extra: property is not allowed", errors);
    }

    [Fact]
    public void Missing_required_properties_and_wrong_root_type_are_errors()
    {
        var schema = Parse(Verdict);

        Assert.Contains("$: missing required property 'verdict'", schema.Validate(Json("""{"confidence":0,"reasons":[],"count":0,"code":null}""")));
        Assert.Equal(["$: expected object, got array"], schema.Validate(Json("[]")));
    }

    [Theory]
    [InlineData("""{"$ref":"#/definitions/x"}""", "unsupported keyword '$ref'")]
    [InlineData("""{"anyOf":[{"type":"string"}]}""", "unsupported keyword 'anyOf'")]
    [InlineData("""{"type":"string","format":"email"}""", "unsupported keyword 'format'")]
    [InlineData("""{"type":"date"}""", "unknown type 'date'")]
    [InlineData("""{"type":"string","pattern":"("}""", "invalid regular expression")]
    [InlineData("""{"type":"object","properties":{},"required":["a"]}""", "'a' is not declared in properties")]
    [InlineData("""{"type":"array","items":{"$schema":"x"}}""", "unsupported keyword '$schema'")]
    [InlineData("""{"enum":[]}""", "non-empty array")]
    [InlineData("""{"minLength":-1}""", "non-negative integer")]
    [InlineData("""[]""", "must be a JSON object")]
    public void Unsupported_or_invalid_schemas_are_rejected(string json, string expected)
    {
        Assert.False(ResponseSchema.TryParse(Json(json), out var schema, out var errors));
        Assert.Null(schema);
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Schemas_are_bounded_in_depth_and_size()
    {
        var deep = "{\"type\":\"string\"}";
        for (var i = 0; i <= ResponseSchema.MaxDepth; i++)
            deep = $"{{\"type\":\"array\",\"items\":{deep}}}";
        Assert.False(ResponseSchema.TryParse(Json(deep), out _, out var depthErrors));
        Assert.Contains(depthErrors, e => e.Contains("nesting exceeds depth"));

        var wide = "{\"type\":\"object\",\"properties\":{"
                   + string.Join(",", Enumerable.Range(0, ResponseSchema.MaxNodes + 1).Select(i => $"\"p{i}\":{{}}"))
                   + "}}";
        Assert.False(ResponseSchema.TryParse(Json(wide), out _, out var nodeErrors));
        Assert.Contains(nodeErrors, e => e.Contains("schema nodes"));

        var huge = $"{{\"description\":\"{new string('x', ResponseSchema.MaxSchemaBytes)}\"}}";
        Assert.False(ResponseSchema.TryParse(Json(huge), out _, out var sizeErrors));
        Assert.Contains(sizeErrors, e => e.Contains("bytes"));
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","maxLength":3}},"required":["a"],"additionalProperties":false}""")]
    [InlineData("""{"type":"object","properties":{"a":{"enum":["x"]}},"required":["a"],"additionalProperties":false}""")]
    [InlineData("""{"type":"array","items":{"type":"string"}}""")]
    public void Schemas_outside_openai_strict_mode_are_not_strict_compatible(string json)
        => Assert.False(Parse(json).IsStrictCompatible);

    [Fact]
    public void Root_schema_keyword_and_annotations_are_accepted()
    {
        var schema = Parse("""{"$schema":"https://json-schema.org/draft/2020-12/schema","title":"t","description":"d","const":"x"}""");

        Assert.Empty(schema.Validate(Json("\"x\"")));
        Assert.Equal(["$: must equal \"x\""], schema.Validate(Json("\"y\"")));
    }

    [Theory]
    [InlineData("""{"a":1}""", """{"a":1}""")]
    [InlineData("  [1,2]  ", "[1,2]")]
    [InlineData("Here you go:\n```json\n{\"a\":1}\n```\nDone.", """{"a":1}""")]
    [InlineData("The verdict is {\"a\":\"}{\"} as requested.", """{"a":"}{"}""")]
    [InlineData("noise {not json} then {\"a\":2}", """{"a":2}""")]
    public void Json_is_extracted_deterministically(string text, string expected)
    {
        Assert.True(ResponseSchema.TryExtractJson(text, out var value));
        Assert.Equal(expected, JsonSerializer.Serialize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no json here")]
    [InlineData("{unterminated")]
    public void Absent_json_is_not_invented(string? text)
        => Assert.False(ResponseSchema.TryExtractJson(text, out _));

    [Fact]
    public void Prompt_instruction_carries_the_compact_schema()
    {
        var schema = Parse("{ \"type\" : \"string\" }");

        Assert.Equal("{\"type\":\"string\"}", schema.Json);
        Assert.EndsWith("JSON Schema:\n{\"type\":\"string\"}", schema.PromptInstruction());
    }

    private static ResponseSchema Parse(string json)
    {
        Assert.True(ResponseSchema.TryParse(Json(json), out var schema, out var errors), string.Join("; ", errors));
        return schema!;
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
