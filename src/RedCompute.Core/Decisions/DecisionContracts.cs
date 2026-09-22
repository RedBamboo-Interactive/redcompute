using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RedCompute.Core.Decisions;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ScoreQuestion), "score")]
[JsonDerivedType(typeof(NoulQuestion), "noul")]
public abstract class DecisionQuestion
{
    [JsonPropertyName("instructions")]
    public required JsonElement Instructions { get; init; }
}

public sealed class ChoiceQuestion : DecisionQuestion
{
    [JsonPropertyName("criteria")]
    public required Dictionary<string, JsonElement> Criteria { get; init; }
}

public sealed class ScoreQuestion : DecisionQuestion
{
    [JsonPropertyName("criteria")]
    public required List<JsonElement> Criteria { get; init; }
}

public sealed class NoulQuestion : DecisionQuestion
{
    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Criteria { get; init; }
}

public sealed class DecisionRequest
{
    [JsonPropertyName("state")]
    public required JsonElement State { get; init; }

    [JsonPropertyName("questions")]
    public required Dictionary<string, DecisionQuestion> Questions { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ScoreAnswer), "score")]
[JsonDerivedType(typeof(NoulAnswer), "noul")]
public abstract class DecisionAnswer;

public sealed class ChoiceAnswer : DecisionAnswer
{
    [JsonPropertyName("choice")]
    public required string Choice { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("probabilities")]
    public required Dictionary<string, double> Probabilities { get; init; }
}

public sealed class ScoreAnswer : DecisionAnswer
{
    [JsonPropertyName("score")]
    public required double Score { get; init; }

    [JsonPropertyName("confidence")]
    public required double Confidence { get; init; }

    [JsonPropertyName("legend")]
    public required Dictionary<string, string> Legend { get; init; }

    [JsonPropertyName("probabilities")]
    public required Dictionary<string, double> Probabilities { get; init; }
}

public sealed class NoulAnswer : DecisionAnswer
{
    [JsonPropertyName("noul")]
    public required double Noul { get; init; }

    [JsonPropertyName("probabilities")]
    public required Dictionary<string, double> Probabilities { get; init; }
}

