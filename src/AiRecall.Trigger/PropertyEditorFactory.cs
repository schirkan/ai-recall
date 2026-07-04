using System.ComponentModel;

namespace AiRecall.Trigger;

/// <summary>
/// Pure-logic factory that converts a <see cref="PropertyDescriptor"/> into
/// a string representation suitable for the dynamic SettingsDialog form
/// (Spec 0009 §Config-Schema via Reflection §Custom-Type-Editoren).
///
/// Returns three values:
///   <list type="bullet">
///     <item><c>DisplayKind</c> — which control kind to render</item>
///     <item><c>DisplayText</c> — current value rendered as text</item>
///     <item><c>ParseValue(text)</c> — parses edited text back to the property type</item>
///   </list>
///
/// Lives outside the WinForms UI so it can be unit-tested.
/// </summary>
public static class PropertyEditorFactory
{
    public enum EditorKind
    {
        TextBox,           // string, int, long (parsed)
        CheckBox,          // bool
        ComboBox,          // enum
        ListStringTextBox, // List<string> als comma-separated
        ReadOnly           // nicht editierbar
    }

    public readonly record struct EditorInfo(EditorKind Kind, string DisplayText, Func<string, object?>? Parser);

    public static EditorInfo GetEditor(PropertyDescriptor prop)
    {
        ArgumentNullException.ThrowIfNull(prop);

        if (prop.IsReadOnly) return new EditorInfo(EditorKind.ReadOnly, prop.GetValue(null)?.ToString() ?? "", null);

        var type = prop.PropertyType;

        if (type == typeof(bool))
        {
            var v = (bool)prop.GetValue(null)!;
            return new EditorInfo(EditorKind.CheckBox, v ? "true" : "false", s => bool.Parse(s));
        }

        if (type == typeof(int))
        {
            var v = (int)prop.GetValue(null)!;
            return new EditorInfo(EditorKind.TextBox, v.ToString(), s => int.Parse(s));
        }

        if (type == typeof(long))
        {
            var v = (long)prop.GetValue(null)!;
            return new EditorInfo(EditorKind.TextBox, v.ToString(), s => long.Parse(s));
        }

        if (type == typeof(string))
        {
            var v = (string?)prop.GetValue(null) ?? "";
            return new EditorInfo(EditorKind.TextBox, v, s => s);
        }

        if (type.IsEnum)
        {
            var v = prop.GetValue(null);
            return new EditorInfo(EditorKind.ComboBox, v?.ToString() ?? "", s => Enum.Parse(type, s, ignoreCase: true));
        }

        if (type == typeof(List<string>) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var list = (List<string>?)prop.GetValue(null);
            var joined = list is null ? "" : string.Join(", ", list);
            return new EditorInfo(EditorKind.ListStringTextBox, joined, s =>
            {
                if (string.IsNullOrEmpty(s)) return new List<string>();
                return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            });
        }

        return new EditorInfo(EditorKind.ReadOnly, $"({type.Name})", null);
    }
}