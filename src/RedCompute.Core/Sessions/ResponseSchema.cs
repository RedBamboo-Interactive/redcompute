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
///
/// <para><c>pattern</c> is compiled with <see cref="RegexOptions.NonBacktracking"/>, so matching is
/// linear in the input. It uses the .NET regex dialect, not ECMA-262 as JSON Schema specifies.
/// Patterns NonBacktracking cannot run (backreferences, lookarounds, atomic groups, conditionals,
/// patterns whose automaton would be too large) are rejected when parsed. Differences that remain:
/// <c>\d</c>, <c>\w</c> and <c>\s</c> match Unicode digits, word and space characters, not just
/// ASCII; <c>$</c> also matches before a final <c>\n</c>; <c>.</c> excludes only <c>\n</c>;
/// .NET-only syntax is accepted (character-class subtraction <c>[a-z-[aeiou]]</c>, inline options
/// <c>(?i)</c>, <c>\p{IsGreek}</c> block names) while ECMA-only syntax (<c>\u{1F600}</c>,
/// <c>\p{Script=Greek}</c>) is not. As in JSON Schema, a pattern is unanchored.</para>
///
/// <para>Model output is untrusted input: extraction and validation never throw on it (unpaired
/// surrogates, hostile nesting, huge text) and both run within fixed work budgets.</para>
/// </summary>
public sealed class ResponseSchema
{
    public const int MaxSchemaBytes = 32 * 1024;
    public const int MaxDepth = 16;
    public const int MaxNodes = 512;
    public const int MaxEnumValues = 256;
    public const int MaxPatternLength = 512;
    public const int MaxErrors = 50;
    /// <summary>Longest model text that extraction will scan.</summary>
    public const int MaxExtractChars = 256 * 1024;
    /// <summary>Aggregate validation work: one unit per value visited plus one per pattern input character.</summary>
    public const int MaxValidationWork = 500_000;
    public static readonly TimeSpan MaxValidationTime = TimeSpan.FromMilliseconds(250);

    // OpenAI strict structured outputs (developers.openai.com/api/docs/guides/structured-outputs,
    // checked 2026-10-22): "up to 5000 object properties total, with up to 10 levels of nesting",
    // "up to 1000 enum values across all enum properties", and at most 15,000 characters across one
    // string enum with more than 250 values. The 120,000-character total cannot be reached under
    // MaxSchemaBytes.
    public const int StrictMaxNestingLevels = 10;
    public const int StrictMaxProperties = 5000;
    public const int StrictMaxEnumValues = 1000;

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
    /// The schema to hand a native strict decoder (root <c>$schema</c> removed), or null when it is
    /// outside what OpenAI strict structured outputs accepts. Conservative by design: anything that
    /// strict mode might reject (const, string length bounds, patterns, wide unions, enum values that
    /// disagree with the type, the size limits above) is prompted instead, never sent to fail as 400.
    /// </summary>
    public string? StrictJson { get; }

    public bool IsStrictCompatible => StrictJson != null;

    private ResponseSchema(Node root, string json, string? strictJson)
    {
        _root = root;
        Json = json;
        StrictJson = strictJson;
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
        var strictJson = IsStrictSchema(root) ? JsonSerializer.Serialize(WithoutRootSchemaKeyword(compact.RootElement)) : null;
        parsed = new ResponseSchema(root, JsonSerializer.Serialize(compact.RootElement), strictJson);
        return true;
    }