public sealed class DecisionTiming
{
    [JsonPropertyName("queueMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? QueueMs { get; init; }

    [JsonPropertyName("modelMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ModelMs { get; init; }

    [JsonPropertyName("totalMs")]
    public required double TotalMs { get; init; }
}

public sealed class DecisionResponse
{
    [JsonPropertyName("contractVersion")]
    public string ContractVersion { get; init; } = DecisionContract.Version;

    [JsonPropertyName("answers")]
    public required Dictionary<string, DecisionAnswer> Answers { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("modelRevision")]
    public required string ModelRevision { get; init; }

    [JsonPropertyName("calibrationRevision")]
    public required string CalibrationRevision { get; init; }

    [JsonPropertyName("timing")]
    public required DecisionTiming Timing { get; init; }

    [JsonPropertyName("actionAuthority")]
    public bool ActionAuthority => false;
}

public sealed record DecisionValidationResult(
    DecisionRequest? Request,
    Dictionary<string, string> Errors)
{
    public bool IsValid => Request is not null && Errors.Count == 0;
}

public static class DecisionContract
{
    public const string Version = "1.0";
    public const int MaxRequestBytes = 1_048_576;
    public const int MaxStateBytes = 262_144;
    public const int MaxInstructionsBytes = 16_384;
    public const int MaxQuestions = 64;
    public const int MaxOptions = 255;
    public const int MaxIdentifierLength = 128;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static DecisionValidationResult Validate(Dictionary<string, object?> parameters)
    {
        JsonElement root;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters);
            if (bytes.Length > MaxRequestBytes)
                return Invalid("$", $"request must be at most {MaxRequestBytes} UTF-8 bytes");
            using var document = JsonDocument.Parse(bytes);
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Invalid("$", "request is not valid JSON");
        }

        var errors = ValidateElement(root);
        if (errors.Count > 0)
            return new DecisionValidationResult(null, errors);

        try
        {
            var request = JsonSerializer.Deserialize<DecisionRequest>(root.GetRawText(), StrictJson);
            return request is null
                ? Invalid("$", "request is required")
                : new DecisionValidationResult(request, errors);
        }
        catch (JsonException ex)
        {
            return Invalid(string.IsNullOrWhiteSpace(ex.Path) ? "$" : ex.Path!, ex.Message);
        }
    }

    public static Dictionary<string, string> ValidateOnly(Dictionary<string, object?> parameters)
        => Validate(parameters).Errors;

    private static Dictionary<string, string> ValidateElement(JsonElement root)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors["$"] = "must be an object";
            return errors;
        }

        RejectUnknown(root, "$", ["state", "questions"], errors);
        if (!root.TryGetProperty("state", out var state))
            errors["state"] = "required";
        else if (state.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            errors["state"] = "must be a string, object, or array";
        else if (Utf8Size(state) > MaxStateBytes)
            errors["state"] = $"must be at most {MaxStateBytes} UTF-8 bytes";

        if (!root.TryGetProperty("questions", out var questions))
        {
            errors["questions"] = "required";
            return errors;
        }
        if (questions.ValueKind != JsonValueKind.Object)
        {
            errors["questions"] = "must be an object map";
            return errors;
        }

        var entries = questions.EnumerateObject().ToList();
        if (entries.Count is < 1 or > MaxQuestions)
            errors["questions"] = $"must contain 1..{MaxQuestions} questions";

        foreach (var entry in entries)
        {
            var path = $"questions.{entry.Name}";
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > MaxIdentifierLength)
            {
                errors[path] = $"question id must contain 1..{MaxIdentifierLength} characters";
                continue;
            }
            ValidateQuestion(entry.Value, path, errors);
        }
        return errors;
    }

