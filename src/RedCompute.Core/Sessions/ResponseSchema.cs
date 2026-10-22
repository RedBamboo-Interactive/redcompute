using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RedCompute.Core.Sessions;

/// <summary>
/// A bounded JSON Schema subset for <c>/ai-session/generate</c> structured output. The model is
/// never trusted: every response is validated here, deterministically, whatever the provider did.
///
/// Supported keywords: <c>type</c> (one name or an array of names), <c>properties</c>,
/// <c>required</c>, <c>additionalProperties</c> (boolean or schema), <c>items</c>, <c>enum</c>,
/// <c>const</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>, <c>minimum</c>,
/// <c>maximum</c>, <c>exclusiveMinimum</c>, <c>exclusiveMaximum</c>, <c>minItems</c>,
/// <c>maxItems</c>, and the annotations <c>title</c>, <c>description</c> and root <c>$schema</c>.
/// Anything else (<c>$ref</c>, <c>anyOf</c>, <c>format</c>, ...) is rejected when the schema is
/// parsed, so a caller never believes a constraint was checked when it was not.
/// </summary>
public sealed class ResponseSchema
{
    public const int MaxSchemaBytes = 32 * 1024;
    public const int MaxDepth = 16;
    public const int MaxNodes = 512;
    public const int MaxEnumValues = 256;
    public const int MaxPatternLength = 512;
    public const int MaxErrors = 50;

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly HashSet<string> TypeNames =
        ["string", "number", "integer", "boolean", "object", "array", "null"];

    private static readonly HashSet<string> Keywords =
    [
        "type", "properties", "required", "additionalProperties", "items", "enum", "const",
        "minLength", "maxLength", "pattern", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "minItems", "maxItems", "title", "description",
    ];

    private readonly Node _root;

    /// <summary>The schema as the caller sent it, re-serialized compactly.</summary>
    public string Json { get; }

    /// <summary>
    /// True when the schema also satisfies OpenAI strict structured outputs: an object root, every
    /// node typed, every object closed with all properties required, and no string length bounds.
    /// Providers may only hand such a schema to a native strict decoder; anything else is prompted.
    /// </summary>
    public bool IsStrictCompatible { get; }

    private ResponseSchema(Node root, string json, bool strictCompatible)
    {
        _root = root;
        Json = json;
        IsStrictCompatible = strictCompatible;
    }

    public static bool TryParse(JsonElement schema, out ResponseSchema? parsed, out IReadOnlyList<string> errors)
    {
        parsed = null;
        var problems = new List<string>();
        errors = problems;

        var json = schema.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxSchemaBytes)
        {
            problems.Add($"responseSchema exceeds {MaxSchemaBytes} bytes");
            return false;
        }

        var nodes = 0;
        var root = ParseNode(schema, "$", 0, isRoot: true, problems, ref nodes);
        if (problems.Count > 0 || root is null)
            return false;

