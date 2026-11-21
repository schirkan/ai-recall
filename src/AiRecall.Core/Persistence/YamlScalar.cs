using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiRecall.Core.Persistence;

/// <summary>Renders arbitrary text as a double-quoted YAML scalar on one physical line.</summary>
public static class YamlScalar
{
    public static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";

        var output = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': output.Append("\\\\"); break;
                case '"': output.Append("\\\""); break;
                case '\t': output.Append("\\t"); break;
                case '\r': output.Append("\\r"); break;
                case '\n': output.Append("\\n"); break;
                default:
                    if (char.IsControl(ch) || ch is '\u2028' or '\u2029' or '\uFEFF' or '\uFFFE' or '\uFFFF')
                        output.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        output.Append(ch);
                    break;
            }
        }
        return output.Append('"').ToString();
    }

    /// <summary>Reads scalars written by Quote and tolerates legacy unescaped values.</summary>
    public static string Read(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        try { return JsonSerializer.Deserialize<string>(value)!; }
        catch (JsonException) { return value[1..^1]; }
    }
}