    private static void ValidateQuestion(
        JsonElement question,
        string path,
        Dictionary<string, string> errors)
    {
        if (question.ValueKind != JsonValueKind.Object)
        {
            errors[path] = "must be an object";
            return;
        }

        RejectUnknown(question, path, ["type", "instructions", "criteria"], errors);
        if (!question.TryGetProperty("type", out var typeNode) ||
            typeNode.ValueKind != JsonValueKind.String)
        {
            errors[$"{path}.type"] = "required and must be one of: choice, score, noul";
            return;
        }

        var type = typeNode.GetString();
        if (type is not ("choice" or "score" or "noul"))
        {
            errors[$"{path}.type"] = "must be one of: choice, score, noul";
            return;
        }

        if (!question.TryGetProperty("instructions", out var instructions))
            errors[$"{path}.instructions"] = "required";
        else if (instructions.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            errors[$"{path}.instructions"] = "must not be null";
        else if (Utf8Size(instructions) > MaxInstructionsBytes)
            errors[$"{path}.instructions"] = $"must be at most {MaxInstructionsBytes} UTF-8 bytes";

        question.TryGetProperty("criteria", out var criteria);
        switch (type)
        {
            case "choice":
                ValidateChoice(criteria, path, errors);
                break;
            case "score":
                ValidateScore(criteria, path, errors);
                break;
            case "noul":
                ValidateNoul(criteria, path, errors);
                break;
        }
    }

    private static void ValidateChoice(
        JsonElement criteria,
        string path,
        Dictionary<string, string> errors)
    {
        var criteriaPath = $"{path}.criteria";
        if (criteria.ValueKind != JsonValueKind.Object)
        {
            errors[criteriaPath] = "required and must be an object map";
            return;
        }

        var options = criteria.EnumerateObject().ToList();
        if (options.Count is < 1 or > MaxOptions)
            errors[criteriaPath] = $"must contain 1..{MaxOptions} options";
        foreach (var option in options)
        {
            var optionPath = $"{criteriaPath}.{option.Name}";
            if (string.IsNullOrWhiteSpace(option.Name) || option.Name.Length > MaxIdentifierLength)
                errors[optionPath] = $"option id must contain 1..{MaxIdentifierLength} characters";
            else if (Utf8Size(option.Value) > MaxInstructionsBytes)
                errors[optionPath] = $"description must be at most {MaxInstructionsBytes} UTF-8 bytes";
        }
    }

    private static void ValidateScore(
        JsonElement criteria,
        string path,
        Dictionary<string, string> errors)
    {
        var criteriaPath = $"{path}.criteria";
        if (criteria.ValueKind != JsonValueKind.Array)
        {
            errors[criteriaPath] = "required and must be an ordered array";
            return;
        }

        var levels = criteria.EnumerateArray().ToList();
        if (levels.Count is < 2 or > MaxOptions)
            errors[criteriaPath] = $"must contain 2..{MaxOptions} ordered levels";
        for (var i = 0; i < levels.Count; i++)
        {
            if (Utf8Size(levels[i]) > MaxInstructionsBytes)
                errors[$"{criteriaPath}.{i}"] =
                    $"level must be at most {MaxInstructionsBytes} UTF-8 bytes";
        }
    }

    private static void ValidateNoul(
        JsonElement criteria,
        string path,
        Dictionary<string, string> errors)
    {
        if (criteria.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;
        var criteriaPath = $"{path}.criteria";
        if (criteria.ValueKind != JsonValueKind.Object)
        {
            errors[criteriaPath] = "must be an object with optional false and true descriptions";
            return;
        }

        RejectUnknown(criteria, criteriaPath, ["false", "true"], errors);
        foreach (var item in criteria.EnumerateObject())
        {
            if (Utf8Size(item.Value) > MaxInstructionsBytes)
                errors[$"{criteriaPath}.{item.Name}"] =
                    $"description must be at most {MaxInstructionsBytes} UTF-8 bytes";
        }
    }

    private static void RejectUnknown(
        JsonElement value,
        string path,
        HashSet<string> allowed,
        Dictionary<string, string> errors)
    {
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                errors[path == "$" ? property.Name : $"{path}.{property.Name}"] = "unknown field";
    }

    private static int Utf8Size(JsonElement element)
        => Encoding.UTF8.GetByteCount(element.GetRawText());

    private static DecisionValidationResult Invalid(string path, string message)
        => new(null, new Dictionary<string, string> { [path] = message });

    public static object RequestSchema { get; } = BuildRequestSchema();
    public static object ResponseSchema { get; } = BuildResponseSchema();

    private static Dictionary<string, object?> BuildRequestSchema()
    {
        var content = new Dictionary<string, object?>
        {
            ["oneOf"] = new object[]
            {
                new { type = "string" },
                new { type = "object" },
                new { type = "array" },
                new { type = "number" },
                new { type = "boolean" },
            }
        };
        var nullableContent = new Dictionary<string, object?>
        {
            ["oneOf"] = ((object[])content["oneOf"]!).Append(new { type = "null" }).ToArray(),
        };
        var questionBase = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["required"] = new[] { "type", "instructions" },
            ["additionalProperties"] = false,
        };
        object Question(string type, object criteria, bool criteriaRequired)
        {
            var schema = new Dictionary<string, object?>(questionBase)
            {
                ["properties"] = new Dictionary<string, object?>
                {
                    ["type"] = new { type = "string", @const = type },
                    ["instructions"] = content,
                    ["criteria"] = criteria,
                }
            };
            if (criteriaRequired)
                schema["required"] = new[] { "type", "instructions", "criteria" };
            return schema;
        }

        var choiceCriteria = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["minProperties"] = 1,
            ["maxProperties"] = MaxOptions,
            ["additionalProperties"] = new Dictionary<string, object?>
            {
                ["oneOf"] = nullableContent["oneOf"]
            }
        };
        var scoreCriteria = new Dictionary<string, object?>
        {
            ["type"] = "array",
            ["minItems"] = 2,
            ["maxItems"] = MaxOptions,
            ["items"] = nullableContent,
        };
        var noulCriteria = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new Dictionary<string, object?>
            {
                ["false"] = nullableContent,
                ["true"] = nullableContent,
            }
        };

