using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using RedCompute.Core.Discovery;

namespace RedCompute.App.ViewModels;

public partial class ParameterFieldViewModel : ObservableObject
{
    public string Name { get; }
    public string DisplayName { get; }
    public string FieldType { get; }
    public bool IsRequired { get; }
    public string? Description { get; }
    public List<string>? EnumOptions { get; }
    public double? Min { get; }
    public double? Max { get; }

    [ObservableProperty]
    private object? _value;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ParameterFieldViewModel(string name, ParameterSchema schema)
    {
        Name = name;
        DisplayName = Humanize(name);
        IsRequired = schema.Required;
        Description = schema.Description;
        EnumOptions = schema.Enum;
        Min = schema.Min;
        Max = schema.Max;

        FieldType = schema.Type == "string" && schema.Enum is { Count: > 0 }
            ? "enum"
            : schema.Type;

        Value = UnwrapDefault(schema.Default);
    }

    partial void OnValueChanged(object? value)
    {
        if (ErrorMessage != null)
            Validate();
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    public bool Validate()
    {
        ErrorMessage = null;

        var str = Value?.ToString();
        var isEmpty = string.IsNullOrWhiteSpace(str);

        if (IsRequired && isEmpty)
        {
            ErrorMessage = "Required";
            return false;
        }

        if (isEmpty)
            return true;

        if (FieldType is "number" or "integer")
        {
            if (!double.TryParse(str, CultureInfo.InvariantCulture, out var num))
            {
                ErrorMessage = "Must be a number";
                return false;
            }

            if (Min.HasValue && num < Min.Value)
            {
                ErrorMessage = $"Min {Min.Value}";
                return false;
            }

            if (Max.HasValue && num > Max.Value)
            {
                ErrorMessage = $"Max {Max.Value}";
                return false;
            }

            if (FieldType == "integer" && num != Math.Floor(num))
            {
                ErrorMessage = "Must be a whole number";
                return false;
            }
        }

        return true;
    }

    public object? GetTypedValue()
    {
        var str = Value?.ToString();
        if (string.IsNullOrWhiteSpace(str))
            return null;

        return FieldType switch
        {
            "number" => double.TryParse(str, CultureInfo.InvariantCulture, out var d) ? d : null,
            "integer" => long.TryParse(str, CultureInfo.InvariantCulture, out var l) ? l : null,
            "boolean" => Value is bool b ? b : null,
            _ => str
        };
    }

    private static string Humanize(string name)
    {
        var words = name.Replace('_', ' ').Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(w => char.ToUpper(w[0]) + w[1..]));
    }

    private static object? UnwrapDefault(object? raw)
    {
        if (raw is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.GetDouble().ToString(CultureInfo.InvariantCulture),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => je.GetRawText()
            };
        }

        return raw?.ToString();
    }
}
