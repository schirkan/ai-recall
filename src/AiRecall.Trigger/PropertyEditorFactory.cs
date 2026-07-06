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
/// The descriptor + the owning <paramref name="instance"/> must both be
/// passed because the descriptor typically comes from
/// <c>TypeDescriptor.GetProperties(instance)</c> (instance-bound); calling
/// <c>prop.GetValue(null)</c> on such a descriptor returns the type's
/// default value, not the instance's current value — which would silently
/// overwrite real config with defaults on save. See bug-bash 2026-07-05
/// finding I-1.
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

    public static EditorInfo GetEditor(PropertyDescriptor prop, object? instance)
    {
        ArgumentNullException.ThrowIfNull(prop);

        if (prop.IsReadOnly) return new EditorInfo(EditorKind.ReadOnly, prop.GetValue(instance)?.ToString() ?? "", null);

        var type = prop.PropertyType;

        // Bug-Bash 2026-07-06 I-20: Nullable<T> explizit behandeln, damit der
        // Fallback nicht "(Nullable`1)" anzeigt (z. B. fuer MarkdownSettings.GithubFlavored,
        // OneNoteConfig.HierarchyDepth, TeamsConfig.PreferredStrategy).
        // Strategie: DisplayText = "null" wenn kein Wert; Parser konvertiert
        // "null"/"" zurueck in null. Fuer bool? speziell: tri-state CheckBox
        // (null wird als unchecked dargestellt; explizites "true"/"false" beim
        // Save behaelt null nur wenn der Text "null" war).
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var inner = Nullable.GetUnderlyingType(type)!;
            var current = prop.GetValue(instance);
            var display = current?.ToString() ?? "null";

            if (inner == typeof(bool))
            {
                // bool?: CheckBox. Wir koennen null nicht visuell tri-state
                // abbilden, aber DisplayText="null" macht es sichtbar. Beim
                // Save: nur wenn Original null war UND der User die Box
                // nicht angefasst hat (also "null" zurueckkommt), geben wir
                // null zurueck. Sobald der User "true"/"false" speichert,
                // ist der Wert nicht mehr null.
                var wasNull = current is null;
                var boolDisplay = current is bool b ? (b ? "true" : "false") : "null";
                return new EditorInfo(EditorKind.CheckBox, boolDisplay, s =>
                {
                    if (wasNull && s == "null") return null;
                    return bool.Parse(s);
                });
            }

            if (inner == typeof(int))
            {
                return new EditorInfo(EditorKind.TextBox, display, s =>
                    string.IsNullOrEmpty(s) || s == "null" ? null : int.Parse(s));
            }

            if (inner == typeof(long))
            {
                return new EditorInfo(EditorKind.TextBox, display, s =>
                    string.IsNullOrEmpty(s) || s == "null" ? null : long.Parse(s));
            }

            if (inner == typeof(string))
            {
                return new EditorInfo(EditorKind.TextBox, current as string ?? "", s =>
                    string.IsNullOrEmpty(s) ? null : s);
            }

            if (inner.IsEnum)
            {
                return new EditorInfo(EditorKind.ComboBox, display, s =>
                    string.IsNullOrEmpty(s) || s == "null"
                        ? null
                        : Enum.Parse(inner, s, ignoreCase: true));
            }
        }

        if (type == typeof(bool))
        {
            var v = (bool)prop.GetValue(instance)!;
            return new EditorInfo(EditorKind.CheckBox, v ? "true" : "false", s => bool.Parse(s));
        }

        if (type == typeof(int))
        {
            var v = (int)prop.GetValue(instance)!;
            return new EditorInfo(EditorKind.TextBox, v.ToString(), s => int.Parse(s));
        }

        if (type == typeof(long))
        {
            var v = (long)prop.GetValue(instance)!;
            return new EditorInfo(EditorKind.TextBox, v.ToString(), s => long.Parse(s));
        }

        if (type == typeof(string))
        {
            var v = (string?)prop.GetValue(instance) ?? "";
            return new EditorInfo(EditorKind.TextBox, v, s => s);
        }

        if (type.IsEnum)
        {
            var v = prop.GetValue(instance);
            return new EditorInfo(EditorKind.ComboBox, v?.ToString() ?? "", s => Enum.Parse(type, s, ignoreCase: true));
        }

        if (type == typeof(List<string>) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
        {
            var list = (List<string>?)prop.GetValue(instance);
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