        return new Dictionary<string, object?>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Decision request",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[] { "state", "questions" },
            ["properties"] = new Dictionary<string, object?>
            {
                ["state"] = new Dictionary<string, object?>
                {
                    ["oneOf"] = new object[]
                    {
                        new { type = "string" },
                        new { type = "object" },
                        new { type = "array" },
                    },
                    ["description"] = "The bounded state evaluated independently by every question.",
                },
                ["questions"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["minProperties"] = 1,
                    ["maxProperties"] = MaxQuestions,
                    ["additionalProperties"] = new Dictionary<string, object?>
                    {
                        ["oneOf"] = new[]
                        {
                            Question("choice", choiceCriteria, true),
                            Question("score", scoreCriteria, true),
                            Question("noul", noulCriteria, false),
                        },
                        ["discriminator"] = new { propertyName = "type" },
                    }
                },
            },
        };
    }

    private static Dictionary<string, object?> BuildResponseSchema()
    {
        object Distribution() => new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["additionalProperties"] = new { type = "number", minimum = 0, maximum = 1 },
        };
        object Answer(string type, Dictionary<string, object?> specific)
        {
            specific["type"] = new { type = "string", @const = type };
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = specific.Keys.ToArray(),
                ["properties"] = specific,
            };
        }

        var choice = Answer("choice", new Dictionary<string, object?>
        {
            ["choice"] = new { type = "string" },
            ["confidence"] = new
            {
                type = "number", minimum = 0, maximum = 1,
                description = "Distribution concentration, not probability of correctness.",
            },
            ["probabilities"] = Distribution(),
        });
        var score = Answer("score", new Dictionary<string, object?>
        {
            ["score"] = new { type = "number" },
            ["confidence"] = new
            {
                type = "number", minimum = 0, maximum = 1,
                description = "Distribution concentration, not probability of correctness.",
            },
            ["legend"] = new { type = "object", additionalProperties = new { type = "string" } },
            ["probabilities"] = Distribution(),
        });
        var noul = Answer("noul", new Dictionary<string, object?>
        {
            ["noul"] = new
            {
                type = "number", minimum = 0, maximum = 1,
                description = "Probability that the proposition is true; not action authority.",
            },
            ["probabilities"] = Distribution(),
        });

        return new Dictionary<string, object?>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Decision response",
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new[]
            {
                "contractVersion", "answers", "provider", "model", "modelRevision",
                "calibrationRevision", "timing", "actionAuthority",
            },
            ["properties"] = new Dictionary<string, object?>
            {
                ["contractVersion"] = new { type = "string", @const = Version },
                ["answers"] = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = new Dictionary<string, object?>
                    {
                        ["oneOf"] = new[] { choice, score, noul },
                        ["discriminator"] = new { propertyName = "type" },
                    }
                },
                ["provider"] = new { type = "string" },
                ["model"] = new { type = "string" },
                ["modelRevision"] = new { type = "string" },
                ["calibrationRevision"] = new { type = "string" },
                ["timing"] = new
                {
                    type = "object",
                    properties = new
                    {
                        queueMs = new { type = "number", minimum = 0 },
                        modelMs = new { type = "number", minimum = 0 },
                        totalMs = new { type = "number", minimum = 0 },
                    },
                    required = new[] { "totalMs" },
                },
                ["actionAuthority"] = new
                {
                    type = "boolean", @const = false,
                    description = "A decision result is advisory and grants no authority to act.",
                },
            },
        };
    }
}
