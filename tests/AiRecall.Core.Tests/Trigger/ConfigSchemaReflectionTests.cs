using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Xunit;

namespace AiRecall.Core.Tests.Trigger;

public class ConfigSchemaReflectionTests
{
    [Fact]
    public void GetTopLevelSections_ReturnsAllTopLevel()
    {
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        Assert.Equal(7, sections.Count);
        Assert.Contains(sections, s => s.Name == "capture");
        Assert.Contains(sections, s => s.Name == "screenRecorder");
        Assert.Contains(sections, s => s.Name == "ocr");
        Assert.Contains(sections, s => s.Name == "logging");
        Assert.Contains(sections, s => s.Name == "appReader");
        Assert.Contains(sections, s => s.Name == "trigger");
        Assert.Contains(sections, s => s.Name == "conversion");
    }

    [Fact]
    public void GetTopLevelSections_AppReader_HasAllSubSections()
    {
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        var appReader = sections.First(s => s.Name == "appReader");
        // Bug-Bash 2026-07-06 I-18: OneNote + Teams sind jetzt auch dabei (Spec 0010/0011).
        Assert.Equal(7, appReader.SubSections.Count);
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.outlook");
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.browser");
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.notepad");
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.documents");
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.pdf");
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.onenote");
        Assert.Contains(appReader.SubSections, s => s.Path == "appReader.teams");
    }

    [Fact]
    public void GetTopLevelSections_Recursive_SubSubConfigsAreVisible()
    {
        // Bug-Bash 2026-07-06 I-18: CdpConfig, MarkdownSettings,
        // WinEventSubscription, TriggerBlacklist, HtmlToMarkdownOptions
        // waren vorher unsichtbar. Jetzt: rekursiv.
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        var browser = sections.First(s => s.Name == "appReader")
            .SubSections.First(s => s.Path == "appReader.browser");
        Assert.Contains(browser.SubSections, s => s.Path == "appReader.browser.cdp");
        Assert.Contains(browser.SubSections, s => s.Path == "appReader.browser.markdown");

        var outlook = sections.First(s => s.Name == "appReader")
            .SubSections.First(s => s.Path == "appReader.outlook");
        Assert.Contains(outlook.SubSections, s => s.Path == "appReader.outlook.htmlToMarkdown");

        var trigger = sections.First(s => s.Name == "trigger");
        Assert.Contains(trigger.SubSections, s => s.Path == "trigger.winEvents");
        Assert.Contains(trigger.SubSections, s => s.Path == "trigger.blacklist");
    }

    [Fact]
    public void FindByPath_SubSubSection_ReturnsDeepSection()
    {
        var config = new AppConfig();
        var section = ConfigSchemaReflection.FindByPath(config, "appReader.browser.cdp");
        Assert.NotNull(section);
        Assert.Equal("CDP", section!.DisplayName);
        Assert.Equal(typeof(CdpConfig), section.SectionType);
    }

    [Fact]
    public void Section_HasDisplayNameAndPath()
    {
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        var appReader = sections.First(s => s.Name == "appReader");
        Assert.Equal("App Reader", appReader.DisplayName);
        Assert.Equal("appReader", appReader.Path);
        Assert.Equal(typeof(AppReaderConfig), appReader.SectionType);
    }

    [Fact]
    public void Section_PropertiesAreNonEmpty_AndContainKnownFields()
    {
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        var ocr = sections.First(s => s.Name == "ocr");
        Assert.NotEmpty(ocr.Properties);
        var propNames = ocr.Properties.Select(p => p.Name).ToList();
        Assert.Contains("Engine", propNames);
        Assert.Contains("Languages", propNames);
        Assert.Contains("TessDataPath", propNames);
    }

    [Fact]
    public void Section_InstanceIsLive_EditingPropertyReflectsInAppConfig()
    {
        var config = new AppConfig();
        var sections = ConfigSchemaReflection.GetTopLevelSections(config);
        var ocr = sections.First(s => s.Name == "ocr");

        // Mutate via descriptor instance
        var engineProp = ocr.Properties.First(p => p.Name == "Engine");
        engineProp.SetValue(ocr.Instance, "test-engine");
        Assert.Equal("test-engine", config.Ocr.Engine);
    }

    [Fact]
    public void FindByPath_TopLevel_ReturnsSection()
    {
        var config = new AppConfig();
        var section = ConfigSchemaReflection.FindByPath(config, "appReader");
        Assert.NotNull(section);
        Assert.Equal("App Reader", section!.DisplayName);
    }

    [Fact]
    public void FindByPath_SubSection_ReturnsSubSection()
    {
        var config = new AppConfig();
        var section = ConfigSchemaReflection.FindByPath(config, "appReader.browser");
        Assert.NotNull(section);
        Assert.Equal("Browser", section!.DisplayName);
        Assert.Equal(typeof(BrowserConfig), section.SectionType);
    }

    [Fact]
    public void FindByPath_NotFound_ReturnsNull()
    {
        var config = new AppConfig();
        var section = ConfigSchemaReflection.FindByPath(config, "nonexistent");
        Assert.Null(section);
    }

    [Fact]
    public void FindByPath_Empty_Throws()
    {
        var config = new AppConfig();
        Assert.Throws<ArgumentException>(() => ConfigSchemaReflection.FindByPath(config, ""));
        Assert.Throws<ArgumentNullException>(() => ConfigSchemaReflection.FindByPath(null!, "x"));
    }

    [Fact]
    public void GetTopLevelSections_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConfigSchemaReflection.GetTopLevelSections(null!));
    }

    [Fact]
    public void Section_Properties_NoReadOnly()
    {
        var sections = ConfigSchemaReflection.GetTopLevelSections(new AppConfig());
        foreach (var section in sections)
        {
            foreach (var prop in section.Properties)
            {
                Assert.False(prop.IsReadOnly, $"Property {section.Name}.{prop.Name} should be editable");
            }
        }
    }
}