    private static Dictionary<string, JsonElement> WithoutRootSchemaKeyword(JsonElement root)
        => root.EnumerateObject()
            .Where(property => property.Name != "$schema")
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);

    /// <summary>The instruction appended to the system prompt when native enforcement is absent.</summary>
    public string PromptInstruction()
        => "Respond with exactly one JSON value that conforms to the JSON Schema below. "
           + "Output only that JSON: no Markdown fences, no commentary, no text before or after it.\n"
           + "JSON Schema:\n" + Json;

    public IReadOnlyList<string> Validate(JsonElement value)
    {
        var context = new ValidationContext();
        try
        {
            ValidateNode(_root, value, "$", context);
        }
        catch (InvalidOperationException)
        {
            // Defence in depth: extraction already rejects undecodable strings and names.
            context.Add("$: value contains text that cannot be decoded (unpaired UTF-16 surrogate)");
        }
        catch (BudgetExceededException)
        {
        }
        return context.Errors;
    }

    private sealed class BudgetExceededException : Exception;

    private sealed class ValidationContext
    {
        private readonly long _deadline = Environment.TickCount64 + (long)MaxValidationTime.TotalMilliseconds;
        private int _work;

        public List<string> Errors { get; } = [];

        public bool Full => Errors.Count >= MaxErrors;

        public void Add(string error)
        {
            if (Errors.Count < MaxErrors) Errors.Add(error);
        }

        /// <summary>Charges work; past either budget, records one error and unwinds validation.</summary>
        public void Spend(int units)
        {
            _work += units;
            if (_work <= MaxValidationWork && Environment.TickCount64 <= _deadline) return;
            Errors.Add("$: validation budget exceeded; the value is too large or complex to check");
            throw new BudgetExceededException();
        }
    }

    /// <summary>
    /// Extracts the JSON value a model returned. The whole message must be the JSON, optionally
    /// wrapped in one Markdown code fence; failing that, the text must contain exactly one balanced
    /// object or array. Two candidates are ambiguous (for example an echoed injected verdict next to
    /// the real one) and extract nothing. The scan is a single linear pass over at most
    /// <see cref="MaxExtractChars"/> characters, and a value whose strings or property names cannot
    /// be decoded (unpaired surrogates) is refused. Returns a detached clone.
    /// </summary>
    public static bool TryExtractJson(string? text, out JsonElement value)
        => TryExtractJson(text, out value, out _);

    public static bool TryExtractJson(string? text, out JsonElement value, out string? error)
    {
        value = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "response is empty";
            return false;
        }
        if (text.Length > MaxExtractChars)
        {
            error = $"response exceeds {MaxExtractChars} characters";
            return false;
        }

        var trimmed = text.Trim();
        if (TryParseValue(StripSingleFence(trimmed), out value, out error))
            return true;
        if (error != null)
            return false;

        var candidates = BalancedCandidates(trimmed, limit: 2);
        switch (candidates.Count)
        {
            case 0:
                error = "response did not contain a JSON value";
                return false;
            case > 1:
                error = "response contained more than one JSON candidate";
                return false;
        }

        var (offset, length) = candidates[0];
        if (TryParseValue(trimmed.Substring(offset, length), out value, out error))
            return true;
        error ??= "response did not contain a JSON value";
        return false;
    }

    /// <summary>
    /// Parses one candidate. A syntax error returns false with a null error (try the next rule);
    /// valid JSON that cannot be decoded returns false with a terminal error.
    /// </summary>
    private static bool TryParseValue(string candidate, out JsonElement value, out string? error)
    {
        value = default;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (!IsDecodable(document.RootElement))
            {
                error = "response JSON contains text that cannot be decoded (unpaired UTF-16 surrogate)";
                return false;
            }
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // JsonDocument.Parse(string) refuses a string holding a lone surrogate character.
            error = "response contains text that cannot be decoded (unpaired UTF-16 surrogate)";
            return false;
        }
    }

    /// <summary>True when every string and property name decodes; System.Text.Json throws otherwise.</summary>
    private static bool IsDecodable(JsonElement root)
    {
        var pending = new Stack<JsonElement>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                var element = pending.Pop();
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        _ = element.GetString();
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) pending.Push(item);
                        break;
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            _ = property.Name;
                            pending.Push(property.Value);
                        }
                        break;
                }
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Removes one Markdown fence enclosing the whole text (<c>```</c> or <c>```json</c>).</summary>
    private static string StripSingleFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal) || !text.EndsWith("```", StringComparison.Ordinal))
            return text;
        var firstLineEnd = text.IndexOf('\n');
        if (firstLineEnd < 0 || firstLineEnd >= text.Length - 3) return text;
        var info = text[3..firstLineEnd].Trim();
        if (info.Length > 0 && !info.Equals("json", StringComparison.OrdinalIgnoreCase)) return text;
        var body = text[(firstLineEnd + 1)..^3];
        return body.Contains("```", StringComparison.Ordinal) ? text : body.Trim();
    }

    /// <summary>
    /// One linear pass that finds top-level balanced <c>{...}</c>/<c>[...]</c> spans. Outside a span,
    /// quotes are prose and ignored; inside, JSON string escapes are honoured. A mismatched closer
    /// abandons the current span and scanning continues from the next character, so nothing is
    /// rescanned. Stops after <paramref name="limit"/> spans.
    /// </summary>
    private static List<(int Offset, int Length)> BalancedCandidates(string text, int limit)
    {
        var spans = new List<(int, int)>();
        var stack = new Stack<char>();
        var start = -1;
        var inString = false;
        for (var i = 0; i < text.Length && spans.Count < limit; i++)
        {
            var c = text[i];
            if (stack.Count == 0)
            {
                if (c is '{' or '[')
                {
                    start = i;
                    stack.Push(c == '{' ? '}' : ']');
                }
                continue;
            }

            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    stack.Push('}');
                    break;
                case '[':
                    stack.Push(']');
                    break;
                case '}' or ']':
                    if (stack.Pop() != c)
                    {
                        stack.Clear();
                        break;
                    }
                    if (stack.Count == 0) spans.Add((start, i - start + 1));
                    break;
            }
        }
        return spans;
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
                    else if (!IsDecodable(value))
                        AddError(errors, $"{path}.enum: contains text that cannot be decoded");
                    else
                        node.Enum = value.EnumerateArray().Select(item => item.Clone()).ToList();
                    break;
                case "const":
                    if (IsDecodable(value)) node.Const = value.Clone();
                    else AddError(errors, $"{path}.const: contains text that cannot be decoded");
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
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, PatternTimeout);
        }
        catch (ArgumentException ex)
        {
            AddError(errors, $"{path}.pattern: invalid regular expression ({ex.Message})");
            return null;
        }
        catch (NotSupportedException ex)
        {
            AddError(errors, $"{path}.pattern: not supported by the linear-time regex engine ({ex.Message})");
            return null;
        }
    }

    private static bool IsStrictSchema(Node root)
    {
        if (root.Types is not { Count: 1 } || !root.Types.Contains("object")) return false;
        var totals = new StrictTotals();
        return IsStrict(root, level: 1, totals)
               && totals.Properties <= StrictMaxProperties
               && totals.EnumValues <= StrictMaxEnumValues;
    }

    private sealed class StrictTotals
    {
        public int Properties;
        public int EnumValues;
    }

    private static bool IsStrict(Node node, int level, StrictTotals totals)
    {
        if (level > StrictMaxNestingLevels) return false;
        if (node.Types is not { Count: > 0 } types) return false;
        // One type, or one type plus null: wider unions are anyOf territory.
        if (types.Count > 2 || (types.Count == 2 && !types.Contains("null"))) return false;
        if (node.MinLength.HasValue || node.MaxLength.HasValue || node.Pattern != null || node.Const.HasValue)
            return false;

        if (node.Enum != null)
        {
            if (node.Enum.Any(option => !types.Any(type => MatchesType(type, option)))) return false;
            totals.EnumValues += node.Enum.Count;
            if (node.Enum.Count > 250
                && node.Enum.Where(option => option.ValueKind == JsonValueKind.String).Sum(option => option.GetString()!.Length) > 15_000)
                return false;
        }

        if (types.Contains("object"))
        {
            if (node.Properties is null || node.AdditionalAllowed != false || node.AdditionalSchema != null)
                return false;
            if (!node.Properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(node.Required))
                return false;
            totals.Properties += node.Properties.Count;
            if (node.Properties.Values.Any(child => !IsStrict(child, level + 1, totals)))
                return false;
        }
        else if (node.Properties != null || node.AdditionalSchema != null || node.Required.Count > 0)
        {
            return false;
        }

        if (types.Contains("array"))
        {
            if (node.Items is null || !IsStrict(node.Items, level + 1, totals)) return false;
        }
        else if (node.Items != null || node.MinItems.HasValue || node.MaxItems.HasValue)
        {
            return false;
        }

        return true;
    }

    // --- Validation ---

    private static void ValidateNode(Node node, JsonElement value, string path, ValidationContext context)
    {
        if (context.Full) return;
        context.Spend(1);

        if (node.Types != null && !node.Types.Any(type => MatchesType(type, value)))
        {
            context.Add($"{path}: expected {string.Join(" or ", node.Types)}, got {Describe(value)}");
            return;
        }

        if (node.Const is { } constant && !JsonEquals(constant, value, context))
            context.Add($"{path}: must equal {constant.GetRawText()}");
        if (node.Enum != null && !node.Enum.Any(option => JsonEquals(option, value, context)))
            context.Add($"{path}: must be one of {string.Join(", ", node.Enum.Select(option => option.GetRawText()))}");

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (TryDecode(value, out var text))
                    ValidateString(node, text, path, context);
                else
                    context.Add($"{path}: string cannot be decoded (unpaired UTF-16 surrogate)");
                break;
            case JsonValueKind.Number:
                ValidateNumber(node, value.GetDouble(), path, context);
                break;
            case JsonValueKind.Array:
                ValidateArray(node, value, path, context);
                break;
            case JsonValueKind.Object:
                ValidateObject(node, value, path, context);
                break;
        }
    }

    private static bool TryDecode(JsonElement value, out string text)
    {
        try
        {
            text = value.GetString()!;
            return true;
        }
        catch (InvalidOperationException)
        {
            text = "";
            return false;
        }
    }

    private static void ValidateString(Node node, string text, string path, ValidationContext context)
    {
        // JSON Schema counts string length in code points, not UTF-16 units.
        var length = text.EnumerateRunes().Count();
        if (node.MinLength is { } min && length < min)
            context.Add($"{path}: shorter than minLength {min}");
        if (node.MaxLength is { } max && length > max)
            context.Add($"{path}: longer than maxLength {max}");
        if (node.Pattern != null)
        {
            context.Spend(text.Length);
            try
            {
                if (!node.Pattern.IsMatch(text))
                    context.Add($"{path}: does not match pattern {node.Pattern}");
            }
            catch (RegexMatchTimeoutException)
            {
                context.Add($"{path}: pattern evaluation timed out");
            }
        }
    }

    private static void ValidateNumber(Node node, double number, string path, ValidationContext context)
    {
        if (node.Minimum is { } min && number < min) context.Add($"{path}: less than minimum {min}");
        if (node.Maximum is { } max && number > max) context.Add($"{path}: greater than maximum {max}");
        if (node.ExclusiveMinimum is { } xmin && number <= xmin) context.Add($"{path}: not greater than exclusiveMinimum {xmin}");
        if (node.ExclusiveMaximum is { } xmax && number >= xmax) context.Add($"{path}: not less than exclusiveMaximum {xmax}");
    }

    private static void ValidateArray(Node node, JsonElement array, string path, ValidationContext context)
    {
        var count = array.GetArrayLength();
        if (node.MinItems is { } min && count < min) context.Add($"{path}: fewer than minItems {min}");
        if (node.MaxItems is { } max && count > max) context.Add($"{path}: more than maxItems {max}");
        if (node.Items == null) return;

        var index = 0;
        foreach (var item in array.EnumerateArray())
            ValidateNode(node.Items, item, $"{path}[{index++}]", context);
    }

    private static void ValidateObject(Node node, JsonElement obj, string path, ValidationContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            context.Spend(1);
            if (!seen.Add(property.Name))
            {
                context.Add($"{path}: duplicate property '{property.Name}'");
                continue;
            }

            var childPath = $"{path}.{property.Name}";
            if (node.Properties != null && node.Properties.TryGetValue(property.Name, out var child))
                ValidateNode(child, property.Value, childPath, context);
            else if (node.AdditionalSchema != null)
                ValidateNode(node.AdditionalSchema, property.Value, childPath, context);
            else if (node.AdditionalAllowed == false)
                context.Add($"{childPath}: property is not allowed");
        }

        foreach (var name in node.Required.Where(name => !seen.Contains(name)))
            context.Add($"{path}: missing required property '{name}'");
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

    /// <summary>
    /// Deep equality of a schema constant (<paramref name="expected"/>, decodable by construction)
    /// and a model value. Strings compare with <see cref="JsonElement.ValueEquals(string)"/>; a model
    /// string that cannot be decoded simply does not match.
    /// </summary>
    private static bool JsonEquals(JsonElement expected, JsonElement actual, ValidationContext context)
    {
        context.Spend(1);
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            return expected.GetDouble() == actual.GetDouble();
        if (expected.ValueKind != actual.ValueKind) return false;

        switch (expected.ValueKind)
        {
            case JsonValueKind.String:
                try
                {
                    return actual.ValueEquals(expected.GetString());
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            case JsonValueKind.Array:
                if (expected.GetArrayLength() != actual.GetArrayLength()) return false;
                return expected.EnumerateArray().Zip(actual.EnumerateArray())
                    .All(pair => JsonEquals(pair.First, pair.Second, context));
            case JsonValueKind.Object:
                var expectedProperties = expected.EnumerateObject().ToList();
                if (expectedProperties.Count != actual.EnumerateObject().Count()) return false;
                return expectedProperties.All(property =>
                    actual.EnumerateObject().Count(other => other.NameEquals(property.Name)) == 1
                    && JsonEquals(property.Value, actual.EnumerateObject().First(other => other.NameEquals(property.Name)).Value, context));
            default:
                return true;
        }
    }

    private static void AddError(List<string> errors, string error)
    {
        if (errors.Count < MaxErrors) errors.Add(error);
    }
}
