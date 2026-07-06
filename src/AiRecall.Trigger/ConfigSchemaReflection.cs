using System.ComponentModel;
using System.Reflection;
using AiRecall.Core.Configuration;

namespace AiRecall.Trigger;

/// <summary>
/// Describes one editable config section (e.g. "App Reader" or "App Reader > Browser").
/// Used by <c>SettingsDialog</c> to populate the TreeView and PropertyGrid
/// (Spec 0009 §Config-Schema via Reflection).
/// </summary>
public sealed class ConfigSectionDescriptor
{
    /// <summary>JSON key in the config file (e.g. <c>"appReader"</c>).</summary>
    public string Name { get; }

    /// <summary>Human-readable label for the TreeView (e.g. <c>"App Reader"</c>).</summary>
    public string DisplayName { get; }

    /// <summary>Hierarchical path with <c>.</c> separator (e.g. <c>"appReader.browser"</c>).</summary>
    public string Path { get; }

    /// <summary>The POCO <see cref="Type"/> (e.g. <c>typeof(BrowserConfig)</c>).</summary>
    public Type SectionType { get; }

    /// <summary>The live POCO instance from the loaded <see cref="AppConfig"/>.</summary>
    public object Instance { get; }

    /// <summary>Sub-sections (only populated for sections that have nested config types, e.g. <c>appReader</c>).</summary>
    public IReadOnlyList<ConfigSectionDescriptor> SubSections { get; }

    /// <summary>Editable properties (filtered: only public, non-indexed, writable).</summary>
    public IReadOnlyList<PropertyDescriptor> Properties { get; }

    public ConfigSectionDescriptor(
        string name,
        string displayName,
        string path,
        Type sectionType,
        object instance,
        IReadOnlyList<ConfigSectionDescriptor> subSections,
        IReadOnlyList<PropertyDescriptor> properties)
    {
        Name = name;
        DisplayName = displayName;
        Path = path;
        SectionType = sectionType;
        Instance = instance;
        SubSections = subSections;
        Properties = properties;
    }
}

/// <summary>
/// Pure-logic reflection-based discovery of <see cref="AppConfig"/> sections.
/// Single source of truth: the POCO types in <see cref="AiRecall.Core.Configuration"/>.
/// Used by <c>SettingsDialog</c> to populate the TreeView + PropertyGrid
/// (Spec 0009 §Config-Schema via Reflection).
/// </summary>
public static class ConfigSchemaReflection
{
    /// <summary>
    /// Returns the top-level editable sections of <paramref name="config"/>:
    /// Capture, ScreenRecorder, Ocr, Logging, AppReader, Trigger, Conversion.
    /// Sub-Sections werden rekursiv aufgeloest: jede Property vom Typ einer
    /// [JsonPropertyName]-POCO-Klasse wird zur eigenen Sub-Section (z. B.
    /// <c>appReader.browser.cdp</c>, <c>trigger.winEvents</c>).
    ///
    /// Bug-Bash 2026-07-06 I-18: Vorher waren Sub-Sub-Konfigs
    /// (CdpConfig, MarkdownSettings, WinEventSubscription, TriggerBlacklist,
    /// OneNoteConfig, TeamsConfig) im TreeView unsichtbar — sie waren
    /// weder Property (wegen IsExpandableConfigType) noch SubSection (harte
    /// Liste). Jetzt: rekursiv.
    /// </summary>
    public static IReadOnlyList<ConfigSectionDescriptor> GetTopLevelSections(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sections = new List<ConfigSectionDescriptor>();

        sections.Add(BuildSection(config.Capture, "Capture", "capture", typeof(CaptureConfig), parentPath: null));
        sections.Add(BuildSection(config.ScreenRecorder, "Screen Recorder", "screenRecorder", typeof(ScreenRecorderConfig), parentPath: null));
        sections.Add(BuildSection(config.Ocr, "OCR", "ocr", typeof(OcrConfig), parentPath: null));
        sections.Add(BuildSection(config.Logging, "Logging", "logging", typeof(LoggingConfig), parentPath: null));
        sections.Add(BuildSection(config.AppReader, "App Reader", "appReader", typeof(AppReaderConfig), parentPath: null));
        sections.Add(BuildSection(config.Trigger, "Trigger", "trigger", typeof(TriggerConfig), parentPath: null));
        sections.Add(BuildSection(config.Conversion, "Conversion", "conversion", typeof(ConversionConfig), parentPath: null));

        return sections;
    }