        using var compact = JsonDocument.Parse(json);
        parsed = new ResponseSchema(root, JsonSerializer.Serialize(compact.RootElement), IsStrict(root, isRoot: true));
        return true;
    }

    /// <summary>The instruction appended to the system prompt when native enforcement is absent.</summary>
    public string PromptInstruction()
        => "Respond with exactly one JSON value that conforms to the JSON Schema below. "
           + "Output only that JSON: no Markdown fences, no commentary, no text before or after it.\n"
           + "JSON Schema:\n" + Json;

    public IReadOnlyList<string> Validate(JsonElement value)
    {
        var errors = new List<string>();
        ValidateNode(_root, value, "$", errors);
        return errors;
    }

    /// <summary>
    /// Extracts the JSON value a model returned. In order: the whole trimmed text, then each fenced
    /// code block, then the first balanced object or array that parses. Returns a detached clone.
    /// </summary>
    public static bool TryExtractJson(string? text, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (TryParseValue(trimmed, out value)) return true;

        foreach (var block in FencedBlocks(trimmed))
            if (TryParseValue(block, out value)) return true;

        for (var start = 0; start < trimmed.Length; start++)
        {
            if (trimmed[start] is not ('{' or '['))
                continue;
            var end = FindBalancedEnd(trimmed, start);
            if (end > start && TryParseValue(trimmed[start..(end + 1)], out value))
                return true;
        }

        return false;
    }

    private static bool TryParseValue(string candidate, out JsonElement value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(candidate);
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> FencedBlocks(string text)
    {
        var index = 0;
        while ((index = text.IndexOf("```", index, StringComparison.Ordinal)) >= 0)
        {
            var bodyStart = text.IndexOf('\n', index + 3);
            if (bodyStart < 0) yield break;
            var close = text.IndexOf("```", bodyStart + 1, StringComparison.Ordinal);
            if (close < 0) yield break;
            yield return text[(bodyStart + 1)..close].Trim();
            index = close + 3;
        }
    }

    private static int FindBalancedEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    depth--;
                    if (depth == 0) return i;
                    break;
            }
        }

        return -1;
    }

    // --- Parsing ---

    private sealed class Node
    {
        public HashSet<string>? Types;
        public Dictionary<string, Node>? Properties;
        public List<string> Required = [];
        public bool? AdditionalAllowed;
        public Node? AdditionalSchema;
        public Node? Items;
        public List<JsonElement>? Enum;
        public JsonElement? Const;
        public int? MinLength, MaxLength, MinItems, MaxItems;
        public Regex? Pattern;
        public double? Minimum, Maximum, ExclusiveMinimum, ExclusiveMaximum;
    }

    private static Node? ParseNode(JsonElement schema, string path, int depth, bool isRoot,
        List<string> errors, ref int nodes)
    {
        if (++nodes > MaxNodes)
        {
            AddError(errors, $"responseSchema has more than {MaxNodes} schema nodes");
            return null;
        }
        if (depth > MaxDepth)
        {
            AddError(errors, $"{path}: nesting exceeds depth {MaxDepth}");
            return null;
        }
        if (schema.ValueKind != JsonValueKind.Object)
        {
            AddError(errors, $"{path}: a schema must be a JSON object");
            return null;
        }

        var node = new Node();
        foreach (var property in schema.EnumerateObject())
        {
            var keyword = property.Name;
            var value = property.Value;
            if (keyword == "$schema" && isRoot) continue;
            if (!Keywords.Contains(keyword))
            {
                AddError(errors, $"{path}: unsupported keyword '{keyword}'");
                continue;
            }

            switch (keyword)
            {
                case "title" or "description":
                    if (value.ValueKind != JsonValueKind.String)
                        AddError(errors, $"{path}.{keyword}: must be a string");
                    break;
                case "type":
                    node.Types = ParseTypes(value, path, errors);
                    break;
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        AddError(errors, $"{path}.properties: must be an object");
                        break;
                    }
                    node.Properties = new Dictionary<string, Node>(StringComparer.Ordinal);
                    foreach (var child in value.EnumerateObject())
                    {
                        var parsed = ParseNode(child.Value, $"{path}.properties.{child.Name}", depth + 1, false, errors, ref nodes);
                        if (parsed != null) node.Properties[child.Name] = parsed;
                    }
                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array
                        || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                    {
                        AddError(errors, $"{path}.required: must be an array of strings");
                        break;
                    }
                    node.Required = value.EnumerateArray().Select(item => item.GetString()!).Distinct().ToList();
                    break;
                case "additionalProperties":
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        node.AdditionalAllowed = value.GetBoolean();
                    else
                        node.AdditionalSchema = ParseNode(value, $"{path}.additionalProperties", depth + 1, false, errors, ref nodes);
                    break;
                case "items":
                    node.Items = ParseNode(value, $"{path}.items", depth + 1, false, errors, ref nodes);
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
                        AddError(errors, $"{path}.enum: must be a non-empty array");
                    else if (value.GetArrayLength() > MaxEnumValues)
                        AddError(errors, $"{path}.enum: more than {MaxEnumValues} values");
                    else
                        node.Enum = value.EnumerateArray().Select(item => item.Clone()).ToList();
                    break;
                case "const":
                    node.Const = value.Clone();
                    break;
                case "minLength": node.MinLength = ParseCount(value, path, keyword, errors); break;
                case "maxLength": node.MaxLength = ParseCount(value, path, keyword, errors); break;
                case "minItems": node.MinItems = ParseCount(value, path, keyword, errors); break;
                case "maxItems": node.MaxItems = ParseCount(value, path, keyword, errors); break;
                case "minimum": node.Minimum = ParseNumber(value, path, keyword, errors); break;
                case "maximum": node.Maximum = ParseNumber(value, path, keyword, errors); break;
                case "exclusiveMinimum": node.ExclusiveMinimum = ParseNumber(value, path, keyword, errors); break;
                case "exclusiveMaximum": node.ExclusiveMaximum = ParseNumber(value, path, keyword, errors); break;
                case "pattern":
                    node.Pattern = ParsePattern(value, path, errors);
                    break;
            }
        }

        if (node.Properties != null)
            foreach (var name in node.Required.Where(name => !node.Properties.ContainsKey(name)))
                AddError(errors, $"{path}.required: '{name}' is not declared in properties");

        return node;
    }

    private static HashSet<string>? ParseTypes(JsonElement value, string path, List<string> errors)
    {
        var names = value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array when value.GetArrayLength() > 0
                && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
                => value.EnumerateArray().Select(item => item.GetString()!).ToList(),
            _ => null,
        };
        if (names == null)
        {
            AddError(errors, $"{path}.type: must be a type name or a non-empty array of type names");
            return null;
        }

        foreach (var name in names.Where(name => !TypeNames.Contains(name)))
            AddError(errors, $"{path}.type: unknown type '{name}'");
        return names.ToHashSet(StringComparer.Ordinal);
    }

    private static int? ParseCount(JsonElement value, string path, string keyword, List<string> errors)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0)
            return count;
        AddError(errors, $"{path}.{keyword}: must be a non-negative integer");
        return null;
    }

    private static double? ParseNumber(JsonElement value, string path, string keyword, List<string> errors)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number))
            return number;
        AddError(errors, $"{path}.{keyword}: must be a finite number");
        return null;
    }

    private static Regex? ParsePattern(JsonElement value, string path, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            AddError(errors, $"{path}.pattern: must be a string");
            return null;
        }
        var pattern = value.GetString()!;
        if (pattern.Length > MaxPatternLength)
        {
            AddError(errors, $"{path}.pattern: longer than {MaxPatternLength} characters");
            return null;
        }
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException ex)
        {
            AddError(errors, $"{path}.pattern: invalid regular expression ({ex.Message})");
            return null;
        }
    }

    private static bool IsStrict(Node node, bool isRoot)
    {
        if (node.Types is not { Count: > 0 }) return false;
        if (isRoot && !(node.Types.Count == 1 && node.Types.Contains("object"))) return false;
        if (node.MinLength.HasValue || node.MaxLength.HasValue) return false;

        if (node.Types.Contains("object"))
        {
            if (node.Properties is null || node.AdditionalAllowed != false || node.AdditionalSchema != null)
                return false;
            if (!node.Properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(node.Required))
                return false;
            if (node.Properties.Values.Any(child => !IsStrict(child, isRoot: false)))
                return false;
        }

        if (node.Types.Contains("array") && (node.Items is null || !IsStrict(node.Items, isRoot: false)))
            return false;

        return true;
    }

    // --- Validation ---

    private static void ValidateNode(Node node, JsonElement value, string path, List<string> errors)
    {
        if (errors.Count >= MaxErrors) return;

        if (node.Types != null && !node.Types.Any(type => MatchesType(type, value)))
        {
            AddError(errors, $"{path}: expected {string.Join(" or ", node.Types)}, got {Describe(value)}");
            return;
        }

        if (node.Const is { } constant && !JsonEquals(constant, value))
            AddError(errors, $"{path}: must equal {constant.GetRawText()}");
        if (node.Enum != null && !node.Enum.Any(option => JsonEquals(option, value)))
            AddError(errors, $"{path}: must be one of {string.Join(", ", node.Enum.Select(option => option.GetRawText()))}");

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                ValidateString(node, value.GetString()!, path, errors);
                break;
            case JsonValueKind.Number:
                ValidateNumber(node, value.GetDouble(), path, errors);
                break;
            case JsonValueKind.Array:
                ValidateArray(node, value, path, errors);
                break;
            case JsonValueKind.Object:
                ValidateObject(node, value, path, errors);
                break;
        }
    }

    private static void ValidateString(Node node, string text, string path, List<string> errors)
    {
        // JSON Schema counts string length in code points, not UTF-16 units.
        var length = text.EnumerateRunes().Count();
        if (node.MinLength is { } min && length < min)
            AddError(errors, $"{path}: shorter than minLength {min}");
        if (node.MaxLength is { } max && length > max)
            AddError(errors, $"{path}: longer than maxLength {max}");
        if (node.Pattern != null)
        {
            try
            {
                if (!node.Pattern.IsMatch(text))
                    AddError(errors, $"{path}: does not match pattern {node.Pattern}");
            }
            catch (RegexMatchTimeoutException)
            {
                AddError(errors, $"{path}: pattern evaluation timed out");
            }
        }
    }

    private static void ValidateNumber(Node node, double number, string path, List<string> errors)
    {
        if (node.Minimum is { } min && number < min) AddError(errors, $"{path}: less than minimum {min}");
        if (node.Maximum is { } max && number > max) AddError(errors, $"{path}: greater than maximum {max}");
        if (node.ExclusiveMinimum is { } xmin && number <= xmin) AddError(errors, $"{path}: not greater than exclusiveMinimum {xmin}");
        if (node.ExclusiveMaximum is { } xmax && number >= xmax) AddError(errors, $"{path}: not less than exclusiveMaximum {xmax}");
    }

    private static void ValidateArray(Node node, JsonElement array, string path, List<string> errors)
    {
        var count = array.GetArrayLength();
        if (node.MinItems is { } min && count < min) AddError(errors, $"{path}: fewer than minItems {min}");
        if (node.MaxItems is { } max && count > max) AddError(errors, $"{path}: more than maxItems {max}");
        if (node.Items == null) return;

        var index = 0;
        foreach (var item in array.EnumerateArray())
            ValidateNode(node.Items, item, $"{path}[{index++}]", errors);
    }

    private static void ValidateObject(Node node, JsonElement obj, string path, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                AddError(errors, $"{path}: duplicate property '{property.Name}'");
                continue;
            }

            var childPath = $"{path}.{property.Name}";
            if (node.Properties != null && node.Properties.TryGetValue(property.Name, out var child))
                ValidateNode(child, property.Value, childPath, errors);
            else if (node.AdditionalSchema != null)
                ValidateNode(node.AdditionalSchema, property.Value, childPath, errors);
            else if (node.AdditionalAllowed == false)
                AddError(errors, $"{childPath}: property is not allowed");
        }

        foreach (var name in node.Required.Where(name => !seen.Contains(name)))
            AddError(errors, $"{path}: missing required property '{name}'");
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static bool IsInteger(JsonElement value)
        => value.TryGetInt64(out _)
           || (value.TryGetDouble(out var number) && double.IsFinite(number) && Math.Floor(number) == number);

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => IsInteger(value) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return left.GetDouble() == right.GetDouble();
        if (left.ValueKind != right.ValueKind) return false;

        switch (left.ValueKind)
        {
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Array:
                if (left.GetArrayLength() != right.GetArrayLength()) return false;
                return left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => JsonEquals(pair.First, pair.Second));
            case JsonValueKind.Object:
                var leftProperties = left.EnumerateObject().ToList();
                var rightProperties = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return leftProperties.Count == rightProperties.Count
                    && leftProperties.All(p => rightProperties.TryGetValue(p.Name, out var other) && JsonEquals(p.Value, other));
            default:
                return true;
        }
    }

    private static void AddError(List<string> errors, string error)
    {
        if (errors.Count < MaxErrors) errors.Add(error);
    }
}
