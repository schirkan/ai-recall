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
    /// The <c>AppReader</c> section has nested sub-sections (Outlook, Browser, Notepad, Documents, Pdf).
    /// </summary>
    public static IReadOnlyList<ConfigSectionDescriptor> GetTopLevelSections(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var sections = new List<ConfigSectionDescriptor>();

        sections.Add(Make(config.Capture, "Capture", "capture", sectionType: typeof(CaptureConfig)));
        sections.Add(Make(config.ScreenRecorder, "Screen Recorder", "screenRecorder", sectionType: typeof(ScreenRecorderConfig)));
        sections.Add(Make(config.Ocr, "OCR", "ocr", sectionType: typeof(OcrConfig)));
        sections.Add(Make(config.Logging, "Logging", "logging", sectionType: typeof(LoggingConfig)));

        // AppReader mit Sub-Sections
        var appReader = config.AppReader;
        var appReaderSubs = new List<ConfigSectionDescriptor>
        {
            Make(appReader.Outlook,   "Outlook",    "appReader.outlook",   typeof(OutlookConfig)),
            Make(appReader.Browser,   "Browser",    "appReader.browser",   typeof(BrowserConfig)),
            Make(appReader.Notepad,   "Notepad",    "appReader.notepad",   typeof(NotepadConfig)),
            Make(appReader.Documents, "Documents",  "appReader.documents", typeof(DocumentsConfig)),
            Make(appReader.Pdf,       "PDF",        "appReader.pdf",       typeof(PdfConfig))
        };
        sections.Add(new ConfigSectionDescriptor(
            name: "appReader",
            displayName: "App Reader",
            path: "appReader",
            sectionType: typeof(AppReaderConfig),
            instance: appReader,
            subSections: appReaderSubs,
            properties: GetEditableProperties(typeof(AppReaderConfig), appReader)));

        sections.Add(Make(config.Trigger, "Trigger", "trigger", sectionType: typeof(TriggerConfig)));
        sections.Add(Make(config.Conversion, "Conversion", "conversion", sectionType: typeof(ConversionConfig)));

        return sections;
    }

    /// <summary>
    /// Finds a section by hierarchical path (e.g. <c>"appReader"</c> or
    /// <c>"appReader.browser"</c>). Returns <c>null</c> if not found.
    /// </summary>
    public static ConfigSectionDescriptor? FindByPath(AppConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(path);

        foreach (var section in GetTopLevelSections(config))
        {
            if (section.Path == path) return section;
            foreach (var sub in section.SubSections)
            {
                if (sub.Path == path) return sub;
            }
        }
        return null;
    }

    private static ConfigSectionDescriptor Make(object instance, string displayName, string name, Type sectionType)
    {
        return new ConfigSectionDescriptor(
            name: name,
            displayName: displayName,
            path: name,
            sectionType: sectionType,
            instance: instance,
            subSections: Array.Empty<ConfigSectionDescriptor>(),
            properties: GetEditableProperties(sectionType, instance));
    }

    private static IReadOnlyList<PropertyDescriptor> GetEditableProperties(Type type, object instance)
    {
        var collection = TypeDescriptor.GetProperties(instance);
        var result = new List<PropertyDescriptor>();
        foreach (PropertyDescriptor? prop in collection)
        {
            if (prop is null) continue;
            if (prop.IsReadOnly) continue;       // read-only properties nicht editierbar
            if (prop.PropertyType.IsArray) continue; // Arrays schwierig für PropertyGrid
            // Sub-Config-Klassen (BrowserConfig, CdpConfig, MarkdownSettings) werden als
            // Sub-Sections im TreeView angezeigt, NICHT als Property — sonst rekursive
            // Expansion. Ausnahme: einfache POCOs (z. B. WinEventSubscription, TriggerBlacklist)
            // werden als Property angezeigt, weil sie keine eigene Section im TreeView haben.
            if (IsExpandableConfigType(prop.PropertyType) && HasConfigAttribute(prop))
            {
                continue;
            }
            result.Add(prop);
        }
        return result;
    }

    private static bool IsExpandableConfigType(Type type)
    {
        // Klassen die selbst [JsonPropertyName] Attribute haben und mehrere eigene
        // editierbare Properties mitbringen — wir expandieren sie als Sub-Section statt
        // als einzelne PropertyGrid-Zeile. Sub-Sub-Configs (z. B. CdpConfig) bleiben als
        // einfache Properties sichtbar (BrowserConfig hat Sub-Section für Markdown).
        return type.IsClass
            && type != typeof(string)
            && !type.IsPrimitive
            && !type.IsGenericType   // List<>, Dictionary<> etc.
            && type != typeof(object)
            && type.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>() is not null;
    }

    private static bool HasConfigAttribute(PropertyDescriptor prop)
    {
        // Browser.Cdp / Browser.Markdown sind im PropertyGrid sichtbar als Sub-Section,
        // aber wir wollen sie NICHT als Property anzeigen (sie haben eigene Sub-Sections
        // in der Zukunft oder sind zu komplex). Hier: konservativ — wenn Property Type
        // eine Klasse mit [JsonPropertyName] ist, blende sie aus (User navigiert zur Sub-Section).
        return prop.PropertyType.IsClass
            && prop.PropertyType != typeof(string)
            && prop.PropertyType.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>() is not null;
    }
}