    /// <summary>
    /// Finds a section by hierarchical path (e.g. <c>"appReader"</c> or
    /// <c>"appReader.browser.cdp"</c>). Returns <c>null</c> if not found.
    /// Bug-Bash 2026-07-06 I-18: jetzt rekursiv, da Sub-Sections selbst
    /// weitere Sub-Sections haben koennen.
    /// </summary>
    public static ConfigSectionDescriptor? FindByPath(AppConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(path);

        foreach (var section in GetTopLevelSections(config))
        {
            var hit = FindRecursive(section, path);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static ConfigSectionDescriptor? FindRecursive(ConfigSectionDescriptor section, string path)
    {
        if (section.Path == path) return section;
        foreach (var sub in section.SubSections)
        {
            var hit = FindRecursive(sub, path);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Baut eine Section inkl. rekursiver Sub-Sections. Jede Property vom Typ
    /// einer expandierbaren POCO (siehe <see cref="IsExpandableConfigType"/>)
    /// wird zur eigenen Sub-Section; die uebrigen Properties landen in
    /// <see cref="ConfigSectionDescriptor.Properties"/>.
    /// </summary>
    /// <param name="parentPath">
    /// Pfad der Parent-Section (z. B. <c>"appReader"</c>). Bei Top-Level
    /// Sections <c>null</c> — dann wird <paramref name="name"/> als Pfad verwendet.
    /// </param>
    private static ConfigSectionDescriptor BuildSection(
        object instance, string displayName, string name, Type sectionType, string? parentPath)
    {
        var path = parentPath is null ? name : parentPath + "." + name;
        var collection = TypeDescriptor.GetProperties(instance);
        var properties = new List<PropertyDescriptor>();
        var subSections = new List<ConfigSectionDescriptor>();

        foreach (PropertyDescriptor? prop in collection)
        {
            if (prop is null) continue;
            if (prop.IsReadOnly) continue;
            if (prop.PropertyType.IsArray) continue;

            // JSON-Name aus [JsonPropertyName] (camelCase, matches config.json).
            // Fallback auf Property-Name (PascalCase) wenn Attribut fehlt.
            var jsonName = prop.GetJsonPropertyName() ?? prop.Name;

            if (IsExpandableConfigType(prop.PropertyType))
            {
                var childInstance = prop.GetValue(instance);
                if (childInstance is null) continue;
                var childDisplay = HumanizeName(jsonName);
                subSections.Add(BuildSection(
                    childInstance, childDisplay, jsonName, prop.PropertyType, parentPath: path));
            }
            else
            {
                properties.Add(prop);
            }
        }

        return new ConfigSectionDescriptor(
            name: name,
            displayName: displayName,
            path: path,
            sectionType: sectionType,
            instance: instance,
            subSections: subSections,
            properties: properties);
    }

    /// <summary>
    /// Macht aus einem camelCase Property-Namen einen Display-Namen
    /// (z. B. <c>"winEvents"</c> → <c>"Win Events"</c>, <c>"cdp"</c> → <c>"CDP"</c>).
    /// </summary>
    private static string HumanizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        // Bekannte Akronyme case-correct
        var acronyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cdp", "CDP" }, { "pdf", "PDF" }, { "ocr", "OCR" }, { "ui", "UI" },
            { "uia", "UIA" }, { "url", "URL" }, { "uri", "URI" }, { "json", "JSON" }
        };
        if (acronyms.TryGetValue(name, out var acr)) return acr;

        // camelCase → "Camel Case": "winEvents" -> "Win Events"
        var sb = new System.Text.StringBuilder(name.Length + 4);
        sb.Append(char.ToUpperInvariant(name[0]));
        for (int i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append(' ');
            sb.Append(name[i]);
        }
        return sb.ToString();
    }

    private static bool IsExpandableConfigType(Type type)
    {
        // Klassen mit eigenen editierbaren Properties werden als Sub-Section
        // expandiert statt als einzelne Property angezeigt.
        //
        // Heuristik: POCO mit mindestens einer oeffentlichen, schreibbaren
        // Instanz-Property, deren Typ selbst "einfach" ist (bool, int, string,
        // enum, List<string>) — dann ist es ein Konfig-Blatt, das wir
        // expandieren sollten.
        //
        // Fruehere Version hat auf [JsonPropertyName] an der Klasse selbst
        // geprueft; das ist immer null, weil das Attribut an Properties haengt.
        // Folge: Sub-Sub-Configs (CdpConfig, OutlookConfig, ...) wurden
        // uebersprungen und waren unsichtbar (Bug-Bash 2026-07-06 I-18).
        if (!type.IsClass) return false;
        if (type == typeof(string)) return false;
        if (type.IsPrimitive) return false;
        if (type.IsGenericType) return false;   // List<>, Dictionary<> etc.
        if (type == typeof(object)) return false;

        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var p in props)
        {
            if (!p.CanWrite) continue;
            if (p.GetIndexParameters().Length > 0) continue;
            if (IsLeafPropertyType(p.PropertyType)) return true;
        }
        return false;
    }

    /// <summary>
    /// True fuer Property-Typen, die als editierbare Zeile (bool/int/long/string/enum/List&lt;string&gt;)
    /// gerendert werden. Komplexere Typen (andere POCOs) zaehlen nicht als
    /// "einfach" und loesen damit keine Section-Expansion der Parent-Klasse aus.
    /// </summary>
    /// <summary>
    /// Liest [JsonPropertyName] aus einem PropertyDescriptor. PropertyDescriptor
    /// hat dafuer keine direkte API; der zugrundeliegende PropertyInfo schon.
    /// </summary>
    private static string? GetJsonPropertyName(this PropertyDescriptor prop)
    {
        if (prop is null) return null;
        // PropertyDescriptor.ComponentType ist der Typ, der den Descriptor besitzt.
        // Die echte PropertyInfo finden wir ueber den Namen.
        var pi = prop.ComponentType.GetProperty(prop.Name);
        if (pi is null) return null;
        var attr = pi.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();
        return attr?.Name;
    }

    private static bool IsLeafPropertyType(Type type)
    {
        if (type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(string))
            return true;
        if (type.IsEnum) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var arg = type.GetGenericArguments()[0];
            return arg == typeof(string);
        }
        // Bug-Bash 2026-07-06 I-20: Nullable<T> ist ebenfalls ein Leaf
        // (Checkbox fuer bool?, TextBox fuer int?/long?, TextBox fuer string?).
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            return IsLeafPropertyType(Nullable.GetUnderlyingType(type)!);
        }
        return false;
    }
}